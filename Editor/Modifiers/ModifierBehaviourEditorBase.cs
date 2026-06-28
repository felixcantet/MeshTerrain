using UnityEditor;
using UnityEngine;
using Fca.MeshTerrain.Streaming;

namespace Fca.MeshTerrain.EditorTools
{
    /// <summary>
    /// Shared base for the modifier-wrapper inspectors. Centralizes the UE-style edit→rebuild routing: any
    /// inspector field change or scene-handle drag calls <see cref="NotifyEdited"/>, which rebuilds the
    /// wrapper's core and re-cooks only the covered cells via <c>MeshTerrainStreamer.NotifyModifierEdited</c>
    /// (UE <c>PostEditChangeProperty</c>/<c>PostEditComponentMove</c> → <c>OnChanged(bounds)</c>).
    ///
    /// <para>Also detects viewport <b>transform</b> drags (move/rotate/scale) — which don't raise
    /// <c>OnValidate</c> — by watching <c>transform.hasChanged</c> in <see cref="OnSceneGUI"/>, mirroring UE's
    /// apply-on-move-end behaviour: the gizmo updates live during the drag, the re-cook fires when the change
    /// settles (next scene-GUI pass after the handle is released).</para>
    /// </summary>
    public abstract class ModifierBehaviourEditorBase : UnityEditor.Editor
    {
        ModifierBehaviour Wrapper => (ModifierBehaviour)target;

        public override void OnInspectorGUI()
        {
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck())
                NotifyEdited("Edit Modifier");
        }

        protected virtual void OnSceneGUI()
        {
            var w = Wrapper;
            if (w == null) return;

            DrawHandles(w);

            // Catch transform drags (move/rotate/scale don't raise OnValidate). UE applies on move-END
            // (PostEditComponentMove); mirror that — mark dirty during the drag (gizmos follow live), but defer
            // the re-cook until the drag settles (no active hot control), so we don't re-cook every mouse frame.
            if (w.transform.hasChanged)
            {
                w.MarkDirty();
                bool dragging = GUIUtility.hotControl != 0;
                if (!dragging)
                {
                    w.transform.hasChanged = false;
                    NotifyEditedNoUndo();
                }
            }
        }

        /// <summary>Per-type scene handles (radius, coverage, …). Wrap drags in Record/NotifyEdited.</summary>
        protected virtual void DrawHandles(ModifierBehaviour wrapper) { }

        /// <summary>Records undo on the wrapper and routes the edit to the owning streamer.</summary>
        protected void NotifyEdited(string undoLabel)
        {
            var w = Wrapper;
            Undo.RecordObject(w, undoLabel);
            w.MarkDirty();
            NotifyEditedNoUndo();
        }

        protected void NotifyEditedNoUndo()
        {
            var w = Wrapper;
            var streamer = w.GetComponentInParent<MeshTerrainStreamer>();
            if (streamer != null) streamer.NotifyModifierEdited(w);
        }
    }
}
