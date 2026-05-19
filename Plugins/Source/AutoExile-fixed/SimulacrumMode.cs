using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using AutoExile.Modes.Shared;
using System.Numerics;
using System.IO;

namespace AutoExile.Modes
{
    /// <summary>
    /// Simulacrum farming loop:
    /// Hideout: stash items → insert simulacrum fragment → enter portal
    /// In map: find monolith → wave cycle (fight/loot/stash between waves) → exit after all waves or abort
    /// Death: revive (handled by BotCore) → re-enter map if portals remain
    /// </summary>
    public class SimulacrumMode : IBotMode
    {
        public string Name => "Simulacrum";

        private SimulacrumState _state = new();
        private SimPhase _phase = SimPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;
        private DateTime _lastActionTime = DateTime.MinValue;

        // Settings reference
        private BotSettings.SimulacrumSettings _settings = new();

        // Hideout/loop tracking
        private bool _mapCompleted;
        private string _lastAreaName = "";
        private DateTime _mapEnteredAt = DateTime.MinValue;

        // Fragment safety: stop if too many consecutive runs abort without completing even wave 1
        private int _consecutiveFailedRuns;
        private const int MaxConsecutiveFailedRuns = 3;

        // Death detection — track player alive state to catch deaths the base class may miss
        private bool _playerWasAlive = true;

        // Phase change tracking — for event log
        private SimPhase _prevPhase = SimPhase.Idle;

        // Wave lifecycle tracking — for WaveStart/WaveEnd events
        private bool _prevWaveActive;
        private DateTime _waveActiveStartTime = DateTime.MinValue;

        // Monolith found tracking — fire event only once per run
        private bool _monolithFoundLogged;

        // Loot tracking — only record on confirmed pickup
        private DateTime _lastLootScan = DateTime.MinValue;
        private const float LootScanIntervalMs = 500;
        private readonly LootPickupTracker _lootTracker = new();

        // Hideout flow
        private readonly HideoutFlow _hideoutFlow = new();

        // Between-wave stash tracking
        private bool _isStashing;
        // Set when stash times out mid-run; cleared on wave completion so the next
        // between-wave window gets a fresh attempt (inventory may have changed).
        private bool _stashBailed;

        // Wave transition tracking — tracks WavesCompleted so we reset exploration/timers
        // each time a wave actually finishes (not CurrentWave which stays at 15 in Mirage league).
        private int _lastKnownWavesCompleted;
        // Track whether we were searching (no monsters) last tick
        private bool _wasSearching;

        // Wave start retry tracking — bail if we can't start the next wave
        private DateTime _waveStartFirstTryTime = DateTime.MinValue;
        private DateTime _waveStartLastClickTime = DateTime.MinValue;
        private const float WaveStartTimeoutSeconds = 45f;
        private const float WaveStartClickCooldownMs = 2000f; // wait 2s between monolith clicks
        private DateTime _betweenWaveStartTime = DateTime.MinValue;
        private const float BetweenWaveTimeoutSeconds = 120f;

        // Post-click spawn grace — hold near monolith after clicking it to let monsters spawn
        private DateTime _waveSpawnWaitUntil = DateTime.MinValue;

        // Combat stuck detection — if fighting same monsters too long, move on
        private DateTime _combatEngageTime = DateTime.MinValue;
        private int _combatEngageCount;
        private int _combatEngageMaxTotal;
        private const float CombatStuckSeconds = 30f;

        // Dead zone unstick — player position hasn't changed for this long during an active wave
        private Vector2 _lastMovementPos;
        private DateTime _lastMovementAt = DateTime.MinValue;
        private const float DeadZoneUnstickSeconds = 15f;
        private const float MovementThresholdGrid = 3f;

        // Monster blacklist — temporarily ignore monsters we can't kill so we reposition via explore
        private readonly Dictionary<long, DateTime> _blacklistedMonsters = new();
        private const float MonsterBlacklistSeconds = 10f;

        // Movement and loot data collection.
        // Each run produces timestamped files in Logs/SimMovement/ next to the game exe.
        private const bool MovementLogEnabled = true;
        private const float MovementLogIntervalMs = 500f;
        private StreamWriter? _movementLog;
        private StreamWriter? _lootLog;
        private DateTime _lastMovementLogWrite = DateTime.MinValue;
        private DateTime _runLogStart = DateTime.MinValue;
        private Vector2 _lastLoggedPlayerPos;


        // Action cooldown
        private const float MajorActionCooldownMs = 500f;

        // Final-wave fallback timer — if wave 15 active state never transitions to 0
        // (known Mirage league behaviour), we exit after 10s with zero cached monsters.
        private DateTime _finalWaveNoMonstersAt = DateTime.MinValue;
        private static readonly Random _rng = new();
        private static float RandRange(float min, float max) =>
            min + (float)(_rng.NextDouble() * (max - min));
        private float _currentWaveDelay;

        // Overlay — set ShowOverlay = false to hide the on-screen HUD entirely.
        // Change OverlayX / OverlayY to reposition it on your screen.
        private const bool ShowOverlay = true;
        private const float OverlayX = 20f;
        private const float OverlayY = 100f;

        // Public for ImGui display
        public SimulacrumState State => _state;
        public SimPhase Phase => _phase;
        public string StatusText { get; private set; } = "";
        public string Decision
        {
            get => _decision;
            private set => _decision = value;
        }
        private string _decision = "";

        public void OnEnter(BotContext ctx)
        {
            _settings = ctx.Settings.Simulacrum;
            _currentWaveDelay = RandRange(
                _settings.MinWaveDelaySeconds.Value,
                _settings.MinWaveDelaySeconds.Value + 1.5f);
            _mapCompleted = false;
            _lastAreaName = "";
            _isStashing = false;
            _stashBailed = false;
            _lootTracker.Reset();
            _lastKnownWavesCompleted = 0;
            _wasSearching = false;
            _betweenWaveStartTime = DateTime.MinValue;
            _mapEnteredAt = DateTime.MinValue;
            _consecutiveFailedRuns = 0;
            _waveStartFirstTryTime = DateTime.MinValue;
            _waveStartLastClickTime = DateTime.MinValue;
            _waveSpawnWaitUntil = DateTime.MinValue;

            _combatEngageTime = DateTime.MinValue;
            _combatEngageCount = 0;
            _combatEngageMaxTotal = 0;
            _blacklistedMonsters.Clear();
            _lastMovementAt = DateTime.MinValue;

            _finalWaveNoMonstersAt = DateTime.MinValue;

            // Enable combat
            ModeHelpers.EnableDefaultCombat(ctx);

            OpenMovementLog();
            OpenLootLog();

            ctx.Loot.OnItemSkipped = (itemName, reason, chaosValue) =>
                WriteLootEvent("Skip", itemName, chaosValue, reason);

            // Determine starting phase based on location
            var gc = ctx.Game;
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                _phase = SimPhase.InHideout;
                _phaseStartTime = DateTime.Now;
                StatusText = "In hideout — preparing";
            }
            else
            {
                // Already in a map — try to find monolith
                _state.Reset();
                _phase = SimPhase.FindMonolith;
                _phaseStartTime = DateTime.Now;
                StatusText = "In map — finding monolith";

                // Initialize exploration if BotCore missed it (plugin reload mid-game)
                if (!ctx.Exploration.IsInitialized)
                {
                    var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                    var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
                    if (pfGrid != null && gc.Player != null)
                    {
                        var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                        ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                            ctx.Settings.Build.BlinkRange.Value);
                    }
                }
            }
        }

        public void OnExit()
        {
            CloseMovementLog();
            CloseLootLog();
            _state.Reset();
            _phase = SimPhase.Idle;
            _isStashing = false;
        }

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;

            // Detect area changes
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (!string.IsNullOrEmpty(currentArea) && currentArea != _lastAreaName)
            {
                OnAreaChanged(ctx, currentArea);
                _lastAreaName = currentArea;
            }

            // Phase change event logging
            if (_phase != _prevPhase)
            {
                WriteEvent("PhaseChange", _phase.ToString(), _prevPhase.ToString());
                _prevPhase = _phase;
            }

            // Always tick state when in map; combat only during active phases
            bool inMap = gc.Area?.CurrentArea != null &&
                         !gc.Area.CurrentArea.IsHideout &&
                         !gc.Area.CurrentArea.IsTown;
            if (inMap)
            {
                _state.Tick(gc, _currentWaveDelay);

                // Death detection: catch deaths the base class may not propagate to DeathCount
                var playerAlive = gc.Player?.IsAlive ?? true;
                if (!playerAlive && _playerWasAlive && _phase == SimPhase.WaveCycle)
                {
                    _state.DeathCount++;
                    WriteEvent("Death", $"Wave {_state.WavesCompleted + 1} death #{_state.DeathCount}", "");
                }
                _playerWasAlive = playerAlive;

                // Wave lifecycle events
                var waveNowActive = _state.IsWaveActive;
                if (waveNowActive && !_prevWaveActive)
                {
                    _waveActiveStartTime = DateTime.Now;
                    WriteEvent("WaveStart", $"Wave {_state.WavesCompleted + 1}", "");
                }
                else if (!waveNowActive && _prevWaveActive && _waveActiveStartTime != DateTime.MinValue)
                {
                    var waveDuration = (DateTime.Now - _waveActiveStartTime).TotalSeconds;
                    WriteEvent("WaveEnd", $"Wave {_state.WavesCompleted}", $"duration={waveDuration:F0}s");
                    _waveActiveStartTime = DateTime.MinValue;
                }
                _prevWaveActive = waveNowActive;

                // Monolith found — log once per run when position first locks in
                if (!_monolithFoundLogged && _state.MonolithPosition.HasValue)
                {
                    _monolithFoundLogged = true;
                    var mp = _state.MonolithPosition.Value;
                    WriteEvent("MonolithFound", $"({mp.X:F0};{mp.Y:F0})", "");
                }

                // Disable combat during LootSweep/ExitMap — we need to navigate freely
                // to pick up remaining items and reach the portal without being dragged into fights
                bool combatAllowed = _phase != SimPhase.LootSweep && _phase != SimPhase.ExitMap;
                if (combatAllowed)
                {
                    // Suppress cursor-moving skills when interaction is busy picking up loot
                    ctx.Combat.SuppressPositioning = ctx.Interaction.IsBusy;
                    ctx.Combat.SuppressTargetedSkills = ctx.Interaction.IsBusy;
                    ctx.Combat.Tick(ctx);
                }
            }

            // Tick interaction system
            var interactionResult = ctx.Interaction.Tick(gc);

            switch (_phase)
            {
                // --- Hideout phases ---
                case SimPhase.InHideout:
                case SimPhase.StashItems:
                case SimPhase.OpenMap:
                case SimPhase.EnterPortal:
                    var signal = _hideoutFlow.Tick(ctx);
                    StatusText = _hideoutFlow.Status;
                    if (signal == HideoutSignal.PortalTimeout)
                    {
                        _consecutiveFailedRuns++;
                        _state.Reset();
                        if (_consecutiveFailedRuns >= MaxConsecutiveFailedRuns)
                            _consecutiveFailedRuns = 0;
                        _phase = SimPhase.InHideout;
                        _phaseStartTime = DateTime.Now;
                        StartHideoutFlow(ctx);
                        StatusText = "No portal found — starting fresh run";
                    }
                    else if (signal == HideoutSignal.NoFragments)
                    {
                        _phase = SimPhase.Idle;
                        StatusText = "Stopped: out of simulacrum fragments. Restock and restart.";
                    }
                    break;

                // --- Map phases ---
                case SimPhase.FindMonolith:
                    TickFindMonolith(ctx);
                    break;
                case SimPhase.NavigateToMonolith:
                    TickNavigateToMonolith(ctx);
                    break;
                case SimPhase.WaveCycle:
                    TickWaveCycle(ctx, interactionResult);
                    break;
                case SimPhase.BetweenWaveStash:
                    TickBetweenWaveStash(ctx, interactionResult);
                    break;
                case SimPhase.LootSweep:
                    TickLootSweep(ctx, interactionResult);
                    break;
                case SimPhase.ExitMap:
                    TickExitMap(ctx);
                    break;
                case SimPhase.Done:
                    StatusText = "Simulacrum complete";
                    break;
                case SimPhase.Idle:
                    StatusText = "Idle";
                    break;
            }

            if (inMap)
                WriteMovementSnapshot(ctx);
        }

        // =================================================================
        // Area change detection
        // =================================================================

        private void OnAreaChanged(BotContext ctx, string newArea)
        {
            var gc = ctx.Game;

            // Cancel any in-flight systems
            ModeHelpers.CancelAllSystems(ctx);
            _hideoutFlow.Cancel();
            _isStashing = false;

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                if (_mapCompleted)
                {
                    // Map completed — start new cycle
                    WriteEvent("RunEnd", $"Complete-w{_state.WavesCompleted}", "");
                    _consecutiveFailedRuns = 0;
                    _state.RecordRunComplete();
                    _state.Reset();
                    _phase = SimPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    _mapCompleted = false;
                    _lootTracker.ResetCount();
                    StartHideoutFlow(ctx);
                    StatusText = "Back in hideout — starting new run";
                }
                else if (_state.DeathCount > 0 && _state.DeathCount < ctx.Settings.Run.MaxDeaths.Value)
                {
                    if (_state.WavesCompleted >= SimulacrumState.MaxWavesInEncounter - 2)
                    {
                        // Died on final waves — encounter is effectively over, start fresh
                        WriteEvent("RunEnd", $"DiedFinalWaves-w{_state.WavesCompleted + 1}-deaths{_state.DeathCount}", "");
                        _state.Reset();
                        _lootTracker.ResetCount();
                        _consecutiveFailedRuns = 0;
                        _phase = SimPhase.InHideout;
                        _phaseStartTime = DateTime.Now;
                        StartHideoutFlow(ctx);
                        StatusText = "Died on final waves — encounter done, starting fresh run";
                    }
                    else
                    {
                        // Died mid-run — try to re-enter
                        WriteEvent("RunEnd", $"DiedMidRun-w{_state.WavesCompleted + 1}-deaths{_state.DeathCount}", "reentry");
                        _phase = SimPhase.EnterPortal;
                        _phaseStartTime = DateTime.Now;
                        _hideoutFlow.StartPortalReentry();
                        StatusText = $"Revived (death {_state.DeathCount}) — re-entering map";
                    }
                }
                else if (_state.DeathCount >= ctx.Settings.Run.MaxDeaths.Value)
                {
                    // Too many deaths — start fresh
                    WriteEvent("RunEnd", $"TooManyDeaths-w{_state.WavesCompleted + 1}-deaths{_state.DeathCount}", "");
                    _consecutiveFailedRuns++;
                    _state.RecordRunComplete();
                    _state.Reset();
                    _phase = SimPhase.InHideout;
                    _phaseStartTime = DateTime.Now;
                    _lootTracker.ResetCount();
                    if (_consecutiveFailedRuns >= MaxConsecutiveFailedRuns)
                    {
                        // Too many back-to-back aborted runs — stop to prevent burning all fragments
                        _phase = SimPhase.Idle;
                        StatusText = $"Stopped: {_consecutiveFailedRuns} consecutive failed runs (deaths). " +
                                     "Fix the character build or reduce MaxDeaths, then restart.";
                        return;
                    }
                    StartHideoutFlow(ctx);
                    StatusText = "Too many deaths — starting new run";
                }
                else
                {
                    // Returned to hideout without dying and without completing the map.
                    var timeInMap = _mapEnteredAt != DateTime.MinValue
                        ? (DateTime.Now - _mapEnteredAt).TotalSeconds
                        : 0;

                    if (timeInMap > 0 && timeInMap < 30)
                    {
                        _consecutiveFailedRuns++;
                        if (_consecutiveFailedRuns >= MaxConsecutiveFailedRuns)
                        {
                            _phase = SimPhase.Idle;
                            StatusText = $"Stopped: exiting simulacrum within {timeInMap:F0}s on " +
                                         $"{_consecutiveFailedRuns} consecutive runs. Check the bot " +
                                         "is not clicking the entrance portal accidentally.";
                            return;
                        }
                    }

                    // If we were deep into the run (wave 10+), skip portal re-entry and start
                    // fresh. Re-entry after high waves is unreliable: the bot re-enters with
                    // no death context, gets confused about wave state, exits again, then
                    // hunts for a portal that's now gone — causing 5-10 min hideout stalls.
                    if (_state.WavesCompleted >= SimulacrumState.MaxWavesInEncounter - 5)
                    {
                        WriteEvent("RunEnd", $"UnexpectedReturn-w{_state.WavesCompleted + 1}", "");
                        _consecutiveFailedRuns = 0;
                        _state.RecordRunComplete();
                        _state.Reset();
                        _lootTracker.ResetCount();
                        _phase = SimPhase.InHideout;
                        _phaseStartTime = DateTime.Now;
                        StartHideoutFlow(ctx);
                        StatusText = $"Returned from late waves — starting fresh run";
                        return;
                    }

                    // Try portal re-entry before burning a new fragment
                    WriteEvent("RunEnd", $"UnexpectedReturn-w{_state.WavesCompleted + 1}-reentry", "");
                    _phase = SimPhase.EnterPortal;
                    _phaseStartTime = DateTime.Now;
                    _hideoutFlow.StartPortalReentry();
                    StatusText = "Returned to hideout unexpectedly — checking for re-entry portal";
                }
            }
            else
            {
                // Entered map — reset exploration so we start fresh.
                var deathCount = _state.DeathCount;
                var prevMonolithPos = _state.MonolithPosition;
                var prevWavesCompleted = _state.WavesCompleted;
                _state.OnAreaChanged();
                _state.DeathCount = deathCount;
                if (deathCount > 0 && prevMonolithPos.HasValue)
                {
                    _state.RestoreForReentry(prevMonolithPos.Value, prevWavesCompleted);
                    _phase = SimPhase.NavigateToMonolith;
                    StatusText = "Re-entering after death — navigating to monolith";
                }
                else
                {
                    _phase = SimPhase.FindMonolith;
                }
                _phaseStartTime = DateTime.Now;

                // Compute a fresh random wave delay for this map entry.
                _currentWaveDelay = RandRange(
                    _settings.MinWaveDelaySeconds.Value,
                    _settings.MinWaveDelaySeconds.Value + 1.5f);

                // Reset all per-wave fields so stale timers from a previous run don't
                // immediately trigger timeouts on the first WaveCycle tick.
                _betweenWaveStartTime = DateTime.MinValue;
                _waveStartFirstTryTime = DateTime.MinValue;
                _waveStartLastClickTime = DateTime.MinValue;
                _waveSpawnWaitUntil = DateTime.MinValue;
                _lastKnownWavesCompleted = 0;
                _wasSearching = false;
                _combatEngageTime = DateTime.MinValue;
                _combatEngageCount = 0;
                _combatEngageMaxTotal = 0;
                _blacklistedMonsters.Clear();
                _lastMovementAt = DateTime.MinValue;
                _finalWaveNoMonstersAt = DateTime.MinValue;
                _prevWaveActive = false;
                _waveActiveStartTime = DateTime.MinValue;
                _monolithFoundLogged = false;

                // Force-reinitialize exploration for this new instance
                var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
                if (pfGrid != null && gc.Player != null)
                {
                    var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                    ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                        ctx.Settings.Build.BlinkRange.Value);
                }

                _mapEnteredAt = DateTime.Now;
                _lootTracker.ResetCount();
                StatusText = "Entered map — finding monolith";
            }
        }

        // Full Simulacrum metadata path — same item the map device consumes.
        private const string FullSimulacrumPath = "CurrencyAfflictionFragment";

        /// <summary>
        /// Stash filter: stash everything EXCEPT full Simulacrums.
        /// </summary>
        private static bool KeepSimulacrumsFilter(ServerInventory.InventSlotItem item)
        {
            var path = item.Item?.Path;
            if (path != null && path.Contains(FullSimulacrumPath, StringComparison.OrdinalIgnoreCase))
                return false; // keep — don't stash
            return true;      // stash everything else
        }

        private void StartHideoutFlow(BotContext ctx)
        {
            var stash = ctx.Settings.Stash;
            var sim   = ctx.Settings.Simulacrum;

            _hideoutFlow.Start(MapDeviceSystem.IsSimulacrum,
                stashItemFilter:    KeepSimulacrumsFilter,
                stashItemThreshold: ctx.Settings.Run.StashItemThreshold.Value,
                dumpTabName:        string.IsNullOrWhiteSpace(stash.DumpTabName.Value)     ? null : stash.DumpTabName.Value,
                resourceTabName:    string.IsNullOrWhiteSpace(stash.FragmentTabName.Value) ? null : stash.FragmentTabName.Value,
                withdrawFragmentPath:  FullSimulacrumPath,
                inventoryFragmentPath: FullSimulacrumPath,
                fragmentStock:  sim.SimulacrumStock.Value,
                minFragments:   1);
        }

        // =================================================================
        // Map phases
        // =================================================================

        private void TickFindMonolith(BotContext ctx)
        {
            if (_state.MonolithPosition.HasValue)
            {
                _phase = SimPhase.NavigateToMonolith;
                _phaseStartTime = DateTime.Now;
                StatusText = "Monolith found — navigating";
                return;
            }

            var gc = ctx.Game;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            // Wait for entity list to settle after zone load
            if (elapsed < ctx.Settings.AreaSettleSeconds.Value)
            {
                StatusText = "Searching for monolith...";
                return;
            }

            // Explore the map until the monolith entity enters the network bubble.
            if (ctx.Exploration.IsInitialized)
            {
                ctx.Exploration.Update(gc.Player.GridPosNum);
                var playerPos = gc.Player.GridPosNum;

                if (ctx.Exploration.ActiveBlobCoverage >= 0.99f)
                {
                    ctx.Exploration.ResetSeen();
                    ctx.Exploration.Update(gc.Player.GridPosNum);
                }

                if (!ctx.Navigation.IsNavigating)
                {
                    var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                    if (target.HasValue)
                    {
                        ctx.Navigation.NavigateTo(gc, target.Value);
                    }
                }
            }

            StatusText = "Exploring to find monolith...";

            if (elapsed > _settings.WaveTimeoutMinutes.Value * 60)
            {
                StatusText = "No monolith found — timeout";
                _phase = SimPhase.Done;
            }
        }

        private void TickNavigateToMonolith(BotContext ctx)
        {
            if (!_state.MonolithPosition.HasValue)
            {
                _phase = SimPhase.FindMonolith;
                return;
            }

            // If wave is already active (re-entry after death), go straight to wave cycle
            if (_state.IsWaveActive)
            {
                ctx.Navigation.Stop(ctx.Game);
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                StatusText = "Wave already active — joining combat";
                return;
            }

            var playerPos = ctx.Game.Player.GridPosNum;
            var dist = Vector2.Distance(playerPos, _state.MonolithPosition.Value);

            if (dist < 18f)
            {
                ctx.Navigation.Stop(ctx.Game);
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                StatusText = "Near monolith — entering wave cycle";
                return;
            }

            if (!ctx.Navigation.IsNavigating)
            {
                var success = ctx.Navigation.NavigateTo(ctx.Game,
                    _state.MonolithPosition.Value);
                if (!success)
                {
                    StatusText = "No path to monolith";
                    _phase = SimPhase.Done;
                    return;
                }
            }

            StatusText = $"Navigating to monolith (dist: {dist:F0})";
        }

        // =================================================================
        // Wave cycle — the main decision loop
        // =================================================================

        private void TickWaveCycle(BotContext ctx, InteractionResult interactionResult)
        {
            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;

            // Handle pending loot pickup results
            _lootTracker.HandleResult(interactionResult, ctx);

            // --- Wave transition: a wave just completed ---
            // Use WavesCompleted (internally tracked active→inactive transitions) instead of
            // CurrentWave, which stays at 15 in Mirage league and never triggers this block.
            if (_state.WavesCompleted != _lastKnownWavesCompleted)
            {
                _lastKnownWavesCompleted = _state.WavesCompleted;
                // Re-randomize the inter-wave delay for this new wave
                _currentWaveDelay = RandRange(
                    _settings.MinWaveDelaySeconds.Value,
                    _settings.MinWaveDelaySeconds.Value + 1.5f);
                _finalWaveNoMonstersAt = DateTime.MinValue;
                ctx.Exploration.SeenRadiusOverride = 0; // restore normal radius for new wave
                ctx.Exploration.ResetSeen();
                ctx.Loot.ClearFailed(); // items that failed in earlier waves may be pickable now
                _blacklistedMonsters.Clear(); // new wave = fresh monster spawns
                _wasSearching = false;
                _waveStartFirstTryTime = DateTime.MinValue;
                _waveStartLastClickTime = DateTime.MinValue;
                _betweenWaveStartTime = DateTime.MinValue;
                _lastMovementAt = DateTime.MinValue;
                _stashBailed = false; // new wave — retry stash if inventory still needs it
            }

            // --- Priority 0: Don't interrupt active loot pickup ---
            if (ctx.Interaction.IsBusy && _lootTracker.HasPending)
            {
                Decision = $"Loot pickup in progress: {_lootTracker.PendingItemName}";
                StatusText = $"Picking up {_lootTracker.PendingItemName}";
                return;
            }

            // --- Priority 1: Pick up nearby loot (during active waves only) ---
            if (_state.IsWaveActive)
            {
                if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= LootScanIntervalMs)
                {
                    ctx.Loot.Scan(gc);
                    _lastLootScan = DateTime.Now;
                }

                if (ctx.Loot.HasLootNearby && !ctx.Interaction.IsBusy)
                {
                    var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                    if (candidate != null && ctx.Interaction.IsBusy)
                    {
                        _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                        WriteLootEvent("Pickup", candidate.ItemName, candidate.ChaosValue);
                        Decision = $"Loot: {candidate.ItemName}";
                        StatusText = $"Picking up {candidate.ItemName}";
                        return;
                    }
                }
            }

            // --- Priority 2: Wave timeout check ---
            if (_state.IsWaveActive &&
                (DateTime.Now - _state.WaveStartedAt).TotalMinutes > _settings.WaveTimeoutMinutes.Value)
            {
                Decision = "Wave timeout → LootSweep";
                WriteEvent("LootSweepStart", $"Wave {_state.WavesCompleted + 1}", "wave-timeout");
                _phase = SimPhase.LootSweep;
                _phaseStartTime = DateTime.Now;
                _sweepNearMonolith = false;
                _lastEmptyScanAt = DateTime.MinValue;
                StatusText = $"Wave {_state.WavesCompleted + 1} timed out — sweeping loot before exit";
                return;
            }

            // --- Priority 2b: Final-wave fallback ---
            // In Mirage league the monolith 'active' state can stay 1 after wave 15 ends,
            // so WavesCompleted never reaches 15 via the normal active→inactive transition.
            // If we're on the last wave and see zero cached monsters for 10 consecutive
            // seconds we treat the encounter as complete and move to LootSweep.
            if (_state.IsWaveActive &&
                _state.WavesCompleted == SimulacrumState.MaxWavesInEncounter - 1 &&
                ctx.Combat.CachedMonsterCount == 0 &&
                ctx.Combat.NearbyMonsterCount == 0)
            {
                if (_finalWaveNoMonstersAt == DateTime.MinValue)
                    _finalWaveNoMonstersAt = DateTime.Now;

                var noMonsterSecs = (DateTime.Now - _finalWaveNoMonstersAt).TotalSeconds;
                if (noMonsterSecs >= 10.0)
                {
                    Decision = "Wave 15: no monsters for 10s → LootSweep";
                    WriteEvent("LootSweepStart", $"Wave {_state.WavesCompleted + 1}", "final-wave-no-monsters");
                    _phase = SimPhase.LootSweep;
                    _phaseStartTime = DateTime.Now;
                    _sweepNearMonolith = false;
                    _lastEmptyScanAt = DateTime.MinValue;
                    StatusText = "Wave 15 complete — sweeping loot";
                    return;
                }
                // Keep exploring while the timer runs
                StatusText = $"Wave 15 — confirming clear ({noMonsterSecs:F0}s / 10s)";
            }
            else
            {
                _finalWaveNoMonstersAt = DateTime.MinValue;
            }

            // --- Priority 3: Wave active — fight and explore ---
            if (_state.IsWaveActive)
            {
                // After clicking the monolith, pause near it to let monsters fully spawn.
                if (DateTime.Now < _waveSpawnWaitUntil)
                {
                    // Keep the stuck timer from counting during an intentional pause
                    _lastMovementAt = DateTime.Now;
                    _lastMovementPos = playerPos;
                    IdleNearMonolith(ctx);
                    var spawnWait = (_waveSpawnWaitUntil - DateTime.Now).TotalSeconds;
                    Decision = $"Wave {_state.WavesCompleted + 1} — waiting for spawn ({spawnWait:F1}s)";
                    StatusText = $"Wave {_state.WavesCompleted + 1}/{SimulacrumState.MaxWavesInEncounter} — waiting for monsters to spawn...";
                    return;
                }

                // Dead zone unstick: if the player hasn't moved for DeadZoneUnstickSeconds,
                // pathfinding has silently stalled. Force a nav+exploration reset.
                if (_lastMovementAt == DateTime.MinValue ||
                    Vector2.Distance(playerPos, _lastMovementPos) > MovementThresholdGrid)
                {
                    _lastMovementPos = playerPos;
                    _lastMovementAt = DateTime.Now;
                }
                else if ((DateTime.Now - _lastMovementAt).TotalSeconds > DeadZoneUnstickSeconds)
                {
                    _lastMovementAt = DateTime.Now;
                    _lastMovementPos = playerPos;
                    ctx.Navigation.Stop(gc);
                    _wasSearching = true;
                    ctx.Exploration.SeenRadiusOverride = 40;
                    ctx.Exploration.ResetSeen();
                    WriteEvent("UnstickFired", $"Wave {_state.WavesCompleted + 1}", $"nearby={ctx.Combat.NearbyMonsterCount};cached={ctx.Combat.CachedMonsterCount}");
                    Decision = $"Wave {_state.WavesCompleted + 1} — dead zone unstick (no movement for {DeadZoneUnstickSeconds:F0}s)";
                    StatusText = $"Wave {_state.WavesCompleted + 1}/{SimulacrumState.MaxWavesInEncounter} — unsticking navigation...";
                }

                if (ctx.Combat.NearbyMonsterCount > 0)
                {
                    int currentTotal = ctx.Combat.CachedMonsterCount;
                    if (_combatEngageTime == DateTime.MinValue)
                    {
                        _combatEngageTime = DateTime.Now;
                        _combatEngageCount = ctx.Combat.NearbyMonsterCount;
                        _combatEngageMaxTotal = currentTotal;
                    }
                    else
                    {
                        if (currentTotal > _combatEngageMaxTotal)
                            _combatEngageMaxTotal = currentTotal;
                        // Reset timer when kills are happening (total dropped below peak)
                        // or nearby count fell below initial engage count.
                        if (currentTotal < _combatEngageMaxTotal ||
                            ctx.Combat.NearbyMonsterCount < _combatEngageCount)
                        {
                            _combatEngageTime = DateTime.Now;
                            _combatEngageCount = ctx.Combat.NearbyMonsterCount;
                            _combatEngageMaxTotal = currentTotal;
                        }
                    }

                    var combatElapsed = (DateTime.Now - _combatEngageTime).TotalSeconds;
                    if (combatElapsed > CombatStuckSeconds)
                    {
                        _combatEngageTime = DateTime.MinValue;
                        _combatEngageCount = 0;
                        _combatEngageMaxTotal = 0;
                        BlacklistNearbyMonsters(gc, gc.Player.GridPosNum, ctx.Settings.Build.CombatRange.Value);
                        ctx.Navigation.Stop(gc);
                        if (!_wasSearching)
                        {
                            _wasSearching = true;
                            ctx.Exploration.SeenRadiusOverride = 40;
                            ctx.Exploration.ResetSeen();
                        }
                        Decision = $"Wave {_state.WavesCompleted + 1} — combat stuck ({combatElapsed:F0}s), blacklisted {_blacklistedMonsters.Count} monsters";
                        TickExploreForMonsters(ctx);
                    }
                    else
                    {
                        if (_wasSearching)
                        {
                            _wasSearching = false;
                            ctx.Exploration.SeenRadiusOverride = 0;
                            // Do not stop navigation here — let the existing path continue.
                            // The combat redirect below will smooth-adjust the destination.
                        }

                        if (ctx.Combat.WantsToMove &&
                            ctx.Combat.Profile.Positioning == CombatPositioning.Aggressive &&
                            !ctx.Interaction.IsBusy)
                        {
                            var combatTarget = ctx.Combat.MoveTargetGrid;
                            // Smooth redirect: adjust existing path rather than stop+restart.
                            // Drift threshold of 40 prevents micro-corrections when the combat
                            // target shifts slightly between ticks due to moving enemies.
                            if (ctx.Navigation.IsNavigating)
                                ctx.Navigation.UpdateDestination(gc, combatTarget, driftThreshold: 40f);
                            else
                                ctx.Navigation.NavigateTo(gc, combatTarget);
                            Decision = $"Wave {_state.WavesCompleted + 1} — aggressive: pathing to density @ ({combatTarget.X:F0},{combatTarget.Y:F0})";
                        }
                        else if (ctx.Combat.CachedMonsterCount > ctx.Combat.NearbyMonsterCount &&
                                 !ctx.Interaction.IsBusy)
                        {
                            // More monsters exist beyond the nearby pack. Minions will kill
                            // what's here automatically — keep walking toward the next cluster.
                            TickExploreForMonsters(ctx);
                            Decision = $"Wave {_state.WavesCompleted + 1} — advancing to next cluster ({ctx.Combat.CachedMonsterCount - ctx.Combat.NearbyMonsterCount} ahead)";
                        }
                        else
                        {
                            Decision = $"Wave {_state.WavesCompleted + 1} — fighting ({ctx.Combat.NearbyMonsterCount} nearby, {ctx.Combat.CachedMonsterCount} total)";
                        }
                        StatusText = $"Wave {_state.WavesCompleted + 1}/{SimulacrumState.MaxWavesInEncounter} — fighting {ctx.Combat.NearbyMonsterCount} monsters";
                    }
                }
                else
                {
                    if (!_wasSearching)
                    {
                        _wasSearching = true;
                        ctx.Exploration.SeenRadiusOverride = 40;
                        ctx.Exploration.ResetSeen();
                    }
                    _combatEngageTime = DateTime.MinValue;
                    _combatEngageCount = 0;
                    _combatEngageMaxTotal = 0;

                    Decision = $"Wave {_state.WavesCompleted + 1} — patrolling ({ctx.Combat.CachedMonsterCount} distant)";
                    TickExploreForMonsters(ctx);
                }
                return;
            }

            // --- Between waves ---

            // Priority 4: Stash items if inventory above threshold.
            if (!_state.IsWaveActive && _state.StashPosition.HasValue && !ctx.Interaction.IsBusy)
            {
                int stashableCount = 0;
                var slots = StashSystem.GetInventorySlotItems(gc);
                if (slots != null)
                    foreach (var it in slots)
                        if (KeepSimulacrumsFilter(it)) stashableCount++;

                bool shouldStartStashing    = !_stashBailed && stashableCount >= ctx.Settings.Run.StashItemThreshold.Value;
                bool shouldContinueStashing = !_stashBailed && _isStashing && stashableCount > 0;

                if (shouldStartStashing || shouldContinueStashing)
                {
                    _isStashing = true;
                    Decision = $"Between waves → Stash ({stashableCount} items)";
                    _phase = SimPhase.BetweenWaveStash;
                    _phaseStartTime = DateTime.Now;
                    StatusText = $"Stashing items ({stashableCount} stashable in inventory)";
                    return;
                }
                _isStashing = false;
            }

            // Priority 5: Loot must be fully cleared before starting next wave.
            if (!_state.IsWaveActive)
            {
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;

                bool hasLoot = ctx.Loot.HasLootNearby;
                bool pickingUp = ctx.Interaction.IsBusy && _lootTracker.HasPending;

                if (hasLoot || pickingUp)
                {
                    if (hasLoot)
                        _state.ResetWaveDelay(_currentWaveDelay);

                    if (hasLoot && !ctx.Interaction.IsBusy)
                    {
                        var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                        if (candidate != null && ctx.Interaction.IsBusy)
                        {
                            _lootTracker.SetPending(candidate.Entity.Id, candidate.ItemName, candidate.ChaosValue);
                            WriteLootEvent("Pickup", candidate.ItemName, candidate.ChaosValue);
                            Decision = $"Between waves — loot: {candidate.ItemName}";
                            StatusText = $"Picking up {candidate.ItemName} (between waves)";
                            return;
                        }
                    }

                    if (!ctx.Interaction.IsBusy)
                        IdleNearMonolith(ctx);
                    Decision = pickingUp ? "Between waves — picking up loot" : "Between waves — clearing loot";
                    StatusText = pickingUp ? $"Picking up loot (between waves)" : "Loot nearby — clearing before next wave";
                    return;
                }

                // No loot from current position — return to monolith before allowing wave start.
                if (_state.MonolithPosition.HasValue)
                {
                    var distToMonolith = Vector2.Distance(gc.Player.GridPosNum, _state.MonolithPosition.Value);
                    if (distToMonolith > 30f)
                    {
                        IdleNearMonolith(ctx);
                        _state.ResetWaveDelay(_currentWaveDelay);
                        Decision = "Between waves — returning to monolith before wave start";
                        StatusText = "Returning to monolith to check for remaining loot";
                        return;
                    }
                }
            }

            // Priority 6: All waves complete — sweep remaining loot and exit.
            // IsEncounterComplete is set by SimulacrumState once WavesCompleted >= MaxWavesInEncounter.
            // We track this internally (counting active→inactive transitions) rather than using the
            // 'wave' or 'goodbye' StateMachine values which are unreliable in Mirage league.
            if (_state.IsEncounterComplete && !_state.IsWaveActive)
            {
                Decision = $"All {SimulacrumState.MaxWavesInEncounter} waves complete → LootSweep";
                WriteEvent("LootSweepStart", $"Wave {_state.WavesCompleted + 1}", "all-waves-complete");
                _phase = SimPhase.LootSweep;
                _phaseStartTime = DateTime.Now;
                _sweepNearMonolith = false;
                _lastEmptyScanAt = DateTime.MinValue;
                StatusText = $"All waves complete — sweeping loot";
                return;
            }

            // Priority 7: Start next wave (loot is clear AND delay has passed)
            if (_state.CanStartWaveAt == DateTime.MinValue)
            {
                _state.ResetWaveDelay(_currentWaveDelay);
            }

            // Track how long we've been between waves — bail if stuck too long
            if (_betweenWaveStartTime == DateTime.MinValue)
                _betweenWaveStartTime = DateTime.Now;
            var betweenWaveElapsed = (DateTime.Now - _betweenWaveStartTime).TotalSeconds;
            if (betweenWaveElapsed > BetweenWaveTimeoutSeconds)
            {
                Decision = "Between-wave timeout → LootSweep";
                WriteEvent("LootSweepStart", $"Wave {_state.WavesCompleted + 1}", "between-wave-timeout");
                _phase = SimPhase.LootSweep;
                _phaseStartTime = DateTime.Now;
                _sweepNearMonolith = false;
                _lastEmptyScanAt = DateTime.MinValue;
                StatusText = $"Stuck between waves for {BetweenWaveTimeoutSeconds}s — exiting";
                return;
            }

            if (_waveStartFirstTryTime != DateTime.MinValue &&
                (DateTime.Now - _waveStartFirstTryTime).TotalSeconds > WaveStartTimeoutSeconds)
            {
                Decision = $"Failed to start wave after {WaveStartTimeoutSeconds}s → LootSweep";
                WriteEvent("LootSweepStart", $"Wave {_state.WavesCompleted + 1}", "wave-start-timeout");
                _phase = SimPhase.LootSweep;
                _phaseStartTime = DateTime.Now;
                _sweepNearMonolith = false;
                _lastEmptyScanAt = DateTime.MinValue;
                StatusText = $"Can't start wave {_state.WavesCompleted + 1} — no wave started in {WaveStartTimeoutSeconds}s";
                return;
            }

            if (DateTime.Now >= _state.CanStartWaveAt && !_state.IsEncounterComplete)
            {
                var elapsed = _waveStartFirstTryTime == DateTime.MinValue ? 0.0 : (DateTime.Now - _waveStartFirstTryTime).TotalSeconds;
                Decision = $"Wave {_state.WavesCompleted + 1}/{SimulacrumState.MaxWavesInEncounter} → StartWave ({elapsed:F1}s / {WaveStartTimeoutSeconds}s)";
                TickStartWave(ctx);
                return;
            }

            // Waiting for wave delay (loot is clear, timer running)
            var waitRemaining = (_state.CanStartWaveAt - DateTime.Now).TotalSeconds;
            Decision = $"Loot clear — waiting ({waitRemaining:F1}s)";
            IdleNearMonolith(ctx);
            StatusText = $"Wave {_state.WavesCompleted}/{SimulacrumState.MaxWavesInEncounter} done — {waitRemaining:F1}s until next wave";
        }

        // =================================================================
        // Movement data collection
        // =================================================================

        private void OpenMovementLog()
        {
            if (!MovementLogEnabled) return;
            try
            {
                var logDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Logs", "SimMovement");
                Directory.CreateDirectory(logDir);
                var fileName = $"sim_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                _movementLog = new StreamWriter(Path.Combine(logDir, fileName), append: false);
                _movementLog.WriteLine(
                    "TimeMs,Wave,WaveActive,Phase,Decision," +
                    "PlayerX,PlayerY,MovedSinceLastLog," +
                    "Nearby,Cached,Navigating,NavDestX,NavDestY," +
                    "WantsToMove,Searching,NoMoveSecs,CombatEngageSecs," +
                    "LootNearby,LootCount,Deaths");
                _movementLog.Flush();
                _runLogStart = DateTime.Now;
                _lastMovementLogWrite = DateTime.MinValue;
            }
            catch { _movementLog = null; }
        }

        private void CloseMovementLog()
        {
            try { _movementLog?.Flush(); _movementLog?.Close(); }
            catch { }
            _movementLog = null;
        }

        private void OpenLootLog()
        {
            if (!MovementLogEnabled) return;
            try
            {
                var logDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Logs", "SimMovement");
                Directory.CreateDirectory(logDir);
                var fileName = $"loot_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                _lootLog = new StreamWriter(Path.Combine(logDir, fileName), append: false);
                _lootLog.WriteLine("TimeMs,Wave,WaveActive,Phase,Action,Detail,ChaosValue,Reason");
                _lootLog.Flush();
            }
            catch { _lootLog = null; }
        }

        private void CloseLootLog()
        {
            try { _lootLog?.Flush(); _lootLog?.Close(); }
            catch { }
            _lootLog = null;
        }

        private void WriteLootEvent(string action, string itemName, double chaosValue, string reason = "")
        {
            if (_lootLog == null) return;
            var timeMs = (long)(DateTime.Now - _runLogStart).TotalMilliseconds;
            var safeName = itemName.Replace(',', ';');
            var safeReason = reason.Replace(',', ';');
            _lootLog.WriteLine(
                $"{timeMs},{_state.WavesCompleted + 1},{(_state.IsWaveActive ? 1 : 0)}," +
                $"{_phase},{action},{safeName},{chaosValue:F1},{safeReason}");
            _lootLog.Flush();
        }

        // Log non-loot events (deaths, phase changes, run boundaries) to the same file
        private void WriteEvent(string action, string detail, string reason)
        {
            if (_lootLog == null) return;
            var timeMs = (long)(DateTime.Now - _runLogStart).TotalMilliseconds;
            var safeDetail = detail.Replace(',', ';');
            var safeReason = reason.Replace(',', ';');
            _lootLog.WriteLine(
                $"{timeMs},{_state.WavesCompleted + 1},{(_state.IsWaveActive ? 1 : 0)}," +
                $"{_phase},{action},{safeDetail},0,{safeReason}");
            _lootLog.Flush();
        }

        private void WriteMovementSnapshot(BotContext ctx)
        {
            if (!MovementLogEnabled || _movementLog == null) return;
            if ((DateTime.Now - _lastMovementLogWrite).TotalMilliseconds < MovementLogIntervalMs) return;
            _lastMovementLogWrite = DateTime.Now;

            try
            {
                var gc = ctx.Game;
                var pos = gc.Player?.GridPosNum ?? Vector2.Zero;
                var timeMs = (long)(DateTime.Now - _runLogStart).TotalMilliseconds;

                // Nav destination — last waypoint in current path
                var navPath = ctx.Navigation.CurrentNavPath;
                var navDest = navPath.Count > 0 ? navPath[navPath.Count - 1].Position : Vector2.Zero;

                // Whether player has moved meaningfully since last snapshot
                var moved = Vector2.Distance(pos, _lastLoggedPlayerPos) > 2f;
                _lastLoggedPlayerPos = pos;

                // Seconds since last meaningful movement (from dead zone tracker)
                var noMoveSecs = _lastMovementAt == DateTime.MinValue ? 0.0
                    : (DateTime.Now - _lastMovementAt).TotalSeconds;

                // Seconds engaged with the same combat target (0 if not engaged)
                var combatSecs = _combatEngageTime == DateTime.MinValue ? 0.0
                    : (DateTime.Now - _combatEngageTime).TotalSeconds;

                // Sanitise decision for CSV (strip commas)
                var decision = _decision.Replace(',', ';');

                _movementLog.WriteLine(
                    $"{timeMs},{_state.WavesCompleted + 1},{(_state.IsWaveActive ? 1 : 0)}," +
                    $"{_phase},{decision}," +
                    $"{pos.X:F1},{pos.Y:F1},{(moved ? 1 : 0)}," +
                    $"{ctx.Combat.NearbyMonsterCount},{ctx.Combat.CachedMonsterCount}," +
                    $"{(ctx.Navigation.IsNavigating ? 1 : 0)}," +
                    $"{navDest.X:F1},{navDest.Y:F1}," +
                    $"{(ctx.Combat.WantsToMove ? 1 : 0)},{(_wasSearching ? 1 : 0)}," +
                    $"{noMoveSecs:F1},{combatSecs:F1}," +
                    $"{(ctx.Loot.HasLootNearby ? 1 : 0)},{ctx.Loot.LootableCount},{_state.DeathCount}");

                // Flush every 10 writes so data isn't lost if the game crashes
                if (timeMs % 5000 < MovementLogIntervalMs)
                    _movementLog.Flush();
            }
            catch { }
        }

        /// <summary>
        /// Find and navigate to monsters when none are in chase range.
        /// </summary>
        private void TickExploreForMonsters(BotContext ctx)
        {
            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;

            PruneBlacklist();

            // If already navigating to our own position, abort — this is the zero-distance nav lock
            if (ctx.Navigation.IsNavigating)
            {
                var navPath = ctx.Navigation.CurrentNavPath;
                if (navPath.Count > 0)
                {
                    var navDest = navPath[navPath.Count - 1].Position;
                    if (Vector2.Distance(playerPos, navDest) < 5f)
                        ctx.Navigation.Stop(gc);
                }
            }

            // Tier 1: Known monsters — navigate toward nearest non-blacklisted
            if (ctx.Combat.CachedMonsterCount > 0)
            {
                var nearestPos = FindNearestNonBlacklisted(gc, playerPos, ctx.Combat.BlacklistedEnemies);
                if (nearestPos.HasValue)
                {
                    _wasSearching = true;
                    var monsterDist = Vector2.Distance(playerPos, nearestPos.Value);
                    if (monsterDist > 20f)
                    {
                        if (ctx.Navigation.IsNavigating)
                            ctx.Navigation.UpdateDestination(gc, nearestPos.Value, driftThreshold: 30f);
                        else
                            ctx.Navigation.NavigateTo(gc, nearestPos.Value);
                    }
                    StatusText = $"Wave {_state.WavesCompleted + 1}/{SimulacrumState.MaxWavesInEncounter} — chasing nearest monster (dist: {monsterDist:F0}, {ctx.Combat.CachedMonsterCount} alive, {_blacklistedMonsters.Count} blacklisted)";
                    return;
                }
            }

            // Tier 2: No cached monsters — explore
            if (ctx.Navigation.IsNavigating)
            {
                StatusText = $"Wave {_state.WavesCompleted + 1}/{SimulacrumState.MaxWavesInEncounter} — searching for monsters";
                return;
            }

            if (ctx.Exploration.IsInitialized)
            {
                var target = ctx.Exploration.GetNextExplorationTarget(playerPos);
                if (target.HasValue)
                {
                    ctx.Navigation.NavigateTo(gc, target.Value);
                    StatusText = $"Wave {_state.WavesCompleted + 1}/{SimulacrumState.MaxWavesInEncounter} — exploring for monsters";
                    return;
                }
            }

            // Tier 3: Exploration exhausted — patrol around monolith
            if (_state.MonolithPosition.HasValue)
            {
                var distToMonolith = Vector2.Distance(playerPos, _state.MonolithPosition.Value);
                if (distToMonolith > 80f)
                {
                    ctx.Navigation.NavigateTo(gc, _state.MonolithPosition.Value);
                    StatusText = $"Wave {_state.WavesCompleted + 1}/{SimulacrumState.MaxWavesInEncounter} — returning to monolith (dist: {distToMonolith:F0})";
                    return;
                }

                if (!ctx.Navigation.IsNavigating)
                {
                    var angle = (float)(DateTime.Now.Ticks % 62830) / 10000f;
                    var radius = 40f + 25f * MathF.Sin(angle * 0.3f);
                    var orbitTarget = _state.MonolithPosition.Value + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                    if (Vector2.Distance(playerPos, orbitTarget) > 15f)
                        ctx.Navigation.NavigateTo(gc, orbitTarget);
                }
                StatusText = $"Wave {_state.WavesCompleted + 1}/{SimulacrumState.MaxWavesInEncounter} — sweeping for monsters";
                return;
            }

            StatusText = $"Wave {_state.WavesCompleted + 1}/{SimulacrumState.MaxWavesInEncounter} — searching (no exploration targets)";
        }

        // ═══════════════════════════════════════════════════
        // Monster blacklist helpers
        // ═══════════════════════════════════════════════════

        private void BlacklistNearbyMonsters(GameController gc, Vector2 playerGrid, float radius)
        {
            var now = DateTime.Now;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster || !entity.IsHostile || !entity.IsAlive)
                    continue;
                if (Vector2.Distance(entity.GridPosNum, playerGrid) <= radius)
                    _blacklistedMonsters[entity.Id] = now;
            }
        }

        private Vector2? FindNearestNonBlacklisted(GameController gc, Vector2 playerGrid, HashSet<string> enemyBlacklist)
        {
            float nearestDist = float.MaxValue;
            Vector2? nearestPos = null;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.Monster || !entity.IsHostile || !entity.IsAlive || !entity.IsTargetable)
                    continue;
                if (_blacklistedMonsters.ContainsKey(entity.Id))
                    continue;
                if (enemyBlacklist.Count > 0 && !string.IsNullOrEmpty(entity.RenderName) &&
                    enemyBlacklist.Contains(entity.RenderName)) continue;

                var dist = Vector2.Distance(entity.GridPosNum, playerGrid);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestPos = entity.GridPosNum;
                }
            }

            return nearestPos;
        }

        private void PruneBlacklist()
        {
            if (_blacklistedMonsters.Count == 0) return;
            var now = DateTime.Now;
            var expired = new List<long>();
            foreach (var kvp in _blacklistedMonsters)
            {
                if ((now - kvp.Value).TotalSeconds > MonsterBlacklistSeconds)
                    expired.Add(kvp.Key);
            }
            foreach (var id in expired)
                _blacklistedMonsters.Remove(id);
        }

        private void IdleNearMonolith(BotContext ctx)
        {
            if (!_state.MonolithPosition.HasValue) return;
            var gc = ctx.Game;
            var dist = Vector2.Distance(gc.Player.GridPosNum, _state.MonolithPosition.Value);

            if (dist > 30f && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, _state.MonolithPosition.Value);
            else if (dist <= 20f && ctx.Navigation.IsNavigating)
                ctx.Navigation.Stop(gc);
        }

        private void TickStartWave(BotContext ctx)
        {
            if (!_state.MonolithPosition.HasValue)
            {
                StatusText = "Can't start wave — monolith not found";
                return;
            }

            var gc = ctx.Game;
            var playerPos = gc.Player.GridPosNum;
            var monolithPos = _state.MonolithPosition.Value;
            var dist = Vector2.Distance(playerPos, monolithPos);

            if (dist > ctx.Interaction.InteractRadius)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, monolithPos);
                StatusText = $"Navigating to monolith to start wave {_state.WavesCompleted + 1} (dist: {dist:F0})";
                return;
            }

            ctx.Navigation.Stop(gc);

            if (_waveStartFirstTryTime == DateTime.MinValue)
                _waveStartFirstTryTime = DateTime.Now;

            if (!ModeHelpers.CanAct(_waveStartLastClickTime, WaveStartClickCooldownMs)) return;

            // Resolve monolith entity
            Entity? monolith = null;
            if (_state.MonolithId.HasValue)
            {
                monolith = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Id == _state.MonolithId.Value);
            }
            if (monolith == null)
            {
                monolith = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Metadata?.Contains("Objects/Afflictionator") == true);
            }

            if (monolith == null)
            {
                StatusText = "Monolith entity not found for clicking";
                return;
            }

            var elapsed = (DateTime.Now - _waveStartFirstTryTime).TotalSeconds;

            // Try 1: Click entity label if visible
            if (TryClickEntityLabel(gc, monolith))
            {
                _waveStartLastClickTime = DateTime.Now;
                _waveSpawnWaitUntil = DateTime.Now.AddSeconds(RandRange(3.5f, 5.5f));
                StatusText = $"Clicking monolith label to start wave {_state.WavesCompleted + 1} ({elapsed:F1}s elapsed)";
                return;
            }

            // Try 2: Click entity directly
            if (BotInput.ClickEntity(gc, monolith))
            {
                _waveStartLastClickTime = DateTime.Now;
                _waveSpawnWaitUntil = DateTime.Now.AddSeconds(RandRange(3.5f, 5.5f));
                StatusText = $"Clicking monolith to start wave {_state.WavesCompleted + 1} ({elapsed:F1}s elapsed)";
            }
            else
            {
                StatusText = $"Monolith off screen or gate blocked — waiting ({elapsed:F1}s elapsed)";
            }
        }

        private bool TryClickEntityLabel(GameController gc, Entity monolith)
        {
            try
            {
                var labels = gc.IngameState.IngameUi.ItemsOnGroundLabelsVisible;
                if (labels == null) return false;

                foreach (var label in labels)
                {
                    if (label.ItemOnGround?.Id != monolith.Id) continue;
                    if (label.Label == null || !label.Label.IsVisible) continue;

                    if (BotInput.ClickLabel(gc, label.Label.GetClientRect()))
                    {
                        _lastActionTime = DateTime.Now;
                        return true;
                    }
                    return false;
                }
            }
            catch { }
            return false;
        }

        // =================================================================
        // Between-wave stash
        // =================================================================

        private void TickBetweenWaveStash(BotContext ctx, InteractionResult interactionResult)
        {
            // If wave started while stashing, cancel and return to wave cycle
            if (_state.IsWaveActive)
            {
                if (ctx.Stash.IsBusy)
                    ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
                _isStashing = false;
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                StatusText = "Wave started — cancelling stash";
                return;
            }

            // Timeout — if stash hasn't completed in 30s, bail and continue the run.
            // _stashBailed blocks re-entry until the next wave completes (inventory may change).
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                if (ctx.Stash.IsBusy)
                    ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
                _isStashing = false;
                _stashBailed = true;
                _phase = SimPhase.WaveCycle;
                _phaseStartTime = DateTime.Now;
                int remaining = 0;
                var bailSlots = StashSystem.GetInventorySlotItems(ctx.Game);
                if (bailSlots != null)
                    foreach (var it in bailSlots)
                        if (KeepSimulacrumsFilter(it)) remaining++;
                WriteEvent("StashBailed", $"Wave {_state.WavesCompleted + 1}", $"remaining={remaining}");
                StatusText = "Stash timeout — skipping until next wave";
                return;
            }

            var gc = ctx.Game;

            // Step 1: Navigate to cached stash position
            if (_state.StashPosition.HasValue)
            {
                var playerPos = gc.Player.GridPosNum;
                var dist = Vector2.Distance(
                    new Vector2(playerPos.X, playerPos.Y),
                    _state.StashPosition.Value);

                if (dist > ctx.Interaction.InteractRadius)
                {
                    if (ctx.Stash.IsBusy)
                        ctx.Stash.Cancel(gc, ctx.Navigation);
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, _state.StashPosition.Value);
                    StatusText = $"Navigating to stash (dist: {dist:F0})";
                    return;
                }
            }

            // Step 2: Start StashSystem if not running
            if (!ctx.Stash.IsBusy)
            {
                ctx.Navigation.Stop(gc);
                var dumpTab = ctx.Settings.Stash.DumpTabName.Value;
                ctx.Stash.Start(
                    storeTabName: string.IsNullOrWhiteSpace(dumpTab) ? null : dumpTab,
                    itemFilter:   KeepSimulacrumsFilter);
            }

            // Step 3: Tick StashSystem
            var result = ctx.Stash.Tick(gc, ctx.Navigation);

            switch (result)
            {
                case StashResult.Succeeded:
                    _phase = SimPhase.WaveCycle;
                    _phaseStartTime = DateTime.Now;
                    StatusText = $"Stashed {ctx.Stash.ItemsStored} items — resuming wave cycle";
                    break;
                case StashResult.Failed:
                    StatusText = $"Stash failed ({ctx.Stash.Status}) — retrying";
                    break;
                default:
                    StatusText = $"Between-wave stash: {ctx.Stash.Status}";
                    break;
            }
        }

        // =================================================================
        // Loot sweep — after all waves complete, pick up remaining items then exit
        // =================================================================

        private DateTime _lastEmptyScanAt = DateTime.MinValue;
        private bool _sweepNearMonolith;
        private const float EmptyGraceSeconds = 5f;
        private const float LootSweepTimeoutSeconds = 60f;
        private const float SweepMonolithProximity = 25f;

        private void TickLootSweep(BotContext ctx, InteractionResult interactionResult)
        {
            _lootTracker.HandleResult(interactionResult, ctx);

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > LootSweepTimeoutSeconds)
            {
                EnterExitMapPhase(ctx);
                StatusText = $"Loot sweep timeout — exiting ({_lootTracker.PickupCount} items)";
                return;
            }

            if (ctx.Interaction.IsBusy) return;

            var gc = ctx.Game;

            // Step 1: Navigate to monolith before scanning
            if (!_sweepNearMonolith && _state.MonolithPosition.HasValue)
            {
                var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var distToMonolith = Vector2.Distance(playerPos, _state.MonolithPosition.Value);

                if (distToMonolith > SweepMonolithProximity)
                {
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, _state.MonolithPosition.Value);
                    StatusText = $"Sweep: returning to monolith for drops (dist: {distToMonolith:F0})";
                    _lastEmptyScanAt = DateTime.MinValue;
                    return;
                }

                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);
                _sweepNearMonolith = true;
                _lastEmptyScanAt = DateTime.MinValue;
            }

            // Step 2: Stash items if above threshold
            if (_state.StashPosition.HasValue)
            {
                int stashableCount = 0;
                var slots = StashSystem.GetInventorySlotItems(gc);
                if (slots != null)
                    foreach (var it in slots)
                        if (KeepSimulacrumsFilter(it)) stashableCount++;
                if (stashableCount >= ctx.Settings.Run.StashItemThreshold.Value)
                {
                    var playerPos = gc.Player.GridPosNum;
                    var dist = Vector2.Distance(
                        new Vector2(playerPos.X, playerPos.Y),
                        _state.StashPosition.Value);
                    if (dist > ctx.Interaction.InteractRadius)
                    {
                        if (!ctx.Navigation.IsNavigating)
                            ctx.Navigation.NavigateTo(gc, _state.StashPosition.Value);
                        StatusText = $"Navigating to stash before exit (dist: {dist:F0})";
                        return;
                    }

                    if (!ctx.Stash.IsBusy)
                    {
                        ctx.Navigation.Stop(gc);
                        var dumpTab = ctx.Settings.Stash.DumpTabName.Value;
                        ctx.Stash.Start(
                            storeTabName: string.IsNullOrWhiteSpace(dumpTab) ? null : dumpTab,
                            itemFilter:   KeepSimulacrumsFilter);
                    }
                }
                if (ctx.Stash.IsBusy)
                {
                    var stashResult = ctx.Stash.Tick(gc, ctx.Navigation);
                    if (stashResult == StashResult.Succeeded || stashResult == StashResult.Failed)
                        _sweepNearMonolith = false;
                    else
                    {
                        StatusText = $"Stashing before exit: {ctx.Stash.Status}";
                        return;
                    }
                }
            }

            // Step 3: Scan and pick up loot
            ctx.Loot.Scan(gc);
            var best = ctx.Loot.GetBestCandidate();
            if (best != null)
            {
                _lastEmptyScanAt = DateTime.MinValue;
                var withinRadius = best.Distance <= ctx.Interaction.InteractRadius;
                ctx.Interaction.PickupGroundItem(best.Entity, ctx.Navigation,
                    requireProximity: !withinRadius);
                _lootTracker.SetPending(best.Entity.Id, best.ItemName, best.ChaosValue);
                WriteLootEvent("Pickup", best.ItemName, best.ChaosValue);
                StatusText = $"Sweep: picking up {best.ItemName} ({_lootTracker.PickupCount} picked)";
                return;
            }

            // Step 4: Grace period — wait near monolith for items to finish dropping
            if (_lastEmptyScanAt == DateTime.MinValue)
                _lastEmptyScanAt = DateTime.Now;

            if ((DateTime.Now - _lastEmptyScanAt).TotalSeconds >= EmptyGraceSeconds)
            {
                EnterExitMapPhase(ctx);
                StatusText = $"Sweep complete — exiting ({_lootTracker.PickupCount} items)";
                return;
            }

            StatusText = $"Sweep: waiting for drops near monolith... ({_lootTracker.PickupCount} picked)";
        }

        // =================================================================
        // Exit map
        // =================================================================

        private void EnterExitMapPhase(BotContext ctx)
        {
            _phase = SimPhase.ExitMap;
            _phaseStartTime = DateTime.Now;
            _mapCompleted = true;
            _consecutiveFailedRuns = 0;
            ctx.LootTracker.RecordMapComplete();

            if (ctx.Stash.IsBusy)
                ctx.Stash.Cancel(ctx.Game, ctx.Navigation);
            ctx.Navigation.Stop(ctx.Game);

            StatusText = "Exiting map via portal";
        }

        private void TickExitMap(BotContext ctx)
        {
            var gc = ctx.Game;

            if (gc.Area.CurrentArea.IsHideout)
                return;

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                _phase = SimPhase.Done;
                StatusText = "Exit timeout — giving up";
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true ||
                gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true)
            {
                BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                _lastActionTime = DateTime.Now;
                StatusText = "Closing panels before exit";
                return;
            }

            var portal = ModeHelpers.FindNearestPortal(gc);
            if (portal == null)
            {
                if (_state.PortalPosition.HasValue)
                {
                    var playerPos = gc.Player.GridPosNum;
                    var dist = Vector2.Distance(playerPos, _state.PortalPosition.Value);
                    if (dist > ctx.Interaction.InteractRadius)
                    {
                        if (!ctx.Navigation.IsNavigating)
                            ctx.Navigation.NavigateTo(gc,
                                _state.PortalPosition.Value);
                        StatusText = $"Walking to cached portal (dist: {dist:F0})";
                    }
                    else
                    {
                        StatusText = "Near cached portal — waiting for entity";
                    }
                }
                else
                {
                    StatusText = "No portal found — waiting";
                }
                return;
            }

            var playerGridPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var portalGridPos = new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y);
            var portalDist = Vector2.Distance(playerGridPos, portalGridPos);

            if (portalDist > ctx.Interaction.InteractRadius)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, portalGridPos);
                StatusText = $"Walking to portal (dist: {portalDist:F0})";
                return;
            }

            ctx.Navigation.Stop(gc);
            ModeHelpers.ClickEntity(gc, portal, ref _lastActionTime);
            StatusText = "Clicking portal to exit";
        }

        // =================================================================
        // Render
        // =================================================================

        public void Render(BotContext ctx)
        {
            if (!ShowOverlay) return;
            if (ctx.Graphics == null) return;
            var gc = ctx.Game;
            var cam = gc.IngameState.Camera;
            var g = ctx.Graphics;

            var hudY = OverlayY;
            var hudX = OverlayX;
            var lineH = 16f;

            g.DrawText($"Phase: {_phase}", new Vector2(hudX, hudY), SharpDX.Color.White);
            hudY += lineH;
            g.DrawText(StatusText, new Vector2(hudX, hudY), SharpDX.Color.LightGreen);
            hudY += lineH;

            g.DrawText($"Wave: {_state.WavesCompleted}/{SimulacrumState.MaxWavesInEncounter} {(_state.IsWaveActive ? "ACTIVE" : "idle")}",
                new Vector2(hudX, hudY),
                _state.IsWaveActive ? SharpDX.Color.Red : SharpDX.Color.Cyan);
            hudY += lineH;

            if (_state.DeathCount > 0)
            {
                g.DrawText($"Deaths: {_state.DeathCount}/{ctx.Settings.Run.MaxDeaths.Value}",
                    new Vector2(hudX, hudY), SharpDX.Color.Red);
                hudY += lineH;
            }

            var runElapsed = DateTime.Now - _state.RunStartedAt;
            g.DrawText($"Runs: {_state.RunsCompleted} | This run: {runElapsed.Minutes}m{runElapsed.Seconds:D2}s",
                new Vector2(hudX, hudY), SharpDX.Color.Gold);
            hudY += lineH;

            if (_state.RunsCompleted > 0)
            {
                var avgDur = _state.AverageRunDuration;
                g.DrawText($"Avg: {avgDur.Minutes}m{avgDur.Seconds:D2}s | {_state.AverageWavesPerRun:F1} waves/run",
                    new Vector2(hudX, hudY), SharpDX.Color.Gold);
                hudY += lineH;
            }

            g.DrawText($"Loot: {_lootTracker.PickupCount} items",
                new Vector2(hudX, hudY), SharpDX.Color.Gold);
            hudY += lineH;

            if (!string.IsNullOrEmpty(Decision))
            {
                g.DrawText($"Decision: {Decision}", new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                hudY += lineH;
            }

            if (ctx.Interaction.IsBusy)
            {
                g.DrawText($"Interact: {ctx.Interaction.Status}",
                    new Vector2(hudX, hudY), SharpDX.Color.Yellow);
                hudY += lineH;
            }

            // --- World drawing (only in map) ---
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
                return;

            if (_state.MonolithPosition.HasValue)
            {
                var monolithWorld = Systems.Pathfinding.GridToWorld3D(gc, _state.MonolithPosition.Value);
                g.DrawText("MONOLITH", cam.WorldToScreen(monolithWorld), SharpDX.Color.Purple);
                g.DrawCircleInWorld(monolithWorld, 30f, SharpDX.Color.Purple, 2f);
            }

            if (_state.PortalPosition.HasValue)
            {
                var portalWorld = Systems.Pathfinding.GridToWorld3D(gc, _state.PortalPosition.Value);
                g.DrawText("PORTAL", cam.WorldToScreen(portalWorld) + new Vector2(-20, -15),
                    SharpDX.Color.Aqua);
                g.DrawCircleInWorld(portalWorld, 20f, SharpDX.Color.Aqua, 1.5f);
            }

            if (_state.StashPosition.HasValue)
            {
                g.DrawText("STASH", Systems.Pathfinding.GridToScreen(gc, _state.StashPosition.Value) + new Vector2(-15, -15),
                    SharpDX.Color.Gold);
            }

            if (ctx.Navigation.IsNavigating)
            {
                var path = ctx.Navigation.CurrentNavPath;
                for (int i = ctx.Navigation.CurrentWaypointIndex; i < path.Count - 1; i++)
                {
                    var from = Systems.Pathfinding.GridToScreen(gc, path[i].Position);
                    var to = Systems.Pathfinding.GridToScreen(gc, path[i + 1].Position);
                    g.DrawLine(from, to, 1.5f, SharpDX.Color.CornflowerBlue);
                }
            }

            g.DrawText($"Monsters: {ctx.Combat.NearbyMonsterCount}",
                new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            if (ctx.Loot.FailedCount > 0)
            {
                g.DrawText($"Ignored items: {ctx.Loot.FailedCount}",
                    new Vector2(hudX, hudY), SharpDX.Color.OrangeRed);
                hudY += lineH;
            }

            foreach (var entry in ctx.Loot.FailedEntries.Values)
            {
                Entity? failedEntity = null;
                foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (e.Id == entry.EntityId)
                    {
                        failedEntity = e;
                        break;
                    }
                }
                if (failedEntity == null) continue;

                var worldPos = failedEntity.BoundsCenterPosNum;
                var screenPos = cam.WorldToScreen(worldPos);
                if (screenPos.X < 0 || screenPos.X > gc.Window.GetWindowRectangle().Width ||
                    screenPos.Y < 0 || screenPos.Y > gc.Window.GetWindowRectangle().Height)
                    continue;

                var age = (DateTime.Now - entry.FailedAt).TotalSeconds;
                g.DrawText($"X {entry.Reason} ({age:F0}s ago)",
                    screenPos + new Vector2(5, -10), SharpDX.Color.OrangeRed);
            }
        }

    }

    public enum SimPhase
    {
        Idle,

        // Hideout phases
        InHideout,
        StashItems,
        OpenMap,
        EnterPortal,

        // Map phases
        FindMonolith,
        NavigateToMonolith,
        WaveCycle,
        BetweenWaveStash,
        LootSweep,
        ExitMap,
        Done,
    }
}
