// Instanced fish shader for URP.
// - Swimming: a travelling sine wave is applied in the vertex shader. Its amplitude grows from head to tail.
//   The position along the body is read from the geometry (s = 0.5 - z: the fish points along +Z, is 1 unit
//   long and centred), so the same wave drives every 3D model alike.
//   * Instanced (FishSchool): the tail phase and effort of each fish come from the simulation, through the
//     _FishSwimState StructuredBuffer, so the tail beat follows the fish's speed (burst and glide).
//     Only use GPU instancing with this shader through FishSchool, which binds that buffer.
//   * Not instanced (e.g. FishPredator): _SwimState = (phase, effort, useIt, 0) set from a MaterialPropertyBlock;
//     when _SwimState.z is 0 the tail simply beats at _SwimFrequency.
// - Colour: the model's own texture, through its own UVs, with a fresnel sheen stronger on the flanks and belly
//   than on the back.
// - Variation between fish: each fish gets fixed random values from its index (no extra data), which drive
//   * its proportions: slightly deeper or slimmer body, larger or smaller tail (same in every pass),
//   * its colour: brightness, saturation and hue shifted a little,
//   * its wear: faded patches, a few scratches and paler fin edges, in a pattern of its own (some fish are
//     pristine, others weathered). Size differences (including young fish) come from the simulation.
Shader "SeaSim/FishInstanced"
{
    Properties
    {
        [Header(Look)]
        _BaseMap ("Texture", 2D) = "white" {}
        _BaseColor ("Texture Tint", Color) = (1, 1, 1, 1)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.75
        _Sheen ("Silver Sheen", Range(0, 4)) = 2.0

        [Header(Variation between fish)]
        _ColorVariation ("Colour Variation", Range(0, 1)) = 0.7
        _WearAmount ("Wear (faded patches, scars, scratches)", Range(0, 1)) = 0.7
        _ShapeVariation ("Shape Variation", Range(0, 1)) = 0.5

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
            half _Smoothness;
            half _Sheen;
            float _SwimFrequency;
            float _SwimAmplitude;
            float _HeadAmplitude;
            float _WaveCount;
            float4 _SwimState;
            half _ColorVariation;
            half _WearAmount;
            half _ShapeVariation;
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

        // Four fixed random values (0..1) for this fish, from its index in the simulation (stable over time).
        // 'salt' gives independent sets (shape, colour, wear). Not instanced (predator): always the same fish.
        float4 FishRandom(uint salt)
        {
        #if defined(UNITY_INSTANCING_ENABLED)
            uint id = unity_InstanceID + (uint)_FishInstanceOffset;
        #else
            uint id = 0u;
        #endif
            uint4 h = uint4(id, id + 0x9E3779B9u, id + 0x632BE5ABu, id + 0x85EBCA77u) * 747796405u + salt * 2891336453u;
            h = ((h >> ((h >> 28u) + 4u)) ^ h) * 277803737u;
            h = (h >> 22u) ^ h;
            return float4(h & 0xFFFFFFu) / 16777216.0;
        }

        // Proportions of this fish: body slightly deeper or slimmer, tail (caudal fin) larger or smaller.
        void Shape(inout float3 positionOS, inout float3 normalOS)
        {
            float4 r = FishRandom(1u) * 2.0 - 1.0;
            float k = _ShapeVariation;
            float s = saturate(0.5 - positionOS.z);                      // 0 nose .. 1 tail tip
            float tail = 1.0 + r.z * 0.15 * k * smoothstep(0.75, 1.0, s);
            float3 scale = float3(1.0 + r.y * 0.1 * k, (1.0 + r.x * 0.08 * k) * tail, 1.0);
            positionOS *= scale;
            normalOS = normalize(normalOS / scale);
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

        // Every deformation of the body, identical in every pass (shape, then the swimming wave).
        void DeformFish(inout float3 positionOS, inout float3 normalOS)
        {
            Shape(positionOS, normalOS);
            Swim(positionOS, normalOS, saturate(0.5 - positionOS.z));
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
            #include "../../Environment/Shaders/EnvironmentNoise.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float  schoolLight : TEXCOORD3;
                float3 bodyPos    : TEXCOORD4;           // position on the undeformed body: wear sticks to the fish
                nointerpolation float4 colorRandom : TEXCOORD5;
                nointerpolation float4 wearRandom  : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // Rotates a colour's hue (radians) around the grey axis.
            half3 HueShift(half3 c, half angle)
            {
                const half3 k = 0.57735;
                half ca = cos(angle), sa = sin(angle);
                return c * ca + cross(k, c) * sa + k * dot(k, c) * (1.0 - ca);
            }

            // This fish's own colour and wear, applied on top of its species' colours.
            // Returns how much its scales still shine (worn patches are duller).
            half Individualise(inout half3 albedo, float3 p, float4 colorRandom, float4 wearRandom)
            {
                // Colour: brightness, saturation and hue shifted a little.
                float4 c = colorRandom * 2.0 - 1.0;
                half k = _ColorVariation;
                half lum = dot(albedo, half3(0.299, 0.587, 0.114));
                albedo = lerp(lum.xxx, albedo, 1.0 + c.y * 0.6 * k);
                albedo = HueShift(albedo, c.z * 0.25 * k);
                albedo = max(albedo * (1.0 + c.x * 0.35 * k), 0.0);

                // Wear: how weathered this fish is. Spread over the whole range, so a school mixes pristine,
                // scuffed and badly marked fish.
                half level = saturate(_WearAmount * (0.2 + 1.8 * wearRandom.w));
                if (level <= 0.001) return 1.0;
                float3 seed = wearRandom.xyz * 97.0;
                lum = dot(albedo, half3(0.299, 0.587, 0.114));
                half3 faded = lerp(albedo, lum.xxx * 1.1 + 0.08, 0.85);   // washed out, almost grey
                half3 dark = albedo * 0.35;                                // bruises, healed bites

                // Large faded, discoloured areas.
                half patch = smoothstep(0.42, 0.68, Fbm(p * 4.5 + seed)) * level;
                albedo = lerp(albedo, faded, patch);

                // Missing scales: small pale spots where the skin shows.
                half spots = smoothstep(0.82, 0.9, ValueNoise(p * 38.0 + seed.zxy))
                           * smoothstep(0.45, 0.6, ValueNoise(p * 5.0 + seed.yzx)) * level;
                albedo = lerp(albedo, faded + 0.15, spots);

                // Dark scars: one or two bruises or healed bites on the most worn fish.
                half scar = smoothstep(0.78, 0.86, Fbm(p * 3.0 + seed.yxz + 31.0)) * saturate(level * 1.6 - 0.4);
                albedo = lerp(albedo, dark, scar * 0.85);

                // Scratches: thin bright lines at an angle of their own, more of them on worn fish.
                float angle = wearRandom.x * 3.14159;
                float2 dir = float2(cos(angle), sin(angle));
                float along = dot(p.zy, dir), across = dot(p.zy, float2(-dir.y, dir.x));
                half scratch = smoothstep(0.86, 0.94, ValueNoise(float3(across * 45.0, along * 2.0, seed.z)));
                half scratched = smoothstep(0.65 - 0.3 * level, 0.8 - 0.3 * level, ValueNoise(p * 6.0 + seed.yzx));
                albedo = lerp(albedo, faded + 0.25, scratch * scratched * saturate(level * 1.5));

                // Paler, frayed fin edges toward the tail tip.
                half s = saturate(0.5 - p.z);
                albedo = lerp(albedo, faded, smoothstep(0.82, 1.0, s) * level * 0.9);

                return 1.0 - (patch + spots) * 0.6;
            }

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.schoolLight = GetSchoolLight();
                o.bodyPos = v.positionOS.xyz;
                o.colorRandom = FishRandom(2u);
                o.wearRandom = FishRandom(3u);

                float3 pos = v.positionOS.xyz;
                float3 nrm = v.normalOS;
                DeformFish(pos, nrm);

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

                // The model's own texture. Fish swim level, so the world normal tells back from belly.
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, TRANSFORM_TEX(i.uv, _BaseMap)).rgb * _BaseColor.rgb;
                half h = n.y; // -1 belly .. +1 back, drives the silver sheen

                half shine = Individualise(albedo, i.bodyPos, i.colorRandom, i.wearRandom);

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
                half3 spec = mainLight.color * pow(saturate(dot(n, halfDir)), specPower) * _Smoothness * atten * caustics * shine;

                // Silvery, mirror-like flanks: fish scales reflect the bright water around them.
                // Strong on the belly and sides, weak on the dark back.
                half fresnel = pow(1.0 - saturate(dot(n, viewDir)), 3.0);
                half silver = lerp(1.0, 0.2, smoothstep(0.0, 0.7, h));
                half3 sheen = SampleSH(reflect(-viewDir, n)) * (0.35 + 0.65 * fresnel) * silver * _Sheen * shine;

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
                DeformFish(pos, nrm);

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
                DeformFish(pos, nrm);
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
                DeformFish(pos, nrm);
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
