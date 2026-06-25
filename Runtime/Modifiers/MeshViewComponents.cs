using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Read/write component mask a modifier declares on a <see cref="MeshView"/>. Unity port of UE
    /// <c>EMeshViewComponents</c> (see <c>doc/source/.../MeshPartitionMeshView.h</c>), restricted to the
    /// simple (non-topology) path for Phase 2: UE's <c>DynamicSubmesh</c> bit is intentionally omitted —
    /// it requires the <see cref="MeshData"/> append/builder still deferred from Phase 0/1 and is only
    /// needed by advanced (Patch/Boolean) modifiers.
    /// </summary>
    [Flags]
    public enum MeshViewComponents
    {
        None = 0,

        /// <summary>Reads or writes vertex positions. UE <c>VertexPos</c>.</summary>
        VertexPos = 1 << 0,

        /// <summary>Reads or writes per-vertex weight-layer (channel) values. UE <c>VertexAttributeWeight</c>.</summary>
        Weight = 1 << 1,

        /// <summary>Reads or writes the source UV set. UE <c>VertexUVs</c>.</summary>
        UV = 1 << 2,
    }

    /// <summary>
    /// One application instance of a modifier within a queried region. Unity port of UE
    /// <c>FInstanceInfo</c>. Most modifiers produce a single instance, but some (e.g. instanced patches)
    /// share settings across several bounds. Declares which components the instance reads/writes and which
    /// weight channels it touches, so the <see cref="MeshView"/> can collect exactly that data.
    /// </summary>
    public struct InstanceInfo
    {
        /// <summary>Region (mesh-local) this instance affects. UE <c>FInstanceInfo.Bounds</c>.</summary>
        public Bounds Bounds;

        /// <summary>Instance id within the modifier (0 for single-instance modifiers).</summary>
        public int InstanceID;

        /// <summary>Components this instance reads. UE <c>ReadViewComponents</c>.</summary>
        public MeshViewComponents Read;

        /// <summary>Components this instance writes. UE <c>WriteViewComponents</c>.</summary>
        public MeshViewComponents Write;

        /// <summary>Weight channels this instance reads/writes. UE <c>UsedChannels</c>. May be null.</summary>
        public List<string> UsedChannels;

        /// <summary>Convenience factory for the common single-instance case.</summary>
        public static InstanceInfo Default(Bounds bounds, MeshViewComponents read, MeshViewComponents write)
            => new InstanceInfo { Bounds = bounds, InstanceID = 0, Read = read, Write = write, UsedChannels = null };
    }
}
