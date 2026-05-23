using ExileCore;
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

        // Random warmup window after reaching the monolith ring edge before pressing anything.
        // Lets the carry trigger the first wave cleanly before resets begin.
        private const float WarmupMinSeconds = 7f;
        private const float WarmupMaxSeconds = 10f;

        // Total encounter duration in seconds.
        // Base 15s + 1 min per emblem. With 5 emblems = ~315s. Stop a few seconds early
        // so the last reset completes cleanly before the encounter ends.
        private const float EncounterDurationSeconds = 305f;

        // Grid distance at which the bot stops and considers itself positioned at the ring edge.
        // The ring in Domain of Timeless Conflict is ~20-22 grid units radius.
        // Arriving at 18 units puts the bot just inside the ring boundary — ideal for Dash-exit.
        private const float MonolithArrivalDist = 18f;

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

        // Stats
        private int _runsCompleted;
        private DateTime _sessionStart = DateTime.MinValue;

        // Logging
        private StreamWriter? _log;

        // Metadata substrings to identify the central monolith entity in the Domain.
        private static readonly string[] MonolithMetaPaths =
        {
            "LegionMonolith",
            "TimelessConflict",
            "Legion/Monolith",
            "LegionStone",
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

            ModeHelpers.EnableDefaultCombat(ctx);
            OpenLog();

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
            CloseLog();
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

                WriteEvent("AreaChange", "BackInHideout",
                    $"phase={_phase},resets={_resetCount},runs={_runsCompleted}");

                _resetCount = 0;
                _phase = FiveWayPhase.WaitingInHideout;
                _phaseStartTime = DateTime.Now;
                StatusText = "Returned to hideout — waiting for next run";
                return;
            }

            // Suppress combat movement and targeted skills in the Domain so the bot
            // stays planted at the ring edge and doesn't chase monsters.
            bool inDomain = IsInDomain(gc);
            ctx.Combat.SuppressPositioning = inDomain;
            ctx.Combat.SuppressTargetedSkills = inDomain;

            switch (_phase)
            {
                case FiveWayPhase.WaitingInHideout:    TickWaitingInHideout(ctx);    break;
                case FiveWayPhase.FindMonolith:        TickFindMonolith(ctx);        break;
                case FiveWayPhase.NavigatingToMonolith:TickNavigatingToMonolith(ctx);break;
                case FiveWayPhase.WarmupDelay:         TickWarmupDelay(ctx);         break;
                case FiveWayPhase.Resetting:           TickResetting(ctx);           break;
                case FiveWayPhase.EncounterEnded:      TickEncounterEnded(ctx);      break;
            }
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

            if (_phase == FiveWayPhase.Resetting)
            {
                var elapsed = (DateTime.Now - _encounterStartTime).TotalSeconds;
                var remaining = Math.Max(0, EncounterDurationSeconds - elapsed);
                g.DrawText($"Resets: {_resetCount}  Timer: {remaining:F0}s remaining",
                    new Vector2(OverlayX, y), SharpDX.Color.Cyan);
                y += lh;

                var dashIn = Math.Max(0, (_nextDashAt - DateTime.Now).TotalMilliseconds);
                g.DrawText(_pendingShieldCharge
                        ? $"Next: Shield Charge (in {(_shieldChargeAt - DateTime.Now).TotalMilliseconds:F0}ms)"
                        : $"Next: Dash (in {dashIn:F0}ms)",
                    new Vector2(OverlayX, y), SharpDX.Color.Yellow);
                y += lh;
            }

            if (_runsCompleted > 0)
            {
                g.DrawText($"Completed runs: {_runsCompleted}",
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

                // Close enough — check if a portal just appeared near the leader
                var portal = FindNearestPortal(gc, leaderGrid, PortalSearchRadius);
                if (portal != null)
                {
                    TryEnterPortal(ctx, portal);
                    return;
                }

                StatusText = $"Near {leaderName} — waiting for Domain portal";
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

                if (dist <= MonolithArrivalDist)
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
        // Hold position for 7-10s to let the carry trigger the first wave.
        // ──────────────────────────────────────────────────────────────────────

        private void TickWarmupDelay(BotContext ctx)
        {
            ctx.Navigation.Stop(ctx.Game);
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            if (elapsed >= _warmupDelay)
            {
                // Begin with a Dash immediately — starts the exit cycle
                _nextDashAt = DateTime.Now;
                _pendingShieldCharge = false;
                _resetCount = 0;
                _encounterStartTime = DateTime.Now;
                WriteEvent("ResetStart", "WarmupDone", $"delay={elapsed:F1}s");
                _phase = FiveWayPhase.Resetting;
                _phaseStartTime = DateTime.Now;
                StatusText = "Starting reset loop — Dash→ShieldCharge cycles";
            }
            else
            {
                StatusText = $"Holding position — reset starts in {_warmupDelay - elapsed:F1}s";
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase: Resetting
        //
        // Core loop:
        //   1. Press Dash (W) — exits the ring via micro-movement
        //   2. After ShieldChargeDelayAfterDashMs, press Shield Charge (Q) — re-enters ring
        //   3. Wait DashCooldownMs from last Dash before repeating
        //
        // The ~2.21s Dash cooldown satisfies the 2-second in-ring stay requirement.
        // Shield Charge has no cooldown so it fires as fast as BotInput allows.
        // ──────────────────────────────────────────────────────────────────────

        private void TickResetting(BotContext ctx)
        {
            var elapsed = (DateTime.Now - _encounterStartTime).TotalSeconds;

            if (elapsed >= EncounterDurationSeconds)
            {
                WriteEvent("ResetEnd", "TimerExpired",
                    $"resets={_resetCount},elapsed={elapsed:F0}s");
                _phase = FiveWayPhase.EncounterEnded;
                _phaseStartTime = DateTime.Now;
                StatusText = "Encounter over — carry is looting";
                return;
            }

            var remaining = EncounterDurationSeconds - elapsed;
            StatusText = $"Resetting: {_resetCount} resets — {remaining:F0}s left";

            if (!BotInput.CanAct) return;

            var now = DateTime.Now;

            if (_pendingShieldCharge)
            {
                // Fire Shield Charge as soon as the short post-Dash gap has elapsed
                if (now >= _shieldChargeAt)
                {
                    if (BotInput.PressKey(ShieldChargeKey))
                    {
                        _pendingShieldCharge = false;
                        _resetCount++;
                    }
                }
            }
            else
            {
                // Fire Dash when cooldown is ready
                if (now >= _nextDashAt)
                {
                    if (BotInput.PressKey(DashKey))
                    {
                        _nextDashAt = now.AddMilliseconds(DashCooldownMs);
                        _shieldChargeAt = now.AddMilliseconds(ShieldChargeDelayAfterDashMs);
                        _pendingShieldCharge = true;
                    }
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase: EncounterEnded
        // Hold position until the area transition carries everyone back to hideout.
        // ──────────────────────────────────────────────────────────────────────

        private void TickEncounterEnded(BotContext ctx)
        {
            ctx.Navigation.Stop(ctx.Game);
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            StatusText = $"Waiting for party to loot and exit ({elapsed:F0}s)";
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
            return gc.EntityListWrapper
                .ValidEntitiesByType[EntityType.Player]
                .FirstOrDefault(e => string.Equals(
                    e.RenderName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static Entity? FindNearestPortal(GameController gc, Vector2 nearGrid, float maxDist)
        {
            Entity? best = null;
            float bestDist = float.MaxValue;
            foreach (var p in gc.EntityListWrapper.ValidEntitiesByType[EntityType.TownPortal])
            {
                var d = Vector2.Distance(
                    new Vector2(p.GridPosNum.X, p.GridPosNum.Y), nearGrid);
                if (d < maxDist && d < bestDist) { best = p; bestDist = d; }
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

        private void TryEnterPortal(BotContext ctx, Entity portal)
        {
            WriteEvent("EnterPortal", portal.Metadata ?? "portal", "");
            BotInput.ClickEntity(ctx.Game, portal);
            StatusText = "Entering Domain of Timeless Conflict portal";
        }

        private static float RandRange(float min, float max) =>
            min + (float)(_rng.NextDouble() * (max - min));

        // ──────────────────────────────────────────────────────────────────────
        // Logging
        // ──────────────────────────────────────────────────────────────────────

        private void OpenLog()
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "FiveWay");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"fiveway_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                _log = new StreamWriter(path, append: false) { AutoFlush = true };
                _log.WriteLine("TimeMs,Event,Detail1,Detail2");
            }
            catch { _log = null; }
        }

        private void CloseLog()
        {
            try { _log?.Flush(); _log?.Close(); } catch { }
            _log = null;
        }

        private void WriteEvent(string evt, string detail1, string detail2)
        {
            if (_sessionStart == DateTime.MinValue) return;
            var ms = (long)(DateTime.Now - _sessionStart).TotalMilliseconds;
            _log?.WriteLine($"{ms},{evt},{detail1},{detail2}");
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
