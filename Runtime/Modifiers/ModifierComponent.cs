using Unity.Collections;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Base class for a non-destructive modifier. Unity port of UE <c>UModifierComponent</c> (see
    /// <c>doc/source/.../MeshPartitionModifierComponent.h</c>), trimmed to what the Phase 2 stack needs.
    ///
    /// A modifier carries bounds, an ordering key (<see cref="PriorityLayer"/> + <see cref="SubPriority"/>),
    /// a complexity estimate, and a disabled flag. <b>Base</b> modifiers (<see cref="IsBase"/>) produce the
    /// initial geometry via <see cref="ProduceBaseMesh"/>; all others transform it via the
    /// <see cref="IModifierJob"/> from <see cref="CreateJob"/>.
    ///
    /// Not a <c>MonoBehaviour</c> yet — kept as a plain class so the stack is testable in EditMode without
    /// scene objects. A component wrapper can be added when interactive editing lands (Phase 6).
    /// </summary>
    public abstract class ModifierComponent
    {
        /// <summary>
        /// Priority layer ordering modifiers across groups (lower applies first). UE orders named layers
        /// via the Definition; here it is a plain int until the Definition grows a priority-layer list.
        /// </summary>
        public int PriorityLayer;

        /// <summary>Sub-priority ordering modifiers within the same layer (higher applies later). UE <c>Priority</c>.</summary>
        public double SubPriority;

        /// <summary>When true the modifier is skipped by the stack. UE <c>IsDisabled</c>.</summary>
        public bool IsDisabled;

        /// <summary>The region (mesh-local) this modifier affects. UE <c>ComputeCombinedBounds</c>.</summary>
        public abstract Bounds ComputeBounds();

        /// <summary>Complexity added by this modifier (e.g. vertex count for a base). UE <c>GetComplexity</c>.</summary>
        public virtual double GetComplexity() => 0.0;

        /// <summary>True if this modifier produces the base geometry. UE <c>IsBase</c>.</summary>
        public virtual bool IsBase => false;

        /// <summary>
        /// Produces the initial geometry. Only called on <see cref="IsBase"/> modifiers; the caller owns
        /// and must dispose the result. UE base modifiers expose the mesh via <c>IBaseMeshProviderOp</c>.
        /// </summary>
        public virtual MeshData ProduceBaseMesh(Allocator allocator)
            => throw new System.NotSupportedException($"{GetType().Name} is not a base modifier.");

        /// <summary>
        /// Creates the thread-safe operation applying this (non-base) modifier. UE <c>CreateBackgroundOp</c>.
        /// Base modifiers may return null.
        /// </summary>
        public virtual IModifierJob CreateJob() => null;
    }
}
