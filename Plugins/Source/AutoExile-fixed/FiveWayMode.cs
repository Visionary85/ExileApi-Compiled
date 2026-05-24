using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Modes.Shared;
using AutoExile.Systems;
using System.IO;
using System.Numerics;
using System.Windows.Forms;

namespace AutoExile.Modes
{
    /// <summary>
    /// 5-Way Domain of Timeless Conflict resetter.
    ///
    /// Mechanic: monsters spawn when a player leaves the ring, re-enters it,
    /// then stays inside for 2 seconds. Dash (W) exits the ring; Shield Charge (Q)
    /// re-enters it instantly. Dash cooldown ~2.21s is the ideal wait time —
    /// perfectly matching the 2s spawn timer with room to spare.
    ///
    /// Cycle: Dash OUT → (150ms) → Shield Charge IN → (wait Dash cooldown) → repeat.
    ///
    /// Both skills must have "Always Attack Without Moving" ticked in-game.
    /// Shield Charge has no cooldown so it fires immediately every cycle.
    ///
    /// Configuration: edit the constants below to match your key bindings.
    /// </summary>
    public class FiveWayMode : IBotMode
    {
        public string Name => "5-Way Legion";

        // ──────────────────────────────────────────────────────────────────────
        // CONFIGURATION — edit these to match your key bindings
        // ──────────────────────────────────────────────────────────────────────
        // Q = Shield Charge (no cooldown — fires instantly after each Dash)
        private const Keys ShieldChargeKey = Keys.Q;
        // W = Dash (cooldown skill — drives the reset timing)
        private const Keys DashKey = Keys.W;

        // Dash cooldown in ms — set by gem level/quality. 2.21s = 20% quality target.
        // Shield Charge has no cooldown so only DashCooldownMs matters for timing.
        private const float DashCooldownMs = 2210f;

        // Delay between pressing Dash and pressing Shield Charge.
        // Gives Dash time to begin its animation so the exit micro-movement registers
        // before Shield Charge's enter micro-movement fires.
        private const float ShieldChargeDelayAfterDashMs = 200f;

        // How long to hold inside the ring after crystal explosion is detected before
        // firing the first Dash. Crystal entity goes dead slightly before the visual
        // explosion completes — this gap lets the carry fully position before oscillation begins.
        private const float CrystalExplodedHoldMs = 1500f;

        // Extra hold inside the ring after each Shield Charge before the next Dash.
        // DashCooldownMs (2210ms) alone gives only ~2010ms actual in-ring time after SC
        // animation completes, barely meeting the 2s requirement. This adds safety margin
        // so the crystal reliably resets each cycle.
        private const float InRingExtraHoldMs = 1790f;

        // Brief settle after navigation stops before the first Dash fires.
        // Gives the camera time to stabilise so the monolith screen position is accurate.
        private const float WarmupMinSeconds = 1f;
        private const float WarmupMaxSeconds = 2f;

        // Total encounter duration in seconds.
        // Base 15s + 1 min per emblem. With 5 emblems = ~315s. Stop a few seconds early
        // so the last reset completes cleanly before the encounter ends.
        private const float EncounterDurationSeconds = 305f;

        // Grid distance at which the bot stops and considers itself positioned at the ring edge.
        // The LegionEndlessInitiator entity origin is at the centre of the stone structure.
        // The stone itself is ~40 grid units in radius — the pathfinder stops at the outer
        // surface and cannot walk inside. That surface IS the ring boundary, so 50 gives
        // comfortable arrival headroom regardless of which side the bot approaches from.
        private const float MonolithArrivalDist = 50f;

        // Grid distance at which we stop following the leader while in the hideout.
        private const float FollowStopDist = 12f;

        // Grid radius around leader's last known position to search for the entry portal.
        private const float PortalSearchRadius = 70f;

        // How long to wait for the leader / portal to appear before resetting tracking.
        private const float PortalWaitTimeout = 45f;

        // Seconds to let entity list settle after zone load before acting.
        private const float AreaSettleSeconds = 2.5f;

        // Seconds before giving up on monolith search and resetting from current position.
        private const float FindMonolithTimeout = 25f;

        // Overlay position on screen.
        private const float OverlayX = 20f;
        private const float OverlayY = 100f;

        // ──────────────────────────────────────────────────────────────────────
        // State
        // ──────────────────────────────────────────────────────────────────────
        private FiveWayPhase _phase = FiveWayPhase.Idle;
        private DateTime _phaseStartTime = DateTime.MinValue;
        public string StatusText { get; private set; } = "";

        // Leader tracking — hideout following and in-domain monolith navigation
        private Vector2 _lastLeaderPos;
        private bool _hasLastLeaderPos;
        private DateTime _leaderLastSeenAt = DateTime.MinValue;

        // Monolith ring centre position (grid coords)
        private Vector2? _monolithPos;
        private uint? _monolithId;

        // Reset loop timing
        // Pattern: Dash (exit ring) → short gap → Shield Charge (re-enter ring) → wait dash CD → repeat
        private DateTime _nextDashAt = DateTime.MinValue;           // when to fire Dash next
        private DateTime _shieldChargeAt = DateTime.MinValue;       // when to fire Shield Charge after Dash
        private bool _pendingShieldCharge;                          // true = Dash fired, waiting to Shield Charge
        private DateTime _encounterStartTime = DateTime.MinValue;
        private float _warmupDelay;
        private int _resetCount;                                    // number of complete Dash→SC cycles
        private DateTime _crystalExplodedAt = DateTime.MinValue;   // when crystal explosion was detected

        // Cached screen-space position of the monolith stone centre.
        // Cursor stays fixed here: Shield Charge moves TOWARD cursor (stone is solid, stops the
        // charge — character ends up AT the stone, inside the ring); Dash moves BACKWARD away
        // from cursor (exits the ring). This oscillation across the boundary triggers resets.
        // Calculated once when the reset loop starts and reused every tick.
        private Vector2 _monolithScreenPos;

        // Stats
        private int _runsCompleted;
        private DateTime _sessionStart = DateTime.MinValue;
        private bool _hasLoggedDomainEntities;

        // Kill tracking — same pattern as the KillCounter plugin:
        // scan dead hostile monsters each tick, use entity ID to avoid double-counting.
        private readonly HashSet<uint> _countedKills = new();
        private int _killCount;             // kills this encounter
        private int _sessionKillCount;      // kills this session (across all runs)

        // Portal click rate-limiting — BotInput.ClickEntity runs on the game thread at
        // tick rate (~60/s). Without a cooldown the bot spams 60+ clicks per second on the
        // portal which jams the game's click queue and makes entry unreliable.
        private DateTime _lastPortalClickAt = DateTime.MinValue;
        private const float PortalClickCooldownMs = 600f;

        // Logging — two files per session:
        //   fiveway_events_*.csv  : named events (phase transitions, skill fires, portal clicks, etc.)
        //   fiveway_state_*.csv   : 500ms position/state snapshots for timeline analysis
        private StreamWriter? _eventLog;
        private StreamWriter? _stateLog;
        private DateTime _lastStateSnap = DateTime.MinValue;
        private const float StateSnapIntervalMs = 500f;
        private Vector2 _lastLoggedPos;

        // Metadata substrings to identify the central monolith entity in the Domain.
        // Confirmed from live entity scan: the stone is EntityType.Terrain with path
        // "Metadata/Terrain/Leagues/Legion/Objects/LegionEndlessInitiator".
        private static readonly string[] MonolithMetaPaths =
        {
            "LegionEndlessInitiator",   // confirmed — Domain of Timeless Conflict central stone
        };

        private static readonly Random _rng = new();

        // ──────────────────────────────────────────────────────────────────────
        // IBotMode
        // ──────────────────────────────────────────────────────────────────────

        public void OnEnter(BotContext ctx)
        {
            _sessionStart = DateTime.Now;
            _phase = FiveWayPhase.Idle;
            _phaseStartTime = DateTime.Now;
            _hasLastLeaderPos = false;
            _monolithPos = null;
            _monolithId = null;
            _nextDashAt = DateTime.MinValue;
            _shieldChargeAt = DateTime.MinValue;
            _pendingShieldCharge = false;
            _encounterStartTime = DateTime.MinValue;
            _warmupDelay = 0;
            _resetCount = 0;
            _leaderLastSeenAt = DateTime.MinValue;
            _countedKills.Clear();
            _killCount = 0;
            _sessionKillCount = 0;

            _lastStateSnap = DateTime.MinValue;
            _lastLoggedPos = Vector2.Zero;
            _hasLoggedDomainEntities = false;
            _lastPortalClickAt = DateTime.MinValue;

            ModeHelpers.EnableDefaultCombat(ctx);
            OpenLogs();

            var gc = ctx.Game;
            if (IsInDomain(gc))
            {
                WriteEvent("OnEnter", "AlreadyInDomain", "");
                _phase = FiveWayPhase.FindMonolith;
                _phaseStartTime = DateTime.Now;
                StatusText = "Already in Domain — finding monolith";
            }
            else
            {
                WriteEvent("OnEnter", "WaitingInHideout", "");
                _phase = FiveWayPhase.WaitingInHideout;
                _phaseStartTime = DateTime.Now;
                StatusText = "Waiting in hideout for leader";
            }
        }

        public void OnExit()
        {
            CloseLogs();
            _phase = FiveWayPhase.Idle;
        }

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;

            // Return to hideout from any map phase → clean reset for next run
            if ((gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown) &&
                _phase != FiveWayPhase.WaitingInHideout &&
                _phase != FiveWayPhase.Idle)
            {
                ModeHelpers.CancelAllSystems(ctx);
                _monolithPos = null;
                _monolithId = null;

                if (_phase == FiveWayPhase.Resetting || _phase == FiveWayPhase.EncounterEnded)
                    _runsCompleted++;

                var encElapsed = _encounterStartTime == DateTime.MinValue
                    ? 0 : (DateTime.Now - _encounterStartTime).TotalSeconds;

                // Theoretical max resets = encounter duration / Dash cooldown
                var theoreticalMax = encElapsed > 0
                    ? (int)(encElapsed / (DashCooldownMs / 1000f)) : 0;
                var efficiency = theoreticalMax > 0
                    ? (float)_resetCount / theoreticalMax * 100f : 0f;

                _sessionKillCount += _killCount;
                WriteEvent("RunSummary", "BackInHideout",
                    $"kills={_killCount},resets={_resetCount},maxResets={theoreticalMax}," +
                    $"efficiency={efficiency:F1}%,encElapsed={encElapsed:F0}s," +
                    $"sessionKills={_sessionKillCount},runs={_runsCompleted}");

                _resetCount = 0;
                _killCount = 0;
                _countedKills.Clear();
                _encounterStartTime = DateTime.MinValue;
                // Reset leader tracking so we don't immediately re-enter via stale last position
                _hasLastLeaderPos = false;
                _leaderLastSeenAt = DateTime.MinValue;
                _hasLoggedDomainEntities = false;
                _lastPortalClickAt = DateTime.MinValue;
                _phase = FiveWayPhase.WaitingInHideout;
                _phaseStartTime = DateTime.Now;
                StatusText = "Returned to hideout — waiting for next run";
                return;
            }

            bool inDomain = IsInDomain(gc);

            // Detect domain entry — transition WaitingInHideout → FindMonolith.
            // There is no OnZoneChange callback so we poll each tick. Without this,
            // TickWaitingInHideout runs forever inside the domain and the bot just
            // follows the carry instead of doing resets.
            if (inDomain && _phase == FiveWayPhase.WaitingInHideout)
            {
                _monolithPos = null;
                _monolithId = null;
                _hasLoggedDomainEntities = false;
                _crystalExplodedAt = DateTime.MinValue;
                WriteEvent("EnteredDomain", "StartFindMonolith", "");
                _phase = FiveWayPhase.FindMonolith;
                _phaseStartTime = DateTime.Now;
                StatusText = "Entered Domain — finding monolith";
            }

            // Suppress combat AI targeting in the domain so it doesn't fight monsters.
            // Only suppress movement (positioning) during the planted phases — FindMonolith
            // and NavigatingToMonolith need free navigation to reach the ring edge.
            ctx.Combat.SuppressTargetedSkills = inDomain;
            ctx.Combat.SuppressPositioning = inDomain && (
                _phase == FiveWayPhase.WarmupDelay ||
                _phase == FiveWayPhase.Resetting);

            var phaseBefore = _phase;

            switch (_phase)
            {
                case FiveWayPhase.WaitingInHideout:    TickWaitingInHideout(ctx);    break;
                case FiveWayPhase.FindMonolith:        TickFindMonolith(ctx);        break;
                case FiveWayPhase.NavigatingToMonolith:TickNavigatingToMonolith(ctx);break;
                case FiveWayPhase.WarmupDelay:         TickWarmupDelay(ctx);         break;
                case FiveWayPhase.Resetting:           TickResetting(ctx);           break;
                case FiveWayPhase.EncounterEnded:      TickEncounterEnded(ctx);      break;
            }

            if (_phase != phaseBefore)
                WriteEvent("PhaseChange", _phase.ToString(), phaseBefore.ToString());

            WriteStateSnapshot(ctx);
        }

        public void Render(BotContext ctx)
        {
            if (ctx.Graphics == null) return;
            var g = ctx.Graphics;
            var y = OverlayY;
            const float lh = 16f;

            g.DrawText($"[5-Way] Phase: {_phase}", new Vector2(OverlayX, y), SharpDX.Color.White);
            y += lh;
            g.DrawText(StatusText, new Vector2(OverlayX, y), SharpDX.Color.LightGreen);
            y += lh;

            if (_phase == FiveWayPhase.Resetting || _phase == FiveWayPhase.EncounterEnded)
            {
                var elapsed = _encounterStartTime == DateTime.MinValue
                    ? 0 : (DateTime.Now - _encounterStartTime).TotalSeconds;
                var remaining = Math.Max(0, EncounterDurationSeconds - elapsed);
                var theoreticalMax = elapsed > 0 ? (int)(elapsed / (DashCooldownMs / 1000f)) : 0;
                var efficiency = theoreticalMax > 0
                    ? (float)_resetCount / theoreticalMax * 100f : 0f;

                g.DrawText(
                    $"Kills: {_killCount}  Resets: {_resetCount}/{theoreticalMax} ({efficiency:F0}%)  {remaining:F0}s left",
                    new Vector2(OverlayX, y), SharpDX.Color.Cyan);
                y += lh;

                if (_phase == FiveWayPhase.Resetting)
                {
                    var dashIn = _nextDashAt == DateTime.MinValue
                        ? 0 : Math.Max(0, (_nextDashAt - DateTime.Now).TotalMilliseconds);
                    g.DrawText(_pendingShieldCharge
                            ? $"Next: Shield Charge (in {Math.Max(0, (_shieldChargeAt - DateTime.Now).TotalMilliseconds):F0}ms)"
                            : $"Next: Dash (in {dashIn:F0}ms)",
                        new Vector2(OverlayX, y), SharpDX.Color.Yellow);
                    y += lh;
                }
            }

            if (_runsCompleted > 0)
            {
                g.DrawText($"Runs: {_runsCompleted}  Session kills: {_sessionKillCount}",
                    new Vector2(OverlayX, y), SharpDX.Color.Gold);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase: WaitingInHideout
        // Follow leader → enter portal when it appears near their last position.
        // ──────────────────────────────────────────────────────────────────────

        private void TickWaitingInHideout(BotContext ctx)
        {
            var gc = ctx.Game;
            var leaderName = ctx.Settings.Follower.LeaderName?.Value ?? "";
            var leader = FindLeader(gc, leaderName);

            if (leader != null)
            {
                var leaderGrid = new Vector2(leader.GridPosNum.X, leader.GridPosNum.Y);
                _lastLeaderPos = leaderGrid;
                _hasLastLeaderPos = true;
                _leaderLastSeenAt = DateTime.Now;

                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var dist = Vector2.Distance(playerGrid, leaderGrid);

                if (dist > FollowStopDist + 8f)
                {
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, leaderGrid);
                    StatusText = $"Following {leaderName} (dist: {dist:F0})";
                    return;
                }

                ctx.Navigation.Stop(gc);
                // Leader is still here — do NOT enter the portal. Always follow, never go first.
                // Entry is handled below once the leader vanishes (they went through first).
                StatusText = $"Near {leaderName} — waiting for them to enter portal first";
            }
            else if (_hasLastLeaderPos)
            {
                // Leader vanished — they entered the portal; find it and follow
                var timeSinceSeen = (DateTime.Now - _leaderLastSeenAt).TotalSeconds;

                if (timeSinceSeen > 1.5)
                {
                    var portal = FindNearestPortal(gc, _lastLeaderPos, PortalSearchRadius);
                    if (portal != null)
                    {
                        var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                        var portalGrid = new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y);
                        if (Vector2.Distance(playerGrid, portalGrid) > 10f)
                        {
                            if (!ctx.Navigation.IsNavigating)
                                ctx.Navigation.NavigateTo(gc, portalGrid);
                            StatusText = $"Moving to portal (dist: {Vector2.Distance(playerGrid, portalGrid):F0})";
                        }
                        else
                        {
                            TryEnterPortal(ctx, portal);
                        }
                        return;
                    }

                    StatusText = $"Leader gone — searching for portal ({timeSinceSeen:F0}s)";

                    if (timeSinceSeen > PortalWaitTimeout)
                    {
                        WriteEvent("PortalTimeout", "Reset", $"waited={timeSinceSeen:F0}s");
                        _hasLastLeaderPos = false;
                        _leaderLastSeenAt = DateTime.MinValue;
                        StatusText = "Portal timeout — resetting leader tracking";
                    }
                }
                else
                {
                    StatusText = "Leader just left — waiting before portal search";
                }
            }
            else
            {
                StatusText = string.IsNullOrEmpty(leaderName)
                    ? "Set LeaderName in Follower settings"
                    : $"Waiting for leader '{leaderName}'...";
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase: FindMonolith
        // Search for the central stone entity; fall back to following leader.
        // ──────────────────────────────────────────────────────────────────────

        private void TickFindMonolith(BotContext ctx)
        {
            var gc = ctx.Game;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            if (elapsed < AreaSettleSeconds)
            {
                StatusText = "Zone loading — settling...";
                return;
            }

            // Dump all entity metadata once per zone load so we can identify the monolith path
            if (!_hasLoggedDomainEntities)
            {
                _hasLoggedDomainEntities = true;
                foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
                    if (e.Metadata != null)
                        WriteEvent("DomainEntityScan", e.Type.ToString(), e.Metadata);
            }

            if (_monolithPos.HasValue)
            {
                TransitionToNav(ctx);
                return;
            }

            var monolith = FindMonolithEntity(gc);
            if (monolith != null)
            {
                _monolithId = monolith.Id;
                _monolithPos = new Vector2(monolith.GridPosNum.X, monolith.GridPosNum.Y);
                WriteEvent("MonolithFound", monolith.Metadata ?? "?", "entity");
                TransitionToNav(ctx);
                return;
            }

            // No entity found yet — follow leader (they head straight to the stone)
            var leaderName = ctx.Settings.Follower.LeaderName?.Value ?? "";
            var leader = FindLeader(gc, leaderName);
            if (leader != null)
            {
                var leaderGrid = new Vector2(leader.GridPosNum.X, leader.GridPosNum.Y);
                _lastLeaderPos = leaderGrid;
                _hasLastLeaderPos = true;
                _leaderLastSeenAt = DateTime.Now;

                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var dist = Vector2.Distance(playerGrid, leaderGrid);

                // Commit once close — leader heads straight to the stone so their position
                // at ~40 grid units from us is a reliable enough centre approximation.
                // NavigatingToMonolith will then stop 18 units from this point (ring edge).
                const float LeaderCommitDist = 40f;
                if (dist <= LeaderCommitDist)
                {
                    _monolithPos = leaderGrid;
                    WriteEvent("MonolithApprox", "LeaderPos", $"dist={dist:F0}");
                    TransitionToNav(ctx);
                    return;
                }

                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, leaderGrid);
                StatusText = $"Following leader to monolith (dist: {dist:F0})";
                return;
            }

            // No entity, no leader — explore toward centre
            if (ctx.Exploration.IsInitialized && !ctx.Navigation.IsNavigating)
            {
                var target = ctx.Exploration.GetNextExplorationTarget(gc.Player.GridPosNum);
                if (target.HasValue)
                    ctx.Navigation.NavigateTo(gc, target.Value);
            }

            StatusText = "Searching for monolith...";

            if (elapsed > FindMonolithTimeout)
            {
                var p = gc.Player.GridPosNum;
                _monolithPos = new Vector2(p.X, p.Y);
                WriteEvent("MonolithFallback", "Timeout", $"elapsed={elapsed:F0}s");
                TransitionToNav(ctx);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase: NavigatingToMonolith
        // Walk to the ring edge (MonolithArrivalDist from centre).
        // ──────────────────────────────────────────────────────────────────────

        private void TickNavigatingToMonolith(BotContext ctx)
        {
            if (!_monolithPos.HasValue)
            {
                _phase = FiveWayPhase.FindMonolith;
                _phaseStartTime = DateTime.Now;
                return;
            }

            var gc = ctx.Game;
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, _monolithPos.Value);

            if (dist <= MonolithArrivalDist)
            {
                ctx.Navigation.Stop(gc);
                _warmupDelay = RandRange(WarmupMinSeconds, WarmupMaxSeconds);
                WriteEvent("AtRingEdge", $"dist={dist:F0}", $"warmup={_warmupDelay:F1}s");
                _phase = FiveWayPhase.WarmupDelay;
                _phaseStartTime = DateTime.Now;
                StatusText = $"At ring edge — waiting {_warmupDelay:F0}s before resetting";
                return;
            }

            if (!ctx.Navigation.IsNavigating)
            {
                if (!ctx.Navigation.NavigateTo(gc, _monolithPos.Value))
                {
                    WriteEvent("NavFailed", "ToMonolith", $"dist={dist:F0}");
                    _warmupDelay = RandRange(WarmupMinSeconds, WarmupMaxSeconds);
                    _phase = FiveWayPhase.WarmupDelay;
                    _phaseStartTime = DateTime.Now;
                    StatusText = "No path to monolith — resetting from current position";
                }
            }

            StatusText = $"Navigating to ring edge (dist: {dist:F0})";
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase: WarmupDelay
        // Wait for the crystal to explode (LegionEndlessInitiator goes non-alive
        // or disappears) which signals the ring is active and resets can begin.
        // Falls back to a 1–2 s timer if the entity state never changes.
        // ──────────────────────────────────────────────────────────────────────

        private void TickWarmupDelay(BotContext ctx)
        {
            ctx.Navigation.Stop(ctx.Game);
            var gc = ctx.Game;
            var now = DateTime.Now;
            var elapsed = (now - _phaseStartTime).TotalSeconds;

            // Phase 1: watch for crystal explosion (entity goes non-alive or disappears).
            // Once detected, hold CrystalExplodedHoldMs before firing first Dash so the
            // carry has time to position and the visual explosion fully completes.
            if (_crystalExplodedAt == DateTime.MinValue)
            {
                bool exploded = false;
                string trigger = "";

                if (_monolithId.HasValue)
                {
                    var monEnt = gc.EntityListWrapper.OnlyValidEntities
                        .FirstOrDefault(e => e.Id == _monolithId.Value);
                    if (monEnt == null)
                    {
                        exploded = true;
                        trigger = "EntityGone";
                        WriteEvent("CrystalExploded", "EntityGone",
                            $"elapsed={elapsed:F2}s,id={_monolithId.Value}");
                    }
                    else if (!monEnt.IsAlive)
                    {
                        exploded = true;
                        trigger = "EntityDead";
                        WriteEvent("CrystalExploded", "EntityDead",
                            $"elapsed={elapsed:F2}s,id={_monolithId.Value},path={monEnt.Metadata}");
                    }
                }

                // Fallback: max-timer fires even without crystal detection
                bool timerExpired = elapsed >= _warmupDelay;
                if (timerExpired && !exploded)
                {
                    exploded = true;
                    trigger = "TimerExpired";
                    WriteEvent("CrystalExploded", "TimerExpired",
                        $"elapsed={elapsed:F2}s");
                }

                if (exploded)
                {
                    _crystalExplodedAt = now;
                    StatusText = $"Crystal exploded ({trigger}) — holding {CrystalExplodedHoldMs / 1000f:F1}s before loop";
                }
                else
                {
                    StatusText = $"Waiting for crystal explosion ({elapsed:F1}s / {_warmupDelay:F1}s fallback)";
                }
                return;
            }

            // Phase 2: hold inside the ring for CrystalExplodedHoldMs after explosion.
            var holdElapsed = (now - _crystalExplodedAt).TotalMilliseconds;
            if (holdElapsed < CrystalExplodedHoldMs)
            {
                var remaining = (CrystalExplodedHoldMs - holdElapsed) / 1000f;
                StatusText = $"Holding after explosion — loop starts in {remaining:F1}s";
                return;
            }

            // Hold complete — cache screen position and start oscillation loop.
            if (_monolithPos.HasValue)
            {
                var worldPos = AutoExile.Systems.Pathfinding.GridToWorld3D(gc, _monolithPos.Value);
                var screenPos2 = gc.IngameState.Camera.WorldToScreen(worldPos);
                _monolithScreenPos = new Vector2(screenPos2.X, screenPos2.Y);
            }
            else
            {
                var wr = gc.Window.GetWindowRectangle();
                _monolithScreenPos = new Vector2(wr.X + wr.Width / 2f, wr.Y + wr.Height / 2f);
            }

            _nextDashAt = now;
            _pendingShieldCharge = false;
            _resetCount = 0;
            _encounterStartTime = now;
            WriteEvent("ResetStart", "HoldComplete",
                $"totalDelay={elapsed:F1}s,holdMs={holdElapsed:F0}ms,stoneScreen=({_monolithScreenPos.X:F0};{_monolithScreenPos.Y:F0})");
            _phase = FiveWayPhase.Resetting;
            _phaseStartTime = now;
            StatusText = "Starting reset loop — Dash→ShieldCharge cycles";
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase: Resetting
        //
        // Core loop:
        //   1. Press Dash (W) — exits the ring via micro-movement
        //   2. After ShieldChargeDelayAfterDashMs, press Shield Charge (Q) — re-enters ring
        //   3. Wait DashCooldownMs + InRingExtraHoldMs before repeating
        //
        // In-ring hold = DashCooldownMs (2210ms) + InRingExtraHoldMs (500ms) = ~2710ms from SC press.
        // SC animation takes ~200ms so actual in-ring time ≈ 2510ms, comfortably above the 2s requirement.
        // Shield Charge has no cooldown so it fires as fast as BotInput allows.
        // ──────────────────────────────────────────────────────────────────────

        private void TickResetting(BotContext ctx)
        {
            var elapsed = (DateTime.Now - _encounterStartTime).TotalSeconds;

            if (elapsed >= EncounterDurationSeconds)
            {
                var theoreticalMax = (int)(elapsed / ((DashCooldownMs + InRingExtraHoldMs) / 1000f));
                var efficiency = theoreticalMax > 0
                    ? (float)_resetCount / theoreticalMax * 100f : 0f;
                WriteEvent("ResetEnd", "TimerExpired",
                    $"kills={_killCount},resets={_resetCount},maxResets={theoreticalMax}," +
                    $"efficiency={efficiency:F1}%,elapsed={elapsed:F0}s");
                _phase = FiveWayPhase.EncounterEnded;
                _phaseStartTime = DateTime.Now;
                StatusText = $"Encounter over — {_killCount} kills / {_resetCount} resets ({efficiency:F0}% efficiency)";
                return;
            }

            var remaining = EncounterDurationSeconds - elapsed;
            StatusText = $"Resetting: {_resetCount} resets — {remaining:F0}s left";

            // Keep the cached screen position current (camera slowly pans in some builds)
            if (_monolithPos.HasValue)
            {
                var gc2 = ctx.Game;
                var wp = AutoExile.Systems.Pathfinding.GridToWorld3D(gc2, _monolithPos.Value);
                var sp = gc2.IngameState.Camera.WorldToScreen(wp);
                _monolithScreenPos = new Vector2(sp.X, sp.Y);
            }

            if (!BotInput.CanAct) return;

            var now = DateTime.Now;

            if (_pendingShieldCharge)
            {
                // Fire Shield Charge aimed at stone centre — moves bot INTO ring.
                // _nextDashAt is set HERE (after SC) so the bot stays inside the ring
                // for the full DashCooldownMs before dashing out again.
                if (now >= _shieldChargeAt)
                {
                    if (BotInput.CursorPressKey(_monolithScreenPos, ShieldChargeKey))
                    {
                        _pendingShieldCharge = false;
                        _resetCount++;
                        var totalHold = DashCooldownMs + InRingExtraHoldMs;
                        _nextDashAt = now.AddMilliseconds(totalHold);
                        WriteEvent("ShieldCharge", $"reset={_resetCount}",
                            $"elapsed={elapsed:F1}s,nextDashIn={totalHold:F0}ms");
                    }
                }
            }
            else
            {
                // Fire Dash aimed at stone centre — moves bot OUT of ring (away from cursor).
                if (now >= _nextDashAt)
                {
                    if (BotInput.CursorPressKey(_monolithScreenPos, DashKey))
                    {
                        _shieldChargeAt = now.AddMilliseconds(ShieldChargeDelayAfterDashMs);
                        _pendingShieldCharge = true;
                        WriteEvent("Dash", $"scIn={ShieldChargeDelayAfterDashMs}ms",
                            $"elapsed={elapsed:F1}s");
                    }
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase: EncounterEnded
        // Find the exit portal and leave the domain immediately.
        // Bot waits in hideout until the carry is ready for the next run.
        // ──────────────────────────────────────────────────────────────────────

        private void TickEncounterEnded(BotContext ctx)
        {
            var gc = ctx.Game;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            // Brief settle so last reset animation finishes before moving
            if (elapsed < 2.0)
            {
                StatusText = $"Encounter over — leaving in {2.0 - elapsed:F1}s";
                return;
            }

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var portal = FindNearestPortal(gc, playerGrid, 200f);

            if (portal == null)
            {
                StatusText = "Encounter over — searching for exit portal";
                return;
            }

            var dist = Vector2.Distance(playerGrid,
                new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y));

            if (dist > 15f)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc,
                        new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y));
                StatusText = $"Leaving domain — moving to portal (dist: {dist:F0})";
                return;
            }

            TryEnterPortal(ctx, portal, exitLog: true);
            StatusText = "Exiting domain to hideout";
        }

        // ──────────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────────

        private void TransitionToNav(BotContext ctx)
        {
            ctx.Navigation.Stop(ctx.Game);
            _phase = FiveWayPhase.NavigatingToMonolith;
            _phaseStartTime = DateTime.Now;
            StatusText = "Monolith located — navigating to ring edge";
        }

        private static bool IsInDomain(GameController gc) =>
            gc.Area?.CurrentArea?.Name?.Contains("Timeless Conflict") == true;

        private static Entity? FindLeader(GameController gc, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (e.Type != EntityType.Player) continue;
                var playerComp = e.GetComponent<Player>();
                if (playerComp != null &&
                    string.Equals(playerComp.PlayerName, name, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        private static Entity? FindNearestPortal(GameController gc, Vector2 nearGrid, float maxDist)
        {
            Entity? best = null;
            float bestDist = float.MaxValue;
            foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
            {
                bool isPortal = e.Type == EntityType.TownPortal
                    || e.Type == EntityType.Portal
                    || e.Type == EntityType.AreaTransition;
                if (!isPortal) continue;

                var d = Vector2.Distance(new Vector2(e.GridPosNum.X, e.GridPosNum.Y), nearGrid);
                if (d < maxDist && d < bestDist) { best = e; bestDist = d; }
            }
            return best;
        }

        private static Entity? FindMonolithEntity(GameController gc)
        {
            foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (e.Metadata == null) continue;
                foreach (var frag in MonolithMetaPaths)
                    if (e.Metadata.Contains(frag, StringComparison.OrdinalIgnoreCase))
                        return e;
            }
            return null;
        }

        private void TryEnterPortal(BotContext ctx, Entity portal, bool exitLog = false)
        {
            if ((DateTime.Now - _lastPortalClickAt).TotalMilliseconds < PortalClickCooldownMs)
                return;
            _lastPortalClickAt = DateTime.Now;
            var gc = ctx.Game;
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var portalGrid = new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, portalGrid);
            if (exitLog)
                WriteEvent("ExitDomain", "EncounterEnded",
                    $"kills={_killCount},resets={_resetCount},portal={portal.Metadata ?? "?"}");
            else
                WriteEvent("EnterPortal", portal.Metadata ?? "portal",
                    $"dist={dist:F0},leader={(_hasLastLeaderPos ? $"({_lastLeaderPos.X:F0};{_lastLeaderPos.Y:F0})" : "unknown")}");
            BotInput.ClickEntity(gc, portal);
        }

        private static float RandRange(float min, float max) =>
            min + (float)(_rng.NextDouble() * (max - min));

        // ──────────────────────────────────────────────────────────────────────
        // Logging
        //
        // fiveway_events_*.csv  — named events written on each significant action
        //   Columns: TimeMs, Phase, Event, Detail1, Detail2
        //
        // fiveway_state_*.csv   — 500ms position/state snapshots
        //   Columns: TimeMs, Phase, Status, PlayerX, PlayerY, MovedSinceLastSnap,
        //            DistToMonolith, InRing, ResetCount, PendingShieldCharge,
        //            NextDashMs, EncElapsedSec, EncRemainingSec,
        //            Navigating, NavDestX, NavDestY, HasLeader, LeaderX, LeaderY
        // ──────────────────────────────────────────────────────────────────────

        private void OpenLogs()
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "FiveWay");
                Directory.CreateDirectory(dir);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                _eventLog = new StreamWriter(
                    Path.Combine(dir, $"fiveway_events_{ts}.csv"), append: false)
                    { AutoFlush = true };
                _eventLog.WriteLine("TimeMs,Phase,Event,Detail1,Detail2");

                _stateLog = new StreamWriter(
                    Path.Combine(dir, $"fiveway_state_{ts}.csv"), append: false)
                    { AutoFlush = false };  // flushed in batches; AutoFlush off for perf
                _stateLog.WriteLine(
                    "TimeMs,Phase,Status," +
                    "PlayerX,PlayerY,MovedSinceLastSnap," +
                    "DistToMonolith,InRing," +
                    "ResetCount,PendingShieldCharge,NextDashMs," +
                    "EncElapsedSec,EncRemainingSec," +
                    "KillCount,KillsPerReset," +
                    "Navigating,NavDestX,NavDestY," +
                    "HasLeader,LeaderX,LeaderY");
            }
            catch { _eventLog = null; _stateLog = null; }
        }

        private void CloseLogs()
        {
            try { _eventLog?.Flush(); _eventLog?.Close(); } catch { }
            try { _stateLog?.Flush();  _stateLog?.Close();  } catch { }
            _eventLog = null;
            _stateLog = null;
        }

        private void WriteEvent(string evt, string detail1, string detail2)
        {
            if (_sessionStart == DateTime.MinValue) return;
            var ms = (long)(DateTime.Now - _sessionStart).TotalMilliseconds;
            // Replace commas in user strings to keep CSV clean
            _eventLog?.WriteLine(
                $"{ms},{_phase},{evt}," +
                $"{detail1.Replace(',', ';')},{detail2.Replace(',', ';')}");
        }

        private void WriteStateSnapshot(BotContext ctx)
        {
            if (_stateLog == null || _sessionStart == DateTime.MinValue) return;
            if ((DateTime.Now - _lastStateSnap).TotalMilliseconds < StateSnapIntervalMs) return;
            _lastStateSnap = DateTime.Now;

            try
            {
                var gc = ctx.Game;
                var pos = gc.Player?.GridPosNum ?? System.Numerics.Vector2.Zero;
                var playerGrid = new Vector2(pos.X, pos.Y);

                var moved = Vector2.Distance(playerGrid, _lastLoggedPos) > 1.5f ? 1 : 0;
                _lastLoggedPos = playerGrid;

                var distToMono = _monolithPos.HasValue
                    ? Vector2.Distance(playerGrid, _monolithPos.Value) : -1f;
                var inRing = distToMono >= 0 && distToMono <= MonolithArrivalDist ? 1 : 0;

                var encElapsed = _encounterStartTime == DateTime.MinValue
                    ? 0.0 : (DateTime.Now - _encounterStartTime).TotalSeconds;
                var encRemaining = Math.Max(0, EncounterDurationSeconds - encElapsed);

                var nextDashMs = _nextDashAt == DateTime.MinValue
                    ? -1.0 : (_nextDashAt - DateTime.Now).TotalMilliseconds;

                var navPath = ctx.Navigation.CurrentNavPath;
                var navDest = navPath.Count > 0 ? navPath[navPath.Count - 1].Position : Vector2.Zero;
                var navigating = ctx.Navigation.IsNavigating ? 1 : 0;

                var leaderX = _hasLastLeaderPos ? _lastLeaderPos.X : -1f;
                var leaderY = _hasLastLeaderPos ? _lastLeaderPos.Y : -1f;

                // Count newly-dead hostile monsters since last snap (same logic as KillCounter plugin)
                foreach (var e in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
                {
                    if (e.IsAlive || !e.IsHostile) continue;
                    if (!e.HasComponent<ObjectMagicProperties>()) continue;
                    if (_countedKills.Add(e.Id))
                        _killCount++;
                }

                var killsPerReset = _resetCount > 0
                    ? (float)_killCount / _resetCount : 0f;

                var ms = (long)(DateTime.Now - _sessionStart).TotalMilliseconds;
                var safeStatus = StatusText.Replace(',', ';');

                _stateLog.WriteLine(
                    $"{ms},{_phase},{safeStatus}," +
                    $"{playerGrid.X:F1},{playerGrid.Y:F1},{moved}," +
                    $"{distToMono:F1},{inRing}," +
                    $"{_resetCount},{(_pendingShieldCharge ? 1 : 0)},{nextDashMs:F0}," +
                    $"{encElapsed:F1},{encRemaining:F1}," +
                    $"{_killCount},{killsPerReset:F1}," +
                    $"{navigating},{navDest.X:F1},{navDest.Y:F1}," +
                    $"{(_hasLastLeaderPos ? 1 : 0)},{leaderX:F1},{leaderY:F1}");

                // Flush every ~5 seconds to keep disk writes batched
                if (ms % 5000 < StateSnapIntervalMs)
                    _stateLog.Flush();
            }
            catch { /* never crash the bot tick over a log write */ }
        }
    }

    internal enum FiveWayPhase
    {
        Idle,
        WaitingInHideout,
        FindMonolith,
        NavigatingToMonolith,
        WarmupDelay,
        Resetting,
        EncounterEnded,
    }
}
