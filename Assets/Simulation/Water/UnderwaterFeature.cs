using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace SeaSim.Water
{
    /// <summary>
    /// URP renderer feature drawing the water itself (Water/Shaders/Underwater.shader), after the opaque scene and
    /// before post-processing:
    /// 1. light shafts, at reduced resolution: sunlight scattered by the water where it is lit (sun shadow map: cliffs
    ///    and fish schools cut them) and under a caustic line of the surface;
    /// 2. a soft, reduced copy of the scene for the blur with distance;
    /// 3. the composite: slight blur growing with distance, absorption colour by colour, the light the water scatters
    ///    toward the camera (brighter near the surface), and the shafts.
    /// It replaces Unity's fog. Look and strength are set on the scene's <see cref="UnderwaterLight"/>; this feature
    /// only sets the cost (resolutions).
    /// </summary>
    public class UnderwaterFeature : ScriptableRendererFeature
    {
        [SerializeField] Shader shader;
        [Tooltip("Resolution of the light shafts relative to the screen (they are soft: half or less looks the same).")]
        [SerializeField, Range(0.25f, 1f)] float shaftResolution = 0.5f;
        [Tooltip("Resolution of the blurred copy relative to the screen (lower = softer blur, cheaper).")]
        [SerializeField, Range(0.125f, 0.5f)] float blurResolution = 0.25f;

        Material material;
        WaterPass pass;

        public override void Create()
        {
            if (shader == null) shader = Shader.Find("Hidden/SeaSim/Underwater");
            if (shader == null) return;
            material = CoreUtils.CreateEngineMaterial(shader);
            pass = new WaterPass(material) { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
            pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (pass == null) return;
            var type = renderingData.cameraData.cameraType;
            if (type == CameraType.Preview || type == CameraType.Reflection) return;
            pass.shaftResolution = shaftResolution;
            pass.blurResolution = blurResolution;
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing) => CoreUtils.Destroy(material);

        class WaterPass : ScriptableRenderPass
        {
            static readonly int SourceTexelId = Shader.PropertyToID("_SourceTexel");
            static readonly int BlurTexelId = Shader.PropertyToID("_BlurTexel");
            static readonly int BlurTexId = Shader.PropertyToID("_WaterBlurTex");
            static readonly int ShaftsTexId = Shader.PropertyToID("_WaterShaftsTex");
            const int ShaftsPass = 0, DownsamplePass = 1, CompositePass = 2;

            readonly Material material;
            public float shaftResolution = 0.5f, blurResolution = 0.25f;

            class PassData
            {
                public Material material;
                public TextureHandle source, blur, shafts;
                public Vector4 texel;
            }

            public WaterPass(Material material) => this.material = material;

            static RenderTextureDescriptor Scaled(RenderTextureDescriptor desc, float scale)
            {
                desc.width = Mathf.Max(1, Mathf.RoundToInt(desc.width * scale));
                desc.height = Mathf.Max(1, Mathf.RoundToInt(desc.height * scale));
                desc.depthBufferBits = 0;
                desc.depthStencilFormat = GraphicsFormat.None;
                desc.msaaSamples = 1;
                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                return desc;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                if (resources.isActiveTargetBackBuffer) return;

                var screen = cameraData.cameraTargetDescriptor;
                var shaftDesc = Scaled(screen, shaftResolution);
                var blurDesc = Scaled(screen, blurResolution);
                TextureHandle shafts = UniversalRenderer.CreateRenderGraphTexture(renderGraph, shaftDesc, "_WaterShafts", false, FilterMode.Bilinear);
                TextureHandle blur = UniversalRenderer.CreateRenderGraphTexture(renderGraph, blurDesc, "_WaterBlur", false, FilterMode.Bilinear);
                TextureHandle output = UniversalRenderer.CreateRenderGraphTexture(renderGraph, Scaled(screen, 1f), "_WaterOutput", false, FilterMode.Bilinear);
                TextureHandle scene = resources.activeColorTexture;

                // 1. Light shafts.
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("SeaSim Water Shafts", out var data))
                {
                    data.material = material;
                    builder.SetRenderAttachment(shafts, 0, AccessFlags.Write);
                    builder.UseAllGlobalTextures(true); // depth, sun shadow map, caustics texture
                    if (resources.cameraDepthTexture.IsValid()) builder.UseTexture(resources.cameraDepthTexture);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                        Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), d.material, ShaftsPass));
                }

                // 2. Soft, reduced copy of the scene.
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("SeaSim Water Blur", out var data))
                {
                    data.material = material;
                    data.source = scene;
                    data.texel = new Vector4(1f / screen.width, 1f / screen.height, 0f, 0f);
                    builder.UseTexture(scene);
                    builder.SetRenderAttachment(blur, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    {
                        d.material.SetVector(SourceTexelId, d.texel);
                        Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, DownsamplePass);
                    });
                }

                // 3. Composite into a new colour target, which then becomes the camera colour.
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("SeaSim Water Composite", out var data))
                {
                    data.material = material;
                    data.source = scene;
                    data.blur = blur;
                    data.shafts = shafts;
                    data.texel = new Vector4(1f / blurDesc.width, 1f / blurDesc.height, 0f, 0f);
                    builder.UseTexture(scene);
                    builder.UseTexture(blur);
                    builder.UseTexture(shafts);
                    builder.UseAllGlobalTextures(true); // depth
                    if (resources.cameraDepthTexture.IsValid()) builder.UseTexture(resources.cameraDepthTexture);
                    builder.SetRenderAttachment(output, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    {
                        d.material.SetTexture(BlurTexId, d.blur);
                        d.material.SetTexture(ShaftsTexId, d.shafts);
                        d.material.SetVector(BlurTexelId, d.texel);
                        Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, CompositePass);
                    });
                }
                resources.cameraColor = output;
            }
        }
    }
}
