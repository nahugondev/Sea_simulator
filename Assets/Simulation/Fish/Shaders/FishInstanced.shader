// Instanced fish shader for URP.
// - Swimming: a travelling sine wave is applied in the vertex shader. Its amplitude grows from head to tail.
//   The position along the body is read from the geometry (s = 0.5 - z: the fish points along +Z, is 1 unit
//   long and centred), so the same wave drives the generated bodies and imported 3D models alike.
//   * Instanced (FishSchool): the tail phase and effort of each fish come from the simulation, through the
//     _FishSwimState StructuredBuffer, so the tail beat follows the fish's speed (burst and glide).
//     Only use GPU instancing with this shader through FishSchool, which binds that buffer.
//   * Not instanced (e.g. FishPredator): _SwimState = (phase, effort, useIt, 0) set from a MaterialPropertyBlock;
//     when _SwimState.z is 0 the tail simply beats at _SwimFrequency.
// - Colour, either:
//   * generated: counter-shading, dark back and silvery belly (UV0.y = -1 belly .. +1 back), stripe and bars;
//   * textured (Use Texture on, for imported models): the model's own texture, through its own UVs.
//   Both get a fresnel sheen, stronger on the flanks and belly than on the back.
Shader "SeaSim/FishInstanced"
{
    Properties
    {
        [Header(Texture (imported models))]
        [Toggle] _UseBaseMap ("Use Texture", Float) = 0
        _BaseMap ("Texture", 2D) = "white" {}
        _BaseColor ("Texture Tint", Color) = (1, 1, 1, 1)

        [Header(Generated colours)]
        _BackColor ("Back Color", Color) = (0.10, 0.20, 0.28, 1)
        _BellyColor ("Belly Color", Color) = (0.78, 0.82, 0.85, 1)
        _StripeColor ("Lateral Stripe Color", Color) = (0.35, 0.55, 0.65, 1)
        _BackBarColor ("Back Bars Color", Color) = (0.03, 0.06, 0.08, 1)
        _BackBarCount ("Back Bars Count (0 = none)", Float) = 0
        _BackBarStrength ("Back Bars Strength", Range(0, 1)) = 0.8
        _Smoothness ("Smoothness", Range(0, 1)) = 0.75
        _Sheen ("Silver Sheen", Range(0, 4)) = 2.0

        [Header(Swimming)]
        _SwimFrequency ("Tail Beats Per Second (fallback)", Float) = 2.2
        _SwimAmplitude ("Tail Amplitude", Float) = 0.07
        _HeadAmplitude ("Head Amplitude", Float) = 0.012
        _WaveCount ("Waves Along Body", Float) = 0.9
        [HideInInspector] _SwimState ("Swim State (phase, effort, enabled)", Vector) = (0, 1, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _UseBaseMap;
            half4 _BackColor;
            half4 _BellyColor;
            half4 _StripeColor;
            half4 _BackBarColor;
            half _BackBarCount;
            half _BackBarStrength;
            half _Smoothness;
            half _Sheen;
            float _SwimFrequency;
            float _SwimAmplitude;
            float _HeadAmplitude;
            float _WaveCount;
            float4 _SwimState;
        CBUFFER_END

        #if defined(UNITY_INSTANCING_ENABLED)
            // per fish: x = tail phase (rad), y = effort (0..1), z = light reaching the fish inside its school (0..1)
            StructuredBuffer<float4> _FishSwimState;
            float _FishInstanceOffset;               // first fish of the current draw batch
        #endif

        // Share of the sunlight reaching this fish through the bodies above it (1 outside schools / not instanced).
        float GetSchoolLight()
        {
        #if defined(UNITY_INSTANCING_ENABLED)
            return _FishSwimState[unity_InstanceID + (uint)_FishInstanceOffset].z;
        #else
            return 1.0;
        #endif
        }

        // Tail phase (radians) and effort (amplitude multiplier, low while gliding).
        void GetSwimState(out float phase, out float effort)
        {
        #if defined(UNITY_INSTANCING_ENABLED)
            float4 state = _FishSwimState[unity_InstanceID + (uint)_FishInstanceOffset];
            phase = state.x;
            effort = state.y;
        #else
            if (_SwimState.z > 0.5)
            {
                phase = _SwimState.x;
                effort = _SwimState.y;
            }
            else
            {
                phase = _Time.y * 6.2831853 * _SwimFrequency;
                effort = 1.0;
            }
        #endif
        }

        // Bends the fish sideways (object-space X) with a wave travelling from head to tail.
        void Swim(inout float3 positionOS, inout float3 normalOS, float s)
        {
            float phase, effort;
            GetSwimState(phase, effort);

            float k = 6.2831853 * _WaveCount;
            float arg = k * s - phase;

            float envelope = lerp(_HeadAmplitude, _SwimAmplitude, s * s) * effort;
            float dEnvelope = (_SwimAmplitude - _HeadAmplitude) * 2.0 * s * effort;

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
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "../../Water/Shaders/Caustics.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float  schoolLight : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.schoolLight = GetSchoolLight();

                float3 pos = v.positionOS.xyz;
                float3 nrm = v.normalOS;
                Swim(pos, nrm, saturate(0.5 - pos.z));

                VertexPositionInputs vp = GetVertexPositionInputs(pos);
                o.positionCS = vp.positionCS;
                o.positionWS = vp.positionWS;
                o.normalWS = TransformObjectToWorldNormal(nrm);
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 n = normalize(i.normalWS) * (isFrontFace ? 1.0 : -1.0);
                float3 viewDir = GetWorldSpaceNormalizeViewDir(i.positionWS);

                half3 albedo;
                half h; // -1 belly .. +1 back, drives the silver sheen
                if (_UseBaseMap > 0.5)
                {
                    // Imported model: its own texture. Fish swim level, so the world normal tells back from belly.
                    albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, TRANSFORM_TEX(i.uv, _BaseMap)).rgb * _BaseColor.rgb;
                    h = n.y;
                }
                else
                {
                    // Counter-shading + a soft lateral stripe.
                    h = i.uv.y;
                    albedo = lerp(_BellyColor.rgb, _BackColor.rgb, smoothstep(-0.15, 0.55, h));
                    half stripe = exp(-pow((h - 0.2) * 9.0, 2.0)) * smoothstep(0.1, 0.3, i.uv.x) * (1.0 - smoothstep(0.75, 0.86, i.uv.x));
                    albedo = lerp(albedo, _StripeColor.rgb, stripe * 0.6);

                    // Optional wavy dark bars across the back (e.g. mackerel), fading toward the flanks and the tail.
                    if (_BackBarCount > 0.5)
                    {
                        half wave = sin(i.uv.x * _BackBarCount * 6.2831853 + sin(h * 7.0) * 1.6);
                        half bars = smoothstep(0.15, 0.55, wave);
                        half mask = smoothstep(0.15, 0.55, h) * smoothstep(0.08, 0.2, i.uv.x) * (1.0 - smoothstep(0.78, 0.86, i.uv.x));
                        albedo = lerp(albedo, _BackBarColor.rgb, bars * mask * _BackBarStrength);
                    }
                }

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                // Only the shadow: the main light is the sun, whose distance attenuation is always 1. URP reads that
                // value from per-object data (unity_LightData) that GPU-instanced draws (RenderMeshInstanced) do not
                // fill: it is 0 there, which silently removed all direct sunlight from the fish.
                half atten = mainLight.shadowAttenuation;
                // Wrapped diffuse: light scattered by the water reaches the sides of the body too.
                half ndl = saturate((dot(n, mainLight.direction) + 0.4) / 1.4);
                // Caustics glide over the fish as it swims through the light web: the direct sunlight (wrapped, so the
                // flanks catch it too) gets brighter on the lines and darker between them. No effect in shadows.
                half caustics = CausticsFish(i.positionWS, mainLight.direction);
                half3 diffuse = albedo * mainLight.color * ndl * atten * caustics;
                half3 ambient = albedo * SampleSH(n);

                float3 halfDir = normalize(mainLight.direction + viewDir);
                half specPower = exp2(10.0 * _Smoothness + 1.0);
                half3 spec = mainLight.color * pow(saturate(dot(n, halfDir)), specPower) * _Smoothness * atten * caustics;

                // Silvery, mirror-like flanks: fish scales reflect the bright water around them.
                // Strong on the belly and sides, weak on the dark back.
                half fresnel = pow(1.0 - saturate(dot(n, viewDir)), 3.0);
                half silver = lerp(1.0, 0.2, smoothstep(0.0, 0.7, h));
                half3 sheen = SampleSH(reflect(-viewDir, n)) * (0.35 + 0.65 * fresnel) * silver * _Sheen;

                // Inside a school, the bodies above filter the sunlight (fully) and the light scattered from the
                // surface (partly: some still comes in from the sides).
                half schoolLight = i.schoolLight;
                half scattered = lerp(1.0, schoolLight, 0.6);
                half3 color = (diffuse + spec) * schoolLight + (ambient + sheen) * scattered;
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
            #pragma target 4.5
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
                Swim(pos, nrm, saturate(0.5 - pos.z));

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
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            float4 vert(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                float3 pos = v.positionOS.xyz;
                float3 nrm = v.normalOS;
                Swim(pos, nrm, saturate(0.5 - pos.z));
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
            #pragma target 4.5
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
                Swim(pos, nrm, saturate(0.5 - pos.z));
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
