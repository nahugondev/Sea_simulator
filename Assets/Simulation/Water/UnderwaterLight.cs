using UnityEngine;

namespace SeaSim.Water
{
    /// <summary>
    /// Underwater lighting settings shared by every shader of the scene (environment and fish).
    /// Values are sent once as global shader properties, so the effect costs nothing on the CPU per object.
    ///
    /// Caustics: the moving web of light focused by the wavy surface onto everything below it. The pattern is
    /// computed per pixel in the shaders (Water/Shaders/Caustics.hlsl), projected from the surface along the sun
    /// direction, and follows the sun's shadows: rock overhangs and fish schools block it.
    /// </summary>
    [ExecuteAlways]
    public class UnderwaterLight : MonoBehaviour
    {
        static readonly int CausticsParamsId = Shader.PropertyToID("_CausticsParams");
        static readonly int CausticsFadeId = Shader.PropertyToID("_CausticsFade");

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

        [Header("Caustics on fish")]
        [Tooltip("Contrast of the caustics on fish: their direct sunlight gets brighter on the light lines and darker " +
                 "between them (0 = none, 1 = as on the ground).")]
        [SerializeField, Range(0f, 2f)] float fishCausticsStrength = 1f;
        [Tooltip("Width of the light lines on fish, relative to the ground. Fish are small: slightly wider, softer " +
                 "lines make a fish visibly light up as it crosses one, instead of being crossed by a thin line.")]
        [SerializeField, Range(1f, 4f)] float fishCausticsWidth = 1.6f;

        public float SurfaceHeight => surfaceHeight;

        void OnEnable() => Apply();
        void OnValidate() => Apply();
        void OnDisable() => Shader.SetGlobalVector(CausticsParamsId, Vector4.zero); // no caustics without this component

        void Apply()
        {
            if (!isActiveAndEnabled) return;
            Shader.SetGlobalVector(CausticsParamsId, new Vector4(causticsCellSize, causticsSpeed, causticsIntensity, surfaceHeight));
            Shader.SetGlobalVector(CausticsFadeId, new Vector4(causticsFadeDepth, causticsFadeDistance, fishCausticsStrength, fishCausticsWidth));
        }
    }
}
