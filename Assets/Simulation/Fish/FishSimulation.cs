using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using SeaSim.Environment;
using UnityEngine;

namespace SeaSim.Fish
{
    /// <summary>
    /// Runs every fish of every <see cref="FishSchool"/> in one shared simulation, so fish of different species
    /// see each other: bodies never overlap whatever the species, schools only form within a species, schools of
    /// different species avoid each other, and alarm spreads across species.
    ///
    /// Data layout: all fish live in the same arrays. Each school owns a contiguous range [start, start + count),
    /// so its fish can be rendered with its own mesh and material in a few instanced draws. Each fish stores its
    /// species index (settings read by the jobs).
    /// Adding a species needs no code: create a <see cref="FishSpecies"/> asset and a <see cref="FishSchool"/>.
    ///
    /// Every fish swims in one volume, defined in a single place: the scene's <see cref="FishEnclave"/>.
    /// A FishSimulation is created automatically by the first FishSchool if the scene has none.
    /// </summary>
    [DefaultExecutionOrder(-10)]
    public class FishSimulation : MonoBehaviour
    {
        [Header("Swimming volume")]
        [Tooltip("The enclave whose swimming volume every fish shares. Found automatically when left empty.")]
        [SerializeField] FishEnclave enclave;
        [Tooltip("Depth (m) of the band inside the volume's edges where fish start turning back.")]
        [SerializeField, Min(0.1f)] float volumeMargin = 4f;

        bool missingEnclaveWarned;
        Bounds drawBounds;

        const int MaxInstancesPerDraw = 1023;
        const int MaxGridCells = 4_000_000;
        /// <summary>
        /// Grid cells per body reach (1 = 3x3x3 search). Measured: 2 (5x5x5 search with smaller cells) barely reduces
        /// the steering cost while the counting sort over 8x more cells costs more, so 1 is faster at every fish count.
        /// </summary>
        const int ReachCells = 1;
        static readonly int SwimStateId = Shader.PropertyToID("_FishSwimState");
        static readonly int InstanceOffsetId = Shader.PropertyToID("_FishInstanceOffset");

        static FishSimulation instance;

        /// <summary>The scene's simulation, created on demand.</summary>
        public static FishSimulation Instance
        {
            get
            {
                if (instance != null) return instance;
                instance = FindAnyObjectByType<FishSimulation>();
                if (instance == null) instance = new GameObject(nameof(FishSimulation)).AddComponent<FishSimulation>();
                return instance;
            }
        }

        readonly List<FishSchool> schools = new List<FishSchool>();
        readonly List<FishSpecies> speciesList = new List<FishSpecies>();
        readonly Dictionary<FishSpecies, Mesh> meshes = new Dictionary<FishSpecies, Mesh>();
        readonly List<MaterialPropertyBlock[]> schoolBatchProperties = new List<MaterialPropertyBlock[]>();
        bool dirty = true;
        int fishCount;

        // Per fish (original index)
        NativeArray<float3> positions, velocities, desiredDirections, corrections;
        NativeArray<float> scales, cruiseSpeeds, desiredSpeeds, panic, newPanic;
        NativeArray<int> fishSpecies;
        NativeArray<byte> beating;
        NativeArray<float4> swimStates;
        NativeArray<Matrix4x4> matrices;
        // Sorted spatial grid (see GridLayout): fish sorted by cell, with contiguous copies of their data.
        NativeArray<int> fishCell, order, cellStart, cellCursor;
        NativeArray<float3> sortedPositions, sortedVelocities;
        NativeArray<float> sortedScales, sortedPanic;
        NativeArray<int> sortedSpecies;
        // Per species
        NativeArray<SpeciesData> speciesData;

        GraphicsBuffer swimBuffer;
        // A rebuild replaces the swim buffer, but draws already queued this frame (e.g. by the editor's Scene view)
        // may still reference the old one: it is released one frame later.
        GraphicsBuffer retiredSwimBuffer;
        int retiredFrame;
        JobHandle handle;
        double scheduleTime;

        /// <summary>
        /// Wall-clock time (ms) between scheduling the simulation jobs (Update) and their completion (LateUpdate).
        /// It equals the simulation cost when the main thread has little else to do, e.g. in the benchmark scene.
        /// </summary>
        public float LastSimulationMilliseconds { get; private set; }
        public int FishCount => fishCount;
        public int SpeciesCount => speciesList.Count;
        public float VolumeMargin => volumeMargin;

        /// <summary>The enclave fish swim in (the one assigned, otherwise the scene's), or null if there is none.</summary>
        public FishEnclave Enclave
        {
            get
            {
                if (enclave == null && FishEnclave.Active.Count > 0) enclave = FishEnclave.Active[0];
                return enclave;
            }
        }

        /// <summary>The swimming volume shared by every fish (world space). False when the scene has no enclave.</summary>
        public bool TryGetSwimVolume(out Bounds volume)
        {
            var e = Enclave;
            volume = e != null ? e.SwimVolume : default;
            return e != null;
        }

        internal void Register(FishSchool school)
        {
            if (!schools.Contains(school)) schools.Add(school);
            dirty = true;
        }

        internal void Unregister(FishSchool school)
        {
            if (schools.Remove(school)) dirty = true;
        }

        internal void MarkDirty() => dirty = true;

        internal NativeArray<float3>.ReadOnly GetPositions(int start, int count) => Slice(positions, start, count);
        internal NativeArray<float3>.ReadOnly GetVelocities(int start, int count) => Slice(velocities, start, count);
        internal NativeArray<float>.ReadOnly GetPanic(int start, int count) => Slice(panic, start, count);

        NativeArray<T>.ReadOnly Slice<T>(NativeArray<T> array, int start, int count) where T : unmanaged
        {
            handle.Complete();
            if (!array.IsCreated || count <= 0 || start + count > array.Length)
                return new NativeArray<T>(0, Allocator.Temp).AsReadOnly(); // nothing simulated yet
            return array.GetSubArray(start, count).AsReadOnly();
        }

        void OnEnable()
        {
            if (instance == null) instance = this;
            dirty = true; // arrays are (re)built on the next Update
        }

        void OnDisable()
        {
            handle.Complete();
            DisposeFish();
            retiredSwimBuffer?.Release();
            retiredSwimBuffer = null;
            foreach (var mesh in meshes.Values) if (mesh != null) Destroy(mesh);
            meshes.Clear();
            if (instance == this) instance = null;
        }

        void Update()
        {
            if (!TryGetSwimVolume(out var swimVolume))
            {
                if (!missingEnclaveWarned) Debug.LogWarning("FishSimulation: the scene has no FishEnclave, so fish have no swimming volume and are not simulated.", this);
                missingEnclaveWarned = true;
                return;
            }
            missingEnclaveWarned = false;

            foreach (var school in schools) if (school.NeedsRebuild) dirty = true;
            if (dirty) Rebuild(swimVolume);
            if (fishCount == 0) return;

            scheduleTime = Time.realtimeSinceStartupAsDouble;
            float dt = math.min(Time.deltaTime, 1f / 30f);

            // Settings are re-read every frame so species and volumes can be tweaked live in Play Mode.
            float reach = 0.05f;
            for (int s = 0; s < speciesList.Count; s++)
            {
                var sp = speciesList[s];
                speciesData[s] = sp.ToData();
                // Body reach = largest distance at which two bodies can interact.
                float maxLength = sp.length * (1f + sp.sizeVariation);
                reach = math.max(reach, maxLength * (1f + 2f * sp.schooling.bodyRadius * math.max(1f, sp.schooling.bodyHeightRatio) + sp.schooling.personalSpace));
            }
            drawBounds = new Bounds(swimVolume.center, swimVolume.size + Vector3.one * 2f);
            var volumeData = new SwimVolumeData { center = swimVolume.center, extents = swimVolume.extents, margin = volumeMargin };

            var layout = GridLayout.Create(swimVolume.center, swimVolume.size, reach / ReachCells, MaxGridCells);
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
            var cellJob = new CellIndexJob { positions = positions, fishCell = fishCell, layout = layout }.Schedule(fishCount, 256);
            var sortJob = new CountingSortJob { fishCell = fishCell, cellStart = cellStart, cellCursor = cellCursor, order = order }.Schedule(cellJob);
            var gatherJob = new GatherSortedJob
            {
                order = order,
                positions = positions,
                velocities = velocities,
                scales = scales,
                panic = panic,
                species = fishSpecies,
                sortedPositions = sortedPositions,
                sortedVelocities = sortedVelocities,
                sortedScales = sortedScales,
                sortedPanic = sortedPanic,
                sortedSpecies = sortedSpecies,
            }.Schedule(fishCount, 256, sortJob);

            var steerJob = new SteerJob
            {
                order = order,
                cellStart = cellStart,
                sortedPositions = sortedPositions,
                sortedVelocities = sortedVelocities,
                sortedScales = sortedScales,
                sortedPanic = sortedPanic,
                sortedSpecies = sortedSpecies,
                cruiseSpeeds = cruiseSpeeds,
                species = speciesData,
                volume = volumeData,
                predators = predators,
                desiredDirections = desiredDirections,
                desiredSpeeds = desiredSpeeds,
                corrections = corrections,
                newPanic = newPanic,
                layout = layout,
                nearCells = nearCells,
                time = Time.time,
                deltaTime = dt,
            }.Schedule(fishCount, 32, gatherJob);
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
                fishSpecies = fishSpecies,
                volume = volumeData,
                species = speciesData,
                matrices = matrices,
                deltaTime = dt,
            }.Schedule(fishCount, 64, steerJob);

            // Let the jobs run on worker threads while the main thread does other work.
            JobHandle.ScheduleBatchedJobs();
        }

        void LateUpdate()
        {
            if (retiredSwimBuffer != null && Time.frameCount > retiredFrame)
            {
                retiredSwimBuffer.Release();
                retiredSwimBuffer = null;
            }
            if (fishCount == 0) return;
            handle.Complete();
            LastSimulationMilliseconds = (float)((Time.realtimeSinceStartupAsDouble - scheduleTime) * 1000.0);
            // The panic computed this frame becomes the input of the next one.
            (panic, newPanic) = (newPanic, panic);

            swimBuffer.SetData(swimStates);
            for (int p = 0; p < schools.Count; p++)
            {
                var school = schools[p];
                var material = school.Species.material;
                if (material == null || school.FishCount == 0) continue;
                if (!meshes.TryGetValue(school.Species, out var mesh) || mesh == null) continue;
                var batches = schoolBatchProperties[p];
                for (int b = 0; b < batches.Length; b++)
                {
                    int first = school.StartIndex + b * MaxInstancesPerDraw;
                    var rp = new RenderParams(material)
                    {
                        worldBounds = drawBounds,
                        shadowCastingMode = school.CastShadows,
                        receiveShadows = true,
                        layer = school.gameObject.layer,
                        matProps = batches[b],
                    };
                    int count = math.min(MaxInstancesPerDraw, school.StartIndex + school.FishCount - first);
                    Graphics.RenderMeshInstanced(rp, mesh, 0, matrices, count, first);
                }
            }
        }

        /// <summary>(Re)allocates every array and re-spawns all fish, after a school, a count or a species changed.</summary>
        void Rebuild(Bounds swimVolume)
        {
            handle.Complete();
            retiredSwimBuffer?.Release();
            retiredSwimBuffer = swimBuffer;
            retiredFrame = Time.frameCount;
            swimBuffer = null;
            DisposeFish();
            dirty = false;

            schools.RemoveAll(s => s == null || !s.isActiveAndEnabled || s.Species == null);
            speciesList.Clear();
            fishCount = 0;
            foreach (var school in schools)
            {
                if (!speciesList.Contains(school.Species)) speciesList.Add(school.Species);
                if (!meshes.ContainsKey(school.Species)) meshes[school.Species] = FishMeshBuilder.Build(school.Species);
                fishCount += school.FishCountSetting;
            }
            if (fishCount == 0) return;

            int n = fishCount;
            positions = new NativeArray<float3>(n, Allocator.Persistent);
            velocities = new NativeArray<float3>(n, Allocator.Persistent);
            desiredDirections = new NativeArray<float3>(n, Allocator.Persistent);
            corrections = new NativeArray<float3>(n, Allocator.Persistent);
            scales = new NativeArray<float>(n, Allocator.Persistent);
            cruiseSpeeds = new NativeArray<float>(n, Allocator.Persistent);
            desiredSpeeds = new NativeArray<float>(n, Allocator.Persistent);
            panic = new NativeArray<float>(n, Allocator.Persistent);
            newPanic = new NativeArray<float>(n, Allocator.Persistent);
            fishSpecies = new NativeArray<int>(n, Allocator.Persistent);
            beating = new NativeArray<byte>(n, Allocator.Persistent);
            swimStates = new NativeArray<float4>(n, Allocator.Persistent);
            matrices = new NativeArray<Matrix4x4>(n, Allocator.Persistent);
            fishCell = new NativeArray<int>(n, Allocator.Persistent);
            order = new NativeArray<int>(n, Allocator.Persistent);
            sortedPositions = new NativeArray<float3>(n, Allocator.Persistent);
            sortedVelocities = new NativeArray<float3>(n, Allocator.Persistent);
            sortedScales = new NativeArray<float>(n, Allocator.Persistent);
            sortedPanic = new NativeArray<float>(n, Allocator.Persistent);
            sortedSpecies = new NativeArray<int>(n, Allocator.Persistent);
            speciesData = new NativeArray<SpeciesData>(speciesList.Count, Allocator.Persistent);
            // Cell arrays are sized in Update, once the grid layout is known.
            swimBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, sizeof(float) * 4);

            schoolBatchProperties.Clear();
            int start = 0;
            for (int p = 0; p < schools.Count; p++)
            {
                var school = schools[p];
                int count = school.FishCountSetting;
                school.AssignRange(start, count);
                Spawn(school, swimVolume, speciesList.IndexOf(school.Species), start, count);

                int batches = (count + MaxInstancesPerDraw - 1) / MaxInstancesPerDraw;
                var properties = new MaterialPropertyBlock[batches];
                for (int b = 0; b < batches; b++)
                {
                    properties[b] = new MaterialPropertyBlock();
                    properties[b].SetBuffer(SwimStateId, swimBuffer);
                    properties[b].SetFloat(InstanceOffsetId, start + b * MaxInstancesPerDraw);
                }
                schoolBatchProperties.Add(properties);
                start += count;
            }
        }

        /// <summary>Spawns a school's fish in a few clusters, each with a shared heading, so schools are visible from frame one.</summary>
        void Spawn(FishSchool school, Bounds volume, int speciesIndex, int start, int count)
        {
            var species = school.Species;
            var swim = species.swimming;
            var rng = new Unity.Mathematics.Random(school.Seed == 0 ? 1u : school.Seed);
            float3 center = volume.center;
            float3 inner = math.max((float3)volume.extents - volumeMargin, 0.5f);
            int clusters = math.max(1, school.SpawnClusters);

            var clusterCenters = new float3[clusters];
            var clusterHeadings = new float3[clusters];
            for (int c = 0; c < clusters; c++)
            {
                clusterCenters[c] = center + rng.NextFloat3(-inner, inner) * 0.8f;
                var h = rng.NextFloat3Direction();
                h.y *= 0.2f;
                clusterHeadings[c] = math.normalizesafe(h);
            }

            for (int k = 0; k < count; k++)
            {
                int i = start + k;
                int c = k % clusters;
                float length = species.length * (1f + rng.NextFloat(-species.sizeVariation, species.sizeVariation));
                scales[i] = length;
                cruiseSpeeds[i] = swim.cruiseSpeed * length * (1f + rng.NextFloat(-swim.speedVariation, swim.speedVariation));
                positions[i] = clusterCenters[c] + rng.NextFloat3Direction() * rng.NextFloat(0f, 3f);
                velocities[i] = math.normalizesafe(clusterHeadings[c] + rng.NextFloat3Direction() * 0.3f) * cruiseSpeeds[i];
                fishSpecies[i] = speciesIndex;
                beating[i] = (byte)(rng.NextBool() ? 1 : 0);
                swimStates[i] = new float4(rng.NextFloat(0f, 6.2831853f), 1f, 0f, 0f);
                matrices[i] = Matrix4x4.TRS(positions[i], Quaternion.LookRotation(velocities[i]), Vector3.one * length);
            }
        }

        void DisposeFish()
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
            if (fishSpecies.IsCreated) fishSpecies.Dispose();
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
            if (sortedSpecies.IsCreated) sortedSpecies.Dispose();
            if (speciesData.IsCreated) speciesData.Dispose();
            swimBuffer?.Release();
            swimBuffer = null;
            fishCount = 0;
        }
    }
}
