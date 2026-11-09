using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace SeaSim.Fish
{
    /// <summary>
    /// A population of fish of one <see cref="FishSpecies"/>: how many, where they spawn and which volume they swim in.
    /// The fish themselves are simulated and rendered by the scene's <see cref="FishSimulation"/>, together with the
    /// fish of every other school, so different species interact (they avoid each other and never form one school).
    /// Use several FishSchool components (same or different species) to populate a scene.
    /// </summary>
    public class FishSchool : MonoBehaviour
    {
        static readonly List<FishSchool> active = new List<FishSchool>();
        /// <summary>All enabled schools (used by predators to find prey).</summary>
        public static IReadOnlyList<FishSchool> Active => active;

        [SerializeField] FishSpecies species;

        [Header("Population")]
        [SerializeField, Range(1, 50000)] int fishCount = 1500;
        [SerializeField] uint seed = 12345;
        [Tooltip("Number of clusters fish are spawned in (they merge or split freely afterwards, within their species).")]
        [SerializeField, Range(1, 32)] int spawnClusters = 6;

        [Header("Swimming volume (local space)")]
        [SerializeField] Vector3 volumeSize = new Vector3(40f, 12f, 40f);
        [SerializeField, Min(0.1f)] float volumeMargin = 4f;

        [Header("Rendering")]
        [SerializeField] ShadowCastingMode castShadows = ShadowCastingMode.On;

        int startIndex, simulatedCount;
        FishSpecies simulatedSpecies;

        public FishSpecies Species => species;
        /// <summary>Number of fish currently simulated for this school.</summary>
        public int FishCount => simulatedCount;
        internal int FishCountSetting => fishCount;
        internal int StartIndex => startIndex;
        public uint Seed => seed;
        public int SpawnClusters => spawnClusters;
        public float VolumeMargin => volumeMargin;
        public ShadowCastingMode CastShadows => castShadows;
        public Bounds Volume => new Bounds(transform.position, volumeSize);

        /// <summary>Simulation time of the whole simulation (all schools), see <see cref="FishSimulation.LastSimulationMilliseconds"/>.</summary>
        public float LastSimulationMilliseconds => FishSimulation.Instance.LastSimulationMilliseconds;

        /// <summary>Fish positions of this school (valid after the simulation's LateUpdate). Useful for cameras, predators, debugging.</summary>
        public NativeArray<float3>.ReadOnly Positions => FishSimulation.Instance.GetPositions(startIndex, simulatedCount);
        public NativeArray<float3>.ReadOnly Velocities => FishSimulation.Instance.GetVelocities(startIndex, simulatedCount);
        /// <summary>Per-fish panic level, 0 (calm) to 1 (fleeing).</summary>
        public NativeArray<float>.ReadOnly Panic => FishSimulation.Instance.GetPanic(startIndex, simulatedCount);

        /// <summary>Changes the number of fish; all schools are re-spawned on the next Update.</summary>
        public void SetFishCount(int count)
        {
            fishCount = math.max(1, count);
            FishSimulation.Instance.MarkDirty();
        }

        /// <summary>True when the inspector values no longer match what is simulated (count or species changed).</summary>
        internal bool NeedsRebuild => fishCount != simulatedCount || species != simulatedSpecies;

        internal void AssignRange(int start, int count)
        {
            startIndex = start;
            simulatedCount = count;
            simulatedSpecies = species;
        }

        void OnEnable()
        {
            if (species == null)
            {
                Debug.LogWarning($"{name}: no FishSpecies assigned, the school is disabled.", this);
                enabled = false;
                return;
            }
            active.Add(this);
            FishSimulation.Instance.Register(this);
        }

        void OnDisable()
        {
            active.Remove(this);
            simulatedCount = 0;
            // Don't create a simulation while the scene is being torn down.
            var simulation = FindAnyObjectByType<FishSimulation>();
            if (simulation != null) simulation.Unregister(this);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
            Gizmos.DrawWireCube(transform.position, volumeSize);
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.2f);
            Gizmos.DrawWireCube(transform.position, volumeSize - Vector3.one * volumeMargin * 2f);
        }
    }
}
