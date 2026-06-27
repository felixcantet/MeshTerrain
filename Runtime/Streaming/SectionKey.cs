using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// Cache key + invalidation hashes for one section. Unity port of UE
    /// <c>FCompiledSectionBuildInfo</c> (<c>doc/02_SYSTEM_ANALYSIS.md §9</c>,
    /// <c>doc/08_STREAMING_SYSTEM_DESIGN.md §7.1</c>).
    ///
    /// <para>Equality/identity is the absolute grid <see cref="Coord"/> plus four invalidation hashes:</para>
    /// <list type="bullet">
    ///   <item><see cref="ModifiersHash"/> — params of the modifiers <b>covering this cell</b> (scoped, so
    ///   editing one modifier only changes the hash of the cells it overlaps).</item>
    ///   <item><see cref="ModifierSetHash"/> — membership of the stack (a modifier added/removed).</item>
    ///   <item><see cref="VariantHash"/> — Definition + LOD/skirt/channel + <c>CellSize</c>/<c>Is2D</c>.</item>
    ///   <item><see cref="ClassHash"/> — implementation version of the modifier classes (manual bump).</item>
    /// </list>
    /// UE's <c>PackageHash</c> (referenced disk assets) is deferred — no assets are referenced yet.
    /// </summary>
    public readonly struct SectionKey : IEquatable<SectionKey>
    {
        public readonly int3 Coord;
        public readonly Hash128 ModifiersHash;
        public readonly Hash128 ModifierSetHash;
        public readonly Hash128 VariantHash;
        public readonly Hash128 ClassHash;

        public SectionKey(int3 coord, Hash128 modifiersHash, Hash128 modifierSetHash, Hash128 variantHash, Hash128 classHash)
        {
            Coord = coord;
            ModifiersHash = modifiersHash;
            ModifierSetHash = modifierSetHash;
            VariantHash = variantHash;
            ClassHash = classHash;
        }

        /// <summary>The combined content hash (excludes <see cref="Coord"/>, which is the per-cell address).</summary>
        public Hash128 ContentHash
        {
            get
            {
                var h = new Hash128();
                h.Append(ModifiersHash.ToString());
                h.Append(ModifierSetHash.ToString());
                h.Append(VariantHash.ToString());
                h.Append(ClassHash.ToString());
                return h;
            }
        }

        /// <summary>Disk filename stem: <c>x_y_z_&lt;contentHash&gt;</c>. Stable for unchanged inputs.</summary>
        public string FileStem => $"{Coord.x}_{Coord.y}_{Coord.z}_{ContentHash}";

        public bool Equals(SectionKey other)
            => Coord.Equals(other.Coord)
            && ModifiersHash == other.ModifiersHash
            && ModifierSetHash == other.ModifierSetHash
            && VariantHash == other.VariantHash
            && ClassHash == other.ClassHash;

        public override bool Equals(object obj) => obj is SectionKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Coord.GetHashCode();
                h = (h * 397) ^ ModifiersHash.GetHashCode();
                h = (h * 397) ^ ModifierSetHash.GetHashCode();
                h = (h * 397) ^ VariantHash.GetHashCode();
                h = (h * 397) ^ ClassHash.GetHashCode();
                return h;
            }
        }

        public override string ToString() => FileStem;
    }

    /// <summary>
    /// Builds <see cref="SectionKey"/>s from a modifier stack + grid/variant settings, with the
    /// bounds-scoped <see cref="SectionKey.ModifiersHash"/> that makes invalidation incremental
    /// (<c>doc/08 §7.1</c>). The class hash is a manual constant bumped when build code changes.
    /// </summary>
    public static class SectionKeyBuilder
    {
        /// <summary>
        /// Bump when the cook pipeline's <b>implementation</b> changes in a way that alters output for the
        /// same params (e.g. partition/skirt/channel code). Mirrors UE <c>MegaMeshClassVersion</c>.
        /// </summary>
        public const int ClassVersion = 3; // bumped: fixed-resolution atlas option (shared-atlas instancing)

        /// <summary>Class-version hash (folded into every key; a bump invalidates the whole cache).</summary>
        public static Hash128 ClassHash()
        {
            var h = new Hash128();
            h.Append(ClassVersion);
            return h;
        }

        /// <summary>Membership hash: which modifier types are present (order-independent of their params).</summary>
        public static Hash128 ModifierSetHash(IReadOnlyList<ModifierComponent> stack)
        {
            // Order-independent over type names so reordering equal-priority modifiers doesn't churn the set.
            var names = new List<string>(stack.Count);
            foreach (var m in stack)
                if (m != null) names.Add(m.GetType().FullName);
            names.Sort(StringComparer.Ordinal);

            var h = new Hash128();
            foreach (var n in names) h.Append(n);
            return h;
        }

        /// <summary>
        /// Variant hash: the per-build settings shared by every section. Folds the grid (<c>CellSize</c>/
        /// <c>Is2D</c>/anchor) and the channel-cook flags. LOD/skirt settings live in the presenter
        /// (Phase 5.2/5.3); extend this when they affect the <b>cooked</b> bytes.
        /// </summary>
        public static Hash128 VariantHash(in GridSettings grid, in ChannelCookOptions channels, in LodCookOptions lod)
        {
            var h = new Hash128();
            h.Append(grid.CellSize);
            h.Append(grid.Is2D ? 1 : 0);
            h.AppendValue(grid.WorldOriginOffset);
            h.Append(channels.Generate ? 1 : 0);
            h.Append(channels.TexelSize3D);
            h.Append(channels.GutterFill ? 1 : 0);
            h.Append(channels.FixedResolution);

            // LOD baking affects the cooked bytes (the blob now carries baked LODs).
            h.Append(lod.BakeLods ? 1 : 0);
            if (lod.Qualities != null)
                foreach (var q in lod.Qualities) h.Append(q);
            h.Append(lod.Skirt.Enabled ? 1 : 0);
            h.Append(lod.Skirt.Width);
            h.Append(lod.Skirt.PushDown);
            h.Append((int)lod.Skirt.PushMethod);
            return h;
        }

        /// <summary>
        /// Scoped modifiers hash for <paramref name="coord"/>: folds only the modifiers whose
        /// <see cref="ModifierComponent.ComputeBounds"/> intersect the cell (widened by
        /// <paramref name="cellMargin"/>). Editing a modifier thus changes only the cells it overlaps.
        /// </summary>
        public static Hash128 ModifiersHashForCell(
            IReadOnlyList<ModifierComponent> stack, in GridDimensions dims, int3 coord, float cellMargin, bool is2D)
        {
            Bounds cell = CellBounds(dims, coord, cellMargin, is2D);

            // Deterministic order: priority then sub-priority (the apply order), so the hash is stable.
            var covering = new List<ModifierComponent>();
            foreach (var m in stack)
            {
                if (m == null || m.IsDisabled) continue;
                if (m.IsBase || Intersects(m.ComputeBounds(), cell))
                    covering.Add(m);
            }
            covering.Sort((a, b) =>
            {
                int p = a.PriorityLayer.CompareTo(b.PriorityLayer);
                return p != 0 ? p : a.SubPriority.CompareTo(b.SubPriority);
            });

            var h = new Hash128();
            foreach (var m in covering)
            {
                Hash128 mh = m.ComputeParamsHash();
                h.Append(mh.ToString());
            }
            return h;
        }

        /// <summary>Builds the full key for <paramref name="coord"/>.</summary>
        public static SectionKey Build(
            IReadOnlyList<ModifierComponent> stack, in GridSettings grid, in GridDimensions dims,
            int3 coord, float cellMargin, in ChannelCookOptions channels, in LodCookOptions lod)
        {
            return new SectionKey(
                coord,
                ModifiersHashForCell(stack, dims, coord, cellMargin, grid.Is2D),
                ModifierSetHash(stack),
                VariantHash(grid, channels, lod),
                ClassHash());
        }

        static Bounds CellBounds(in GridDimensions dims, int3 coord, float margin, bool is2D)
        {
            int3 local = coord - dims.OriginCoord;
            float3 min = dims.CellMin(local);
            float3 max = min + dims.CellExtent;
            var b = new Bounds();
            b.SetMinMax((Vector3)min, (Vector3)max);
            b.Expand(new Vector3(2f * margin, is2D ? 0f : 2f * margin, 2f * margin));
            return b;
        }

        static bool Intersects(Bounds a, Bounds b)
        {
            // XZ overlap is what matters for terrain cells; include Y for the 3D case.
            return a.min.x <= b.max.x && a.max.x >= b.min.x
                && a.min.y <= b.max.y && a.max.y >= b.min.y
                && a.min.z <= b.max.z && a.max.z >= b.min.z;
        }
    }
}
