#ifndef MESH_TERRAIN_CHANNEL_BLEND_INCLUDED
#define MESH_TERRAIN_CHANNEL_BLEND_INCLUDED

// Shared per-channel material LAYER blend for the terrain shaders (GameObject + BRG variants). The two
// presenters differ only in how they fetch a channel's WEIGHT (per-section atlas vs shared atlas + per-instance
// slice); the layer textures + blend math here are identical. Each shader must, before including this:
//   - define SAMPLE_CHANNEL_WEIGHT(c) -> float   (the section's weight for global channel c at this fragment)
//   - provide _AlbedoArray/_NormalArray/_MaskArray (Texture2DArray) + sampler, _LayerParams[24], _LayerCount.
//
// Blend model: weight + HEIGHT blend. Each layer's height (mask.b) biases its weight so transitions interlock
// (rock pokes through grass at edges) instead of a flat crossfade.

// Texture arrays (textures aren't CBUFFER members, so they live here and are SRP/BRG-compatible).
TEXTURE2D_ARRAY(_AlbedoArray); SAMPLER(sampler_AlbedoArray);
TEXTURE2D_ARRAY(_NormalArray);
TEXTURE2D_ARRAY(_MaskArray);

// NOTE: _LayerParams[24] (tiling, normalStrength, heightContrast, _) and _LayerCount are MATERIAL properties
// and MUST be declared in the shader's UnityPerMaterial CBUFFER (BRG requirement). The including shader
// declares them; this include only uses them.

struct TerrainSurface
{
    float3 albedo;
    float3 normalTS;   // tangent-space, accumulated
    float  roughness;
    float  ao;
    float  metallic;
};

// Unpacks a tangent-space normal from an RGBA normal map (xy in rg, z reconstructed), scaled by strength.
float3 UnpackTerrainNormal(float4 packed, float strength)
{
    float3 n;
    n.xy = (packed.rg * 2.0 - 1.0) * strength;
    n.z = sqrt(saturate(1.0 - dot(n.xy, n.xy)));
    return n;
}

// Blends all active channel layers at this fragment by height-biased weight. worldXZ drives layer tiling.
TerrainSurface BlendTerrainLayers(float2 worldXZ, float3 fallbackColor)
{
    TerrainSurface s;
    s.albedo = fallbackColor;
    s.normalTS = float3(0, 0, 1);
    s.roughness = 0.8;
    s.ao = 1.0;
    s.metallic = 0.0;

    int count = (int)round(_LayerCount);
    if (count <= 0) return s;

    float3 albedo = 0;
    float3 normalTS = 0;
    float roughness = 0;
    float ao = 0;
    float metallic = 0;
    float totalW = 0;

    [loop]
    for (int c = 0; c < count && c < 24; c++)
    {
        float w = SAMPLE_CHANNEL_WEIGHT(c);
        if (w <= 1e-4) continue;

        float4 p = _LayerParams[c];
        float tiling = max(0.01, p.x);
        float2 uv = worldXZ / tiling;

        float4 maskTex = SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_AlbedoArray, uv, c);
        float height = maskTex.b;

        // Height-biased weight: layers with higher local height win the transition band.
        float hw = w * (1.0 + (height - 0.5) * 2.0 * p.z);
        hw = max(hw, 0.0);

        float4 alb = SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_AlbedoArray, uv, c);
        float4 nrm = SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_AlbedoArray, uv, c);

        albedo   += alb.rgb * hw;
        normalTS += UnpackTerrainNormal(nrm, p.y) * hw;
        roughness += maskTex.r * hw;
        ao        += maskTex.g * hw;
        metallic  += maskTex.a * hw;
        totalW    += hw;
    }

    if (totalW > 1e-4)
    {
        float inv = 1.0 / totalW;
        s.albedo = albedo * inv;
        s.normalTS = normalize(normalTS * inv + float3(0, 0, 1e-3));
        s.roughness = roughness * inv;
        s.ao = ao * inv;
        s.metallic = metallic * inv;
    }
    return s;
}

#endif // MESH_TERRAIN_CHANNEL_BLEND_INCLUDED
