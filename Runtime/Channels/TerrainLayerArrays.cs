using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Builds and owns the shared <see cref="Texture2DArray"/>s the terrain shader samples for per-channel
    /// material layers: one albedo array, one normal array, one mask array, slice index = channel index.
    /// Built once per Definition (not per section) and bound globally on the terrain material(s) — the channel
    /// WEIGHT (which layer shows where) stays per-section/per-instance, but the layer TEXTURES are shared.
    ///
    /// <para>Each layer's source <see cref="Texture2D"/>s are copied (Blit) into the matching array slice at a
    /// uniform <see cref="Resolution"/>, so mismatched input sizes are normalized. Missing maps get sensible
    /// defaults (white albedo, flat normal, mid mask).</para>
    /// </summary>
    public sealed class TerrainLayerArrays : IDisposable
    {
        public Texture2DArray Albedo { get; private set; }
        public Texture2DArray Normal { get; private set; }
        public Texture2DArray Mask { get; private set; }
        public int LayerCount { get; private set; }
        public int Resolution { get; private set; }

        // Per-layer params packed for the shader: (tiling, normalStrength, heightContrast, _).
        public Vector4[] LayerParams { get; private set; }

        public bool IsValid => LayerCount > 0 && Albedo != null;

        /// <summary>Builds arrays from the Definition's per-channel layers. Returns null if there are none.</summary>
        public static TerrainLayerArrays Build(IReadOnlyList<TerrainLayer> layers, int resolution = 512)
        {
            if (layers == null || layers.Count == 0) return null;

            int count = layers.Count;
            int res = Mathf.Max(4, resolution);

            var result = new TerrainLayerArrays
            {
                LayerCount = count,
                Resolution = res,
                LayerParams = new Vector4[count],
                Albedo = new Texture2DArray(res, res, count, TextureFormat.RGBA32, mipChain: true, linear: false)
                    { name = "TerrainAlbedo", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear },
                Normal = new Texture2DArray(res, res, count, TextureFormat.RGBA32, mipChain: true, linear: true)
                    { name = "TerrainNormal", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear },
                Mask = new Texture2DArray(res, res, count, TextureFormat.RGBA32, mipChain: true, linear: true)
                    { name = "TerrainMask", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear },
            };

            for (int i = 0; i < count; i++)
            {
                TerrainLayer layer = layers[i];
                CopyInto(result.Albedo, i, layer.Albedo, res, new Color(0.5f, 0.5f, 0.5f, 1f), srgb: true);
                CopyInto(result.Normal, i, layer.Normal, res, new Color(0.5f, 0.5f, 1f, 1f), srgb: false); // flat normal
                CopyInto(result.Mask, i, layer.Mask, res, new Color(0.5f, 1f, 0.5f, 0f), srgb: false);      // rough .5, AO 1, height .5
                result.LayerParams[i] = new Vector4(
                    Mathf.Max(0.01f, layer.Tiling == 0 ? 10f : layer.Tiling),
                    layer.NormalStrength == 0 ? 1f : layer.NormalStrength,
                    layer.HeightContrast,
                    0f);
            }

            result.Albedo.Apply(updateMipmaps: true, makeNoLongerReadable: false);
            result.Normal.Apply(updateMipmaps: true, makeNoLongerReadable: false);
            result.Mask.Apply(updateMipmaps: true, makeNoLongerReadable: false);
            return result;
        }

        /// <summary>Copies <paramref name="src"/> into <paramref name="dst"/> slice <paramref name="slice"/> at
        /// the array resolution (Blit handles resize/format), or fills a default color when src is null.</summary>
        static void CopyInto(Texture2DArray dst, int slice, Texture2D src, int res, Color fallback, bool srgb)
        {
            var rt = RenderTexture.GetTemporary(res, res, 0,
                srgb ? RenderTextureFormat.ARGB32 : RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            try
            {
                if (src != null) Graphics.Blit(src, rt);
                else { RenderTexture.active = rt; GL.Clear(false, true, fallback); }

                // Read back into a temp Texture2D, then copy to the array slice.
                RenderTexture.active = rt;
                var tmp = new Texture2D(res, res, TextureFormat.RGBA32, mipChain: false, linear: !srgb);
                tmp.ReadPixels(new Rect(0, 0, res, res), 0, 0);
                tmp.Apply(false);
                Graphics.CopyTexture(tmp, 0, 0, dst, slice, 0);
                if (Application.isPlaying) UnityEngine.Object.Destroy(tmp);
                else UnityEngine.Object.DestroyImmediate(tmp);
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>
        /// Binds the layer arrays + params GLOBALLY (terrain layers are shared by all sections). Globals avoid
        /// the BRG/SRP-Batcher "UnityPerMaterial" restriction that cbuffer arrays can't satisfy. The keyword is
        /// still enabled per-material so the layer-blend variant is selected.
        /// </summary>
        public void Apply(Material mat)
        {
            if (!IsValid) return;
            Shader.SetGlobalTexture(AlbedoArrayId, Albedo);
            Shader.SetGlobalTexture(NormalArrayId, Normal);
            Shader.SetGlobalTexture(MaskArrayId, Mask);
            Shader.SetGlobalVectorArray(LayerParamsId, LayerParams);
            Shader.SetGlobalFloat(LayerCountId, LayerCount);
            if (mat != null) mat.EnableKeyword("_TERRAIN_LAYERS");
        }

        public void Dispose()
        {
            DestroyArr(Albedo); Albedo = null;
            DestroyArr(Normal); Normal = null;
            DestroyArr(Mask); Mask = null;
            LayerCount = 0;
        }

        static void DestroyArr(Texture2DArray a)
        {
            if (a == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(a);
            else UnityEngine.Object.DestroyImmediate(a);
        }

        public static readonly int AlbedoArrayId = Shader.PropertyToID("_AlbedoArray");
        public static readonly int NormalArrayId = Shader.PropertyToID("_NormalArray");
        public static readonly int MaskArrayId = Shader.PropertyToID("_MaskArray");
        public static readonly int LayerParamsId = Shader.PropertyToID("_LayerParams");
        public static readonly int LayerCountId = Shader.PropertyToID("_LayerCount");
    }
}
