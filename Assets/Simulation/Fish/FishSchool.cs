using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace SeaSim.Fish
{
    /// <summary>
    /// Simulates and renders a school of fish.
    /// Simulation: Reynolds boids on the C# Job System + Burst, with a spatial hash for neighbour search.
    /// Rendering: one procedural mesh drawn with GPU instancing; the swimming motion is done in the vertex shader.
    /// </summary>
    public class FishSchool : MonoBehaviour
    {
        const int MaxInstancesPerDraw = 1023;

        [Header("Population")]
        [SerializeField, Range(1, 20000)] int fishCount = 1500;
        [SerializeField] uint seed = 12345;
        [Tooltip("Number of clusters fish are spawned in (they merge or split freely afterwards).")]
        [SerializeField, Range(1, 32)] int spawnClusters = 6;

        [Header("Rendering")]
        [SerializeField] Material fishMaterial;
        [Tooltip("Fish length in metres.")]
        [SerializeField, Min(0.01f)] float fishLength = 0.3f;
        [SerializeField, Range(0f, 0.5f)] float sizeVariation = 0.2f;
        [SerializeField] ShadowCastingMode castShadows = ShadowCastingMode.On;

        [Header("Swimming volume (local space)")]
        [SerializeField] Vector3 volumeSize = new Vector3(40f, 12f, 40f);
        [SerializeField, Min(0.1f)] float volumeMargin = 4f;

        [Header("Behaviour")]
        [SerializeField] BoidSettings settings = BoidSettings.Default;

        NativeArray<float3> positions, velocities, newVelocities, corrections;
        NativeArray<float> scales;
        NativeArray<Matrix4x4> matrices;
        NativeParallelMultiHashMap<int, int> grid, collisionGrid;
        JobHandle handle;
        Mesh fishMesh;
        int allocatedCount;

        public int FishCount => allocatedCount;
        /// <summary>Fish positions (valid after LateUpdate). Useful for cameras, predators, debugging.</summary>
        public NativeArray<float3>.ReadOnly Positions { get { handle.Complete(); return positions.AsReadOnly(); } }
        public NativeArray<float3>.ReadOnly Velocities { get { handle.Complete(); return velocities.AsReadOnly(); } }

        void OnEnable()
        {
            fishMesh = FishMeshBuilder.Build();
            Allocate();
        }

        void OnDisable()
        {
            handle.Complete();
            Dispose();
            if (fishMesh != null) Destroy(fishMesh);
        }

        void Allocate()
        {
            allocatedCount = fishCount;
            positions = new NativeArray<float3>(fishCount, Allocator.Persistent);
            velocities = new NativeArray<float3>(fishCount, Allocator.Persistent);
            newVelocities = new NativeArray<float3>(fishCount, Allocator.Persistent);
            corrections = new NativeArray<float3>(fishCount, Allocator.Persistent);
            scales = new NativeArray<float>(fishCount, Allocator.Persistent);
            matrices = new NativeArray<Matrix4x4>(fishCount, Allocator.Persistent);
            grid = new NativeParallelMultiHashMap<int, int>(fishCount, Allocator.Persistent);
            collisionGrid = new NativeParallelMultiHashMap<int, int>(fishCount, Allocator.Persistent);

            var rng = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
            float3 center = transform.position;
            float3 inner = math.max((float3)volumeSize * 0.5f - volumeMargin, 0.5f);

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

            for (int i = 0; i < fishCount; i++)
            {
                int c = i % spawnClusters;
                positions[i] = clusterCenters[c] + rng.NextFloat3Direction() * rng.NextFloat(0f, 3f);
                velocities[i] = math.normalizesafe(clusterHeadings[c] + rng.NextFloat3Direction() * 0.3f)
                                * math.lerp(settings.minSpeed, settings.maxSpeed, rng.NextFloat());
                scales[i] = fishLength * (1f + rng.NextFloat(-sizeVariation, sizeVariation));
                matrices[i] = Matrix4x4.TRS(positions[i], Quaternion.LookRotation(velocities[i]), Vector3.one * scales[i]);
            }
        }

        void Dispose()
        {
            if (positions.IsCreated) positions.Dispose();
            if (velocities.IsCreated) velocities.Dispose();
            if (newVelocities.IsCreated) newVelocities.Dispose();
            if (corrections.IsCreated) corrections.Dispose();
            if (scales.IsCreated) scales.Dispose();
            if (matrices.IsCreated) matrices.Dispose();
            if (grid.IsCreated) grid.Dispose();
            if (collisionGrid.IsCreated) collisionGrid.Dispose();
        }

        void Update()
        {
            // Allow live tweaking of the fish count in Play Mode.
            if (fishCount != allocatedCount)
            {
                Dispose();
                Allocate();
            }

            float dt = math.min(Time.deltaTime, 1f / 30f);
            float cellSize = math.max(settings.perceptionRadius, 0.1f);
            // Fine grid cell = largest distance at which two bodies can interact, so 27 cells are always enough.
            float maxLength = fishLength * (1f + sizeVariation);
            float collisionCellSize = math.max(maxLength * (1f + 2f * settings.bodyRadius + settings.personalSpace), 0.05f);

            grid.Clear();
            collisionGrid.Clear();
            var hashJob = new HashPositionsJob
            {
                positions = positions,
                invCellSize = 1f / cellSize,
                grid = grid.AsParallelWriter(),
            }.Schedule(allocatedCount, 64);
            var collisionHashJob = new HashPositionsJob
            {
                positions = positions,
                invCellSize = 1f / collisionCellSize,
                grid = collisionGrid.AsParallelWriter(),
            }.Schedule(allocatedCount, 64);

            var steerJob = new SteerJob
            {
                positions = positions,
                velocities = velocities,
                scales = scales,
                grid = grid,
                collisionGrid = collisionGrid,
                newVelocities = newVelocities,
                corrections = corrections,
                settings = settings,
                invCellSize = 1f / cellSize,
                invCollisionCellSize = 1f / collisionCellSize,
                boundsCenter = transform.position,
                boundsExtents = (float3)volumeSize * 0.5f,
                boundsMargin = volumeMargin,
                time = Time.time,
                deltaTime = dt,
            }.Schedule(allocatedCount, 32, JobHandle.CombineDependencies(hashJob, collisionHashJob));

            handle = new IntegrateJob
            {
                positions = positions,
                velocities = velocities,
                newVelocities = newVelocities,
                corrections = corrections,
                scales = scales,
                matrices = matrices,
                deltaTime = dt,
            }.Schedule(allocatedCount, 64, steerJob);

            // Let the jobs run on worker threads while the main thread does other work.
            JobHandle.ScheduleBatchedJobs();
        }

        void LateUpdate()
        {
            handle.Complete();
            if (fishMaterial == null || fishMesh == null) return;

            var rp = new RenderParams(fishMaterial)
            {
                worldBounds = new Bounds(transform.position, volumeSize + Vector3.one * 2f),
                shadowCastingMode = castShadows,
                receiveShadows = true,
                layer = gameObject.layer,
            };
            for (int start = 0; start < allocatedCount; start += MaxInstancesPerDraw)
            {
                int n = math.min(MaxInstancesPerDraw, allocatedCount - start);
                Graphics.RenderMeshInstanced(rp, fishMesh, 0, matrices, n, start);
            }
        }

        void OnValidate()
        {
            settings.maxSpeed = math.max(settings.maxSpeed, settings.minSpeed);
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
