using System;
using System.Collections.Generic;
using Unity.Collections;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Managed side-car holding the per-vertex weight layers of a <see cref="MeshData"/>.
    /// Unity/Burst port of UE <c>FMeshData.WeightLayers : TMap&lt;FName, TArray&lt;float&gt;&gt;</c>.
    ///
    /// Burst jobs cannot hold managed maps or string keys, so the name → layer mapping lives here
    /// (managed) and jobs only ever receive raw <see cref="NativeArray{T}"/> layers (one scalar per
    /// vertex) or int channel indices. Mirrors UE's <c>InitializeWeightLayer</c> /
    /// <c>GetWeightLayerValue</c> / <c>SetWeightLayerValue</c> surface. See
    /// <c>doc/06_BURST_AND_COMPUTE.md §4</c>.
    /// </summary>
    public sealed class WeightLayerSet : IDisposable
    {
        readonly Dictionary<string, NativeArray<float>> _layers = new();
        readonly List<string> _names = new();
        readonly Allocator _allocator;

        public WeightLayerSet(Allocator allocator)
        {
            _allocator = allocator;
        }

        /// <summary>Ordered list of layer names. Index here is the int channel index handed to jobs.</summary>
        public IReadOnlyList<string> LayerNames => _names;

        public int LayerCount => _names.Count;

        public bool HasLayer(string name) => _layers.ContainsKey(name);

        /// <summary>
        /// Creates a zero-filled layer of <paramref name="vertexCount"/> scalars, or returns the
        /// existing one. UE equivalent: <c>InitializeWeightLayer</c> (<c>SetNumZeroed</c>).
        /// </summary>
        public NativeArray<float> InitializeLayer(string name, int vertexCount)
        {
            if (_layers.TryGetValue(name, out var existing))
                return existing;

            var layer = new NativeArray<float>(vertexCount, _allocator, NativeArrayOptions.ClearMemory);
            _layers.Add(name, layer);
            _names.Add(name);
            return layer;
        }

        public bool TryGetLayer(string name, out NativeArray<float> layer)
            => _layers.TryGetValue(name, out layer);

        /// <summary>Gets the channel index for a name, or -1 if absent (mirrors UE <c>FindChannel</c>).</summary>
        public int FindLayerIndex(string name) => _names.IndexOf(name);

        public NativeArray<float> GetLayerByIndex(int index) => _layers[_names[index]];

        public float GetValue(string name, int vertexId) => _layers[name][vertexId];

        public void SetValue(string name, int vertexId, float value)
        {
            var layer = _layers[name];
            layer[vertexId] = value;
        }

        public void Dispose()
        {
            foreach (var layer in _layers.Values)
            {
                if (layer.IsCreated)
                    layer.Dispose();
            }

            _layers.Clear();
            _names.Clear();
        }
    }
}
