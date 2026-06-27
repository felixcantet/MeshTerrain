using UnityEditor;
using UnityEngine;

namespace Fca.MeshTerrain.EditorTools
{
    /// <summary>
    /// Inspector + scene handles for <see cref="WeightUtilityModifierBehaviour"/>. Adds draggable inner-radius
    /// and outer-falloff handles (UE-style direct manipulation of the paint disk). Each drag records undo and
    /// re-cooks the covered cells via the base class.
    /// </summary>
    [CustomEditor(typeof(WeightUtilityModifierBehaviour))]
    [CanEditMultipleObjects]
    public sealed class WeightUtilityModifierBehaviourEditor : ModifierBehaviourEditorBase
    {
        protected override void DrawHandles(ModifierBehaviour wrapper)
        {
            var w = (WeightUtilityModifierBehaviour)wrapper;
            Vector3 c = w.transform.position;

            // Inner radius (full strength).
            EditorGUI.BeginChangeCheck();
            Handles.color = new Color(0.3f, 0.8f, 1f, 1f);
            float newRadius = Handles.RadiusHandle(Quaternion.LookRotation(Vector3.up), c, w.Radius);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(w, "Edit Paint Radius");
                w.Radius = Mathf.Max(0f, newRadius);
                w.MarkDirty();
                NotifyEditedNoUndo();
            }

            // Outer falloff edge (radius + falloff) — edit falloff by dragging the outer ring.
            EditorGUI.BeginChangeCheck();
            Handles.color = new Color(0.3f, 0.8f, 1f, 0.5f);
            float outer = Handles.RadiusHandle(Quaternion.LookRotation(Vector3.up), c, w.Radius + w.Falloff);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(w, "Edit Paint Falloff");
                w.Falloff = Mathf.Max(0f, outer - w.Radius);
                w.MarkDirty();
                NotifyEditedNoUndo();
            }
        }
    }
}
