using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace SeaSim.Fish
{
    /// <summary>Tunable boid parameters, copied by value into the jobs every frame.</summary>
    [System.Serializable]
    public struct BoidSettings
    {
        [Header("Speed")]
        [Min(0f)] public float minSpeed;
        [Min(0f)] public float maxSpeed;
        [Min(0f)] public float maxSteerForce;

        [Header("Perception")]
        [Min(0.1f)] public float perceptionRadius;
        [Range(30f, 360f)] public float fieldOfView;
        [Tooltip("Max neighbours used for alignment and cohesion.")]
        [Range(1, 64)] public int maxNeighbours;
        [Tooltip("Max nearby fish inspected per fish for body avoidance (uses a fine grid, no blind spot).")]
        [Range(1, 128)] public int maxCandidates;

        [Header("Body (in fish lengths)")]
        [Tooltip("Radius of the capsule approximating the body, as a fraction of the fish length.")]
        [Range(0.01f, 0.5f)] public float bodyRadius;
        [Tooltip("Free space each fish tries to keep around its body, as a fraction of the fish length.")]
        [Range(0f, 2f)] public float personalSpace;
        [Tooltip("How much of an overlap between two bodies is pushed out each frame (0 = none, 1 = all).")]
        [Range(0f, 1f)] public float collisionStiffness;

        [Header("Weights")]
        public float separationWeight;
        public float alignmentWeight;
        public float cohesionWeight;
        public float wanderWeight;
        [Tooltip("Fish prefer swimming horizontally: damps vertical velocity.")]
        public float levelingWeight;
        public float boundsWeight;

        public static BoidSettings Default => new BoidSettings
        {
            minSpeed = 2.0f,
            maxSpeed = 4.5f,
            maxSteerForce = 7f,
            perceptionRadius = 2.2f,
            fieldOfView = 290f,
            maxNeighbours = 14,
            maxCandidates = 96,
            bodyRadius = 0.09f,
            personalSpace = 0.8f,
            collisionStiffness = 0.9f,
            separationWeight = 3.5f,
            alignmentWeight = 1.3f,
            cohesionWeight = 0.7f,
            wanderWeight = 0.6f,
            levelingWeight = 0.8f,
            boundsWeight = 3f,
        };
    }

    static class SpatialGrid
    {
        public static int3 Cell(float3 p, float invCellSize) => (int3)math.floor(p * invCellSize);
        public static int Hash(int3 cell) => (int)math.hash(cell);
        /// <summary>Maps 0,1,2 to 0,-1,+1 so the fish's own cell is visited first.</summary>
        public static int Order(int k) => k == 0 ? 0 : (k == 1 ? -1 : 1);
    }

    static class Capsule
    {
        /// <summary>Fraction of the fish length covered by the capsule's inner segment (head to tail root).</summary>
        public const float SegmentFraction = 0.8f;

        /// <summary>Closest points between segments [p1,q1] and [p2,q2] (Ericson, Real-Time Collision Detection).</summary>
        public static void ClosestPoints(float3 p1, float3 q1, float3 p2, float3 q2, out float3 c1, out float3 c2)
        {
            float3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = math.dot(d1, d1), e = math.dot(d2, d2), f = math.dot(d2, r);
            float c = math.dot(d1, r), b = math.dot(d1, d2);
            float denom = a * e - b * b;

            float s = denom > 1e-8f ? math.saturate((b * f - c * e) / denom) : 0f;
            float t = (b * s + f) / e;
            if (t < 0f) { t = 0f; s = math.saturate(-c / a); }
            else if (t > 1f) { t = 1f; s = math.saturate((b - c) / a); }

            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }
    }

    /// <summary>Inserts every fish into a spatial hash so neighbour queries only look at 27 cells.</summary>
    [BurstCompile]
    struct HashPositionsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> positions;
        public float invCellSize;
        public NativeParallelMultiHashMap<int, int>.ParallelWriter grid;

        public void Execute(int i)
        {
            grid.Add(SpatialGrid.Hash(SpatialGrid.Cell(positions[i], invCellSize)), i);
        }
    }

    /// <summary>
    /// Reynolds boids (separation / alignment / cohesion) + bounds, leveling and wander.
    /// Each fish is a capsule (a head-to-tail segment with a radius) rather than a point, so separation
    /// works on the real distance between bodies, and actual overlaps are pushed apart.
    /// Two spatial grids are used, one per interaction scale:
    /// - a coarse grid (cell = perception radius) for alignment / cohesion, where a sample of neighbours is enough;
    /// - a fine grid (cell = body interaction range) for body avoidance, so the closest fish are never missed,
    ///   even in very dense schools.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    struct SteerJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float3> velocities;
        [ReadOnly] public NativeArray<float> scales;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> grid;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> collisionGrid;
        [WriteOnly] public NativeArray<float3> newVelocities;
        [WriteOnly] public NativeArray<float3> corrections;

        public BoidSettings settings;
        public float invCellSize;
        public float invCollisionCellSize;
        public float3 boundsCenter;
        public float3 boundsExtents;
        public float boundsMargin;
        public float time;
        public float deltaTime;

        public void Execute(int i)
        {
            float3 p = positions[i];
            float3 v = velocities[i];
            float3 forward = math.normalizesafe(v, new float3(0, 0, 1));
            float lengthI = scales[i];
            float3 halfSegI = forward * (0.5f * Capsule.SegmentFraction * lengthI);

            float radiusSq = settings.perceptionRadius * settings.perceptionRadius;
            float cosHalfFov = math.cos(math.radians(settings.fieldOfView * 0.5f));

            float3 separation = 0, alignment = 0, cohesion = 0, correction = 0;

            // ---- Pass 1: flocking (alignment / cohesion) on the coarse grid, only for fish actually seen ----
            int flockCount = 0;
            int3 cell = SpatialGrid.Cell(p, invCellSize);
            for (int x = 0; x < 3 && flockCount < settings.maxNeighbours; x++)
            for (int y = 0; y < 3 && flockCount < settings.maxNeighbours; y++)
            for (int z = 0; z < 3 && flockCount < settings.maxNeighbours; z++)
            {
                int3 c = cell + new int3(SpatialGrid.Order(x), SpatialGrid.Order(y), SpatialGrid.Order(z));
                if (!grid.TryGetFirstValue(SpatialGrid.Hash(c), out int j, out var it)) continue;
                do
                {
                    if (j == i) continue;
                    float3 offset = positions[j] - p;
                    float distSq = math.lengthsq(offset);
                    if (distSq > radiusSq || distSq < 1e-10f) continue;
                    if (math.dot(forward, offset) < cosHalfFov * math.sqrt(distSq)) continue; // blind spot

                    alignment += velocities[j];
                    cohesion += positions[j];
                    flockCount++;
                } while (flockCount < settings.maxNeighbours && grid.TryGetNextValue(out j, ref it));
            }

            // ---- Pass 2: body avoidance (capsule vs capsule) on the fine grid, felt all around ----
            int candidates = 0;
            int3 fineCell = SpatialGrid.Cell(p, invCollisionCellSize);
            for (int x = 0; x < 3 && candidates < settings.maxCandidates; x++)
            for (int y = 0; y < 3 && candidates < settings.maxCandidates; y++)
            for (int z = 0; z < 3 && candidates < settings.maxCandidates; z++)
            {
                int3 c = fineCell + new int3(SpatialGrid.Order(x), SpatialGrid.Order(y), SpatialGrid.Order(z));
                if (!collisionGrid.TryGetFirstValue(SpatialGrid.Hash(c), out int j, out var it)) continue;
                do
                {
                    if (j == i) continue;
                    float3 offset = positions[j] - p;
                    float distSq = math.lengthsq(offset);
                    float lengthJ = scales[j];
                    float bodyRadii = settings.bodyRadius * (lengthI + lengthJ);
                    float avoidRange = bodyRadii + settings.personalSpace * 0.5f * (lengthI + lengthJ);
                    float reach = 0.5f * (lengthI + lengthJ) + avoidRange; // centres further apart can't interact
                    if (distSq > reach * reach) continue;
                    candidates++;

                    float3 fwdJ = math.normalizesafe(velocities[j], new float3(0, 0, 1));
                    float3 halfSegJ = fwdJ * (0.5f * Capsule.SegmentFraction * lengthJ);
                    Capsule.ClosestPoints(p - halfSegI, p + halfSegI, positions[j] - halfSegJ, positions[j] + halfSegJ,
                        out float3 onI, out float3 onJ);
                    float3 away = onI - onJ;
                    float gap = math.length(away);
                    float3 dir = gap > 1e-5f ? away / gap : math.normalizesafe(-offset, new float3(0, 1, 0));

                    if (gap < avoidRange)
                    {
                        // Steering: grows quickly as the free space between bodies shrinks.
                        float w = 1f - math.saturate((gap - bodyRadii) / math.max(avoidRange - bodyRadii, 1e-4f));
                        separation += dir * (w * w + (gap < bodyRadii ? 1f : 0f));
                    }
                    if (gap < bodyRadii)
                    {
                        // Hard constraint: each fish takes half of the overlap.
                        correction += dir * (bodyRadii - gap) * 0.5f;
                    }
                } while (candidates < settings.maxCandidates && collisionGrid.TryGetNextValue(out j, ref it));
            }

            float3 accel = 0;
            if (flockCount > 0)
            {
                accel += SteerTowards(v, alignment / flockCount) * settings.alignmentWeight;
                accel += SteerTowards(v, cohesion / flockCount - p) * settings.cohesionWeight;
            }
            if (math.lengthsq(separation) > 0f)
                accel += SteerTowards(v, separation) * settings.separationWeight * math.min(math.length(separation), 2f);

            // Soft containment: the push grows as the fish goes deeper into the margin.
            float3 local = p - boundsCenter;
            float3 inner = math.max(boundsExtents - boundsMargin, 0.01f);
            float3 over = math.max(math.abs(local) - inner, 0f) / math.max(boundsMargin, 0.01f);
            accel += -math.sign(local) * over * over * settings.boundsWeight * settings.maxSteerForce;

            // Fish swim mostly level.
            accel.y -= v.y * settings.levelingWeight;

            // Smooth, coherent wander driven by 4D simplex noise (unique per fish).
            float4 q = new float4(p * 0.08f, time * 0.15f + i * 0.37f);
            float3 wander = new float3(
                noise.snoise(q),
                noise.snoise(q + new float4(31.4f, 0, 0, 0)) * 0.35f,
                noise.snoise(q + new float4(0, 0, 71.9f, 0)));
            accel += wander * settings.wanderWeight * settings.maxSteerForce;

            accel = ClampLength(accel, settings.maxSteerForce);
            float3 nv = v + accel * deltaTime;
            float speed = math.length(nv);
            float3 dir2 = speed > 1e-5f ? nv / speed : forward;
            newVelocities[i] = dir2 * math.clamp(speed, settings.minSpeed, settings.maxSpeed);

            // Never move more than one body radius per frame to resolve overlaps (avoids jitter).
            corrections[i] = ClampLength(correction * settings.collisionStiffness, settings.bodyRadius * lengthI);
        }

        float3 SteerTowards(float3 velocity, float3 direction)
        {
            float3 desired = math.normalizesafe(direction) * settings.maxSpeed;
            return ClampLength(desired - velocity, settings.maxSteerForce);
        }

        static float3 ClampLength(float3 v, float max)
        {
            float lenSq = math.lengthsq(v);
            return lenSq > max * max ? v * (max * math.rsqrt(lenSq)) : v;
        }
    }

    /// <summary>Moves the fish and writes the per-instance matrices used for instanced rendering.</summary>
    [BurstCompile]
    struct IntegrateJob : IJobParallelFor
    {
        public NativeArray<float3> positions;
        public NativeArray<float3> velocities;
        [ReadOnly] public NativeArray<float3> newVelocities;
        [ReadOnly] public NativeArray<float3> corrections;
        [ReadOnly] public NativeArray<float> scales;
        [WriteOnly] public NativeArray<Matrix4x4> matrices;
        public float deltaTime;

        public void Execute(int i)
        {
            float3 oldV = velocities[i];
            float3 v = newVelocities[i];
            float3 p = positions[i] + v * deltaTime + corrections[i];
            positions[i] = p;
            velocities[i] = v;

            float3 forward = math.normalizesafe(v, new float3(0, 0, 1));
            // Bank slightly into turns (roll proportional to lateral acceleration).
            float3 right = math.normalizesafe(math.cross(new float3(0, 1, 0), forward), new float3(1, 0, 0));
            float lateral = math.dot(v - oldV, right) / math.max(deltaTime, 1e-4f);
            float roll = math.clamp(-lateral * 0.06f, -0.6f, 0.6f);
            quaternion rot = math.mul(quaternion.LookRotationSafe(forward, new float3(0, 1, 0)), quaternion.RotateZ(roll));

            matrices[i] = float4x4.TRS(p, rot, new float3(scales[i]));
        }
    }
}
