using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using AutoExile.Modes.Shared;
using System.Numerics;

namespace AutoExile.Modes
{
    /// <summary>
    /// XP leveling loop: open a low-level map via the map device, clear monsters
    /// (zero looting), exit, and repeat indefinitely.
    ///
    /// Setup:
    ///   1. In the bot UI, set "Wave Farming → Map Name" to the map you want to farm
    ///      (e.g. "Shore Map"). The bot will pull one copy per run from the stash tab
    ///      configured in "Wave Farming → Mapping Supplies Tab".
    ///   2. Leave the five Map Device Slot fields blank (no scarabs needed for XP).
    ///   3. Select "Leveling" in the Mode dropdown and start the bot.
    ///
    /// To register this mode add one line in BotCore.cs Initialise():
    ///   RegisterMode(new LevelingMode());
    /// (place it after the SimulacrumMode registration line)
    /// </summary>
    public class LevelingMode : IBotMode
    {
        public string Name => "Leveling";

        private LevelPhase _phase = LevelPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;
        private DateTime _lastActionTime = DateTime.MinValue;
        private string _lastAreaName = "";
        private int _runsCompleted;
        private int _consecutivePortalFailures;
        private const int MaxPortalFailures = 3;

        // How much of the map to explore before exiting (0.0–1.0).
        // 0.85 = exit once 85 % of the walkable area has been seen.
        private const float ClearCoverageThreshold = 0.85f;

        // Safety ceiling: abandon the map and return to hideout after this many seconds.
        private const float ZoneTimeoutSeconds = 600f;   // 10 minutes

        private const float MajorActionCooldownMs = 500f;

        // Map-item path substring used to identify maps in inventory and stash.
        // In PoE 2 map items have paths containing "Maps/". Change this constant
        // if HideoutFlow cannot find your maps (e.g. try "MapAtlas" for PoE 1 maps).
        private const string MapItemPath = "Maps/";

        private readonly HideoutFlow _hideoutFlow = new();

        // Cached portal position for ExitMap navigation fallback
        private Vector2? _cachedPortalPos;

        public string StatusText { get; private set; } = "";

        // =====================================================================
        // IBotMode
        // =====================================================================

        public void OnEnter(BotContext ctx)
        {
            _runsCompleted = 0;
            _consecutivePortalFailures = 0;
            _lastAreaName = "";
            _cachedPortalPos = null;

            ModeHelpers.EnableDefaultCombat(ctx);

            var gc = ctx.Game;
            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                _phase = LevelPhase.InHideout;
                _phaseStartTime = DateTime.Now;
                StartHideoutFlow(ctx);
                StatusText = "In hideout — preparing map";
            }
            else
            {
                // Started mid-map — jump straight to clearing
                _phase = LevelPhase.ClearMap;
                _phaseStartTime = DateTime.Now;
                InitExploration(ctx);
                StatusText = "In map — clearing";
            }
        }

        public void OnExit()
        {
            _phase = LevelPhase.Idle;
        }

        public void Tick(BotContext ctx)
        {
            var gc = ctx.Game;

            // ── Area-change detection ──────────────────────────────────────
            var currentArea = gc.Area?.CurrentArea?.Name ?? "";
            if (!string.IsNullOrEmpty(currentArea) && currentArea != _lastAreaName)
            {
                OnAreaChanged(ctx, currentArea);
                _lastAreaName = currentArea;
            }

            // ── Combat (only while clearing — never suppress it) ───────────
            bool inMap = gc.Area?.CurrentArea != null &&
                         !gc.Area.CurrentArea.IsHideout &&
                         !gc.Area.CurrentArea.IsTown;
            if (inMap && _phase == LevelPhase.ClearMap)
            {
                ctx.Combat.SuppressPositioning = false;
                ctx.Combat.SuppressTargetedSkills = false;
                ctx.Combat.Tick(ctx);
            }

            ctx.Interaction.Tick(gc);

            // ── Phase dispatch ─────────────────────────────────────────────
            switch (_phase)
            {
                case LevelPhase.InHideout:
                    TickHideout(ctx);
                    break;
                case LevelPhase.ClearMap:
                    TickClearMap(ctx);
                    break;
                case LevelPhase.ExitMap:
                    TickExitMap(ctx);
                    break;
                case LevelPhase.Idle:
                    StatusText = "Idle";
                    break;
            }
        }

        public void Render(BotContext ctx) { }

        // =====================================================================
        // Hideout phase — delegated to HideoutFlow
        // =====================================================================

        private void TickHideout(BotContext ctx)
        {
            var signal = _hideoutFlow.Tick(ctx);
            StatusText = _hideoutFlow.Status;

            if (signal == HideoutSignal.PortalTimeout)
            {
                _consecutivePortalFailures++;
                if (_consecutivePortalFailures >= MaxPortalFailures)
                {
                    _phase = LevelPhase.Idle;
                    StatusText = $"Stopped: no portal found on {_consecutivePortalFailures} " +
                                 "consecutive runs. Check the map device and map tab settings.";
                    return;
                }
                StartHideoutFlow(ctx);
                StatusText = "No portal found — retrying";
            }
            else if (signal == HideoutSignal.NoFragments)
            {
                _phase = LevelPhase.Idle;
                StatusText = "Stopped: no maps available. Restock the map stash tab and restart.";
            }
        }

        // =====================================================================
        // Map clearing
        // =====================================================================

        private void TickClearMap(BotContext ctx)
        {
            var gc = ctx.Game;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;

            if (elapsed > ZoneTimeoutSeconds)
            {
                ctx.Log($"[Leveling] Zone timeout after {elapsed:F0}s — exiting map");
                EnterExitPhase(ctx);
                return;
            }

            if (!ctx.Exploration.IsInitialized)
            {
                InitExploration(ctx);
                return;
            }

            ctx.Exploration.Update(gc.Player.GridPosNum);
            var coverage = ctx.Exploration.ActiveBlobCoverage;

            if (coverage >= ClearCoverageThreshold)
            {
                ctx.Log($"[Leveling] Map cleared ({coverage:P0}) after {elapsed:F0}s — exiting");
                EnterExitPhase(ctx);
                return;
            }

            // Navigate towards unexplored area; combat handles any enemies
            if (!ctx.Navigation.IsNavigating)
            {
                var target = ctx.Exploration.GetNextExplorationTarget(gc.Player.GridPosNum);
                if (target.HasValue)
                {
                    ctx.Navigation.NavigateTo(gc, target.Value);
                }
                else
                {
                    // No reachable targets left — treat as complete
                    ctx.Log($"[Leveling] No exploration targets at {coverage:P0} coverage — exiting");
                    EnterExitPhase(ctx);
                    return;
                }
            }

            StatusText = $"Clearing ({coverage:P0}) — run {_runsCompleted + 1}";
        }

        private void EnterExitPhase(BotContext ctx)
        {
            _phase = LevelPhase.ExitMap;
            _phaseStartTime = DateTime.Now;
            _cachedPortalPos = null;
            ctx.Navigation.Stop(ctx.Game);
            ctx.LootTracker.RecordMapComplete();
            StatusText = "Clearing done — finding exit portal";
        }

        // =====================================================================
        // Exit map
        // =====================================================================

        private void TickExitMap(BotContext ctx)
        {
            var gc = ctx.Game;

            if (gc.Area.CurrentArea.IsHideout)
                return;   // area change handler takes over

            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 30)
            {
                _phase = LevelPhase.Idle;
                StatusText = "Exit timeout — giving up. Restart the bot.";
                return;
            }

            if (!ModeHelpers.CanAct(_lastActionTime, MajorActionCooldownMs)) return;

            // Close any open panels first
            if (gc.IngameState.IngameUi.StashElement?.IsVisible == true ||
                gc.IngameState.IngameUi.InventoryPanel?.IsVisible == true)
            {
                BotInput.PressKey(System.Windows.Forms.Keys.Escape);
                _lastActionTime = DateTime.Now;
                return;
            }

            var portal = ModeHelpers.FindNearestPortal(gc);

            // Cache valid portal position for navigation fallback
            if (portal != null)
                _cachedPortalPos = new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y);

            if (portal == null)
            {
                if (_cachedPortalPos.HasValue)
                {
                    var dist = Vector2.Distance(gc.Player.GridPosNum, _cachedPortalPos.Value);
                    if (dist > ctx.Interaction.InteractRadius && !ctx.Navigation.IsNavigating)
                        ctx.Navigation.NavigateTo(gc, _cachedPortalPos.Value);
                    StatusText = $"Walking to portal (dist: {dist:F0})";
                }
                else
                {
                    StatusText = "Looking for exit portal...";
                }
                return;
            }

            var playerPos = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var portalPos = new Vector2(portal.GridPosNum.X, portal.GridPosNum.Y);
            var portalDist = Vector2.Distance(playerPos, portalPos);

            if (portalDist > ctx.Interaction.InteractRadius)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, portalPos);
                StatusText = $"Walking to portal (dist: {portalDist:F0})";
                return;
            }

            ctx.Navigation.Stop(gc);
            ModeHelpers.ClickEntity(gc, portal, ref _lastActionTime);
            StatusText = "Clicking portal to exit";
        }

        // =====================================================================
        // Area-change handler
        // =====================================================================

        private void OnAreaChanged(BotContext ctx, string newArea)
        {
            var gc = ctx.Game;
            ModeHelpers.CancelAllSystems(ctx);
            _hideoutFlow.Cancel();
            _cachedPortalPos = null;

            if (gc.Area.CurrentArea.IsHideout || gc.Area.CurrentArea.IsTown)
            {
                _runsCompleted++;
                _consecutivePortalFailures = 0;
                _phase = LevelPhase.InHideout;
                _phaseStartTime = DateTime.Now;
                StartHideoutFlow(ctx);
                ctx.Log($"[Leveling] Run {_runsCompleted} complete — back in hideout");
                StatusText = $"Run {_runsCompleted} done — preparing next map";
            }
            else
            {
                // Entered a map — start clearing
                _phase = LevelPhase.ClearMap;
                _phaseStartTime = DateTime.Now;
                InitExploration(ctx);
                StatusText = "Entered map — clearing";
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private void StartHideoutFlow(BotContext ctx)
        {
            var farming = ctx.Settings.Farming;
            var mapTab = farming.MapTab?.Value ?? "";

            // Accept any map item that matches MapItemPath ("Maps/").
            // No scarabs, no stashing — pure XP farm.
            _hideoutFlow.Start(
                _ => true,                          // mapFilter: any map is fine
                stashItemFilter:      _ => true,    // keep everything; leveling mode never wants to stash
                stashItemThreshold:   int.MaxValue, // belt-and-suspenders: never trigger stash
                dumpTabName:          null,
                resourceTabName:      string.IsNullOrWhiteSpace(mapTab) ? null : mapTab,
                withdrawFragmentPath: MapItemPath,  // path substring for map items
                inventoryFragmentPath: MapItemPath, // check inventory for a map before going to stash
                fragmentStock:        1,
                minFragments:         1);
        }

        private void InitExploration(BotContext ctx)
        {
            var gc = ctx.Game;
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

    public enum LevelPhase
    {
        Idle,
        InHideout,
        ClearMap,
        ExitMap,
    }
}
