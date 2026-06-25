using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Result of <see cref="ModifierGroup.Process"/>: the recompiled mesh and its weight-layer side-car.
    /// The caller owns both and must <see cref="Dispose"/>. Ready to hand to
    /// <see cref="MeshPartitioner.Partition"/>.
    /// </summary>
    public struct ModifierResult : IDisposable
    {
        public MeshData Mesh;
        public WeightLayerSet Weights;

        public void Dispose()
        {
            Mesh.Dispose();
            Weights?.Dispose();
        }
    }

    /// <summary>
    /// Applies a stack of modifiers to produce one recompiled <see cref="MeshData"/>. Unity port of UE
    /// <c>FMeshBuilder::ProcessModifierGroup</c> (see <c>doc/02_SYSTEM_ANALYSIS.md §5.3</c>).
    ///
    /// The stack is <b>non-destructive</b>: every call rebuilds a fresh mesh from the base modifier
    /// outward — no source/base buffer is mutated in place, so disabling a modifier and re-processing
    /// reproduces the lower-stack result exactly. The output feeds the Phase 1 partitioner (the pipeline
    /// runs modifiers first, then partitions — UE order).
    /// </summary>
    public static class ModifierGroup
    {
        /// <summary>
        /// Sorts <paramref name="modifiers"/> by <c>(PriorityLayer, SubPriority)</c>, lets the first base
        /// modifier produce the geometry, then applies each remaining modifier through a bounded
        /// <see cref="MeshView"/>. <paramref name="meshToWorld"/> is the mesh's world transform (identity
        /// for now). The caller owns and must dispose the returned <see cref="ModifierResult"/>.
        /// </summary>
        public static ModifierResult Process(
            IEnumerable<ModifierComponent> modifiers,
            float4x4 meshToWorld,
            Allocator allocator = Allocator.Persistent)
        {
            // Deterministic order: priority layer, then sub-priority. OrderBy is a stable sort.
            var ordered = modifiers
                .Where(m => m != null && !m.IsDisabled)
                .OrderBy(m => m.PriorityLayer)
                .ThenBy(m => m.SubPriority)
                .ToList();

            var baseModifier = ordered.FirstOrDefault(m => m.IsBase);
            if (baseModifier == null)
                throw new InvalidOperationException("ModifierGroup.Process: the stack has no base modifier to produce geometry.");

            // Base modifier produces the fresh working mesh (non-destructive recompile starts here).
            MeshData mesh = baseModifier.ProduceBaseMesh(allocator);
            var weights = new WeightLayerSet(allocator);

            var instances = new List<InstanceInfo>();
            foreach (var modifier in ordered)
            {
                if (modifier.IsBase) continue;

                IModifierJob job = modifier.CreateJob();
                if (job == null) continue;

                Bounds meshBounds = MeshBounds(mesh);

                instances.Clear();
                job.GetInstancesInBounds(meshBounds, instances);

                foreach (var instance in instances)
                {
                    // Ensure declared channels exist before the view caches/writes them.
                    if (instance.UsedChannels != null)
                        foreach (var channel in instance.UsedChannels)
                            weights.InitializeLayer(channel, mesh.VertexCount);

                    var view = new MeshView(mesh, weights, instance.Bounds,
                        instance.Read, instance.Write, instance.UsedChannels);
                    view.Build();
                    job.ApplyModifications(view, meshToWorld, instance);
                    view.Writeback();
                }
            }

            return new ModifierResult { Mesh = mesh, Weights = weights };
        }

        /// <summary>Mesh-local AABB of the working mesh (CPU reduce; the stack is managed this pass).</summary>
        static Bounds MeshBounds(in MeshData mesh)
        {
            if (mesh.VertexCount == 0)
                return new Bounds(Vector3.zero, Vector3.zero);

            float3 min = mesh.Vertices[0];
            float3 max = min;
            for (int i = 1; i < mesh.VertexCount; i++)
            {
                float3 p = mesh.Vertices[i];
                min = math.min(min, p);
                max = math.max(max, p);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }
    }
}
