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

    /// <summary>The single swimming volume shared by every fish (the enclave's), with its soft containment margin.</summary>
    public struct SwimVolumeData
    {
        public float3 center;
        public float3 extents;
        public float margin;
    }

    static class SearchOrder
    {
        /// <summary>Maps 0,1,2,3,4,... to 0,-1,+1,-2,+2,... so the closest rows of cells are visited first.</summary>
        public static int Offset(int k) => k == 0 ? 0 : ((k + 1) / 2) * ((k & 1) == 1 ? -1 : 1);
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

    /// <summary>
    /// Decides where each fish wants to go and how fast (it does not move it: see <see cref="MotionJob"/>).
    /// All species share one simulation; each fish reads the settings of its own species.
    /// - Runs in sorted order (see <see cref="GridLayout"/>): fish s reads its own data and its neighbours' data
    ///   from the sorted arrays, and writes its results at its original index order[s].
    /// - Bodies are capsules and never overlap, whatever the species: separation uses the real distance between
    ///   bodies, and overlaps are pushed apart.
    /// - Schools only form within a species: the k nearest visible neighbours used for alignment / cohesion are
    ///   fish of the same species. Fish of other species are steered away from, so schools avoid or cross each other.
    /// - Predators: fish that see one flee sideways ('fountain effect'). Panic spreads from neighbour to neighbour,
    ///   across species too: a fleeing fish is an alarm signal for every fish around it.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    struct SteerJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> order;
        [ReadOnly] public NativeArray<int> cellStart;
        [ReadOnly] public NativeArray<float3> sortedPositions;
        [ReadOnly] public NativeArray<float3> sortedVelocities;
        [ReadOnly] public NativeArray<float> sortedScales;
        [ReadOnly] public NativeArray<float> sortedPanic;
        [ReadOnly] public NativeArray<int> sortedSpecies;
        [ReadOnly] public NativeArray<float> cruiseSpeeds;     // original index
        [ReadOnly] public NativeArray<SpeciesData> species;
        public SwimVolumeData volume;
        [ReadOnly] public NativeArray<PredatorState> predators;

        // Indexed by original fish index (order[s]), hence not by the job index.
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<float3> desiredDirections;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<float> desiredSpeeds;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<float3> corrections;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<float> newPanic;

        public GridLayout layout;
        [Tooltip("Cells to scan on each side in pass 1 (the cell size covers the largest body reach of all species).")]
        public int nearCells;
        public float time;
        public float deltaTime;

        public void Execute(int s)
        {
            int i = order[s];
            int mySpecies = sortedSpecies[s];
            var data = species[mySpecies];
            var boids = data.boids;
            var swim = data.swim;
            var escape = data.escape;

            float3 p = sortedPositions[s];
            float3 v = sortedVelocities[s];
            float speed = math.length(v);
            float3 forward = speed > 1e-5f ? v / speed : new float3(0, 0, 1);
            float lengthI = sortedScales[s];
            float3 halfSegI = forward * (0.5f * Capsule.SegmentFraction * lengthI);
            float cosHalfFov = math.cos(math.radians(boids.fieldOfView * 0.5f));
            int k = math.clamp(boids.topologicalNeighbours, 1, 16);

            var neighbourIds = new FixedList128Bytes<int>();
            var neighbourDistSq = new FixedList128Bytes<float>();
            float3 separation = 0, correction = 0, otherSpeciesAway = 0;
            float otherSpeciesPanic = 0f;

            // ---- Pass 1, the cells within body reach ----
            // Cells are numbered x first, so each (y, z) row of cells is one contiguous range of sorted fish.
            int R = nearCells;
            float nearRadius = layout.cellSize * R;
            float nearRadiusSq = nearRadius * nearRadius;
            float perceptionSq = boids.perceptionRadius * boids.perceptionRadius;
            // Other species are avoided within this distance (limited to what pass 1 covers).
            float otherRange = math.min(boids.otherSpeciesDistance * lengthI, nearRadius);
            float otherRangeSq = otherRange * otherRange;
            int3 cell = layout.CellCoord(p);
            int candidates = 0;
            int rows = 2 * R + 1;
            for (int zi = 0; zi < rows && candidates < boids.maxCandidates; zi++)
            for (int yi = 0; yi < rows && candidates < boids.maxCandidates; yi++)
            {
                int y = cell.y + SearchOrder.Offset(yi), z = cell.z + SearchOrder.Offset(zi);
                if (y < 0 || y >= layout.dims.y || z < 0 || z >= layout.dims.z) continue;
                int xMin = math.max(cell.x - R, 0), xMax = math.min(cell.x + R, layout.dims.x - 1);
                int end = cellStart[layout.Flatten(new int3(xMax, y, z)) + 1];
                for (int t = cellStart[layout.Flatten(new int3(xMin, y, z))]; t < end && candidates < boids.maxCandidates; t++)
                {
                    if (t == s) continue;
                    float3 offset = sortedPositions[t] - p;
                    float distSq = math.lengthsq(offset);
                    if (distSq < 1e-10f) continue;
                    bool sameSpecies = sortedSpecies[t] == mySpecies;

                    if (distSq <= nearRadiusSq)
                    {
                        candidates++;
                        // Bodies never overlap, whatever the species.
                        AvoidBody(t, p, halfSegI, lengthI, boids, offset, distSq, ref separation, ref correction);

                        if (!sameSpecies)
                        {
                            // Keep away from other species (stronger when closer), and notice if they are fleeing.
                            if (distSq < otherRangeSq)
                            {
                                float d = math.sqrt(distSq);
                                float w = 1f - d / otherRange;
                                otherSpeciesAway -= offset / d * (w * w);
                            }
                            otherSpeciesPanic = math.max(otherSpeciesPanic, sortedPanic[t]);
                        }
                    }

                    // Only fish of the same species can be schooling neighbours (pass 2 only looks further out).
                    if (sameSpecies && distSq <= perceptionSq && CanEnter(ref neighbourDistSq, k, distSq)
                        && IsVisible(forward, offset, distSq, cosHalfFov))
                        InsertNearest(ref neighbourIds, ref neighbourDistSq, k, t, distSq);
                }
            }

            // ---- Pass 2, wider rings of cells: only for fish in a sparse area that still lack neighbours ----
            if (neighbourIds.Length < k)
            {
                int maxRing = math.min((int)math.ceil(boids.perceptionRadius * layout.invCellSize), R + 6);
                int scanned = 0;
                for (int r = R + 1; r <= maxRing && neighbourIds.Length < k && scanned < boids.maxCandidates * 4; r++)
                {
                    for (int dz = -r; dz <= r; dz++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        int y = cell.y + dy, z = cell.z + dz;
                        if (y < 0 || y >= layout.dims.y || z < 0 || z >= layout.dims.z) continue;
                        bool shellRow = math.abs(dy) == r || math.abs(dz) == r;
                        // On a shell row the whole x range is new; otherwise only the two end cells are.
                        for (int side = 0; side < (shellRow ? 1 : 2); side++)
                        {
                            int xMin = shellRow ? cell.x - r : (side == 0 ? cell.x - r : cell.x + r);
                            int xMax = shellRow ? cell.x + r : xMin;
                            xMin = math.max(xMin, 0);
                            xMax = math.min(xMax, layout.dims.x - 1);
                            if (xMin > xMax) continue;
                            int end = cellStart[layout.Flatten(new int3(xMax, y, z)) + 1];
                            for (int t = cellStart[layout.Flatten(new int3(xMin, y, z))]; t < end; t++)
                            {
                                scanned++;
                                if (sortedSpecies[t] != mySpecies) continue;
                                float3 offset = sortedPositions[t] - p;
                                float distSq = math.lengthsq(offset);
                                if (distSq > perceptionSq) continue;
                                if (CanEnter(ref neighbourDistSq, k, distSq) && IsVisible(forward, offset, distSq, cosHalfFov))
                                    InsertNearest(ref neighbourIds, ref neighbourDistSq, k, t, distSq);
                            }
                        }
                    }
                }
            }

            // ---- Neighbour averages (same species only) ----
            float3 alignment = 0, cohesion = 0;
            float neighbourSpeed = 0, neighbourPanic = otherSpeciesPanic; // alarm crosses species
            int count = neighbourIds.Length;
            for (int n = 0; n < count; n++)
            {
                int t = neighbourIds[n];
                float3 vj = sortedVelocities[t];
                float sj = math.length(vj);
                alignment += sj > 1e-5f ? vj / sj : 0f;
                cohesion += sortedPositions[t];
                neighbourSpeed += sj;
                neighbourPanic = math.max(neighbourPanic, sortedPanic[t]);
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
            float oldPanic = sortedPanic[s];
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
            if (math.lengthsq(otherSpeciesAway) > 0f)
                accel += SteerTowards(v, otherSpeciesAway, desiredSpeed, maxSteer) * boids.otherSpeciesWeight * math.min(math.length(otherSpeciesAway), 2f);
            if (math.lengthsq(flee) > 0f)
                accel += SteerTowards(v, flee, burst, maxSteer) * escape.fleeWeight;

            // Soft containment in the shared swimming volume: the push grows deeper into the margin.
            float3 local = p - volume.center;
            float3 inner = math.max(volume.extents - volume.margin, 0.01f);
            float3 over = math.max(math.abs(local) - inner, 0f) / math.max(volume.margin, 0.01f);
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
            corrections[i] = ClampLength(correction * boids.collisionStiffness, boids.bodyRadius * HeightRatio(boids) * lengthI);
        }

        /// <param name="t">Sorted index of the other fish, of any species.</param>
        /// <remarks>
        /// Bodies are capsules with an elliptical section: half-width = bodyRadius, half-height = bodyRadius x
        /// bodyHeightRatio (fish swim level, so "height" is the world vertical). The distance between the two capsule
        /// axes is measured in a space where the vertical is squashed by width / height, which turns the pair of
        /// ellipses into circles of radius bodyRadii: the usual round-capsule test then applies unchanged, and with a
        /// ratio of 1 the result is exactly the round capsule.
        /// </remarks>
        void AvoidBody(int t, float3 p, float3 halfSegI, float lengthI, BoidSettings boids, float3 offset, float distSq,
            ref float3 separation, ref float3 correction)
        {
            float lengthJ = sortedScales[t];
            var boidsJ = species[sortedSpecies[t]].boids;
            float radiusI = boids.bodyRadius * lengthI, radiusJ = boidsJ.bodyRadius * lengthJ;
            float bodyRadii = radiusI + radiusJ;                                       // combined half-width
            float bodyHeights = radiusI * HeightRatio(boids) + radiusJ * HeightRatio(boidsJ); // combined half-height
            float squash = bodyRadii / bodyHeights;                                     // <= 1
            float avoidRange = bodyRadii + boids.personalSpace * 0.5f * (lengthI + lengthJ);
            // Centres further apart can't interact (vertically, bodies reach up to bodyHeights).
            float reach = 0.5f * (lengthI + lengthJ) + avoidRange + (bodyHeights - bodyRadii);
            if (distSq > reach * reach) return;

            float3 fwdJ = math.normalizesafe(sortedVelocities[t], new float3(0, 0, 1));
            float3 halfSegJ = fwdJ * (0.5f * Capsule.SegmentFraction * lengthJ);
            float3 pj = p + offset;
            Capsule.ClosestPoints(p - halfSegI, p + halfSegI, pj - halfSegJ, pj + halfSegJ, out float3 onI, out float3 onJ);
            float3 away = onI - onJ;
            away.y *= squash;                       // ellipses -> circles
            float gap = math.length(away);
            float3 dir = gap > 1e-5f ? away / gap : math.normalizesafe(-offset, new float3(0, 1, 0));
            // Back to real space: a vertical push must cover height / width times more distance.
            float3 push = dir;
            push.y /= squash;

            if (gap < avoidRange)
            {
                // Steering: grows quickly as the free space between bodies shrinks.
                float w = 1f - math.saturate((gap - bodyRadii) / math.max(avoidRange - bodyRadii, 1e-4f));
                separation += dir * (w * w + (gap < bodyRadii ? 1f : 0f));
            }
            if (gap < bodyRadii)
                correction += push * (bodyRadii - gap) * 0.5f; // each fish takes half of the overlap
        }

        static float HeightRatio(BoidSettings boids) => math.max(1f, boids.bodyHeightRatio);

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
        [ReadOnly] public NativeArray<int> fishSpecies;
        public SwimVolumeData volume;
        [ReadOnly] public NativeArray<SpeciesData> species;
        [WriteOnly] public NativeArray<Matrix4x4> matrices;

        public float deltaTime;

        const float TwoPi = 6.2831853f;
        const float GlideEffort = 0.15f;

        public void Execute(int i)
        {
            var swim = species[fishSpecies[i]].swim;
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
            // Hard limit: whatever the steering did, a fish never leaves its swimming volume (the enclave walls
            // stand beyond it). The soft containment in SteerJob normally keeps fish well inside already.
            p = math.clamp(p, volume.center - volume.extents, volume.center + volume.extents);
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
