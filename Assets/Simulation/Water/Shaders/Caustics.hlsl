// Underwater caustics: the bright, moving web of light that a wavy water surface focuses onto everything below it.
// Shared by the environment and fish shaders; its settings are global shader values set by UnderwaterLight.
//
// How it works:
// - The surface is approximated by two layers of animated cells (Worley noise). Light gathers along the cell
//   borders, so the distance to the nearest border gives a thin bright web; two layers at different scales and
//   speeds give the familiar interlaced pattern.
// - The pattern lives on the water surface and is projected along the sun direction, so it stays consistent with
//   the light and with the shadows (callers multiply it by the main light's shadow attenuation).
// - It fades with depth below the surface (light spreads out) and with distance to the camera, where the thin web
//   would alias; at that point it blends into its average brightness instead of shimmering.
#ifndef SEASIM_CAUSTICS_INCLUDED
#define SEASIM_CAUSTICS_INCLUDED

// x = cell size (m), y = speed, z = intensity, w = surface height (world Y)
float4 _CausticsParams;
// x = depth (m) over which caustics fade to 1/e, y = distance (m) at which they are fully faded to their average,
// z = strength on fish (relative to the ground), w = line width on fish (relative to the ground)
float4 _CausticsFade;

float2 CausticsHash22(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

// Distance to the nearest cell border (F2 - F1) of an animated Worley pattern.
float CausticsCellEdge(float2 uv, float time)
{
    float2 cell = floor(uv);
    float2 f = frac(uv);
    float f1 = 8.0, f2 = 8.0;
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        float2 o = float2(x, y);
        float2 h = CausticsHash22(cell + o);
        // Each feature point circles around its cell at its own pace: the pattern drifts and morphs.
        float2 pt = o + 0.5 + 0.38 * sin(time + 6.2831853 * h);
        float d = length(pt - f);
        if (d < f1) { f2 = f1; f1 = d; }
        else if (d < f2) { f2 = d; }
    }
    return f2 - f1;
}

// The light web at a world position: 0 between the lines, up to 2 where two lines cross.
// lightDir: direction toward the main light (Light.direction in URP).
// widthScale widens the lines (1 = sharp, as on the ground); the falloff gets softer as they widen.
// average: the web's average value (what it fades to at a distance). depthFade: attenuation with depth (0..1).
float CausticsWeb(float3 positionWS, float3 lightDir, float widthScale, out float average, out float depthFade)
{
    // Project the point up to the water surface along the light direction.
    float depth = max(_CausticsParams.w - positionWS.y, 0.0);
    float2 uv = positionWS.xz + lightDir.xz * (depth / max(lightDir.y, 0.2));
    uv /= max(_CausticsParams.x, 0.01);

    float t = _Time.y * _CausticsParams.y;
    // Gentle, moving domain warp: bends the straight cell borders into the curved, wavy filaments of real caustics.
    float2 warp = float2(sin(uv.y * 1.9 + t * 0.7), sin(uv.x * 1.7 - t * 0.6)) * 0.32
                + float2(sin(uv.y * 4.3 - t * 1.1), sin(uv.x * 3.9 + t * 0.9)) * 0.08;
    float e1 = CausticsCellEdge(uv + warp, t);
    float e2 = CausticsCellEdge(uv * 1.37 + float2(17.3, 9.1) - warp.yx * 1.2, t * 1.23 + 2.0);

    // Thin bright borders; the width grows with depth (light spreads out), which also softens deep caustics.
    float width = (0.06 + depth * 0.002) * widthScale;
    float sharpness = 2.0 / widthScale + 0.5;   // wide lines get a softer, rounder profile
    float web = pow(saturate(1.0 - e1 / width), sharpness) + pow(saturate(1.0 - e2 / width), sharpness);

    // Fade the fine web where it would alias, toward its average brightness.
    float footprint = length(fwidth(uv)) / widthScale;
    float detail = saturate(1.0 - footprint * 4.0)
                 * saturate(1.0 - distance(positionWS, _WorldSpaceCameraPos) / max(_CausticsFade.y, 1.0));
    average = 0.35 * widthScale;
    web = lerp(average, web, detail);

    depthFade = exp(-depth / max(_CausticsFade.x, 0.1));
    return web;
}

// Caustics on the environment: extra sunlight on the sharp web (0 = none). Multiply by the direct sunlight.
half Caustics(float3 positionWS, float3 lightDir)
{
    float intensity = _CausticsParams.z;
    if (intensity <= 0.0) return 0.0;
    float average, depthFade;
    float web = CausticsWeb(positionWS, lightDir, 1.0, average, depthFade);
    return web * intensity * depthFade;
}

// Caustics on fish, as a multiplier of the direct sunlight (1 = unchanged). The surface only redistributes light:
// brighter on the lines, darker between them. Unlike an added glow, the darker gaps stay visible on fish whose
// colours are already close to full brightness. Same pattern as the ground, with wider lines (fish are small) and
// its own strength (_CausticsFade.z) and width (_CausticsFade.w).
half CausticsFish(float3 positionWS, float3 lightDir)
{
    float strength = _CausticsFade.z;
    if (_CausticsParams.z <= 0.0 || strength <= 0.0) return 1.0;
    float average, depthFade;
    float web = CausticsWeb(positionWS, lightDir, max(_CausticsFade.w, 1.0), average, depthFade);
    return clamp(1.0 + (web / average - 1.0) * strength * depthFade, 0.1, 4.0);
}

#endif
