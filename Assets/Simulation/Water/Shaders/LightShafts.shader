// Underwater light shafts (volumetric sunlight), drawn by LightShaftsFeature.
// Pass 0 (half resolution): marches along each view ray through the water, up to the first opaque surface. At each
//   sample it asks: is this point in the sun's shadow (cliffs, fish schools)? Is it under a caustic line of the
//   surface? The light scattered toward the camera is summed, dimmed by the water between the sample and the camera.
//   The caustic lines come from the same baked pattern as the ground and the fish, so shafts land exactly on the
//   bright lines of the sand.
// Pass 1: adds the result onto the camera image (bilinear upsampling).
// Settings: _ShaftParams / _ShaftParams2, set by UnderwaterLight.
Shader "Hidden/SeaSim/LightShafts"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        ENDHLSL

        Pass
        {
            Name "March"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
            #include "Caustics.hlsl"

            // x = intensity, y = forward scattering (Henyey-Greenstein g), z = max distance (m), w = steps
            float4 _ShaftParams;
            // x = scattering per metre, y = caustic contrast, z = caustic line width (relative to the ground)
            float4 _ShaftParams2;

            // Henyey-Greenstein phase function: how much light is scattered toward the camera.
            float Phase(float cosTheta, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4), 1.5));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float intensity = _ShaftParams.x;
                if (intensity <= 0.0) return 0;

                float2 uv = input.texcoord;
                float rawDepth = SampleSceneDepth(uv);
                float3 sceneWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 origin = GetCameraPositionWS();
                float3 ray = sceneWS - origin;
                float sceneDist = length(ray);
                ray /= max(sceneDist, 1e-4);

                float dist = min(sceneDist, _ShaftParams.z);
                int steps = max((int)_ShaftParams.w, 1);
                float stepLen = dist / steps;
                // Per-pixel offset of the samples: turns banding into fine noise, smoothed by the upsampling.
                float jitter = InterleavedGradientNoise(input.positionCS.xy, 0);

                Light sun = GetMainLight();
                float sigma = _ShaftParams2.x;
                float surface = _CausticsParams.w;

                float sum = 0.0;
                for (int i = 0; i < steps; i++)
                {
                    float t = (i + jitter) * stepLen;
                    float3 p = origin + ray * t;
                    if (p.y > surface) continue;                       // no water above the surface
                    float shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(p));
                    float lines = CausticsVolume(p, sun.direction, _ShaftParams2.z, _ShaftParams2.y);
                    sum += shadow * lines * exp(-sigma * t);            // dimmed by the water up to the camera
                }

                float phase = Phase(dot(ray, sun.direction), _ShaftParams.y);
                float3 light = sun.color * (sum * stepLen * sigma * phase * intensity);
                return half4(light, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Composite"
            Blend One One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            half4 Frag(Varyings input) : SV_Target
            {
                return half4(SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_LinearClamp, input.texcoord, 0).rgb, 0);
            }
            ENDHLSL
        }
    }
}
