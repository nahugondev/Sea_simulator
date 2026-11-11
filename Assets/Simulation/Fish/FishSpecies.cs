using UnityEngine;

namespace SeaSim.Fish
{
    /// <summary>
    /// Everything that defines a fish species: look, swimming, schooling and predator response.
    /// Several <see cref="FishSchool"/> components can share a species, and a scene can mix species
    /// by using one school per species.
    /// Distances and speeds are expressed in body lengths (BL) whenever it makes biological sense,
    /// so a bigger species automatically swims faster and keeps proportionally larger gaps.
    /// </summary>
    [CreateAssetMenu(menuName = "SeaSim/Fish Species", fileName = "FishSpecies")]
    public class FishSpecies : ScriptableObject
    {
        [Header("Look")]
        public Material material;
        [Tooltip("3D model of the fish (prefab or model asset, one static mesh). It is re-oriented, centred and scaled " +
                 "to a length of 1 automatically; the swimming wave is applied by the shader.")]
        public GameObject model;
        [Tooltip("Direction the fish's nose points to, in the model's own mesh space.")]
        public Vector3 modelForward = Vector3.forward;
        [Tooltip("Direction of the fish's back (dorsal side), in the model's own mesh space.")]
        public Vector3 modelUp = Vector3.up;
        [Tooltip("Average fish length in metres.")]
        [Min(0.01f)] public float length = 0.3f;
        [Range(0f, 0.5f)] public float sizeVariation = 0.2f;

        public SwimSettings swimming = SwimSettings.Default;
        public BoidSettings schooling = BoidSettings.Default;
        public EscapeSettings escape = EscapeSettings.Default;

        /// <summary>The behaviour settings, packed for the simulation jobs.</summary>
        public SpeciesData ToData() => new SpeciesData { boids = schooling, swim = swimming, escape = escape };
    }

    /// <summary>Per-species settings as read by the jobs (one entry per species, indexed by each fish's species index).</summary>
    public struct SpeciesData
    {
        public BoidSettings boids;
        public SwimSettings swim;
        public EscapeSettings escape;
    }

    /// <summary>How an individual fish moves. Speeds in body lengths per second (BL/s).</summary>
    [System.Serializable]
    public struct SwimSettings
    {
        [Header("Speed (BL/s)")]
        [Tooltip("Preferred cruising speed. Small fish typically cruise at 2-4 BL/s.")]
        [Min(0f)] public float cruiseSpeed;
        [Tooltip("Per-fish variation of the cruising speed (individual 'personality').")]
        [Range(0f, 0.5f)] public float speedVariation;
        [Min(0f)] public float minSpeed;
        [Tooltip("Top speed during an escape burst.")]
        [Min(0f)] public float burstSpeed;

        [Header("Burst and glide")]
        [Tooltip("Acceleration while the tail is beating (BL/s²).")]
        [Min(0f)] public float thrustAcceleration;
        [Tooltip("Speed lost per second while gliding (fraction of the current speed).")]
        [Range(0f, 3f)] public float glideDrag;
        [Tooltip("Speed band around the desired speed: beat up to +band, glide down to -band. 0 = continuous swimming.")]
        [Range(0f, 0.5f)] public float glideBand;
        [Tooltip("Distance travelled per tail beat (BL). Tail beat frequency = speed / stride.")]
        [Range(0.3f, 1.5f)] public float strideLength;

        [Header("Turning (BL)")]
        [Tooltip("Minimum turning radius while cruising.")]
        [Min(0.01f)] public float cruiseTurnRadius;
        [Tooltip("Minimum turning radius when panicked (fish are much more agile when escaping).")]
        [Min(0.01f)] public float escapeTurnRadius;

        public static SwimSettings Default => new SwimSettings
        {
            cruiseSpeed = 3f,
            speedVariation = 0.15f,
            minSpeed = 1f,
            burstSpeed = 10f,
            thrustAcceleration = 6f,
            glideDrag = 0.8f,
            glideBand = 0.1f,
            strideLength = 0.7f,
            cruiseTurnRadius = 1f,
            escapeTurnRadius = 0.2f,
        };
    }

    /// <summary>Schooling rules (boids) shared by a species.</summary>
    [System.Serializable]
    public struct BoidSettings
    {
        [Header("Perception")]
        [Tooltip("Max distance (m) at which neighbours can be noticed.")]
        [Min(0.1f)] public float perceptionRadius;
        [Range(30f, 360f)] public float fieldOfView;
        [Tooltip("Number of nearest neighbours each fish follows. Real schools and flocks interact with a fixed number " +
                 "of neighbours (about 7) rather than with everyone within a distance (topological interaction).")]
        [Range(1, 16)] public int topologicalNeighbours;
        [Tooltip("Max fish inspected per neighbour search (performance safety net).")]
        [Range(8, 256)] public int maxCandidates;

        [Header("Body (in fish lengths)")]
        [Tooltip("Radius of the capsule approximating the body.")]
        [Range(0.01f, 0.5f)] public float bodyRadius;
        [Tooltip("Height of the body section relative to its width (1 = round section). Deep-bodied fish or fish with " +
                 "large dorsal and anal fins need more than 1, so they never overlap vertically while still swimming " +
                 "close side by side. Values below 1 are treated as 1.")]
        [Range(1f, 10f)] public float bodyHeightRatio;
        [Tooltip("Free space each fish tries to keep around its body.")]
        [Range(0f, 2f)] public float personalSpace;
        [Tooltip("How much of an overlap between two bodies is pushed out each frame (0 = none, 1 = all).")]
        [Range(0f, 1f)] public float collisionStiffness;

        [Header("Weights")]
        [Tooltip("Max steering acceleration (BL/s²).")]
        [Min(0f)] public float maxSteerForce;
        public float separationWeight;
        public float alignmentWeight;
        public float cohesionWeight;
        [Tooltip("How much a fish adopts the average speed of its neighbours (0..1).")]
        [Range(0f, 1f)] public float speedMatching;
        public float wanderWeight;
        [Tooltip("Fish prefer swimming horizontally: damps vertical velocity.")]
        public float levelingWeight;
        public float boundsWeight;

        [Header("Other species")]
        [Tooltip("Distance (BL) within which fish of another species are steered away from. Schools only form within a " +
                 "species, so this is what makes two schools of different species avoid or cross each other instead of merging.")]
        [Min(0f)] public float otherSpeciesDistance;
        public float otherSpeciesWeight;

        public static BoidSettings Default => new BoidSettings
        {
            otherSpeciesDistance = 2.5f,
            otherSpeciesWeight = 2.5f,
            perceptionRadius = 2.2f,
            fieldOfView = 290f,
            topologicalNeighbours = 7,
            maxCandidates = 96,
            bodyRadius = 0.09f,
            bodyHeightRatio = 1f,
            personalSpace = 0.8f,
            collisionStiffness = 0.9f,
            maxSteerForce = 12f,
            separationWeight = 3.5f,
            alignmentWeight = 1.8f,
            cohesionWeight = 0.7f,
            speedMatching = 0.5f,
            wanderWeight = 0.1f,
            levelingWeight = 0.8f,
            boundsWeight = 3f,
        };
    }

    /// <summary>How fish detect predators and how the alarm spreads through the school.</summary>
    [System.Serializable]
    public struct EscapeSettings
    {
        [Tooltip("Distance (BL) at which a fish notices a predator.")]
        [Min(0f)] public float alarmDistance;
        [Tooltip("Fraction of a neighbour's panic passed on (must be < 1 so the alarm dies out).")]
        [Range(0f, 0.99f)] public float alarmTransmission;
        [Tooltip("How fast panic rises when neighbours panic (1/s). Sets the speed of the escape wave.")]
        [Min(0f)] public float alarmSpreadRate;
        [Tooltip("Seconds for panic to fade from 1 to 0 once the threat is gone.")]
        [Min(0.01f)] public float panicDecayTime;
        public float fleeWeight;
        [Tooltip("How much cohesion is dropped when panicked (the school opens up, 'flash expansion').")]
        [Range(0f, 1f)] public float cohesionDrop;

        public static EscapeSettings Default => new EscapeSettings
        {
            alarmDistance = 12f,
            alarmTransmission = 0.9f,
            alarmSpreadRate = 12f,
            panicDecayTime = 2.5f,
            fleeWeight = 3f,
            cohesionDrop = 0.7f,
        };
    }
}
