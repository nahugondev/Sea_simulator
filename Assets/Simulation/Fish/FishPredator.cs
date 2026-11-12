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
        [Tooltip("SeaSim/FishInstanced material, with Use Texture on for the model's texture.")]
        [SerializeField] Material material;
        [Tooltip("3D model of the predator (one static mesh). It is re-oriented, centred and scaled to the length below; " +
                 "the swimming wave is applied by the shader.")]
        [SerializeField] GameObject model;
        [Tooltip("Direction the nose points to, in the model's own mesh space.")]
        [SerializeField] Vector3 modelForward = Vector3.forward;
        [Tooltip("Direction of the back (dorsal side), in the model's own mesh space.")]
        [SerializeField] Vector3 modelUp = Vector3.up;
        [Tooltip("Length in metres.")]
        [SerializeField, Min(0.05f)] float length = 1.2f;

        [Header("Count")]
        [Tooltip("Number of predators. Extra ones are copies of this one, spawned in Play Mode at random places in the " +
                 "swimming volume (it can be changed while playing). Predators only hunt fish and keep a little distance from each other.")]
        [SerializeField, Range(1, 20)] int count = 1;
        [Tooltip("Distance (m) predators try to keep between each other.")]
        [SerializeField, Min(0f)] float spacing = 4f;

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
        [SerializeField] Vector2 restTime = new Vector2(20f, 40f);
        [SerializeField, Min(0.1f)] float maxChaseTime = 3.5f;
        [SerializeField, Min(0f)] float catchDistance = 0.5f;

        Mode mode = Mode.Patrol;
        float timer;
        float speed;
        float3 direction;
        FishSchool targetSchool;
        int targetIndex = -1;
        float retargetTimer;
        float swimPhase, swimEffort = 1f;
        Vector3 startPosition;
        Transform body;
        MeshRenderer bodyRenderer;
        MaterialPropertyBlock properties;
        Mesh mesh;
        Unity.Mathematics.Random rng;
        float noiseSeed;
        readonly List<GameObject> copies = new List<GameObject>();
        bool isCopy;
        static bool spawningCopies;

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
            noiseSeed = rng.NextFloat(0f, 1000f);
            if (!spawningCopies && !isCopy) SyncCopies();
            startPosition = transform.position;
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
            foreach (var copy in copies) if (copy != null) Destroy(copy);
            copies.Clear();
        }

        void Update()
        {
            // Count can be changed in the Inspector while playing.
            if (!isCopy && copies.Count != count - 1) SyncCopies();
        }

        /// <summary>
        /// Keeps count - 1 copies of this predator (Play Mode only), adding them at random places in the swimming
        /// volume or removing the last ones. Copies are plain scene objects, visible in the Hierarchy.
        /// </summary>
        void SyncCopies()
        {
            if (!Application.isPlaying) return;
            copies.RemoveAll(c => c == null);
            while (copies.Count > math.max(count - 1, 0))
            {
                Destroy(copies[copies.Count - 1]);
                copies.RemoveAt(copies.Count - 1);
            }
            if (copies.Count >= count - 1) return;

            Bounds area = FishSimulation.Instance != null && FishSimulation.Instance.TryGetSwimVolume(out Bounds volume)
                ? volume : new Bounds(transform.position, new Vector3(20f, 6f, 20f));
            spawningCopies = true;
            try
            {
                while (copies.Count < count - 1)
                {
                    float3 p = (float3)area.center + (rng.NextFloat3() * 2f - 1f) * (float3)area.extents * new float3(0.6f, 0.4f, 0.6f);
                    float heading = rng.NextFloat(0f, 360f);
                    var copy = Instantiate(gameObject, p, Quaternion.Euler(0f, heading, 0f), transform.parent);
                    copy.name = $"{name} {copies.Count + 2}";
                    copy.GetComponent<FishPredator>().isCopy = true;
                    copies.Add(copy);
                }
            }
            finally { spawningCopies = false; }
        }

        void BuildBody()
        {
            mesh = model != null ? FishMeshBuilder.FromModel(model, modelForward, modelUp) : null;
            if (mesh == null)
            {
                Debug.LogError($"{name}: no model, or its mesh is not readable (enable Read/Write in the model's import settings).", this);
                return;
            }
            // A copy also duplicates the original's body: drop it, each predator builds its own.
            for (int i = transform.childCount - 1; i >= 0; i--)
                if (transform.GetChild(i).name == "Body") DestroyImmediate(transform.GetChild(i).gameObject);
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
            if (bodyRenderer == null) return;
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
            // Patrol the fish's shared swimming volume; without an enclave, stay around the start position.
            if (!FishSimulation.Instance.TryGetSwimVolume(out Bounds area))
                area = new Bounds(startPosition, new Vector3(20f, 6f, 20f));
            float3 center = area.center;
            float3 local = position - center;
            float3 inner = (float3)area.extents * 0.7f;

            float t = Time.time * 0.1f + noiseSeed; // each predator wanders its own way
            float3 wander = new float3(
                noise.snoise(new float2(t, 1.7f)),
                noise.snoise(new float2(t, 9.2f)) * 0.2f,
                noise.snoise(new float2(t, 4.4f)));
            float3 desired = direction + wander * 0.6f;
            // Keep some distance from the other predators.
            foreach (var other in active)
            {
                if (other == this) continue;
                float3 away = position - (float3)other.transform.position;
                float d = math.length(away);
                if (d < spacing && d > 1e-4f) desired += away / d * (1f - d / spacing) * 2f;
            }
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
