using ExileCore;
using ExileCore.PoEMemory;
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
    /// 5-Way Carry bot — the lead character that opens the domain and kills all waves.
    /// Hideout: stash loot → withdraw 5 Timeless Emblems → open Domain of Timeless Conflict → enter.
    /// In domain: navigate to ring edge → hold Q to attack → reposition if Headhunter displaces.
    /// After EncounterDurationSeconds: exit to hideout.
    /// </summary>
    public class FiveWayCarryMode : IBotMode
    {
        public string Name => "5-Way Carry";

        // ── Keys ──────────────────────────────────────────────────────
        private const Keys AttackKey = Keys.Q;

        // ── Timing ───────────────────────────────────────────────────
        private const float AttackIntervalMs        = 150f;
        private const float EncounterDurationSeconds = 312f;
        private const float AreaSettleSeconds        = 2.5f;
        private const float PortalClickCooldownMs    = 600f;

        // ── Positioning ──────────────────────────────────────────────
        // Stop this many grid units from ring center — parks just outside the ring
        private const float MonolithArrivalDist    = 55f;
        // If displaced more than this, pause attacking and navigate home
        private const float DisplacementThreshold  = 40f;

        // ── Emblem fragment ───────────────────────────────────────────
        // All five Timeless Emblems share this substring in their item paths.
        // Refine to "CurrencyTimeless" if "Timeless" catches unwanted items.
        private const string EmblemPathSubstring = "Timeless";
        private const int    EmblemsNeeded       = 5;

        // ── Monolith ─────────────────────────────────────────────────
        private const string MonolithMeta =
            "Metadata/Terrain/Leagues/Legion/Objects/LegionEndlessInitiator";

        // ── Overlay ───────────────────────────────────────────────────
        private const float OverlayX = 20f;
        private const float OverlayY = 230f;

        // ── Phase ─────────────────────────────────────────────────────
        private CarryPhase _phase        = CarryPhase.Idle;
        private DateTime   _phaseStartTime = DateTime.MinValue;

        // ── Hideout ───────────────────────────────────────────────────
        private readonly HideoutFlow _hideoutFlow           = new();
        private string   _lastAreaName                      = "";
        private bool     _mapCompleted;
        private int      _consecutiveFailedRuns;
        private const int MaxConsecutiveFailedRuns          = 3;

        // ── Arena ─────────────────────────────────────────────────────
        private uint?    _monolithId;
        private Vector2? _monolithPos;
        private Vector2? _homePos;               // where the carry parks in the arena
        private DateTime _nextAttackAt           = DateTime.MinValue;
        private DateTime _encounterStartTime     = DateTime.MinValue;
        private DateTime _lastPortalClickAt      = DateTime.MinValue;

        // ── Stats ─────────────────────────────────────────────────────
        private int _runsCompleted;

        // ── Logging ───────────────────────────────────────────────────
        private StreamWriter? _eventLog;
        private DateTime      _logStart = DateTime.MinValue;

        public string StatusText { get; private set; } = "";

        // =================================================================
        // IBotMode
        // =================================================================

        public void OnEnter(BotContext ctx)
        {
            OpenLog();
            Reset();

            var gc = ctx.Game;
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                _phase = CarryPhase.InHideout;
                _phaseStartTime = DateTime.Now;
                StartHideoutFlow(ctx);
                StatusText = "In hideout — preparing run";
            }
            else
            {
                _phase = CarryPhase.FindPosition;
                _phaseStartTime = DateTime.Now;
                StatusText = "In domain — finding position";
                InitExploration(ctx);
            }

            WriteEvent("ModeEnter", _phase.ToString(), "");
        }

        public void OnExit()
        {
            CloseLog();
            _phase = CarryPhase.Idle;
            _homePos = null;
            _monolithPos = null;
            _hideoutFlow.Cancel();
        }

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;

            // Area change detection
            var area = gc.Area?.CurrentArea?.Name ?? "";
            if (!string.IsNullOrEmpty(area) && area != _lastAreaName)
            {
                OnAreaChanged(ctx);
                _lastAreaName = area;
            }

            // Carry controls its own positioning in the domain
            bool inDomain = _phase != CarryPhase.Idle
                         && _phase != CarryPhase.InHideout
                         && _phase != CarryPhase.WaitingInHideout;
            ctx.Combat.SuppressPositioning     = inDomain;
            ctx.Combat.SuppressTargetedSkills  = inDomain;

            ctx.Interaction.Tick(gc);

            switch (_phase)
            {
                case CarryPhase.InHideout:
                case CarryPhase.WaitingInHideout:
                    TickHideout(ctx);
                    break;
                case CarryPhase.FindPosition:
                    TickFindPosition(ctx);
                    break;
                case CarryPhase.Attacking:
                    TickAttacking(ctx);
                    break;
                case CarryPhase.Repositioning:
                    TickRepositioning(ctx);
                    break;
                case CarryPhase.EncounterEnded:
                    TickEncounterEnded(ctx);
                    break;
            }
        }

        public void Render(BotContext ctx)
        {
            if (ctx.Graphics == null) return;
            var g = ctx.Graphics;
            var y = OverlayY;
            const float lh = 18f;

            g.DrawText($"[5-Way Carry] {_phase}", new Vector2(OverlayX, y), SharpDX.Color.White);
            y += lh;
            g.DrawText(StatusText, new Vector2(OverlayX, y), SharpDX.Color.LightGreen);
            y += lh;

            if (_encounterStartTime != DateTime.MinValue)
            {
                var elapsed   = (DateTime.Now - _encounterStartTime).TotalSeconds;
                var remaining = Math.Max(0, EncounterDurationSeconds - elapsed);
                g.DrawText($"Encounter: {remaining:F0}s left",
                    new Vector2(OverlayX, y), SharpDX.Color.Cyan);
                y += lh;
            }

            if (_runsCompleted > 0)
            {
                g.DrawText($"Runs completed: {_runsCompleted}",
                    new Vector2(OverlayX, y), SharpDX.Color.Gold);
            }
        }

        // =================================================================
        // Area change
        // =================================================================

        private void OnAreaChanged(BotContext ctx)
        {
            _monolithPos = null;
            _monolithId  = null;
            _homePos     = null;
            _hideoutFlow.Cancel();

            var gc = ctx.Game;
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                if (_encounterStartTime != DateTime.MinValue || _mapCompleted)
                {
                    _runsCompleted++;
                    _consecutiveFailedRuns = 0;
                    WriteEvent("RunEnd", $"run={_runsCompleted}", "complete");
                }
                _mapCompleted        = false;
                _encounterStartTime  = DateTime.MinValue;
                _phase               = CarryPhase.InHideout;
                _phaseStartTime      = DateTime.Now;
                StartHideoutFlow(ctx);
                StatusText = "Back in hideout — starting new run";
            }
            else
            {
                _phase          = CarryPhase.FindPosition;
                _phaseStartTime = DateTime.Now;
                StatusText      = "Entered domain — finding position";
                WriteEvent("MapEntered", gc.Area.CurrentArea.Name ?? "?", "");
                InitExploration(ctx);
            }
        }

        // =================================================================
        // Hideout phases
        // =================================================================

        private void TickHideout(BotContext ctx)
        {
            if (_phase == CarryPhase.WaitingInHideout)
            {
                StatusText = "Waiting in hideout — out of emblems or too many failures";
                return;
            }

            var signal = _hideoutFlow.Tick(ctx);
            StatusText = _hideoutFlow.Status;

            if (signal == HideoutSignal.PortalTimeout)
            {
                _consecutiveFailedRuns++;
                WriteEvent("PortalTimeout", $"attempt={_consecutiveFailedRuns}", "");
                if (_consecutiveFailedRuns >= MaxConsecutiveFailedRuns)
                {
                    _phase     = CarryPhase.WaitingInHideout;
                    StatusText = $"Stopped after {_consecutiveFailedRuns} portal failures. Check setup and restart.";
                    return;
                }
                StartHideoutFlow(ctx);
                StatusText = "Portal timeout — retrying";
            }
            else if (signal == HideoutSignal.NoFragments)
            {
                _phase     = CarryPhase.WaitingInHideout;
                StatusText = "Out of Timeless Emblems. Restock fragment tab and restart.";
                WriteEvent("NoFragments", "stopped", "");
            }
        }

        private void StartHideoutFlow(BotContext ctx)
        {
            var stash       = ctx.Settings.Stash;
            var fragmentTab = string.IsNullOrWhiteSpace(stash.FragmentTabName.Value)
                ? null : stash.FragmentTabName.Value;
            var dumpTab     = string.IsNullOrWhiteSpace(stash.DumpTabName.Value)
                ? null : stash.DumpTabName.Value;

            // Withdraw up to EmblemsNeeded Timeless Emblems from the fragment tab
            var withdrawList = new List<(string PathSubstring, int Count)>
            {
                (EmblemPathSubstring, EmblemsNeeded)
            };

            // After the first emblem is right-clicked (auto-inserts + selects domain),
            // ctrl+click 4 more to fill the remaining device slots.
            var extraEmblems = new string[]
            {
                EmblemPathSubstring, EmblemPathSubstring,
                EmblemPathSubstring, EmblemPathSubstring
            };

            _hideoutFlow.Start(
                mapFilter:             MapDeviceSystem.IsTimelessEmblem,
                stashItemFilter:       KeepEmblemsFilter,
                stashItemThreshold:    ctx.Settings.Run.StashItemThreshold.Value,
                dumpTabName:           dumpTab,
                resourceTabName:       fragmentTab,
                inventoryFragmentPath: EmblemPathSubstring,
                withdrawList:          withdrawList,
                scarabPaths:           extraEmblems);
        }

        // Stash everything except Timeless Emblems
        private static bool KeepEmblemsFilter(ServerInventory.InventSlotItem item)
        {
            var path = item.Item?.Path;
            return path == null
                || !path.Contains(EmblemPathSubstring, StringComparison.OrdinalIgnoreCase);
        }

        // =================================================================
        // Find position in the arena
        // =================================================================

        private void TickFindPosition(BotContext ctx)
        {
            var gc      = ctx.Game;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            if (elapsed < AreaSettleSeconds)
            {
                StatusText = "Settling in domain...";
                return;
            }

            FindMonolithEntity(gc);

            if (_monolithPos.HasValue)
            {
                var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var dist      = Vector2.Distance(playerPos, _monolithPos.Value);

                if (dist <= MonolithArrivalDist)
                {
                    LockHomeAndStartAttacking(ctx, playerPos);
                    return;
                }

                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, _monolithPos.Value);
                StatusText = $"Moving to ring (dist: {dist:F0})";
            }
            else
            {
                // Explore until the monolith entity loads in
                if (ctx.Exploration.IsInitialized)
                {
                    ctx.Exploration.Update(gc.Player.GridPosNum);
                    if (!ctx.Navigation.IsNavigating)
                    {
                        var target = ctx.Exploration.GetNextExplorationTarget(gc.Player.GridPosNum);
                        if (target.HasValue)
                            ctx.Navigation.NavigateTo(gc, target.Value);
                    }
                }
                StatusText = "Searching for ring...";

                // Fallback: after 30s just stand here and start attacking
                if (elapsed > 30f)
                {
                    var fallback = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                    LockHomeAndStartAttacking(ctx, fallback);
                    WriteEvent("HomeSet", "fallback-timeout", $"pos=({fallback.X:F0},{fallback.Y:F0})");
                }
            }
        }

        private void LockHomeAndStartAttacking(BotContext ctx, Vector2 homePos)
        {
            ctx.Navigation.Stop(ctx.Game);
            _homePos             = homePos;
            _encounterStartTime  = DateTime.Now;
            _nextAttackAt        = DateTime.Now;
            _phase               = CarryPhase.Attacking;
            _phaseStartTime      = DateTime.Now;
            WriteEvent("HomeSet", $"pos=({homePos.X:F0},{homePos.Y:F0})",
                _monolithPos.HasValue
                    ? $"distToRing={Vector2.Distance(homePos, _monolithPos.Value):F0}"
                    : "no-monolith");
            StatusText = "Position locked — attacking";
        }

        // =================================================================
        // Attacking
        // =================================================================

        private void TickAttacking(BotContext ctx)
        {
            var gc  = ctx.Game;
            var now = DateTime.Now;

            // Check encounter timer
            var elapsed = (now - _encounterStartTime).TotalSeconds;
            if (elapsed >= EncounterDurationSeconds)
            {
                ctx.Navigation.Stop(gc);
                _mapCompleted = true;
                _phase        = CarryPhase.EncounterEnded;
                _phaseStartTime = now;
                WriteEvent("EncounterEnded", "TimerExpired", $"elapsed={elapsed:F0}s");
                StatusText = "Encounter over — finding exit";
                return;
            }

            // Check for Headhunter displacement
            var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            if (_homePos.HasValue &&
                Vector2.Distance(playerPos, _homePos.Value) > DisplacementThreshold)
            {
                ctx.Navigation.Stop(gc);
                _phase      = CarryPhase.Repositioning;
                _phaseStartTime = now;
                WriteEvent("Displaced", "HH",
                    $"dist={Vector2.Distance(playerPos, _homePos.Value):F0}");
                StatusText = "Displaced — repositioning";
                return;
            }

            var remaining = EncounterDurationSeconds - elapsed;
            StatusText = $"Attacking — {remaining:F0}s left";

            // Fire Q on interval — aim at ring center if known, else screen center
            if (now >= _nextAttackAt && BotInput.CanAct)
            {
                var aimPos = GetAttackScreenPos(gc);
                BotInput.CursorPressKey(aimPos, AttackKey);
                _nextAttackAt = now.AddMilliseconds(AttackIntervalMs);
            }
        }

        // =================================================================
        // Repositioning after Headhunter displacement
        // =================================================================

        private void TickRepositioning(BotContext ctx)
        {
            if (!_homePos.HasValue)
            {
                _phase = CarryPhase.Attacking;
                return;
            }

            var gc        = ctx.Game;
            var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var dist      = Vector2.Distance(playerPos, _homePos.Value);

            // Check encounter timer while repositioning
            var elapsed = (DateTime.Now - _encounterStartTime).TotalSeconds;
            if (elapsed >= EncounterDurationSeconds)
            {
                ctx.Navigation.Stop(gc);
                _mapCompleted   = true;
                _phase          = CarryPhase.EncounterEnded;
                _phaseStartTime = DateTime.Now;
                StatusText      = "Encounter over — finding exit";
                return;
            }

            if (dist < DisplacementThreshold / 2f)
            {
                ctx.Navigation.Stop(gc);
                _phase        = CarryPhase.Attacking;
                _phaseStartTime = DateTime.Now;
                _nextAttackAt = DateTime.Now;
                WriteEvent("Repositioned", $"dist={dist:F0}", "");
                StatusText = "Back at position — resuming attack";
                return;
            }

            if (!ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, _homePos.Value);

            StatusText = $"Repositioning (dist: {dist:F0})";
        }

        // =================================================================
        // Encounter ended — find exit portal
        // =================================================================

        private void TickEncounterEnded(BotContext ctx)
        {
            var gc      = ctx.Game;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            if (elapsed < 2.0)
            {
                StatusText = $"Encounter over — leaving in {2.0 - elapsed:F1}s";
                return;
            }

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var portal     = FindNearestPortal(gc, playerGrid, 200f);

            if (portal == null)
            {
                StatusText = "Searching for exit portal...";
                return;
            }

            var dist = Vector2.Distance(playerGrid,
                new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y));

            if (dist > 15f)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc,
                        new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y));
                StatusText = $"Moving to exit portal (dist: {dist:F0})";
                return;
            }

            TryClickPortal(ctx, portal);
            StatusText = "Exiting domain";
        }

        // =================================================================
        // Helpers
        // =================================================================

        private Vector2 GetAttackScreenPos(GameController gc)
        {
            var window = gc.Window.GetWindowRectangle();

            if (_monolithPos.HasValue)
            {
                try
                {
                    var world  = Systems.Pathfinding.GridToWorld3D(gc, _monolithPos.Value);
                    var screen = gc.IngameState.Camera.WorldToScreen(world);
                    return new Vector2(window.X + screen.X, window.Y + screen.Y);
                }
                catch { }
            }

            // Fallback: screen center
            return new Vector2(window.X + window.Width / 2f, window.Y + window.Height / 2f);
        }

        private void FindMonolithEntity(GameController gc)
        {
            // Try cached ID first
            if (_monolithId.HasValue)
            {
                var cached = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Id == _monolithId.Value);
                if (cached != null)
                {
                    _monolithPos = new Vector2(cached.GridPosNum.X, cached.GridPosNum.Y);
                    return;
                }
                _monolithId = null;
            }

            foreach (var e in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (e.Metadata == MonolithMeta)
                {
                    _monolithId  = e.Id;
                    _monolithPos = new Vector2(e.GridPosNum.X, e.GridPosNum.Y);
                    return;
                }
            }
        }

        private static Entity? FindNearestPortal(GameController gc, Vector2 nearGrid, float maxDist)
        {
            Entity? best     = null;
            float   bestDist = float.MaxValue;
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

        private void TryClickPortal(BotContext ctx, Entity portal)
        {
            var now = DateTime.Now;
            if ((now - _lastPortalClickAt).TotalMilliseconds < PortalClickCooldownMs) return;
            _lastPortalClickAt = now;
            WriteEvent("ExitPortal", portal.Metadata ?? "portal", "");
            BotInput.ClickEntity(ctx.Game, portal);
        }

        private void InitExploration(BotContext ctx)
        {
            var gc     = ctx.Game;
            var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
            var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
            if (pfGrid != null && gc.Player != null)
            {
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                    ctx.Settings.Build.BlinkRange.Value);
            }
        }

        private void Reset()
        {
            _mapCompleted          = false;
            _lastAreaName          = "";
            _consecutiveFailedRuns = 0;
            _runsCompleted         = 0;
            _homePos               = null;
            _monolithPos           = null;
            _monolithId            = null;
            _nextAttackAt          = DateTime.MinValue;
            _encounterStartTime    = DateTime.MinValue;
            _lastPortalClickAt     = DateTime.MinValue;
        }

        // =================================================================
        // Logging
        // =================================================================

        private void OpenLog()
        {
            try
            {
                var dir  = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "FiveWayCarry");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"carry_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                _eventLog = new StreamWriter(file, append: false);
                _eventLog.WriteLine("TimeMs,Phase,Action,Detail,Reason");
                _eventLog.Flush();
                _logStart = DateTime.Now;
            }
            catch { _eventLog = null; }
        }

        private void CloseLog()
        {
            try { _eventLog?.Flush(); _eventLog?.Close(); }
            catch { }
            _eventLog = null;
        }

        private void WriteEvent(string action, string detail, string reason)
        {
            if (_eventLog == null) return;
            var ms = (long)(DateTime.Now - _logStart).TotalMilliseconds;
            _eventLog.WriteLine(
                $"{ms},{_phase},{action}," +
                $"{detail.Replace(',', ';')},{reason.Replace(',', ';')}");
            _eventLog.Flush();
        }
    }

    public enum CarryPhase
    {
        Idle,
        InHideout,
        FindPosition,
        Attacking,
        Repositioning,
        EncounterEnded,
        WaitingInHideout,
    }
}
