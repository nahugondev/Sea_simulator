// Bakes the caustics pattern once per frame into a small tiling texture (see UnderwaterLight), so every shader that
// needs it (ground, fish, light shafts) reads the very same pattern with a single texture fetch instead of
// recomputing it. The texture covers CAUSTICS_TILE x CAUSTICS_TILE cells and tiles seamlessly: the cell hashes and
// the warp are periodic over the tile.
//
// It stores distances, not the final light: R and G = distance to the nearest cell border (F2 - F1) of two layers of
// animated Worley cells. Each user turns them into bright lines of its own width (sharp on the ground, softer on
// fish and in the water), so one bake serves all of them.
Shader "Hidden/SeaSim/CausticsBake"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Caustics.hlsl"

            float _CausticsTime;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            float2 Hash22(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.xx + p3.yz) * p3.zy);
            }

            // Distance to the nearest cell border (F2 - F1) of an animated Worley pattern that repeats every
            // 'period' cells.
            float CellEdge(float2 uv, float time, float period)
            {
                float2 cell = floor(uv);
                float2 f = frac(uv);
                float f1 = 8.0, f2 = 8.0;
                [unroll] for (int y = -1; y <= 1; y++)
                [unroll] for (int x = -1; x <= 1; x++)
                {
                    float2 o = float2(x, y);
                    float2 c = cell + o;
                    c -= period * floor(c / period);          // wrap: the pattern tiles
                    float2 h = Hash22(c);
                    // Each feature point circles around its cell at its own pace: the pattern drifts and morphs.
                    float2 pt = o + 0.5 + 0.38 * sin(time + 6.2831853 * h);
                    float d = length(pt - f);
                    if (d < f1) { f2 = f1; f1 = d; }
                    else if (d < f2) { f2 = d; }
                }
                return f2 - f1;
            }

            float2 frag(Varyings i) : SV_Target
            {
                const float N = CAUSTICS_TILE;
                float2 uv = i.uv * N;                     // in cells
                float t = _CausticsTime;
                // Gentle, moving warp (periodic over the tile): bends the straight cell borders into the curved,
                // wavy filaments of real caustics.
                float2 a = 6.2831853 * uv / N;
                float2 warp = float2(sin(a.y * 2.0 + t * 0.7), sin(a.x * 2.0 - t * 0.6)) * 0.32
                            + float2(sin(a.y * 5.0 - t * 1.1), sin(a.x * 5.0 + t * 0.9)) * 0.08;
                float e1 = CellEdge(uv + warp, t, N);
                // Second layer at 11/8 of the scale: still periodic over the tile (11 cells across it).
                float e2 = CellEdge(uv * (11.0 / 8.0) + float2(3.0, 5.0) - warp.yx * 1.2, t * 1.23 + 2.0, 11.0);
                return float2(e1, e2);
            }
            ENDHLSL
        }
    }
}
