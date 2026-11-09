// Small procedural noise toolkit shared by the environment shaders (no textures needed).
#ifndef SEASIM_ENVIRONMENT_NOISE_INCLUDED
#define SEASIM_ENVIRONMENT_NOISE_INCLUDED

// Hash of a 3D cell, 0..1.
float Hash13(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return frac((p.x + p.y) * p.z);
}

// Smooth 3D value noise, 0..1.
float ValueNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * (3.0 - 2.0 * f);
    float n000 = Hash13(i), n100 = Hash13(i + float3(1, 0, 0));
    float n010 = Hash13(i + float3(0, 1, 0)), n110 = Hash13(i + float3(1, 1, 0));
    float n001 = Hash13(i + float3(0, 0, 1)), n101 = Hash13(i + float3(1, 0, 1));
    float n011 = Hash13(i + float3(0, 1, 1)), n111 = Hash13(i + float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y),
                lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y), u.z);
}

// Three octaves of value noise, 0..1.
float Fbm(float3 p)
{
    return ValueNoise(p) * 0.57 + ValueNoise(p * 2.03 + 17.1) * 0.29 + ValueNoise(p * 4.07 + 41.7) * 0.14;
}

// Tilts a surface normal by the gradient of a height field (gradient in world space, per metre).
float3 PerturbNormal(float3 n, float3 gradient, float strength)
{
    float3 tangential = gradient - n * dot(gradient, n);
    return normalize(n - tangential * strength);
}

#endif
