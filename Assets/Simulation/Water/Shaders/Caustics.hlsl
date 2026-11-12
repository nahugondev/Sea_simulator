// Underwater caustics: the bright, moving web of light that a wavy water surface focuses onto everything below it.
// Shared by the environment, fish and light-shaft shaders; settings are global shader values set by UnderwaterLight.
//
// How it works:
// - The pattern is baked once per frame into a tiling texture (CausticsBake.shader): the distance to the cell
//   borders of two layers of animated Worley cells. Light gathers along those borders.
// - It lives on the water surface and is projected along the sun direction, so every user - the ground, the fish
//   and the light shafts in the water - sees the same pattern at the same place: a light shaft is the column of
//   water lit above a caustic line, and a fish crossing it lights up at the same time as the sand below.
// - Each user turns the distances into lines of its own width; they fade with depth below the surface.
#ifndef SEASIM_CAUSTICS_INCLUDED
#define SEASIM_CAUSTICS_INCLUDED

// Cells across the baked texture (the pattern repeats every CAUSTICS_TILE cells).
#define CAUSTICS_TILE 8.0

TEXTURE2D(_CausticsTex); SAMPLER(sampler_CausticsTex);
// x = cell size (m), y = speed, z = intensity, w = surface height (world Y)
float4 _CausticsParams;
// x = depth (m) over which caustics fade to 1/e, y = distance (m) at which they are fully faded to their average,
// z = strength on fish (relative to the ground), w = line width on fish (relative to the ground)
float4 _CausticsFade;

// Position on the surface pattern (in cells) of the light reaching positionWS, and its depth below the surface.
float2 CausticsUV(float3 positionWS, float3 lightDir, out float depth)
{
    depth = max(_CausticsParams.w - positionWS.y, 0.0);
    float2 uv = positionWS.xz + lightDir.xz * (depth / max(lightDir.y, 0.2));
    return uv / max(_CausticsParams.x, 0.01);
}

// The light web: 0 between the lines, up to 2 where two lines cross. widthScale widens the lines (1 = sharp, as on
// the ground); wider lines get a softer profile. average = the web's mean value.
float CausticsLines(float2 uv, float depth, float widthScale, out float average)
{
    float2 e = SAMPLE_TEXTURE2D_LOD(_CausticsTex, sampler_CausticsTex, uv / CAUSTICS_TILE, 0).rg;
    float width = (0.06 + depth * 0.002) * widthScale;   // light spreads out with depth
    float sharpness = 2.0 / widthScale + 0.5;
    average = 0.35 * widthScale;
    return pow(saturate(1.0 - e.x / width), sharpness) + pow(saturate(1.0 - e.y / width), sharpness);
}

// On a surface: fades the fine web toward its average where it would alias (far away, grazing angles).
float CausticsSurfaceWeb(float3 positionWS, float3 lightDir, float widthScale, out float average, out float depthFade)
{
    float depth;
    float2 uv = CausticsUV(positionWS, lightDir, depth);
    float web = CausticsLines(uv, depth, widthScale, average);
    float footprint = length(fwidth(uv)) / widthScale;
    float detail = saturate(1.0 - footprint * 4.0)
                 * saturate(1.0 - distance(positionWS, _WorldSpaceCameraPos) / max(_CausticsFade.y, 1.0));
    depthFade = exp(-depth / max(_CausticsFade.x, 0.1));
    return lerp(average, web, detail);
}

// Caustics on the environment: extra sunlight on the sharp web (0 = none). Multiply by the direct sunlight.
half Caustics(float3 positionWS, float3 lightDir)
{
    float intensity = _CausticsParams.z;
    if (intensity <= 0.0) return 0.0;
    float average, depthFade;
    float web = CausticsSurfaceWeb(positionWS, lightDir, 1.0, average, depthFade);
    return web * intensity * depthFade;
}

// Caustics on fish, as a multiplier of the direct sunlight (1 = unchanged). The surface only redistributes light:
// brighter on the lines, darker between them. Unlike an added glow, the darker gaps stay visible on fish whose
// colours are already close to full brightness. Wider lines (fish are small), own strength (.z) and width (.w).
half CausticsFish(float3 positionWS, float3 lightDir)
{
    float strength = _CausticsFade.z;
    if (_CausticsParams.z <= 0.0 || strength <= 0.0) return 1.0;
    float average, depthFade;
    float web = CausticsSurfaceWeb(positionWS, lightDir, max(_CausticsFade.w, 1.0), average, depthFade);
    return clamp(1.0 + (web / average - 1.0) * strength * depthFade, 0.1, 4.0);
}

// Inside the water (light shafts): the same redistribution, sampled along a view ray, so no screen-space fading.
// contrast: 0 = uniform light, 1 = full contrast between the lit columns and the gaps.
half CausticsVolume(float3 positionWS, float3 lightDir, float widthScale, float contrast)
{
    if (_CausticsParams.z <= 0.0) return 1.0;
    float depth, average;
    float2 uv = CausticsUV(positionWS, lightDir, depth);
    float web = CausticsLines(uv, depth, widthScale, average);
    return max(0.0, 1.0 + (web / average - 1.0) * contrast * exp(-depth / max(_CausticsFade.x, 0.1)));
}

#endif
