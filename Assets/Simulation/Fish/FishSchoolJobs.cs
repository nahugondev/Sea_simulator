using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace SeaSim.Fish
{
    /// <summary>What fish need to know about a predator (filled each frame from every active <see cref="FishPredator"/>).</summary>
    public struct PredatorState
    {
        public float3 position;
        public float3 velocity;
        public float length;
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
    /// Decides where each fish wants to go and how fast (it does not move it: see <see cref="MotionJob"/>).
    /// - Topological schooling: alignment / cohesion use the k nearest visible neighbours, found with a two-scale
    ///   search (fine grid first, coarse grid only when the fish is in a sparse area).
    /// - Bodies are capsules: separation uses the real distance between bodies, and overlaps are pushed apart.
    /// - Predators: fish that see one flee sideways ('fountain effect'), and panic spreads from neighbour to
    ///   neighbour, so an escape wave travels through the school.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    struct SteerJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float3> velocities;
        [ReadOnly] public NativeArray<float> scales;
        [ReadOnly] public NativeArray<float> cruiseSpeeds;
        [ReadOnly] public NativeArray<float> panic;
        [ReadOnly] public NativeArray<PredatorState> predators;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> grid;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> collisionGrid;

        [WriteOnly] public NativeArray<float3> desiredDirections;
        [WriteOnly] public NativeArray<float> desiredSpeeds;
        [WriteOnly] public NativeArray<float3> corrections;
        [WriteOnly] public NativeArray<float> newPanic;

        public BoidSettings boids;
        public SwimSettings swim;
        public EscapeSettings escape;
        public float invCellSize;
        public float collisionCellSize;
        public float3 boundsCenter;
        public float3 boundsExtents;
        public float boundsMargin;
        public float time;
        public float deltaTime;

        public void Execute(int i)
        {
            float3 p = positions[i];
            float3 v = velocities[i];
            float speed = math.length(v);
            float3 forward = speed > 1e-5f ? v / speed : new float3(0, 0, 1);
            float lengthI = scales[i];
            float3 halfSegI = forward * (0.5f * Capsule.SegmentFraction * lengthI);
            float cosHalfFov = math.cos(math.radians(boids.fieldOfView * 0.5f));
            int k = math.clamp(boids.topologicalNeighbours, 1, 16);

            var neighbourIds = new FixedList128Bytes<int>();
            var neighbourDistSq = new FixedList128Bytes<float>();
            float3 separation = 0, correction = 0;

            // ---- Pass 1, fine grid: body avoidance (all around) + nearest visible neighbours ----
            float fineRadiusSq = collisionCellSize * collisionCellSize;
            float invFine = 1f / collisionCellSize;
            int3 fineCell = SpatialGrid.Cell(p, invFine);
            int candidates = 0;
            for (int x = 0; x < 3 && candidates < boids.maxCandidates; x++)
            for (int y = 0; y < 3 && candidates < boids.maxCandidates; y++)
            for (int z = 0; z < 3 && candidates < boids.maxCandidates; z++)
            {
                int3 c = fineCell + new int3(SpatialGrid.Order(x), SpatialGrid.Order(y), SpatialGrid.Order(z));
                if (!collisionGrid.TryGetFirstValue(SpatialGrid.Hash(c), out int j, out var it)) continue;
                do
                {
                    if (j == i) continue;
                    float3 offset = positions[j] - p;
                    float distSq = math.lengthsq(offset);
                    if (distSq > fineRadiusSq || distSq < 1e-10f) continue;
                    candidates++;

                    AvoidBody(i, j, p, halfSegI, lengthI, offset, distSq, ref separation, ref correction);
                    if (CanEnter(ref neighbourDistSq, k, distSq) && IsVisible(forward, offset, distSq, cosHalfFov))
                        InsertNearest(ref neighbourIds, ref neighbourDistSq, k, j, distSq);
                } while (candidates < boids.maxCandidates && collisionGrid.TryGetNextValue(out j, ref it));
            }

            // ---- Pass 2, coarse grid: only for fish in a sparse area that still lack neighbours ----
            if (neighbourIds.Length < k)
            {
                float perceptionSq = boids.perceptionRadius * boids.perceptionRadius;
                int3 cell = SpatialGrid.Cell(p, invCellSize);
                int scanned = 0;
                for (int x = 0; x < 3 && scanned < boids.maxCandidates; x++)
                for (int y = 0; y < 3 && scanned < boids.maxCandidates; y++)
                for (int z = 0; z < 3 && scanned < boids.maxCandidates; z++)
                {
                    int3 c = cell + new int3(SpatialGrid.Order(x), SpatialGrid.Order(y), SpatialGrid.Order(z));
                    if (!grid.TryGetFirstValue(SpatialGrid.Hash(c), out int j, out var it)) continue;
                    do
                    {
                        if (j == i) continue;
                        float3 offset = positions[j] - p;
                        float distSq = math.lengthsq(offset);
                        // Fish within the fine radius were already considered in pass 1.
                        scanned++; // every visited entry counts, so the cost stays bounded in dense areas
                        if (distSq <= fineRadiusSq || distSq > perceptionSq) continue;
                        if (CanEnter(ref neighbourDistSq, k, distSq) && IsVisible(forward, offset, distSq, cosHalfFov))
                            InsertNearest(ref neighbourIds, ref neighbourDistSq, k, j, distSq);
                    } while (scanned < boids.maxCandidates && grid.TryGetNextValue(out j, ref it));
                }
            }

            // ---- Neighbour averages ----
            float3 alignment = 0, cohesion = 0;
            float neighbourSpeed = 0, neighbourPanic = 0;
            int count = neighbourIds.Length;
            for (int n = 0; n < count; n++)
            {
                int j = neighbourIds[n];
                float3 vj = velocities[j];
                float sj = math.length(vj);
                alignment += sj > 1e-5f ? vj / sj : 0f;
                cohesion += positions[j];
                neighbourSpeed += sj;
                neighbourPanic = math.max(neighbourPanic, panic[j]);
            }

            // ---- Predators: direct detection + 'fountain' escape direction ----
            float alarm = 0f;
            float3 flee = 0;
            for (int n = 0; n < predators.Length; n++)
            {
                var pr = predators[n];
                float3 d = p - pr.position;
                float dist = math.length(d);
                float detect = escape.alarmDistance * lengthI + pr.length * 0.5f;
                if (dist >= detect || dist < 1e-4f) continue;

                float closeness = 1f - dist / detect;
                alarm = math.max(alarm, math.saturate(closeness * 3f));
                float3 away = d / dist;
                float3 predForward = math.normalizesafe(pr.velocity, -away);
                // Escape sideways relative to the predator's path, rather than straight ahead of it.
                float3 lateral = math.normalizesafe(away - predForward * math.dot(away, predForward), away);
                flee += math.normalizesafe(away + lateral * 1.5f) * closeness;
            }

            // ---- Panic: rises fast when threatened or when neighbours panic, fades slowly ----
            float oldPanic = panic[i];
            float target = math.max(alarm, neighbourPanic * escape.alarmTransmission);
            float p01 = target > oldPanic
                ? math.lerp(oldPanic, target, math.saturate(deltaTime * escape.alarmSpreadRate))
                : math.max(target, oldPanic - deltaTime / escape.panicDecayTime);
            newPanic[i] = p01;

            // ---- Desired speed: own cruising speed, matched to neighbours, boosted by panic ----
            float burst = swim.burstSpeed * lengthI;
            float desiredSpeed = math.lerp(cruiseSpeeds[i], burst, p01);
            if (count > 0)
                desiredSpeed = math.lerp(desiredSpeed, neighbourSpeed / count, boids.speedMatching * (1f - p01));
            desiredSpeed = math.clamp(desiredSpeed, swim.minSpeed * lengthI, burst);

            // ---- Steering ----
            float maxSteer = boids.maxSteerForce * lengthI * (1f + 2f * p01);
            float3 accel = 0;
            if (count > 0)
            {
                accel += SteerTowards(v, alignment, desiredSpeed, maxSteer) * boids.alignmentWeight * (1f + p01);
                accel += SteerTowards(v, cohesion / count - p, desiredSpeed, maxSteer)
                         * boids.cohesionWeight * (1f - escape.cohesionDrop * p01);
            }
            if (math.lengthsq(separation) > 0f)
                accel += SteerTowards(v, separation, desiredSpeed, maxSteer) * boids.separationWeight * math.min(math.length(separation), 2f);
            if (math.lengthsq(flee) > 0f)
                accel += SteerTowards(v, flee, burst, maxSteer) * escape.fleeWeight;

            // Soft containment: the push grows as the fish goes deeper into the margin.
            float3 local = p - boundsCenter;
            float3 inner = math.max(boundsExtents - boundsMargin, 0.01f);
            float3 over = math.max(math.abs(local) - inner, 0f) / math.max(boundsMargin, 0.01f);
            accel += -math.sign(local) * over * over * boids.boundsWeight * maxSteer;

            // Fish swim mostly level.
            accel.y -= v.y * boids.levelingWeight;

            // Smooth, coherent wander driven by 4D simplex noise (unique per fish).
            float4 q = new float4(p * 0.08f, time * 0.15f + i * 0.37f);
            float3 wander = new float3(
                noise.snoise(q),
                noise.snoise(q + new float4(31.4f, 0, 0, 0)) * 0.35f,
                noise.snoise(q + new float4(0, 0, 71.9f, 0)));
            accel += wander * boids.wanderWeight * boids.maxSteerForce * lengthI;

            accel = ClampLength(accel, maxSteer);
            desiredDirections[i] = math.normalizesafe(v + accel * deltaTime, forward);
            desiredSpeeds[i] = desiredSpeed;

            // Never move more than one body radius per frame to resolve overlaps (avoids jitter).
            corrections[i] = ClampLength(correction * boids.collisionStiffness, boids.bodyRadius * lengthI);
        }

        void AvoidBody(int i, int j, float3 p, float3 halfSegI, float lengthI, float3 offset, float distSq,
            ref float3 separation, ref float3 correction)
        {
            float lengthJ = scales[j];
            float bodyRadii = boids.bodyRadius * (lengthI + lengthJ);
            float avoidRange = bodyRadii + boids.personalSpace * 0.5f * (lengthI + lengthJ);
            float reach = 0.5f * (lengthI + lengthJ) + avoidRange; // centres further apart can't interact
            if (distSq > reach * reach) return;

            float3 fwdJ = math.normalizesafe(velocities[j], new float3(0, 0, 1));
            float3 halfSegJ = fwdJ * (0.5f * Capsule.SegmentFraction * lengthJ);
            float3 pj = p + offset;
            Capsule.ClosestPoints(p - halfSegI, p + halfSegI, pj - halfSegJ, pj + halfSegJ, out float3 onI, out float3 onJ);
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
                correction += dir * (bodyRadii - gap) * 0.5f; // each fish takes half of the overlap
        }

        /// <summary>Cheap test before the visibility check: would this distance make it into the k nearest?</summary>
        static bool CanEnter(ref FixedList128Bytes<float> distSq, int k, float d)
            => distSq.Length < k || d < distSq[distSq.Length - 1];

        static bool IsVisible(float3 forward, float3 offset, float distSq, float cosHalfFov)
            => math.dot(forward, offset) >= cosHalfFov * math.sqrt(distSq);

        /// <summary>Keeps the k nearest candidates, sorted by distance (insertion sort, k ≤ 16).</summary>
        static void InsertNearest(ref FixedList128Bytes<int> ids, ref FixedList128Bytes<float> distSq, int k, int j, float d)
        {
            if (ids.Length < k)
            {
                ids.Add(j);
                distSq.Add(d);
            }
            else if (d < distSq[ids.Length - 1])
            {
                ids[ids.Length - 1] = j;
                distSq[ids.Length - 1] = d;
            }
            else return;

            for (int m = ids.Length - 1; m > 0 && distSq[m] < distSq[m - 1]; m--)
            {
                (ids[m], ids[m - 1]) = (ids[m - 1], ids[m]);
                (distSq[m], distSq[m - 1]) = (distSq[m - 1], distSq[m]);
            }
        }

        static float3 SteerTowards(float3 velocity, float3 direction, float speed, float maxSteer)
        {
            float3 desired = math.normalizesafe(direction) * speed;
            return ClampLength(desired - velocity, maxSteer);
        }

        static float3 ClampLength(float3 v, float max)
        {
            float lenSq = math.lengthsq(v);
            return lenSq > max * max ? v * (max * math.rsqrt(lenSq)) : v;
        }
    }

    /// <summary>
    /// Moves each fish with physical limits, then writes its render matrix and swim state.
    /// - Turning: the heading rotates toward the desired direction no faster than speed / turn radius.
    /// - Burst and glide: the tail beats until the fish is a bit faster than desired, then it glides and slows down.
    /// - Swim state (tail phase, effort) is integrated here and read by the shader, so the tail beat follows speed.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    struct MotionJob : IJobParallelFor
    {
        public NativeArray<float3> positions;
        public NativeArray<float3> velocities;
        public NativeArray<byte> beating;
        public NativeArray<float4> swimStates;
        [ReadOnly] public NativeArray<float3> desiredDirections;
        [ReadOnly] public NativeArray<float> desiredSpeeds;
        [ReadOnly] public NativeArray<float3> corrections;
        [ReadOnly] public NativeArray<float> scales;
        [ReadOnly] public NativeArray<float> panic;
        [WriteOnly] public NativeArray<Matrix4x4> matrices;

        public SwimSettings swim;
        public float deltaTime;

        const float TwoPi = 6.2831853f;
        const float GlideEffort = 0.15f;

        public void Execute(int i)
        {
            float L = scales[i];
            float p01 = panic[i];
            float3 oldV = velocities[i];
            float speed = math.length(oldV);
            float3 target = desiredDirections[i];
            float3 dir = speed > 1e-5f ? oldV / speed : target;

            // ---- Limited turn rate: omega = speed / turn radius ----
            float turnRadius = math.lerp(swim.cruiseTurnRadius, swim.escapeTurnRadius, p01) * L;
            float maxAngle = math.max(speed, swim.minSpeed * L) / math.max(turnRadius, 1e-3f) * deltaTime;
            float angle = math.acos(math.clamp(math.dot(dir, target), -1f, 1f));
            if (angle <= maxAngle)
                dir = target;
            else
            {
                float3 axis = math.cross(dir, target);
                float axisLen = math.length(axis);
                axis = axisLen > 1e-5f ? axis / axisLen : math.normalizesafe(math.cross(dir, new float3(0, 1, 0)), new float3(1, 0, 0));
                dir = math.normalize(math.mul(quaternion.AxisAngle(axis, maxAngle), dir));
            }

            // ---- Burst and glide ----
            float desired = desiredSpeeds[i];
            bool beat = beating[i] != 0;
            if (swim.glideBand <= 0f) beat = speed < desired;
            else if (beat && speed >= desired * (1f + swim.glideBand)) beat = false;
            else if (!beat && speed <= desired * (1f - swim.glideBand)) beat = true;
            beating[i] = (byte)(beat ? 1 : 0);

            if (beat) speed += swim.thrustAcceleration * L * (1f + 2f * p01) * deltaTime;
            else speed -= speed * swim.glideDrag * deltaTime;
            speed = math.clamp(speed, swim.minSpeed * L, swim.burstSpeed * L);

            float3 v = dir * speed;
            float3 p = positions[i] + v * deltaTime + corrections[i];
            positions[i] = p;
            velocities[i] = v;

            // ---- Swim state for the shader: tail phase and effort (amplitude) ----
            float4 s = swimStates[i];
            s.y = math.lerp(s.y, beat ? 1f : GlideEffort, math.saturate(deltaTime * 8f));
            float tailBeatsPerSecond = speed / math.max(swim.strideLength * L, 1e-3f);
            s.x += TwoPi * tailBeatsPerSecond * math.lerp(0.4f, 1f, s.y) * deltaTime;
            s.x -= TwoPi * math.floor(s.x / TwoPi);
            swimStates[i] = s;

            // ---- Render matrix, with a slight bank into turns ----
            float3 right = math.normalizesafe(math.cross(new float3(0, 1, 0), dir), new float3(1, 0, 0));
            float lateral = math.dot(v - oldV, right) / math.max(deltaTime, 1e-4f);
            float roll = math.clamp(-lateral / math.max(L, 1e-3f) * 0.02f, -0.6f, 0.6f);
            quaternion rot = math.mul(quaternion.LookRotationSafe(dir, new float3(0, 1, 0)), quaternion.RotateZ(roll));
            matrices[i] = float4x4.TRS(p, rot, new float3(L));
        }
    }
}
