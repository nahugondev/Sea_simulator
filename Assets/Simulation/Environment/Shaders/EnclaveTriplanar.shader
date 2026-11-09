// Triplanar PBR surface for the procedural enclave (rock and sand), lit by URP's own PBR lighting.
// The enclave mesh has no usable UVs, so every texture is projected from the three world axes and blended by the
// surface normal (no stretching on vertical cliffs). Normal maps use the "whiteout" triplanar blend.
// - Base layer: albedo, normal (OpenGL), roughness and ambient-occlusion maps (CC0 scans from Poly Haven).
// - Anti-tiling: the maps are sampled a second time at a non-integer scale and offset, mixed in noisy patches.
// - Macro variation: the albedo is sampled again at a much larger scale, as a brightness modulation.
// Texture size is set in metres (_TileSize), not with the usual Tiling/Offset fields (hidden: world-space projection).
// - Top layer (optional): a second material that settles on faces looking up (sand on rock ledges).
// UV0.x = cavity from the mesh generator (0 exposed, 1 deep recess), used as extra ambient occlusion.
Shader "SeaSim/EnclaveTriplanar"
{
    Properties
    {
        [Header(Base layer)]
        [NoScaleOffset] _BaseMap ("Albedo", 2D) = "white" {}
        [Normal][NoScaleOffset] _NormalMap ("Normal (OpenGL)", 2D) = "bump" {}
        [NoScaleOffset] _RoughnessMap ("Roughness", 2D) = "white" {}
        [NoScaleOffset] _OcclusionMap ("Ambient Occlusion", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _TileSize ("Texture Size (m)", Float) = 3

        [Header(Anti tiling)]
        _SecondScale ("Second Sample Scale", Range(1, 4)) = 2.37
        _SecondBlend ("Second Sample Amount", Range(0, 1)) = 0.5
        _SecondPatchSize ("Patch Size (m)", Float) = 7

        [Header(Surface)]
        _NormalStrength ("Normal Strength", Range(0, 3)) = 1
        _SmoothnessScale ("Smoothness Scale", Range(0, 1)) = 0.6
        _OcclusionStrength ("Texture Occlusion", Range(0, 1)) = 1
        _CavityOcclusion ("Cavity Occlusion (mesh)", Range(0, 1)) = 0.4
        _MacroScale ("Macro Variation Size (m)", Float) = 23
        _MacroVariation ("Macro Variation", Range(0, 1)) = 0.35
        _BlendSharpness ("Triplanar Blend Sharpness", Range(1, 16)) = 6

        [Header(Top layer on upward faces)]
        [NoScaleOffset] _TopBaseMap ("Top Albedo", 2D) = "white" {}
        [Normal][NoScaleOffset] _TopNormalMap ("Top Normal (OpenGL)", 2D) = "bump" {}
        _TopTint ("Top Tint", Color) = (1, 1, 1, 1)
        _TopTileSize ("Top Texture Size (m)", Float) = 2
        _TopAmount ("Top Amount", Range(0, 1)) = 0
        _TopThreshold ("Top Slope Threshold", Range(0, 1)) = 0.8
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "EnvironmentNoise.hlsl"

            TEXTURE2D(_BaseMap);      SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);    SAMPLER(sampler_NormalMap);
            TEXTURE2D(_RoughnessMap);
            TEXTURE2D(_OcclusionMap);
            TEXTURE2D(_TopBaseMap);   SAMPLER(sampler_TopBaseMap);
            TEXTURE2D(_TopNormalMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST, _TopBaseMap_ST;
                half4 _Tint, _TopTint;
                float _TileSize, _MacroScale, _TopTileSize, _SecondScale, _SecondPatchSize;
                half _SecondBlend;
                half _NormalStrength, _SmoothnessScale, _OcclusionStrength, _CavityOcclusion, _MacroVariation;
                half _BlendSharpness, _TopAmount, _TopThreshold;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs vp = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = vp.positionCS;
                o.positionWS = vp.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                o.fogFactor = ComputeFogFactor(vp.positionCS.z);
                return o;
            }

            // Projection coordinates for the three axes (X sees ZY, Y sees XZ, Z sees XY).
            struct Triplanar { float2 x, y, z; float3 w; };

            Triplanar MakeTriplanar(float3 p, float3 n, float tileSize)
            {
                Triplanar t;
                float3 q = p / tileSize;
                t.x = q.zy; t.y = q.xz; t.z = q.xy;
                float3 w = pow(abs(n), _BlendSharpness);
                t.w = w / (w.x + w.y + w.z);
                return t;
            }

            #define SAMPLE_TRIPLANAR(tex, smp, t) \
                (SAMPLE_TEXTURE2D(tex, smp, t.x) * t.w.x + SAMPLE_TEXTURE2D(tex, smp, t.y) * t.w.y + SAMPLE_TEXTURE2D(tex, smp, t.z) * t.w.z)

            // Whiteout blend: each projected tangent-space normal is combined with the surface normal swizzled into
            // that projection's plane, then swizzled back to world space.
            float3 TriplanarNormal(TEXTURE2D_PARAM(tex, smp), Triplanar t, float3 n, half strength)
            {
                half3 tx = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, smp, t.x), strength);
                half3 ty = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, smp, t.y), strength);
                half3 tz = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, smp, t.z), strength);
                tx = half3(tx.xy + n.zy, abs(tx.z) * n.x);
                ty = half3(ty.xy + n.xz, abs(ty.z) * n.y);
                tz = half3(tz.xy + n.xy, abs(tz.z) * n.z);
                return normalize(tx.zyx * t.w.x + ty.xzy * t.w.y + tz.xyz * t.w.z);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 p = i.positionWS;
                float3 nGeo = normalize(i.normalWS);

                // ---- Base layer ----
                Triplanar t = MakeTriplanar(p, nGeo, _TileSize);
                half3 albedo = SAMPLE_TRIPLANAR(_BaseMap, sampler_BaseMap, t).rgb;
                float3 n = TriplanarNormal(TEXTURE2D_ARGS(_NormalMap, sampler_NormalMap), t, nGeo, _NormalStrength);
                half roughness = SAMPLE_TRIPLANAR(_RoughnessMap, sampler_BaseMap, t).r;
                half ao = SAMPLE_TRIPLANAR(_OcclusionMap, sampler_BaseMap, t).r;

                // ---- Anti-tiling: a second sample of the same maps at a non-integer scale and an offset, mixed in
                // irregular patches. The two grids never line up, so the eye cannot lock onto a repeating motif.
                half patch = smoothstep(0.3, 0.7, Fbm(p / _SecondPatchSize + 71.3)) * _SecondBlend;
                if (patch > 0.001)
                {
                    Triplanar t2 = MakeTriplanar(p + float3(17.3, 5.1, 29.7), nGeo, _TileSize * _SecondScale);
                    albedo = lerp(albedo, SAMPLE_TRIPLANAR(_BaseMap, sampler_BaseMap, t2).rgb, patch);
                    float3 n2 = TriplanarNormal(TEXTURE2D_ARGS(_NormalMap, sampler_NormalMap), t2, nGeo, _NormalStrength);
                    n = normalize(lerp(n, n2, patch));
                    roughness = lerp(roughness, SAMPLE_TRIPLANAR(_RoughnessMap, sampler_BaseMap, t2).r, patch);
                    ao = lerp(ao, SAMPLE_TRIPLANAR(_OcclusionMap, sampler_BaseMap, t2).r, patch);
                }
                albedo *= _Tint.rgb;
                half occlusion = lerp(1.0, ao, _OcclusionStrength);

                // Macro variation: the same albedo at a large scale, used as a brightness modulation.
                Triplanar macro = MakeTriplanar(p + 31.7, nGeo, _MacroScale);
                half macroLum = dot(SAMPLE_TRIPLANAR(_BaseMap, sampler_BaseMap, macro).rgb, half3(0.3, 0.59, 0.11));
                half baseLum = max(dot(SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, float2(0.5, 0.5), 16).rgb, half3(0.3, 0.59, 0.11)), 0.05);
                albedo *= lerp(1.0, macroLum / baseLum, _MacroVariation);

                // ---- Top layer: settles on faces looking up, in irregular patches ----
                // Uses the geometric normal so sand only settles on real ledges, not on texture bumps.
                half up = nGeo.y + (Fbm(p * 0.6) - 0.5) * 0.35;
                half top = smoothstep(_TopThreshold, _TopThreshold + 0.12, up) * _TopAmount;
                if (top > 0.001)
                {
                    Triplanar tt = MakeTriplanar(p, nGeo, _TopTileSize);
                    half3 topAlbedo = SAMPLE_TRIPLANAR(_TopBaseMap, sampler_TopBaseMap, tt).rgb * _TopTint.rgb;
                    float3 topNormal = TriplanarNormal(TEXTURE2D_ARGS(_TopNormalMap, sampler_TopBaseMap), tt, nGeo, 1.0);
                    albedo = lerp(albedo, topAlbedo, top);
                    n = normalize(lerp(n, topNormal, top));
                    roughness = lerp(roughness, 0.9, top);
                    occlusion = lerp(occlusion, 1.0, top * 0.5);
                }

                occlusion *= lerp(1.0, 1.0 - _CavityOcclusion, smoothstep(0.3, 1.0, i.uv.x));

                // ---- URP PBR lighting ----
                InputData inputData = (InputData)0;
                inputData.positionWS = p;
                inputData.positionCS = i.positionCS;
                inputData.normalWS = n;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(p);
                inputData.shadowCoord = TransformWorldToShadowCoord(p);
                inputData.fogCoord = i.fogFactor;
                inputData.bakedGI = SampleSH(n);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.smoothness = (1.0 - roughness) * _SmoothnessScale;
                surface.normalTS = half3(0, 0, 1);
                surface.occlusion = occlusion;
                surface.alpha = 1;

                half4 color = UniversalFragmentPBR(inputData, surface);
                color.rgb = MixFog(color.rgb, i.fogFactor);
                return half4(color.rgb, 1);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
    }

    FallBack Off
}
