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

            ApplyModifiers(ordered, mesh, weights, meshToWorld);

            return new ModifierResult { Mesh = mesh, Weights = weights };
        }

        /// <summary>
        /// Phase 5 bounded per-cell build (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §6</c>): produces just the
        /// section at <paramref name="absoluteCoord"/> without rebuilding the whole world. The base modifier
        /// emits only the geometry within the cell bounds (widened by <paramref name="cellMargin"/> on X/Z to
        /// cover centroid jitter + the skirt ribbon), non-base modifiers run through views bounded to those same
        /// cell bounds, and the result is partitioned with the <b>same</b> anchor so the centroid assignment
        /// carves out exactly the cell a full build would (§6.3 consistency invariant). The caller owns and must
        /// dispose the returned <see cref="ModifierResult"/>.
        ///
        /// This pass supports a <see cref="RectangleBaseModifier"/> (incl. its <c>HeightFn</c>) base only;
        /// an arbitrary source base mesh (per-cell AABB query) is deferred (§6.2, risk #5).
        /// </summary>
        public static ModifierResult ProcessCell(
            IEnumerable<ModifierComponent> modifiers,
            in GridSettings grid,
            in GridDimensions dims,
            int3 absoluteCoord,
            float cellMargin,
            float4x4 meshToWorld,
            Allocator allocator = Allocator.Persistent)
        {
            var ordered = modifiers
                .Where(m => m != null && !m.IsDisabled)
                .OrderBy(m => m.PriorityLayer)
                .ThenBy(m => m.SubPriority)
                .ToList();

            var baseModifier = ordered.FirstOrDefault(m => m.IsBase);
            if (baseModifier == null)
                throw new InvalidOperationException("ModifierGroup.ProcessCell: the stack has no base modifier to produce geometry.");
            if (!(baseModifier is RectangleBaseModifier rectBase))
                throw new NotSupportedException(
                    "ModifierGroup.ProcessCell currently supports a RectangleBaseModifier base only " +
                    "(arbitrary base-mesh per-cell extraction is deferred — see doc/08 §6.2, risk #5).");

            Bounds cellBounds = CellBounds(dims, absoluteCoord, cellMargin, grid.Is2D);

            // Bounded base: a sub-block of the same global grid → identical positions/UVs to a full build.
            MeshData boundedMesh = rectBase.ProduceBaseMesh(cellBounds, allocator);
            var boundedWeights = new WeightLayerSet(allocator);

            ApplyModifiers(ordered, boundedMesh, boundedWeights, meshToWorld, cellBounds);

            // Partition the bounded mesh with the same anchor, then extract the requested cell. The stable
            // anchor makes the absolute coordinates line up with a full build (§6.3).
            var partition = MeshPartitioner.Partition(boundedMesh, grid, boundedWeights, allocator);
            try
            {
                int found = -1;
                for (int i = 0; i < partition.SectionCount; i++)
                {
                    if (partition.SectionCoords[i].Equals(absoluteCoord)) { found = i; break; }
                }

                if (found < 0)
                {
                    // The cell produced no geometry (e.g. fully outside the rectangle): empty section.
                    return new ModifierResult
                    {
                        Mesh = MeshData.Allocate(0, 0, allocator,
                            withNormals: boundedMesh.HasNormals,
                            withChannelUVs: boundedMesh.HasChannelUVs,
                            withSourceUV0: boundedMesh.HasSourceUV0,
                            withBaseIDs: boundedMesh.HasBaseIDs),
                        Weights = new WeightLayerSet(allocator),
                    };
                }

                // Take ownership of the matched section's buffers out of the PartitionResult so disposing the
                // rest does not free them.
                MeshData cellMesh = partition.Sections[found];
                partition.Sections[found] = default;
                WeightLayerSet cellWeights = partition.SectionWeights != null ? partition.SectionWeights[found] : new WeightLayerSet(allocator);
                if (partition.SectionWeights != null) partition.SectionWeights[found] = null;

                return new ModifierResult { Mesh = cellMesh, Weights = cellWeights };
            }
            finally
            {
                partition.Dispose();
                boundedMesh.Dispose();
                boundedWeights.Dispose();
            }
        }

        /// <summary>
        /// Applies the non-base modifiers of an already-ordered stack onto <paramref name="mesh"/>/
        /// <paramref name="weights"/>. When <paramref name="clampBounds"/> is supplied (bounded per-cell build),
        /// each instance's view is intersected with it so a modifier never touches vertices outside the cell.
        /// </summary>
        static void ApplyModifiers(List<ModifierComponent> ordered, MeshData mesh, WeightLayerSet weights,
            float4x4 meshToWorld, Bounds? clampBounds = null)
        {
            var instances = new List<InstanceInfo>();
            Bounds meshBounds = MeshBounds(mesh);
            Bounds queryBounds = clampBounds.HasValue ? Intersection(meshBounds, clampBounds.Value) : meshBounds;

            foreach (var modifier in ordered)
            {
                if (modifier.IsBase) continue;

                IModifierJob job = modifier.CreateJob();
                if (job == null) continue;

                instances.Clear();
                job.GetInstancesInBounds(queryBounds, instances);

                foreach (var instance in instances)
                {
                    Bounds viewBounds = clampBounds.HasValue ? Intersection(instance.Bounds, clampBounds.Value) : instance.Bounds;
                    if (clampBounds.HasValue && (viewBounds.size.x < 0f || viewBounds.size.z < 0f))
                        continue; // instance does not overlap the cell

                    // Ensure declared channels exist before the view caches/writes them.
                    if (instance.UsedChannels != null)
                        foreach (var channel in instance.UsedChannels)
                            weights.InitializeLayer(channel, mesh.VertexCount);

                    var view = new MeshView(mesh, weights, viewBounds,
                        instance.Read, instance.Write, instance.UsedChannels);
                    view.Build();
                    job.ApplyModifications(view, meshToWorld, instance);
                    view.Writeback();
                }
            }
        }

        /// <summary>Mesh-local AABB of cell <paramref name="absoluteCoord"/>, widened by <paramref name="margin"/>
        /// on X/Z (full Y span is already baked into <c>CellExtent</c> in 2D mode).</summary>
        static Bounds CellBounds(in GridDimensions dims, int3 absoluteCoord, float margin, bool is2D)
        {
            int3 local = absoluteCoord - dims.OriginCoord;
            float3 min = dims.CellMin(local);
            float3 max = min + dims.CellExtent;
            var b = new Bounds();
            b.SetMinMax((Vector3)min, (Vector3)max);
            b.Expand(new Vector3(2f * margin, is2D ? 0f : 2f * margin, 2f * margin));
            return b;
        }

        static Bounds Intersection(Bounds a, Bounds b)
        {
            float3 min = math.max((float3)a.min, (float3)b.min);
            float3 max = math.min((float3)a.max, (float3)b.max);
            var r = new Bounds();
            r.SetMinMax((Vector3)min, (Vector3)max);
            return r;
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
