using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// The thread-safe operation a modifier produces to apply its changes. Unity port of UE
    /// <c>IModifierBackgroundOp</c> (see <c>doc/source/.../MeshPartitionModifierComponent.h</c>),
    /// restricted to the two methods the Phase 2 stack needs.
    /// </summary>
    public interface IModifierJob
    {
        /// <summary>
        /// Reports the instances of this modifier that intersect <paramref name="queryBounds"/>, each
        /// declaring the components and channels it reads/writes. UE <c>GetInstancesInBounds</c>.
        /// </summary>
        void GetInstancesInBounds(Bounds queryBounds, List<InstanceInfo> outInstances);

        /// <summary>
        /// Applies the modification to a bounded <see cref="MeshView"/> for one instance. UE
        /// <c>ApplyModifications</c>. <paramref name="meshToWorld"/> is the world transform of the
        /// modified mesh (identity for now).
        /// </summary>
        void ApplyModifications(MeshView view, float4x4 meshToWorld, in InstanceInfo instance);
    }
}
