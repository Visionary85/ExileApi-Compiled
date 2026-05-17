using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;

namespace AutoExile.Systems
{
    /// <summary>
    /// Tracks simulacrum encounter state: monolith, portal, stash positions,
    /// wave state from monolith's StateMachine component, death counter.
    /// Entity IDs are cached and re-resolved each tick — never hold Entity references across ticks.
    /// All positions stored in grid coordinates.
    /// </summary>
    public class SimulacrumState
    {
        // Entity tracking — ID + grid position
        public long? MonolithId { get; private set; }
        public long? PortalId { get; private set; }
        public long? StashId { get; private set; }

        public Vector2? MonolithPosition { get; private set; }
        public Vector2? PortalPosition { get; private set; }
        public Vector2? StashPosition { get; private set; }

        // Wave state — read from monolith's StateMachine component
        // IsWaveActive: true when the 'active' state > 0 (goodbye is deliberately ignored —
        // the Mirage server initializes goodbye to a non-zero value before any wave starts,
        // which would make the original `active > 0 && goodbye == 0` formula always false).
        public bool IsWaveActive { get; private set; }

        // IsEncounterComplete: tracked internally by counting active→inactive transitions.
        // We do NOT use the 'goodbye' or 'wave' StateMachine values for this because both
        // are initialized to unexpected values on fresh entry in Mirage league (wave = 15,
        // goodbye may be > 0) that do not reflect actual encounter progress.
        public bool IsEncounterComplete { get; private set; }

        // WavesCompleted: number of waves that have actually been fought and won.
        // Incremented each time IsWaveActive transitions true → false after ≥ MinWaveActiveSeconds.
        public int WavesCompleted { get; private set; }
        public const int MaxWavesInEncounter = 15;

        // Minimum seconds a wave must be active to count as a real wave (guards against
        // brief flickers of the active state on monolith click registration).
        private const float MinWaveActiveSeconds = 2f;

        // Internal tracking for wave completion counting
        private bool _prevIsWaveActive;
        private DateTime _waveActiveStartedAt = DateTime.MinValue;

        public int CurrentWave { get; private set; }
        public DateTime WaveStartedAt { get; private set; } = DateTime.Now;
        public DateTime CanStartWaveAt { get; private set; } = DateTime.MinValue;

        // Run tracking
        public int DeathCount { get; set; }
        public int RunsCompleted { get; private set; }
        public int HighestWaveThisRun { get; private set; }
        public DateTime RunStartedAt { get; private set; } = DateTime.Now;

        // Run history for session stats
        public struct RunRecord
        {
            public int HighestWave;
            public TimeSpan Duration;
        }
        private readonly List<RunRecord> _runHistory = new();
        public IReadOnlyList<RunRecord> RunHistory => _runHistory;

        // Last valid monolith update — if stale >10s, assume wave inactive
        private DateTime _lastMonolithUpdate = DateTime.MinValue;

        // Position sanity
        private const float PositionSanityThreshold = 50f;

        /// <summary>
        /// Reset the wave timer to now. Call when bot is paused/resumed to prevent
        /// wall-clock time during pause from triggering wave timeout.
        /// </summary>
        public void ResetWaveTimer()
        {
            WaveStartedAt = DateTime.Now;
        }

        public void Reset()
        {
            MonolithId = null;
            PortalId = null;
            StashId = null;
            MonolithPosition = null;
            PortalPosition = null;
            StashPosition = null;
            IsWaveActive = false;
            IsEncounterComplete = false;
            WavesCompleted = 0;
            _prevIsWaveActive = false;
            _waveActiveStartedAt = DateTime.MinValue;
            CurrentWave = 0;
            WaveStartedAt = DateTime.Now;
            CanStartWaveAt = DateTime.MinValue;
            DeathCount = 0;
            HighestWaveThisRun = 0;
            RunStartedAt = DateTime.Now;
            _lastMonolithUpdate = DateTime.MinValue;
        }

        /// <summary>
        /// Restore monolith position and wave count after death re-entry so the bot
        /// navigates directly to the monolith instead of re-exploring the whole arena.
        /// Call immediately after OnAreaChanged() when re-entering after a death.
        /// </summary>
        public void RestoreForReentry(Vector2 monolithPosition, int wavesCompleted)
        {
            MonolithPosition = monolithPosition;
            WavesCompleted = wavesCompleted;
        }

        /// <summary>
        /// Call on area change to clear entity references but preserve run-level state.
        /// </summary>
        public void OnAreaChanged()
        {
            MonolithId = null;
            PortalId = null;
            StashId = null;
            MonolithPosition = null;
            PortalPosition = null;
            StashPosition = null;
            IsWaveActive = false;
            IsEncounterComplete = false;
            WavesCompleted = 0;
            _prevIsWaveActive = false;
            _waveActiveStartedAt = DateTime.MinValue;
            CurrentWave = 0;
            CanStartWaveAt = DateTime.MinValue;
            _lastMonolithUpdate = DateTime.MinValue;
        }

        public void RecordRunComplete()
        {
            _runHistory.Add(new RunRecord
            {
                HighestWave = HighestWaveThisRun,
                Duration = DateTime.Now - RunStartedAt,
            });
            RunsCompleted++;
            HighestWaveThisRun = 0;
        }

        public double AverageWavesPerRun => _runHistory.Count == 0 ? 0 :
            _runHistory.Average(r => r.HighestWave);

        public TimeSpan AverageRunDuration => _runHistory.Count == 0 ? TimeSpan.Zero :
            TimeSpan.FromSeconds(_runHistory.Average(r => r.Duration.TotalSeconds));

        /// <summary>
        /// Push the wave start timer forward. Called when loot is detected between waves
        /// so the full delay restarts after loot is cleared.
        /// </summary>
        public void ResetWaveDelay(float delaySeconds)
        {
            var newTime = DateTime.Now.AddSeconds(delaySeconds);
            if (newTime > CanStartWaveAt)
                CanStartWaveAt = newTime;
        }

        /// <summary>
        /// Tick entity tracking and wave state. Call every tick while in simulacrum map.
        /// </summary>
        public void Tick(GameController gc, float minWaveDelay)
        {
            // --- Track portal ---
            Entity? portal = ResolveById(gc, PortalId, EntityType.TownPortal);
            if (portal == null)
            {
                portal = gc.EntityListWrapper.ValidEntitiesByType[EntityType.TownPortal]
                    .OrderBy(e => e.DistancePlayer)
                    .FirstOrDefault();
                if (portal != null)
                    PortalId = portal.Id;
            }
            if (portal != null)
            {
                var freshPos = portal.GridPosNum;
                if (IsPositionSane(freshPos, PortalPosition))
                    PortalPosition = freshPos;
            }

            // --- Track monolith ---
            Entity? monolith = null;
            if (MonolithId.HasValue)
            {
                monolith = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Id == MonolithId.Value);
                if (monolith != null && !IsPositionSane(monolith.GridPosNum, MonolithPosition))
                    monolith = null;
            }
            if (monolith == null)
            {
                monolith = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Metadata?.Contains("Objects/Afflictionator") == true);
                if (monolith != null)
                    MonolithId = monolith.Id;
            }

            if (monolith != null)
            {
                var freshPos = monolith.GridPosNum;
                if (IsPositionSane(freshPos, MonolithPosition))
                    MonolithPosition = freshPos;

                if (monolith.TryGetComponent<StateMachine>(out var state))
                {
                    // Use only the 'active' state for wave detection.
                    // The 'goodbye' state is intentionally excluded: in Mirage league it is
                    // initialized to a non-zero value before any wave starts, which makes the
                    // original formula (active > 0 && goodbye == 0) always false.
                    var isActive = state.States.FirstOrDefault(s => s.Name == "active")?.Value > 0;
                    var wave = (int)(state.States.FirstOrDefault(s => s.Name == "wave")?.Value ?? 0);

                    // On first monolith contact this session, seed WavesCompleted from the
                    // StateMachine so the bot correctly resumes a mid-run restart.
                    // Guards:
                    //   _lastMonolithUpdate == MinValue  → truly first read this session
                    //   WavesCompleted == 0              → haven't counted any transitions yet
                    //   wave > 0                         → at least one wave has occurred
                    // The case wave==MaxWaves && !isActive is skipped: Mirage league initialises
                    // 'wave' to 15 before the encounter starts, making it indistinguishable from
                    // a legitimate wave-15 completion.
                    if (WavesCompleted == 0 && _lastMonolithUpdate == DateTime.MinValue && wave > 0)
                    {
                        if (isActive)
                        {
                            // Wave N is currently running → N-1 waves have been completed
                            WavesCompleted = Math.Max(0, wave - 1);
                        }
                        else if (wave < MaxWavesInEncounter)
                        {
                            // Paused between waves after wave N finished → N waves completed
                            WavesCompleted = wave;
                        }
                        // wave == MaxWavesInEncounter && !isActive: ambiguous pre-game state, skip

                        if (WavesCompleted > 0)
                        {
                            if (WavesCompleted > HighestWaveThisRun)
                                HighestWaveThisRun = WavesCompleted;
                            _prevIsWaveActive = isActive;
                            _waveActiveStartedAt = isActive ? DateTime.Now : DateTime.MinValue;
                        }
                    }

                    // Wave just ended — enforce delay before next start
                    if (IsWaveActive && !isActive)
                        CanStartWaveAt = DateTime.Now.AddSeconds(minWaveDelay);

                    // Wave number changed (informational — used for stats and exploration resets)
                    if (wave != CurrentWave)
                    {
                        WaveStartedAt = DateTime.Now;
                        if (wave > HighestWaveThisRun)
                            HighestWaveThisRun = wave;
                    }

                    // Count wave completions internally via active state transitions.
                    // IsEncounterComplete is set once we reach MaxWavesInEncounter completions.
                    if (!_prevIsWaveActive && isActive)
                    {
                        _waveActiveStartedAt = DateTime.Now;
                    }
                    else if (_prevIsWaveActive && !isActive)
                    {
                        var waveDuration = (DateTime.Now - _waveActiveStartedAt).TotalSeconds;
                        if (waveDuration >= MinWaveActiveSeconds)
                        {
                            WavesCompleted++;
                            if (WavesCompleted > HighestWaveThisRun)
                                HighestWaveThisRun = WavesCompleted;
                        }
                    }
                    _prevIsWaveActive = isActive;

                    IsWaveActive = isActive;
                    IsEncounterComplete = WavesCompleted >= MaxWavesInEncounter;
                    CurrentWave = wave;
                    _lastMonolithUpdate = DateTime.Now;
                }
            }
            else if (DateTime.Now > _lastMonolithUpdate.AddSeconds(10))
            {
                // Monolith out of range for too long — assume wave inactive
                IsWaveActive = false;
            }

            // --- Track stash (only search once — position is static) ---
            if (!StashPosition.HasValue)
            {
                Entity? stash = null;
                if (StashId.HasValue)
                {
                    stash = gc.EntityListWrapper.OnlyValidEntities
                        .FirstOrDefault(e => e.Id == StashId.Value);
                }
                if (stash == null)
                {
                    stash = gc.EntityListWrapper.OnlyValidEntities
                        .FirstOrDefault(e => e.Metadata?.Contains("Metadata/MiscellaneousObjects/Stash") == true);
                    if (stash != null)
                        StashId = stash.Id;
                }
                if (stash != null)
                {
                    var freshPos = stash.GridPosNum;
                    if (IsValidPosition(freshPos))
                        StashPosition = freshPos;
                }
            }
        }

        /// <summary>
        /// Resolve an entity by cached ID within a specific entity type.
        /// </summary>
        private Entity? ResolveById(GameController gc, long? id, EntityType type)
        {
            if (!id.HasValue) return null;
            return gc.EntityListWrapper.ValidEntitiesByType[type]
                .FirstOrDefault(e => e.Id == id.Value);
        }

        private static bool IsValidPosition(Vector2 pos)
        {
            if (pos == Vector2.Zero) return false;
            if (Math.Abs(pos.X) > 10000 || Math.Abs(pos.Y) > 10000) return false;
            return true;
        }

        private static bool IsPositionSane(Vector2 freshPos, Vector2? storedPos)
        {
            if (!IsValidPosition(freshPos)) return false;
            if (storedPos.HasValue && Vector2.Distance(freshPos, storedPos.Value) > PositionSanityThreshold)
                return false;
            return true;
        }

        /// <summary>
        /// Convert grid position to world coordinates for NavigateTo.
        /// </summary>
        public static Vector2 ToWorld(Vector2 gridPos) =>
            gridPos * Pathfinding.GridToWorld;

        /// <summary>
        /// Convert grid position to Vector3 world coordinates for WorldToScreen.
        /// </summary>
        public static Vector3 ToWorld3(Vector2 gridPos, float z) =>
            new(gridPos.X * Pathfinding.GridToWorld, gridPos.Y * Pathfinding.GridToWorld, z);
    }
}
