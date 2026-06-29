using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// One shared <see cref="Texture2DArray"/> (R8) holding the channel atlases of MANY sections, so they can
    /// be drawn with ONE material in instanced BRG batches (the scaling fix — per-section materials cost one
    /// SetPass each). Each presented section is given a contiguous slice range by a free-list allocator;
    /// per-instance data tells the shader its <c>sliceBase</c>. All sections must share the fixed
    /// <see cref="Resolution"/> (see <see cref="ChannelUVSettings.FixedResolution"/>).
    ///
    /// This is the by-hand equivalent of what Nanite does for Unreal: collapse many primitives into one
    /// GPU-driven batch. (doc plan: brg-shared-atlas-instancing)
    /// </summary>
    public sealed class SharedChannelAtlas : System.IDisposable
    {
        public int Resolution { get; }
        public int Capacity { get; }                 // max slices
        public Texture2DArray Texture { get; private set; }

        // Simple free-list of [start,count) ranges over the slice index space.
        struct Range { public int Start, Count; }
        readonly List<Range> _free = new();
        int _highWater;                              // first never-allocated slice

        public SharedChannelAtlas(int resolution, int capacity)
        {
            Resolution = math.max(4, resolution);
            Capacity = math.max(1, capacity);
            Texture = new Texture2DArray(Resolution, Resolution, Capacity, TextureFormat.R8, mipChain: true, linear: true)
            {
                name = $"SharedChannelAtlas_{Resolution}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        }

        /// <summary>Reserves <paramref name="count"/> contiguous slices; returns the base index, or -1 if full.</summary>
        public int Allocate(int count)
        {
            if (count <= 0) return -1;

            // Reuse a free range if one fits (first-fit).
            for (int i = 0; i < _free.Count; i++)
            {
                if (_free[i].Count >= count)
                {
                    int start = _free[i].Start;
                    if (_free[i].Count == count) _free.RemoveAt(i);
                    else _free[i] = new Range { Start = start + count, Count = _free[i].Count - count };
                    return start;
                }
            }

            // Otherwise bump the high-water mark.
            if (_highWater + count <= Capacity)
            {
                int start = _highWater;
                _highWater += count;
                return start;
            }
            return -1; // full
        }

        public void Free(int start, int count)
        {
            if (start < 0 || count <= 0) return;
            _free.Add(new Range { Start = start, Count = count });
            // (No coalescing in v1; ranges are uniform-sized per section so fragmentation is bounded.)
        }

        /// <summary>Uploads a section's R8 atlas blob (tightly packed, <c>res*res</c> bytes per slice) into the
        /// shared array starting at <paramref name="sliceBase"/>. Main thread.</summary>
        public void WriteSlices(int sliceBase, byte[] blob, int blobResolution, int sliceCount)
        {
            if (blob == null || sliceCount <= 0) return;
            if (blobResolution != Resolution)
            {
                Debug.LogWarning($"SharedChannelAtlas: blob res {blobResolution} != atlas res {Resolution}; skipping (needs FixedResolution).");
                return;
            }

            int sliceBytes = Resolution * Resolution;
            for (int s = 0; s < sliceCount; s++)
            {
                int dstSlice = sliceBase + s;
                if (dstSlice >= Capacity) break;
                var data = Texture.GetPixelData<byte>(0, dstSlice);
                int srcOffset = s * sliceBytes;
                int n = math.min(sliceBytes, blob.Length - srcOffset);
                for (int i = 0; i < n; i++) data[i] = blob[srcOffset + i];
            }
            // Defer the GPU upload: Texture.Apply re-uploads the ENTIRE array (Capacity slices) and (with
            // mipmaps) regenerates all mips — far too costly to run once per presented section. Mark dirty and
            // let the presenter Flush() once per frame so a burst of presents costs a single upload.
            _dirty = true;
        }

        bool _dirty;

        /// <summary>Uploads pending slice writes to the GPU once. Call at most once per frame (e.g. before the
        /// BRG cull/draw) so many per-section writes batch into a single Texture.Apply.</summary>
        public void Flush()
        {
            if (!_dirty) return;
            _dirty = false;
            Texture.Apply(updateMipmaps: true);
        }

        public void Dispose()
        {
            if (Texture != null)
            {
                if (Application.isPlaying) Object.Destroy(Texture);
                else Object.DestroyImmediate(Texture);
                Texture = null;
            }
            _free.Clear();
            _highWater = 0;
        }
    }
}
