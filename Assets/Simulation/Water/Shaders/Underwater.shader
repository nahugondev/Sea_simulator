// How the water itself looks, drawn by UnderwaterFeature after the opaque scene:
// - Pass 0, "Shafts" (reduced resolution): marches each view ray through the water, gathering the sunlight scattered
//   toward the camera where the water is lit (sun shadow map: cliffs and fish schools cut the shafts) and under a
//   caustic line of the surface (same baked pattern as the ground and the fish).
// - Pass 1, "Downsample" (reduced resolution): a soft copy of the scene, for the blur with distance.
// - Pass 2, "Composite": the water between the camera and each pixel
//     * blurs it slightly, more with distance (suspended particles scatter the light),
//     * absorbs it colour by colour (red goes first, then green, then blue): distant things lose contrast and turn
//       blue-green instead of being covered by a flat fog,
//     * adds the light the water scatters toward the camera: brighter near the surface, darker in the depths,
//     * adds the light shafts, absorbed the same way.
// Settings: global values set by UnderwaterLight.
Shader "Hidden/SeaSim/Underwater"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // x = intensity, y = forward scattering (Henyey-Greenstein g), z = max distance (m), w = steps
        float4 _ShaftParams;
        // x = scattering per metre, y = caustic contrast, z = caustic line width (relative to the ground), w = max brightness
        float4 _ShaftParams2;
        // xyz = absorption per metre for red, green, blue; w = how far (m) the water colour is evaluated below the camera
        float4 _WaterAbsorption;
        // rgb = water colour near the surface / in the depths; w = depth (m below the surface) of the deep colour
        float4 _WaterShallowColor;
        float4 _WaterDeepColor;
        // x = blur strength (0..1), y = distance (m) at which the blur is full, z = sun glow in the water
        float4 _WaterBlur;

        // Henyey-Greenstein phase function: how much light is scattered toward the camera.
        float Phase(float cosTheta, float g)
        {
            float g2 = g * g;
            return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4), 1.5));
        }
        ENDHLSL

        Pass
        {
            Name "Shafts"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
            #include "Caustics.hlsl"

            half4 Frag(Varyings input) : SV_Target
            {
                float intensity = _ShaftParams.x;
                if (intensity <= 0.0) return 0;

                float2 uv = input.texcoord;
                float3 sceneWS = ComputeWorldSpacePosition(uv, SampleSceneDepth(uv), UNITY_MATRIX_I_VP);
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
                float surface = _CausticsParams.w;
                float3 sum = 0;
                for (int i = 0; i < steps; i++)
                {
                    float t = (i + jitter) * stepLen;
                    float3 p = origin + ray * t;
                    if (p.y > surface) continue;                       // no water above the surface
                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                    float shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(p));
                #else
                    float shadow = 1.0;                                // no sun shadows: everything is lit
                #endif
                    float lines = CausticsVolume(p, sun.direction, _ShaftParams2.z, _ShaftParams2.y);
                    // Absorbed colour by colour on its way to the camera, like the rest of the scene.
                    sum += shadow * lines * exp(-_WaterAbsorption.xyz * t);
                }

                float phase = Phase(dot(ray, sun.direction), _ShaftParams.y);
                float3 light = sun.color * sum * (stepLen * _ShaftParams2.x * phase * intensity);
                // Cap: looking straight at the sun through a long column of water must not burn out.
                float peak = max(light.r, max(light.g, light.b));
                light *= min(1.0, _ShaftParams2.w / max(peak, 1e-4));
                return half4(light, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Downsample"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 _SourceTexel; // xy = 1 / source size

            // Four bilinear taps one texel apart: a 4x4 box of the source in a single pass.
            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord, o = _SourceTexel.xy;
                half3 c = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x, -o.y), 0).rgb
                        + SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2( o.x, -o.y), 0).rgb
                        + SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x,  o.y), 0).rgb
                        + SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2( o.x,  o.y), 0).rgb;
                return half4(c * 0.25, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Composite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Caustics.hlsl" // surface height

            TEXTURE2D(_WaterBlurTex);
            TEXTURE2D(_WaterShaftsTex);
            float4 _BlurTexel; // xy = 1 / blur texture size

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half3 sharp = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, uv, 0).rgb;

                float3 sceneWS = ComputeWorldSpacePosition(uv, SampleSceneDepth(uv), UNITY_MATRIX_I_VP);
                float3 origin = GetCameraPositionWS();
                float3 toScene = sceneWS - origin;
                float dist = length(toScene);
                float3 ray = toScene / max(dist, 1e-4);

                // Slight blur growing with distance (tent filter on the downsampled copy).
                float2 o = _BlurTexel.xy;
                half3 blurred = 0.25 * (SAMPLE_TEXTURE2D_LOD(_WaterBlurTex, sampler_LinearClamp, uv + float2(-o.x, -o.y), 0).rgb
                                      + SAMPLE_TEXTURE2D_LOD(_WaterBlurTex, sampler_LinearClamp, uv + float2( o.x, -o.y), 0).rgb
                                      + SAMPLE_TEXTURE2D_LOD(_WaterBlurTex, sampler_LinearClamp, uv + float2(-o.x,  o.y), 0).rgb
                                      + SAMPLE_TEXTURE2D_LOD(_WaterBlurTex, sampler_LinearClamp, uv + float2( o.x,  o.y), 0).rgb);
                float blur = _WaterBlur.x * smoothstep(0.0, max(_WaterBlur.y, 0.01), dist);
                half3 scene = lerp(sharp, blurred, blur);

                // Absorption, colour by colour.
                float3 transmittance = exp(-_WaterAbsorption.xyz * dist);

                // Colour of the water in between: from the deep colour to the surface colour, taken halfway along the
                // ray (looking up sees bright water, looking down dark water), plus a glow toward the sun.
                float surface = _CausticsParams.w;
                float midY = origin.y + ray.y * min(dist, _WaterAbsorption.w) * 0.5;
                float shallow = saturate(1.0 - (surface - midY) / max(_WaterDeepColor.w, 1.0));
                float3 water = lerp(_WaterDeepColor.rgb, _WaterShallowColor.rgb, shallow);
                Light sun = GetMainLight();
                water *= 1.0 + _WaterBlur.z * Phase(dot(ray, sun.direction), 0.75);

                half3 shafts = SAMPLE_TEXTURE2D_LOD(_WaterShaftsTex, sampler_LinearClamp, uv, 0).rgb;
                half3 color = scene * transmittance + water * (1.0 - transmittance) + shafts;
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
}
