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
    /// The bot follows the leader into the Domain, walks to the central monolith,
    /// waits a short delay, then alternates Shield Charge and Dash at their
    /// cooldown interval to continuously reset monster spawns for the carry team.
    /// Both skills must have "Always Attack Without Moving" checked in-game.
    ///
    /// Configuration: edit the constants below to match your character's key bindings.
    /// </summary>
    public class FiveWayMode : IBotMode
    {
        public string Name => "5-Way Legion";

        // ──────────────────────────────────────────────────────────────────────
        // CONFIGURATION — edit these to match your key bindings
        // ──────────────────────────────────────────────────────────────────────
        // Key bound to Shield Charge (the "slam" skill)
        private const Keys ShieldChargeKey = Keys.Q;
        // Key bound to Dash (the held/toggle skill)
        private const Keys DashKey = Keys.W;

        // Skill cooldown in ms — 2.21s matches 20% quality lvl-4 Dash / Shield Charge
        private const float ResetIntervalMs = 2210f;
        // Random warmup window after reaching the monolith before pressing anything
        private const float WarmupMinSeconds = 7f;
        private const float WarmupMaxSeconds = 10f;
        // Total encounter duration (base 15s + 1 min per emblem × 5 = ~315s; use 305 to stop a bit early)
        private const float EncounterDurationSeconds = 305f;
        // Grid distance at which we consider ourselves "at" the monolith
        private const float MonolithArrivalDist = 18f;
        // Grid distance at which we stop following the leader in hideout
        private const float FollowStopDist = 12f;
        // Grid radius around leader's last known position to search for the entry portal
        private const float PortalSearchRadius = 70f;
        // How long to wait for the leader to reappear / portal to show before resetting tracking
        private const float PortalWaitTimeout = 45f;
        // Seconds to let entity list settle after zone load before acting
        private const float AreaSettleSeconds = 2.5f;
        // Seconds before giving up on monolith search and resetting from current position
        private const float FindMonolithTimeout = 25f;
        // Overlay position
        private const float OverlayX = 20f;
        private const float OverlayY = 100f;

        // ──────────────────────────────────────────────────────────────────────
        // State
        // ──────────────────────────────────────────────────────────────────────
        private FiveWayPhase _phase = FiveWayPhase.Idle;
        private DateTime _phaseStartTime = DateTime.MinValue;
        public string StatusText { get; private set; } = "";

        // Leader tracking (for portal-follow in hideout and monolith-find in domain)
        private Vector2 _lastLeaderPos;
        private bool _hasLastLeaderPos;
        private DateTime _leaderLastSeenAt = DateTime.MinValue;

        // Monolith target in domain
        private Vector2? _monolithPos;
        private uint? _monolithId;

        // Reset loop
        private bool _useShieldChargeNext = true;  // alternates between the two skills
        private DateTime _nextShieldChargeAt = DateTime.MinValue;
        private DateTime _nextDashAt = DateTime.MinValue;
        private DateTime _encounterStartTime = DateTime.MinValue;
        private float _warmupDelay;
        private int _resetCount;

        // Run stats
        private int _runsCompleted;
        private DateTime _sessionStart = DateTime.MinValue;

        // Logging
        private StreamWriter? _log;

        // Metadata substrings to search for the central monolith entity in the Domain.
        // Multiple patterns because GGG naming isn't consistent across patches.
        private static readonly string[] MonolithMetaPaths =
        {
            "LegionMonolith",
            "TimelessConflict",
            "Legion/Monolith",
            "LegionStone",
            "Afflictionator",   // fallback: simulacrum monolith uses same base type in some builds
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
            _useShieldChargeNext = true;
            _nextShieldChargeAt = DateTime.MinValue;
            _nextDashAt = DateTime.MinValue;
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

            // Any return to hideout/town from a map phase resets us cleanly
            if ((gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown) &&
                _phase != FiveWayPhase.WaitingInHideout &&
                _phase != FiveWayPhase.Idle)
            {
                ModeHelpers.CancelAllSystems(ctx);
                _monolithPos = null;
                _monolithId = null;

                if (_phase == FiveWayPhase.Resetting || _phase == FiveWayPhase.EncounterEnded)
                    _runsCompleted++;

                WriteEvent("AreaChange", "BackInHideout", $"phase={_phase},runs={_runsCompleted}");
                _phase = FiveWayPhase.WaitingInHideout;
                _phaseStartTime = DateTime.Now;
                StatusText = "Returned to hideout — waiting for next run";
                return;
            }

            // Suppress combat movement and targeted skills during reset phases —
            // we don't want the character chasing monsters and leaving the monolith.
            bool inDomain = IsInDomain(gc);
            ctx.Combat.SuppressPositioning = inDomain;
            ctx.Combat.SuppressTargetedSkills = inDomain;

            switch (_phase)
            {
                case FiveWayPhase.WaitingInHideout:
                    TickWaitingInHideout(ctx);
                    break;
                case FiveWayPhase.FindMonolith:
                    TickFindMonolith(ctx);
                    break;
                case FiveWayPhase.NavigatingToMonolith:
                    TickNavigatingToMonolith(ctx);
                    break;
                case FiveWayPhase.WarmupDelay:
                    TickWarmupDelay(ctx);
                    break;
                case FiveWayPhase.Resetting:
                    TickResetting(ctx);
                    break;
                case FiveWayPhase.EncounterEnded:
                    TickEncounterEnded(ctx);
                    break;
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
                g.DrawText($"Resets: {_resetCount}  Timer: {remaining:F0}s",
                    new Vector2(OverlayX, y), SharpDX.Color.Cyan);
                y += lh;
            }

            if (_runsCompleted > 0)
            {
                g.DrawText($"Runs completed: {_runsCompleted}",
                    new Vector2(OverlayX, y), SharpDX.Color.Gold);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase handlers
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

                // Follow the leader
                if (dist > FollowStopDist + 8f)
                {
                    if (!ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, leaderGrid);
                    StatusText = $"Following {leaderName} (dist: {dist:F0})";
                    return;
                }

                ctx.Navigation.Stop(gc);

                // Close enough to leader — watch for a portal to appear near them
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
                // Leader disappeared — they likely entered the portal already
                var timeSinceSeen = (DateTime.Now - _leaderLastSeenAt).TotalSeconds;

                if (timeSinceSeen > 1.5)
                {
                    var portal = FindNearestPortal(gc, _lastLeaderPos, PortalSearchRadius);
                    if (portal != null)
                    {
                        var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                        var portalGrid = new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y);
                        var distToPortal = Vector2.Distance(playerGrid, portalGrid);

                        if (distToPortal > 10f)
                        {
                            if (!ctx.Navigation.IsNavigating)
                                ctx.Navigation.NavigateTo(gc, portalGrid);
                            StatusText = $"Moving to portal (dist: {distToPortal:F0})";
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
                    ? "Set LeaderName in Follower settings to follow into Domain"
                    : $"Waiting for leader '{leaderName}'...";
            }
        }

        private void TickFindMonolith(BotContext ctx)
        {
            var gc = ctx.Game;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            if (elapsed < AreaSettleSeconds)
            {
                StatusText = "Zone loaded — settling...";
                return;
            }

            // Already have position from a previous tick?
            if (_monolithPos.HasValue)
            {
                TransitionToNavMonolith(ctx);
                return;
            }

            // Search entity list for the central monolith stone
            var monolith = FindMonolithEntity(gc);
            if (monolith != null)
            {
                _monolithId = monolith.Id;
                _monolithPos = new Vector2(monolith.GridPosNum.X, monolith.GridPosNum.Y);
                WriteEvent("MonolithFound", monolith.Metadata ?? "?", "entity");
                TransitionToNavMonolith(ctx);
                return;
            }

            // Fall back to leader position — in 5-ways the carry runs straight to the stone
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
                    // Close enough — use leader position as monolith centre
                    _monolithPos = leaderGrid;
                    WriteEvent("MonolithApprox", "LeaderPos", $"dist={dist:F0}");
                    TransitionToNavMonolith(ctx);
                    return;
                }

                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, leaderGrid);
                StatusText = $"Following leader to monolith (dist: {dist:F0})";
                return;
            }

            // No entity, no leader — explore toward the centre
            if (ctx.Exploration.IsInitialized && !ctx.Navigation.IsNavigating)
            {
                var target = ctx.Exploration.GetNextExplorationTarget(gc.Player.GridPosNum);
                if (target.HasValue)
                    ctx.Navigation.NavigateTo(gc, target.Value);
            }

            StatusText = "Searching for monolith...";

            // Hard timeout: start from wherever we are
            if (elapsed > FindMonolithTimeout)
            {
                var pGrid = gc.Player.GridPosNum;
                _monolithPos = new Vector2(pGrid.X, pGrid.Y);
                WriteEvent("MonolithFallback", "Timeout", $"elapsed={elapsed:F0}s");
                TransitionToNavMonolith(ctx);
            }
        }

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
                WriteEvent("AtMonolith", $"dist={dist:F0}", $"warmup={_warmupDelay:F1}s");
                _phase = FiveWayPhase.WarmupDelay;
                _phaseStartTime = DateTime.Now;
                StatusText = $"At monolith — waiting {_warmupDelay:F0}s before resetting";
                return;
            }

            if (!ctx.Navigation.IsNavigating)
            {
                if (!ctx.Navigation.NavigateTo(gc, _monolithPos.Value))
                {
                    WriteEvent("NavFailed", "ToMonolith", $"dist={dist:F0}");
                    // Navigation couldn't find a path — start from here anyway
                    _warmupDelay = RandRange(WarmupMinSeconds, WarmupMaxSeconds);
                    _phase = FiveWayPhase.WarmupDelay;
                    _phaseStartTime = DateTime.Now;
                    StatusText = "No path to monolith — resetting from current position";
                }
            }

            StatusText = $"Navigating to monolith (dist: {dist:F0})";
        }

        private void TickWarmupDelay(BotContext ctx)
        {
            ctx.Navigation.Stop(ctx.Game);

            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            if (elapsed >= _warmupDelay)
            {
                // Stagger the two skills: Shield Charge fires immediately, Dash fires after half a cooldown
                _nextShieldChargeAt = DateTime.Now;
                _nextDashAt = DateTime.Now.AddMilliseconds(ResetIntervalMs / 2f);
                _resetCount = 0;
                _encounterStartTime = DateTime.Now;
                WriteEvent("ResetStart", "WarmupDone", $"delay={elapsed:F1}s");
                _phase = FiveWayPhase.Resetting;
                _phaseStartTime = DateTime.Now;
                StatusText = "Starting reset loop";
            }
            else
            {
                StatusText = $"Waiting {_warmupDelay - elapsed:F1}s before resetting...";
            }
        }

        private void TickResetting(BotContext ctx)
        {
            var elapsedSec = (DateTime.Now - _encounterStartTime).TotalSeconds;

            if (elapsedSec >= EncounterDurationSeconds)
            {
                WriteEvent("ResetEnd", "TimerExpired", $"resets={_resetCount},elapsed={elapsedSec:F0}s");
                _phase = FiveWayPhase.EncounterEnded;
                _phaseStartTime = DateTime.Now;
                StatusText = "Encounter over — waiting for carry to loot and leave";
                return;
            }

            var remaining = EncounterDurationSeconds - elapsedSec;
            StatusText = $"Resetting: {_resetCount} resets — {remaining:F0}s left";

            if (!BotInput.CanAct) return;

            var now = DateTime.Now;

            // Fire Shield Charge when its cooldown is up
            if (now >= _nextShieldChargeAt)
            {
                if (BotInput.PressKey(ShieldChargeKey))
                {
                    _nextShieldChargeAt = now.AddMilliseconds(ResetIntervalMs);
                    _resetCount++;
                }
            }
            // Fire Dash when its cooldown is up (offset from Shield Charge by half-interval at startup)
            else if (now >= _nextDashAt)
            {
                if (BotInput.PressKey(DashKey))
                {
                    _nextDashAt = now.AddMilliseconds(ResetIntervalMs);
                }
            }
        }

        private void TickEncounterEnded(BotContext ctx)
        {
            // Just hold position — area transition back to hideout will trigger OnAreaChanged
            ctx.Navigation.Stop(ctx.Game);
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            StatusText = $"Encounter ended — waiting for party to exit ({elapsed:F0}s)";
        }

        // ──────────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────────

        private void TransitionToNavMonolith(BotContext ctx)
        {
            ctx.Navigation.Stop(ctx.Game);
            _phase = FiveWayPhase.NavigatingToMonolith;
            _phaseStartTime = DateTime.Now;
            StatusText = "Monolith found — navigating";
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
                if (d < maxDist && d < bestDist)
                {
                    best = p;
                    bestDist = d;
                }
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
