using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace SeaSim.Fish
{
    /// <summary>
    /// Simulates and renders a group of fish of one <see cref="FishSpecies"/>.
    /// Use one FishSchool per species to mix species in a scene.
    /// Simulation: boids on the C# Job System + Burst, with spatial hashing for neighbour search.
    /// Rendering: one procedural mesh drawn with GPU instancing; each fish's swim state (tail phase, effort)
    /// is uploaded to a GraphicsBuffer and read by the vertex shader.
    /// </summary>
    [DefaultExecutionOrder(0)]
    public class FishSchool : MonoBehaviour
    {
        const int MaxInstancesPerDraw = 1023;
        static readonly int SwimStateId = Shader.PropertyToID("_FishSwimState");
        static readonly int InstanceOffsetId = Shader.PropertyToID("_FishInstanceOffset");

        static readonly List<FishSchool> active = new List<FishSchool>();
        /// <summary>All enabled schools (used by predators to find prey).</summary>
        public static IReadOnlyList<FishSchool> Active => active;

        [SerializeField] FishSpecies species;

        [Header("Population")]
        [SerializeField, Range(1, 20000)] int fishCount = 1500;
        [SerializeField] uint seed = 12345;
        [Tooltip("Number of clusters fish are spawned in (they merge or split freely afterwards).")]
        [SerializeField, Range(1, 32)] int spawnClusters = 6;

        [Header("Swimming volume (local space)")]
        [SerializeField] Vector3 volumeSize = new Vector3(40f, 12f, 40f);
        [SerializeField, Min(0.1f)] float volumeMargin = 4f;

        [Header("Rendering")]
        [SerializeField] ShadowCastingMode castShadows = ShadowCastingMode.On;

        NativeArray<float3> positions, velocities, desiredDirections, corrections;
        NativeArray<float> scales, cruiseSpeeds, desiredSpeeds, panic, newPanic;
        NativeArray<byte> beating;
        NativeArray<float4> swimStates;
        NativeArray<Matrix4x4> matrices;
        // Sorted spatial grid (see GridLayout): fish sorted by cell, with contiguous copies of their data.
        const int MaxGridCells = 4_000_000;
        /// <summary>
        /// Grid cells per body reach (1 = 3x3x3 search). Measured: 2 (5x5x5 search with smaller cells) barely reduces
        /// the steering cost while the counting sort over 8x more cells costs more, so 1 is faster at every fish count.
        /// </summary>
        const int ReachCells = 1;
        NativeArray<int> fishCell, order, cellStart, cellCursor;
        NativeArray<float3> sortedPositions, sortedVelocities;
        NativeArray<float> sortedScales, sortedPanic;
        JobHandle handle;

        Mesh fishMesh;
        FishSpecies builtSpecies;
        GraphicsBuffer swimBuffer;
        MaterialPropertyBlock[] batchProperties;
        int allocatedCount;

        public FishSpecies Species => species;
        public int FishCount => allocatedCount;
        /// <summary>
        /// Wall-clock time (ms) between scheduling the simulation jobs (Update) and their completion (LateUpdate).
        /// It equals the simulation cost when the main thread has little else to do, e.g. in the benchmark scene.
        /// </summary>
        public float LastSimulationMilliseconds { get; private set; }
        double scheduleTime;

        /// <summary>Changes the number of fish; the school is re-spawned on the next Update.</summary>
        public void SetFishCount(int count) => fishCount = math.max(1, count);
        public Bounds Volume => new Bounds(transform.position, volumeSize);
        /// <summary>Fish positions (valid after this school's LateUpdate). Useful for cameras, predators, debugging.</summary>
        public NativeArray<float3>.ReadOnly Positions { get { handle.Complete(); return positions.AsReadOnly(); } }
        public NativeArray<float3>.ReadOnly Velocities { get { handle.Complete(); return velocities.AsReadOnly(); } }
        /// <summary>Per-fish panic level, 0 (calm) to 1 (fleeing).</summary>
        public NativeArray<float>.ReadOnly Panic { get { handle.Complete(); return panic.AsReadOnly(); } }

        void OnEnable()
        {
            if (species == null)
            {
                Debug.LogWarning($"{name}: no FishSpecies assigned, the school is disabled.", this);
                enabled = false;
                return;
            }
            BuildMesh();
            Allocate();
            active.Add(this);
        }

        void OnDisable()
        {
            active.Remove(this);
            handle.Complete();
            Dispose();
            if (fishMesh != null) Destroy(fishMesh);
            fishMesh = null;
        }

        void BuildMesh()
        {
            if (fishMesh != null) Destroy(fishMesh);
            fishMesh = FishMeshBuilder.Build(species.bodyShape);
            builtSpecies = species;
        }

        void Allocate()
        {
            int n = fishCount;
            allocatedCount = n;
            positions = new NativeArray<float3>(n, Allocator.Persistent);
            velocities = new NativeArray<float3>(n, Allocator.Persistent);
            desiredDirections = new NativeArray<float3>(n, Allocator.Persistent);
            corrections = new NativeArray<float3>(n, Allocator.Persistent);
            scales = new NativeArray<float>(n, Allocator.Persistent);
            cruiseSpeeds = new NativeArray<float>(n, Allocator.Persistent);
            desiredSpeeds = new NativeArray<float>(n, Allocator.Persistent);
            panic = new NativeArray<float>(n, Allocator.Persistent);
            newPanic = new NativeArray<float>(n, Allocator.Persistent);
            beating = new NativeArray<byte>(n, Allocator.Persistent);
            swimStates = new NativeArray<float4>(n, Allocator.Persistent);
            matrices = new NativeArray<Matrix4x4>(n, Allocator.Persistent);
            fishCell = new NativeArray<int>(n, Allocator.Persistent);
            order = new NativeArray<int>(n, Allocator.Persistent);
            sortedPositions = new NativeArray<float3>(n, Allocator.Persistent);
            sortedVelocities = new NativeArray<float3>(n, Allocator.Persistent);
            sortedScales = new NativeArray<float>(n, Allocator.Persistent);
            sortedPanic = new NativeArray<float>(n, Allocator.Persistent);
            // Cell arrays are sized in Update, once the grid layout is known.
            swimBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, sizeof(float) * 4);

            int batches = (n + MaxInstancesPerDraw - 1) / MaxInstancesPerDraw;
            batchProperties = new MaterialPropertyBlock[batches];
            for (int b = 0; b < batches; b++)
            {
                batchProperties[b] = new MaterialPropertyBlock();
                batchProperties[b].SetBuffer(SwimStateId, swimBuffer);
                batchProperties[b].SetFloat(InstanceOffsetId, b * MaxInstancesPerDraw);
            }

            var rng = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
            float3 center = transform.position;
            float3 inner = math.max((float3)volumeSize * 0.5f - volumeMargin, 0.5f);
            var swim = species.swimming;

            // Spawn in a few clusters, each with a shared heading, so schools are visible from frame one.
            var clusterCenters = new float3[spawnClusters];
            var clusterHeadings = new float3[spawnClusters];
            for (int c = 0; c < spawnClusters; c++)
            {
                clusterCenters[c] = center + rng.NextFloat3(-inner, inner) * 0.8f;
                var h = rng.NextFloat3Direction();
                h.y *= 0.2f;
                clusterHeadings[c] = math.normalizesafe(h);
            }

            for (int i = 0; i < n; i++)
            {
                int c = i % spawnClusters;
                float length = species.length * (1f + rng.NextFloat(-species.sizeVariation, species.sizeVariation));
                scales[i] = length;
                cruiseSpeeds[i] = swim.cruiseSpeed * length * (1f + rng.NextFloat(-swim.speedVariation, swim.speedVariation));
                positions[i] = clusterCenters[c] + rng.NextFloat3Direction() * rng.NextFloat(0f, 3f);
                velocities[i] = math.normalizesafe(clusterHeadings[c] + rng.NextFloat3Direction() * 0.3f) * cruiseSpeeds[i];
                beating[i] = (byte)(rng.NextBool() ? 1 : 0);
                swimStates[i] = new float4(rng.NextFloat(0f, 6.2831853f), 1f, 0f, 0f);
                matrices[i] = Matrix4x4.TRS(positions[i], Quaternion.LookRotation(velocities[i]), Vector3.one * length);
            }
        }

        void Dispose()
        {
            if (positions.IsCreated) positions.Dispose();
            if (velocities.IsCreated) velocities.Dispose();
            if (desiredDirections.IsCreated) desiredDirections.Dispose();
            if (corrections.IsCreated) corrections.Dispose();
            if (scales.IsCreated) scales.Dispose();
            if (cruiseSpeeds.IsCreated) cruiseSpeeds.Dispose();
            if (desiredSpeeds.IsCreated) desiredSpeeds.Dispose();
            if (panic.IsCreated) panic.Dispose();
            if (newPanic.IsCreated) newPanic.Dispose();
            if (beating.IsCreated) beating.Dispose();
            if (swimStates.IsCreated) swimStates.Dispose();
            if (matrices.IsCreated) matrices.Dispose();
            if (fishCell.IsCreated) fishCell.Dispose();
            if (order.IsCreated) order.Dispose();
            if (cellStart.IsCreated) cellStart.Dispose();
            if (cellCursor.IsCreated) cellCursor.Dispose();
            if (sortedPositions.IsCreated) sortedPositions.Dispose();
            if (sortedVelocities.IsCreated) sortedVelocities.Dispose();
            if (sortedScales.IsCreated) sortedScales.Dispose();
            if (sortedPanic.IsCreated) sortedPanic.Dispose();
            swimBuffer?.Release();
            swimBuffer = null;
        }

        void Update()
        {
            // Allow live changes of the fish count or species in Play Mode.
            if (fishCount != allocatedCount || species != builtSpecies)
            {
                handle.Complete();
                Dispose();
                if (species != builtSpecies) BuildMesh();
                Allocate();
            }

            scheduleTime = Time.realtimeSinceStartupAsDouble;
            var boids = species.schooling;
            float dt = math.min(Time.deltaTime, 1f / 30f);
            // Body reach = largest distance at which two bodies can interact. Cells are a fraction of it, and the
            // search covers ReachCells cells on each side: smaller cells hug the search sphere more tightly.
            float maxLength = species.length * (1f + species.sizeVariation);
            float reach = math.max(maxLength * (1f + 2f * boids.bodyRadius + boids.personalSpace), 0.05f);
            var layout = GridLayout.Create(transform.position, volumeSize, reach / ReachCells, MaxGridCells);
            int nearCells = (int)math.ceil(reach * layout.invCellSize - 1e-4f);
            if (!cellStart.IsCreated || cellCursor.Length != layout.CellCount)
            {
                if (cellStart.IsCreated) cellStart.Dispose();
                if (cellCursor.IsCreated) cellCursor.Dispose();
                cellStart = new NativeArray<int>(layout.CellCount + 1, Allocator.Persistent);
                cellCursor = new NativeArray<int>(layout.CellCount, Allocator.Persistent);
            }

            // Predators known this frame (their positions were updated last LateUpdate).
            var predatorList = FishPredator.Active;
            var predators = new NativeArray<PredatorState>(predatorList.Count, Allocator.TempJob);
            for (int p = 0; p < predatorList.Count; p++) predators[p] = predatorList[p].State;

            // Sort the fish by grid cell: cell index (parallel) -> counting sort (single) -> gather (parallel).
            var cellJob = new CellIndexJob
            {
                positions = positions,
                fishCell = fishCell,
                layout = layout,
            }.Schedule(allocatedCount, 256);
            var sortJob = new CountingSortJob
            {
                fishCell = fishCell,
                cellStart = cellStart,
                cellCursor = cellCursor,
                order = order,
            }.Schedule(cellJob);
            var gatherJob = new GatherSortedJob
            {
                order = order,
                positions = positions,
                velocities = velocities,
                scales = scales,
                panic = panic,
                sortedPositions = sortedPositions,
                sortedVelocities = sortedVelocities,
                sortedScales = sortedScales,
                sortedPanic = sortedPanic,
            }.Schedule(allocatedCount, 256, sortJob);

            var steerJob = new SteerJob
            {
                order = order,
                cellStart = cellStart,
                sortedPositions = sortedPositions,
                sortedVelocities = sortedVelocities,
                sortedScales = sortedScales,
                sortedPanic = sortedPanic,
                cruiseSpeeds = cruiseSpeeds,
                predators = predators,
                desiredDirections = desiredDirections,
                desiredSpeeds = desiredSpeeds,
                corrections = corrections,
                newPanic = newPanic,
                layout = layout,
                nearCells = nearCells,
                boids = boids,
                swim = species.swimming,
                escape = species.escape,
                boundsCenter = transform.position,
                boundsExtents = (float3)volumeSize * 0.5f,
                boundsMargin = volumeMargin,
                time = Time.time,
                deltaTime = dt,
            }.Schedule(allocatedCount, 32, gatherJob);
            predators.Dispose(steerJob);

            handle = new MotionJob
            {
                positions = positions,
                velocities = velocities,
                beating = beating,
                swimStates = swimStates,
                desiredDirections = desiredDirections,
                desiredSpeeds = desiredSpeeds,
                corrections = corrections,
                scales = scales,
                panic = newPanic,
                matrices = matrices,
                swim = species.swimming,
                deltaTime = dt,
            }.Schedule(allocatedCount, 64, steerJob);

            // Let the jobs run on worker threads while the main thread does other work.
            JobHandle.ScheduleBatchedJobs();
        }

        void LateUpdate()
        {
            handle.Complete();
            LastSimulationMilliseconds = (float)((Time.realtimeSinceStartupAsDouble - scheduleTime) * 1000.0);
            // The panic computed this frame becomes the input of the next one.
            (panic, newPanic) = (newPanic, panic);

            var material = species != null ? species.material : null;
            if (material == null || fishMesh == null) return;

            swimBuffer.SetData(swimStates);
            for (int b = 0, start = 0; start < allocatedCount; b++, start += MaxInstancesPerDraw)
            {
                var rp = new RenderParams(material)
                {
                    worldBounds = new Bounds(transform.position, volumeSize + Vector3.one * 2f),
                    shadowCastingMode = castShadows,
                    receiveShadows = true,
                    layer = gameObject.layer,
                    matProps = batchProperties[b],
                };
                Graphics.RenderMeshInstanced(rp, fishMesh, 0, matrices, math.min(MaxInstancesPerDraw, allocatedCount - start), start);
            }
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
