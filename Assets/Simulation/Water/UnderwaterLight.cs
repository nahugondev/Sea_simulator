using UnityEngine;

namespace SeaSim.Water
{
    /// <summary>
    /// Underwater lighting shared by every shader of the scene (environment, fish, light shafts).
    /// Settings are sent as global shader properties, so they cost nothing on the CPU per object.
    ///
    /// Caustics: the moving web of light focused by the wavy surface onto everything below it. Its pattern is baked
    /// once per frame into a small tiling texture (Water/Shaders/CausticsBake.shader) that every shader reads, so the
    /// ground, the fish and the light shafts all see the same lines at the same place (Water/Shaders/Caustics.hlsl).
    ///
    /// The water itself (drawn by <see cref="UnderwaterFeature"/>, a URP renderer feature): absorption colour by
    /// colour, the light it scatters toward the camera, a slight blur with distance, and light shafts that follow the
    /// same caustic lines and the sun's shadows (cliffs and fish schools cut them). It replaces Unity's fog.
    /// </summary>
    [ExecuteAlways]
    public class UnderwaterLight : MonoBehaviour
    {
        static readonly int CausticsParamsId = Shader.PropertyToID("_CausticsParams");
        static readonly int CausticsFadeId = Shader.PropertyToID("_CausticsFade");
        static readonly int CausticsTexId = Shader.PropertyToID("_CausticsTex");
        static readonly int CausticsTimeId = Shader.PropertyToID("_CausticsTime");
        static readonly int ShaftParamsId = Shader.PropertyToID("_ShaftParams");
        static readonly int ShaftParams2Id = Shader.PropertyToID("_ShaftParams2");
        static readonly int WaterAbsorptionId = Shader.PropertyToID("_WaterAbsorption");
        static readonly int WaterShallowColorId = Shader.PropertyToID("_WaterShallowColor");
        static readonly int WaterDeepColorId = Shader.PropertyToID("_WaterDeepColor");
        static readonly int WaterBlurId = Shader.PropertyToID("_WaterBlur");

        [Header("Water surface")]
        [Tooltip("Height of the water surface (world Y, m).")]
        [SerializeField] float surfaceHeight = 36f;

        [Header("Caustics")]
        [Tooltip("Typical size of a caustic cell on the ground (m).")]
        [SerializeField, Min(0.05f)] float causticsCellSize = 1.6f;
        [Tooltip("How fast the pattern moves and morphs.")]
        [SerializeField, Min(0f)] float causticsSpeed = 0.9f;
        [Tooltip("Brightness of the caustics, relative to the direct sunlight.")]
        [SerializeField, Range(0f, 4f)] float causticsIntensity = 2.5f;
        [Tooltip("Depth below the surface (m) over which caustics fade to about a third.")]
        [SerializeField, Min(1f)] float causticsFadeDepth = 120f;
        [Tooltip("Distance from the camera (m) beyond which the fine pattern is replaced by its average brightness.")]
        [SerializeField, Min(1f)] float causticsFadeDistance = 45f;
        [Tooltip("Resolution of the baked caustics texture (it covers 8 x 8 cells and tiles).")]
        [SerializeField] int causticsResolution = 1024;

        [Header("Caustics on fish")]
        [Tooltip("Contrast of the caustics on fish: their direct sunlight gets brighter on the light lines and darker " +
                 "between them (0 = none, 1 = as on the ground).")]
        [SerializeField, Range(0f, 2f)] float fishCausticsStrength = 1f;
        [Tooltip("Width of the light lines on fish, relative to the ground. Fish are small: slightly wider, softer " +
                 "lines make a fish visibly light up as it crosses one, instead of being crossed by a thin line.")]
        [SerializeField, Range(1f, 4f)] float fishCausticsWidth = 1.6f;

        [Header("Water (needs the Underwater renderer feature)")]
        [Tooltip("Distance (m) over which red, green and blue light fall to about a third. Water absorbs red first, " +
                 "then green, then blue: distant things lose contrast and turn blue-green.")]
        [SerializeField] Vector3 absorptionDistance = new Vector3(45f, 90f, 115f);
        [Tooltip("Colour of the water scattering light toward the camera, near the surface.")]
        [SerializeField] Color shallowWaterColor = new Color(0.12f, 0.34f, 0.43f);
        [Tooltip("Colour of the water in the depths.")]
        [SerializeField] Color deepWaterColor = new Color(0.03f, 0.11f, 0.17f);
        [Tooltip("Depth below the surface (m) at which the water reaches its deep colour.")]
        [SerializeField, Min(1f)] float deepColorDepth = 80f;
        [Tooltip("Glow of the water toward the sun.")]
        [SerializeField, Range(0f, 4f)] float sunGlow = 1f;
        [Tooltip("Slight blur of distant things (0 = none, 1 = fully blurred at the blur distance).")]
        [SerializeField, Range(0f, 1f)] float blurStrength = 0.45f;
        [Tooltip("Distance (m) at which the blur reaches its full strength.")]
        [SerializeField, Min(1f)] float blurDistance = 40f;

        [Header("Light shafts")]
        [Tooltip("Brightness of the sunlight scattered by the water.")]
        [SerializeField, Range(0f, 4f)] float shaftIntensity = 1f;
        [Tooltip("How much the water scatters light per metre: more = denser, brighter shafts.")]
        [SerializeField, Range(0.001f, 0.2f)] float shaftDensity = 0.03f;
        [Tooltip("Brightest the shafts can get (looking toward the sun through a long column of lit water).")]
        [SerializeField, Range(0.05f, 2f)] float shaftMaxBrightness = 0.35f;
        [Tooltip("Forward scattering: shafts are brighter when looking toward the sun (0 = same in every direction).")]
        [SerializeField, Range(0f, 0.95f)] float shaftForwardScattering = 0.6f;
        [Tooltip("Contrast between the lit columns (above caustic lines) and the gaps between them.")]
        [SerializeField, Range(0f, 2f)] float shaftCausticContrast = 1f;
        [Tooltip("Width of the caustic lines seen as shafts, relative to the ground.")]
        [SerializeField, Range(1f, 6f)] float shaftCausticWidth = 2.5f;
        [Tooltip("Distance (m) over which shafts are gathered along each view ray. Shadows only exist within the " +
                 "shadow distance of the URP asset, so keep it below that.")]
        [SerializeField, Min(1f)] float shaftMaxDistance = 45f;
        [Tooltip("Samples per view ray: more = smoother shafts, higher cost.")]
        [SerializeField, Range(8, 96)] int shaftSteps = 32;

        [Header("Shaders (referenced so they are included in builds)")]
        [SerializeField] Shader bakeShader;

        RenderTexture causticsTexture;
        Material bakeMaterial;

        public float SurfaceHeight => surfaceHeight;

        void OnEnable() => Apply();
        void OnValidate() => Apply();

        void OnDisable()
        {
            Shader.SetGlobalVector(CausticsParamsId, Vector4.zero); // no caustics without this component
            Shader.SetGlobalVector(ShaftParamsId, Vector4.zero);
            if (causticsTexture != null) { causticsTexture.Release(); DestroyImmediate(causticsTexture); causticsTexture = null; }
            if (bakeMaterial != null) { DestroyImmediate(bakeMaterial); bakeMaterial = null; }
        }

        void Apply()
        {
            if (!isActiveAndEnabled) return;
            Shader.SetGlobalVector(CausticsParamsId, new Vector4(causticsCellSize, causticsSpeed, causticsIntensity, surfaceHeight));
            Shader.SetGlobalVector(CausticsFadeId, new Vector4(causticsFadeDepth, causticsFadeDistance, fishCausticsStrength, fishCausticsWidth));
            Shader.SetGlobalVector(ShaftParamsId, new Vector4(shaftIntensity, shaftForwardScattering, shaftMaxDistance, shaftSteps));
            Shader.SetGlobalVector(ShaftParams2Id, new Vector4(shaftDensity, shaftCausticContrast, shaftCausticWidth, shaftMaxBrightness));
            var absorption = new Vector3(1f / Mathf.Max(absorptionDistance.x, 0.1f), 1f / Mathf.Max(absorptionDistance.y, 0.1f), 1f / Mathf.Max(absorptionDistance.z, 0.1f));
            Shader.SetGlobalVector(WaterAbsorptionId, new Vector4(absorption.x, absorption.y, absorption.z, 60f));
            Shader.SetGlobalVector(WaterShallowColorId, (Vector4)shallowWaterColor.linear);
            var deep = (Vector4)deepWaterColor.linear; deep.w = deepColorDepth;
            Shader.SetGlobalVector(WaterDeepColorId, deep);
            Shader.SetGlobalVector(WaterBlurId, new Vector4(blurStrength, blurDistance, sunGlow, 0f));
        }

        void Update() => Bake();

        /// <summary>Renders this frame's caustics pattern into the shared texture (about 1M pixels: cheap).</summary>
        void Bake()
        {
            int size = Mathf.Clamp(Mathf.ClosestPowerOfTwo(causticsResolution), 128, 2048);
            if (causticsTexture == null || causticsTexture.width != size)
            {
                if (causticsTexture != null) { causticsTexture.Release(); DestroyImmediate(causticsTexture); }
                causticsTexture = new RenderTexture(size, size, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear)
                {
                    name = "Caustics", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear,
                    useMipMap = false, hideFlags = HideFlags.DontSave,
                };
                causticsTexture.Create();
            }
            if (bakeMaterial == null)
            {
                if (bakeShader == null) bakeShader = Shader.Find("Hidden/SeaSim/CausticsBake");
                if (bakeShader == null) return;
                bakeMaterial = new Material(bakeShader) { hideFlags = HideFlags.DontSave };
            }
            float time = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
            Shader.SetGlobalFloat(CausticsTimeId, time * causticsSpeed);
            Graphics.Blit(null, causticsTexture, bakeMaterial);
            Shader.SetGlobalTexture(CausticsTexId, causticsTexture);
        }
    }
}
