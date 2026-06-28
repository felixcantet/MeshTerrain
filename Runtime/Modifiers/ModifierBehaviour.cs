using Unity.Mathematics;
using UnityEngine;
using Fca.MeshTerrain.Streaming;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Scene-object wrapper for a <see cref="ModifierComponent"/>. Unity port of UE
    /// <c>UModifierComponent</c> (see <c>doc/source/.../MeshPartitionModifierComponent.h</c>): the
    /// editable, transform-carrying layer that lives in the scene hierarchy. The plain
    /// <see cref="ModifierComponent"/> core stays a POD class (worker-thread safe, unit-tested); this
    /// MonoBehaviour merely <see cref="BuildCore"/>s it from serialized fields + this object's
    /// <see cref="Transform"/>, and notifies the owning <c>MeshTerrainStreamer</c> when edited.
    ///
    /// <para>Placement convention: the cooker runs with <c>meshToWorld = identity</c>, so a modifier's
    /// mesh-local frame == the streaming <b>grid</b> frame anchored at the streamer's
    /// <c>WorldOriginOffset</c>. A wrapper's affected region therefore follows its transform <b>relative
    /// to that grid origin</b> — moving/rotating/scaling the GameObject moves the modifier (UE
    /// <c>PostEditComponentMove</c>). Concrete subclasses fold <see cref="GridLocalMatrix"/> into their
    /// core's placement field (<c>PatchTransform</c> / <c>Center</c>).</para>
    /// </summary>
    public abstract class ModifierBehaviour : MonoBehaviour
    {
        [Header("Stack ordering")]
        [Tooltip("Priority layer across groups (lower applies first). UE named-layer priority.")]
        public int PriorityLayer = 0;

        [Tooltip("Sub-priority within the same layer (higher applies later). UE Priority.")]
        public double SubPriority = 0.0;

        [Tooltip("When checked the modifier is skipped by the stack (UE bIsDisabled).")]
        public bool IsDisabled = false;

        // The built core, cached and rebuilt on demand. Built lazily / on MarkDirty so reads off a freshly
        // deserialized wrapper still produce a current core.
        ModifierComponent _core;
        bool _dirty = true;

        /// <summary>True if this modifier produces base geometry — sorted before all non-base modifiers.</summary>
        public abstract bool IsBaseModifier { get; }

        /// <summary>
        /// The weight channel name(s) this modifier writes, if any (e.g. a paint target). The streamer unions
        /// these into the cook's global channel list so a written channel always gets a stable atlas slot —
        /// without this, a channel absent from <c>Definition.ChannelNames</c> scatters across tiles. Returns
        /// null/empty for modifiers that don't write channels.
        /// </summary>
        public virtual void GetWrittenChannels(System.Collections.Generic.List<string> outNames) { }

        /// <summary>
        /// Builds (or rebuilds) the plain core from this wrapper's serialized fields and transform.
        /// <paramref name="gridToWorld"/> maps the streaming grid frame to world; subclasses use
        /// <see cref="GridLocalMatrix"/> (its inverse applied to this transform) to place the modifier.
        /// Called on the main thread before the streamer snapshots the stack for a cook.
        /// </summary>
        protected abstract ModifierComponent BuildCore(float4x4 gridToWorld);

        /// <summary>
        /// Returns the up-to-date core, rebuilding it if dirty. <paramref name="gridToWorld"/> is the
        /// owning streamer's grid origin matrix (identity-rotation translate by WorldOriginOffset, unless
        /// the streamer transform is itself rotated/scaled).
        /// </summary>
        public ModifierComponent GetCore(float4x4 gridToWorld)
        {
            if (_dirty || _core == null)
            {
                _core = BuildCore(gridToWorld);
                _core.PriorityLayer = PriorityLayer;
                _core.SubPriority = SubPriority;
                _core.IsDisabled = IsDisabled;
                _dirty = false;
            }
            return _core;
        }

        /// <summary>Forces the core to rebuild on the next <see cref="GetCore"/>.</summary>
        public void MarkDirty() => _dirty = true;

        /// <summary>
        /// This object's transform expressed in the streaming-grid frame (grid-local). The modifier's
        /// affected region is authored here: <c>gridLocal = inverse(gridToWorld) * this.localToWorldMatrix</c>.
        /// </summary>
        protected float4x4 GridLocalMatrix(float4x4 gridToWorld)
            => math.mul(math.inverse(gridToWorld), (float4x4)transform.localToWorldMatrix);

        /// <summary>Grid-local position only (translation of <see cref="GridLocalMatrix"/>).</summary>
        protected float3 GridLocalPosition(float4x4 gridToWorld)
            => math.transform(math.inverse(gridToWorld), (float3)transform.position);

#if UNITY_EDITOR
        // OnValidate fires on inspector edits (and deserialize). Mark dirty and ask the streamer to rebuild
        // the covered cells. Deferred to avoid SendMessage-during-OnValidate restrictions.
        void OnValidate()
        {
            MarkDirty();
            UnityEditor.EditorApplication.delayCall += NotifyOwnerSafe;
        }

        void NotifyOwnerSafe()
        {
            if (this == null) return;
            var streamer = GetComponentInParent<MeshTerrainStreamer>();
            if (streamer != null) streamer.NotifyModifierEdited(this);
        }
#endif
    }
}
