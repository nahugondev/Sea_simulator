using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace SeaSim.Fish
{
    /// <summary>
    /// A simple hunting predator: it patrols slowly, then picks the closest fish of any <see cref="FishSchool"/>,
    /// charges it at high speed, and rests before the next attack. Fish react to it through their
    /// <see cref="EscapeSettings"/> (escape wave, school opening around it).
    /// It runs after the schools (execution order 100) so it reads up-to-date fish positions.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class FishPredator : MonoBehaviour
    {
        static readonly List<FishPredator> active = new List<FishPredator>();
        public static IReadOnlyList<FishPredator> Active => active;
        static readonly int SwimStateId = Shader.PropertyToID("_SwimState");

        enum Mode { Patrol, Chase }

        [Header("Look")]
        [SerializeField] Material material;
        [SerializeField] FishBodyShape bodyShape = FishBodyShape.Predator;
        [Tooltip("Length in metres.")]
        [SerializeField, Min(0.05f)] float length = 1.2f;

        [Header("Movement")]
        [Tooltip("Patrol speed (m/s).")]
        [SerializeField, Min(0f)] float cruiseSpeed = 1.2f;
        [Tooltip("Attack speed (m/s).")]
        [SerializeField, Min(0f)] float attackSpeed = 5f;
        [SerializeField, Min(0f)] float acceleration = 4f;
        [Tooltip("Minimum turning radius while patrolling / attacking (m).")]
        [SerializeField] Vector2 turnRadius = new Vector2(3f, 1.5f);
        [Tooltip("Distance travelled per tail beat, in body lengths.")]
        [SerializeField, Range(0.3f, 1.5f)] float strideLength = 0.8f;

        [Header("Hunting")]
        [Tooltip("How far (m) the predator can spot prey.")]
        [SerializeField, Min(0f)] float detectionRange = 15f;
        [Tooltip("Random rest time between two attacks (s).")]
        [SerializeField] Vector2 restTime = new Vector2(4f, 9f);
        [SerializeField, Min(0.1f)] float maxChaseTime = 3.5f;
        [SerializeField, Min(0f)] float catchDistance = 0.5f;

        [Header("Territory")]
        [Tooltip("Optional: stay inside this school's volume. Otherwise the predator patrols around its start position.")]
        [SerializeField] FishSchool territory;
        [SerializeField] Vector3 fallbackTerritorySize = new Vector3(40f, 12f, 40f);

        Mode mode = Mode.Patrol;
        float timer;
        float speed;
        float3 direction;
        FishSchool targetSchool;
        int targetIndex = -1;
        float retargetTimer;
        float swimPhase, swimEffort = 1f;
        Bounds fallbackTerritory;
        Transform body;
        MeshRenderer bodyRenderer;
        MaterialPropertyBlock properties;
        Mesh mesh;
        Unity.Mathematics.Random rng;

        public PredatorState State => new PredatorState
        {
            position = transform.position,
            velocity = direction * speed,
            length = length,
        };

        void OnEnable()
        {
            active.Add(this);
            rng = new Unity.Mathematics.Random((uint)(GetHashCode() | 1));
            fallbackTerritory = new Bounds(transform.position, fallbackTerritorySize);
            direction = math.normalizesafe((float3)transform.forward, new float3(0, 0, 1));
            speed = cruiseSpeed;
            timer = rng.NextFloat(restTime.x, restTime.y);
            properties = new MaterialPropertyBlock();
            BuildBody();
        }

        void OnDisable()
        {
            active.Remove(this);
            if (body != null) Destroy(body.gameObject);
            if (mesh != null) Destroy(mesh);
        }

        void BuildBody()
        {
            mesh = FishMeshBuilder.Build(bodyShape);
            var go = new GameObject("Body");
            go.hideFlags = HideFlags.DontSave;
            body = go.transform;
            body.SetParent(transform, false);
            body.localScale = Vector3.one * length;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            bodyRenderer = go.AddComponent<MeshRenderer>();
            bodyRenderer.sharedMaterial = material;
        }

        void LateUpdate()
        {
            float dt = math.min(Time.deltaTime, 1f / 30f);
            float3 position = transform.position;
            timer -= dt;

            float3 desired;
            float desiredSpeed;
            if (mode == Mode.Chase && TryGetTarget(out float3 preyPos, out float3 preyVel))
            {
                retargetTimer -= dt;
                if (retargetTimer <= 0f) { AcquireTarget(position); retargetTimer = 0.4f; }

                float3 toPrey = preyPos + preyVel * 0.3f - position; // aim slightly ahead
                desired = math.normalizesafe(toPrey, direction);
                desiredSpeed = attackSpeed;
                if (math.length(preyPos - position) < catchDistance + length * 0.5f || timer <= 0f)
                    StartPatrol();
            }
            else
            {
                if (mode == Mode.Chase) StartPatrol();
                desired = PatrolDirection(position);
                desiredSpeed = cruiseSpeed;
                if (timer <= 0f)
                {
                    if (AcquireTarget(position)) { mode = Mode.Chase; timer = maxChaseTime; retargetTimer = 0.4f; }
                    else timer = 1f;
                }
            }

            // Limited turn rate, like the fish.
            float radius = mode == Mode.Chase ? turnRadius.y : turnRadius.x;
            float maxAngle = math.max(speed, 0.2f) / math.max(radius, 0.01f) * dt;
            float angle = math.acos(math.clamp(math.dot(direction, desired), -1f, 1f));
            if (angle <= maxAngle) direction = desired;
            else
            {
                float3 axis = math.normalizesafe(math.cross(direction, desired), new float3(0, 1, 0));
                direction = math.normalize(math.mul(quaternion.AxisAngle(axis, maxAngle), direction));
            }

            float oldSpeed = speed;
            speed = Mathf.MoveTowards(speed, desiredSpeed, acceleration * dt);
            position += direction * speed * dt;
            transform.SetPositionAndRotation(position, Quaternion.LookRotation(direction, Vector3.up));

            // Tail beat follows speed; effort is high while accelerating.
            swimEffort = math.lerp(swimEffort, speed >= oldSpeed - 1e-4f ? 1f : 0.3f, math.saturate(dt * 6f));
            swimPhase += 2f * math.PI * speed / (strideLength * length) * math.lerp(0.4f, 1f, swimEffort) * dt;
            swimPhase %= 2f * math.PI;
            properties.SetVector(SwimStateId, new Vector4(swimPhase, swimEffort, 1f, 0f));
            bodyRenderer.SetPropertyBlock(properties);
        }

        void StartPatrol()
        {
            mode = Mode.Patrol;
            targetSchool = null;
            targetIndex = -1;
            timer = rng.NextFloat(restTime.x, restTime.y);
        }

        float3 PatrolDirection(float3 position)
        {
            Bounds area = territory != null ? territory.Volume : fallbackTerritory;
            float3 center = area.center;
            float3 local = position - center;
            float3 inner = (float3)area.extents * 0.7f;

            float t = Time.time * 0.1f;
            float3 wander = new float3(
                noise.snoise(new float2(t, 1.7f)),
                noise.snoise(new float2(t, 9.2f)) * 0.2f,
                noise.snoise(new float2(t, 4.4f)));
            float3 desired = direction + wander * 0.6f;
            // Head back toward the centre when leaving the inner part of the territory.
            float3 over = math.max(math.abs(local) - inner, 0f) / math.max((float3)area.extents - inner, 0.01f);
            desired += -math.sign(local) * over * 2f;
            desired.y *= 0.5f;
            return math.normalizesafe(desired, direction);
        }

        bool TryGetTarget(out float3 position, out float3 velocity)
        {
            position = velocity = 0;
            if (targetSchool == null || !targetSchool.isActiveAndEnabled || targetIndex < 0 || targetIndex >= targetSchool.FishCount)
                return false;
            position = targetSchool.Positions[targetIndex];
            velocity = targetSchool.Velocities[targetIndex];
            return true;
        }

        /// <summary>Picks the closest fish within detection range, across all schools.</summary>
        bool AcquireTarget(float3 position)
        {
            float bestSq = detectionRange * detectionRange;
            FishSchool bestSchool = null;
            int bestIndex = -1;
            foreach (var school in FishSchool.Active)
            {
                var positions = school.Positions;
                // Sample large schools to keep the cost bounded (~2000 checks per school).
                int stride = math.max(1, positions.Length / 2000);
                for (int i = rng.NextInt(stride); i < positions.Length; i += stride)
                {
                    float d = math.distancesq(positions[i], position);
                    if (d < bestSq) { bestSq = d; bestSchool = school; bestIndex = i; }
                }
            }
            if (bestSchool == null) return false;
            targetSchool = bestSchool;
            targetIndex = bestIndex;
            return true;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, detectionRange);
        }
    }
}
