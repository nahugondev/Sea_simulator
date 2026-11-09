// Instanced fish shader for URP.
// - Swimming: a travelling sine wave is applied in the vertex shader. Its amplitude grows from head to tail
//   (UV0.x = 0 at the nose, 1 at the tail), and each instance gets its own phase/frequency from its instance ID.
// - Counter-shading: dark back, silvery belly (UV0.y = -1 belly .. +1 back), with a fresnel sheen.
Shader "SeaSim/FishInstanced"
{
    Properties
    {
        _BackColor ("Back Color", Color) = (0.10, 0.20, 0.28, 1)
        _BellyColor ("Belly Color", Color) = (0.78, 0.82, 0.85, 1)
        _StripeColor ("Lateral Stripe Color", Color) = (0.35, 0.55, 0.65, 1)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.75
        _Sheen ("Silver Sheen", Range(0, 4)) = 2.0

        [Header(Swimming)]
        _SwimFrequency ("Tail Beats Per Second", Float) = 2.2
        _SwimAmplitude ("Tail Amplitude", Float) = 0.07
        _HeadAmplitude ("Head Amplitude", Float) = 0.012
        _WaveCount ("Waves Along Body", Float) = 0.9
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _BackColor;
            half4 _BellyColor;
            half4 _StripeColor;
            half _Smoothness;
            half _Sheen;
            float _SwimFrequency;
            float _SwimAmplitude;
            float _HeadAmplitude;
            float _WaveCount;
        CBUFFER_END

        float Hash11(float n) { return frac(sin(n * 12.9898) * 43758.5453); }

        float InstanceSeed()
        {
        #if defined(UNITY_INSTANCING_ENABLED)
            return (float)unity_InstanceID;
        #else
            // Non-instanced fallback: derive something from the object position.
            return dot(UNITY_MATRIX_M._m03_m13_m23, float3(1.0, 7.0, 13.0));
        #endif
        }

        // Bends the fish sideways (object-space X) with a wave travelling from head to tail.
        void Swim(inout float3 positionOS, inout float3 normalOS, float s)
        {
            float seed = InstanceSeed();
            float phase = Hash11(seed) * 6.2831853;
            float freq = _SwimFrequency * lerp(0.8, 1.25, Hash11(seed + 17.0));

            float k = 6.2831853 * _WaveCount;
            float w = 6.2831853 * freq;
            float arg = k * s - w * _Time.y + phase;

            float envelope = lerp(_HeadAmplitude, _SwimAmplitude, s * s);
            float dEnvelope = (_SwimAmplitude - _HeadAmplitude) * 2.0 * s;

            positionOS.x += envelope * sin(arg);

            // d(offset)/ds, and z = 0.5 - s, so d(offset)/dz = -d(offset)/ds.
            float slope = -(dEnvelope * sin(arg) + envelope * k * cos(arg));
            float angle = atan(slope);
            float c = cos(angle), sn = sin(angle);
            // Rotate the normal around Y so lighting follows the bend.
            normalOS = float3(c * normalOS.x + sn * normalOS.z, normalOS.y, -sn * normalOS.x + c * normalOS.z);
        }

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS   : NORMAL;
            float2 uv         : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                float3 pos = v.positionOS.xyz;
                float3 nrm = v.normalOS;
                Swim(pos, nrm, v.uv.x);

                VertexPositionInputs vp = GetVertexPositionInputs(pos);
                o.positionCS = vp.positionCS;
                o.positionWS = vp.positionWS;
                o.normalWS = TransformObjectToWorldNormal(nrm);
                o.uv = v.uv;
                o.fogFactor = ComputeFogFactor(vp.positionCS.z);
                return o;
            }

            half4 frag(Varyings i, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 n = normalize(i.normalWS) * (isFrontFace ? 1.0 : -1.0);
                float3 viewDir = GetWorldSpaceNormalizeViewDir(i.positionWS);

                // Counter-shading + a soft lateral stripe.
                half h = i.uv.y;
                half3 albedo = lerp(_BellyColor.rgb, _BackColor.rgb, smoothstep(-0.15, 0.55, h));
                half stripe = exp(-pow((h - 0.2) * 9.0, 2.0)) * smoothstep(0.1, 0.3, i.uv.x) * (1.0 - smoothstep(0.75, 0.86, i.uv.x));
                albedo = lerp(albedo, _StripeColor.rgb, stripe * 0.6);

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half atten = mainLight.distanceAttenuation * mainLight.shadowAttenuation;
                // Wrapped diffuse: light scattered by the water reaches the sides of the body too.
                half ndl = saturate((dot(n, mainLight.direction) + 0.4) / 1.4);
                half3 diffuse = albedo * mainLight.color * ndl * atten;
                half3 ambient = albedo * SampleSH(n);

                float3 halfDir = normalize(mainLight.direction + viewDir);
                half specPower = exp2(10.0 * _Smoothness + 1.0);
                half3 spec = mainLight.color * pow(saturate(dot(n, halfDir)), specPower) * _Smoothness * atten;

                // Silvery, mirror-like flanks: fish scales reflect the bright water around them.
                // Strong on the belly and sides, weak on the dark back.
                half fresnel = pow(1.0 - saturate(dot(n, viewDir)), 3.0);
                half silver = lerp(1.0, 0.2, smoothstep(0.0, 0.7, h));
                half3 sheen = SampleSH(reflect(-viewDir, n)) * (0.35 + 0.65 * fresnel) * silver * _Sheen;

                half3 color = diffuse + ambient + spec + sheen;
                color = MixFog(color, i.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            float4 vert(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                float3 pos = v.positionOS.xyz;
                float3 nrm = v.normalOS;
                Swim(pos, nrm, v.uv.x);

                float3 positionWS = TransformObjectToWorld(pos);
                float3 normalWS = TransformObjectToWorldNormal(nrm);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDir = normalize(_LightPosition - positionWS);
            #else
                float3 lightDir = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDir));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            float4 vert(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                float3 pos = v.positionOS.xyz;
                float3 nrm = v.normalOS;
                Swim(pos, nrm, v.uv.x);
                return TransformObjectToHClip(pos);
            }

            half frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                float3 pos = v.positionOS.xyz;
                float3 nrm = v.normalOS;
                Swim(pos, nrm, v.uv.x);
                o.positionCS = TransformObjectToHClip(pos);
                o.normalWS = TransformObjectToWorldNormal(nrm);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                return half4(NormalizeNormalPerPixel(i.normalWS), 0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
