using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace SeaSim.Water
{
    /// <summary>
    /// URP renderer feature drawing underwater light shafts (Water/Shaders/LightShafts.shader):
    /// 1. at reduced resolution, march along each view ray through the water, gathering the sunlight scattered toward
    ///    the camera where the water is lit (sun shadow map: cliffs and fish schools cut the shafts) and under a
    ///    caustic line of the surface (same baked pattern as the ground and the fish);
    /// 2. add the result onto the camera image.
    /// Look and strength are set on the scene's <see cref="UnderwaterLight"/>; this feature only sets the cost.
    /// </summary>
    public class LightShaftsFeature : ScriptableRendererFeature
    {
        [SerializeField] Shader shader;
        [Tooltip("Resolution of the shafts relative to the screen (they are soft, so half or less looks the same).")]
        [SerializeField, Range(0.25f, 1f)] float resolutionScale = 0.5f;

        Material material;
        ShaftsPass pass;

        public override void Create()
        {
            if (shader == null) shader = Shader.Find("Hidden/SeaSim/LightShafts");
            if (shader == null) return;
            material = CoreUtils.CreateEngineMaterial(shader);
            pass = new ShaftsPass(material) { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
            pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (pass == null) return;
            var type = renderingData.cameraData.cameraType;
            if (type == CameraType.Preview || type == CameraType.Reflection) return;
            pass.resolutionScale = resolutionScale;
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing) => CoreUtils.Destroy(material);

        class ShaftsPass : ScriptableRenderPass
        {
            readonly Material material;
            public float resolutionScale = 0.5f;

            class PassData
            {
                public Material material;
                public TextureHandle source;
            }

            public ShaftsPass(Material material) => this.material = material;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                if (resources.isActiveTargetBackBuffer) return;

                var desc = cameraData.cameraTargetDescriptor;
                desc.width = Mathf.Max(1, Mathf.RoundToInt(desc.width * resolutionScale));
                desc.height = Mathf.Max(1, Mathf.RoundToInt(desc.height * resolutionScale));
                desc.depthBufferBits = 0;
                desc.depthStencilFormat = GraphicsFormat.None;
                desc.msaaSamples = 1;
                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                TextureHandle shafts = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_LightShafts", false, FilterMode.Bilinear);

                // 1. March the view rays at reduced resolution.
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("SeaSim Light Shafts", out var data))
                {
                    data.material = material;
                    builder.SetRenderAttachment(shafts, 0, AccessFlags.Write);
                    builder.UseAllGlobalTextures(true); // depth, sun shadow map, caustics texture
                    if (resources.cameraDepthTexture.IsValid()) builder.UseTexture(resources.cameraDepthTexture);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                        Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), d.material, 0));
                }

                // 2. Add them onto the camera image.
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("SeaSim Light Shafts Composite", out var data))
                {
                    data.material = material;
                    data.source = shafts;
                    builder.UseTexture(shafts);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                        Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, 1));
                }
            }
        }
    }
}
