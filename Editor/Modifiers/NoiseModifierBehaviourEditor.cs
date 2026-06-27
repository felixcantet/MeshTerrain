using UnityEditor;
using UnityEngine;

namespace Fca.MeshTerrain.EditorTools
{
    /// <summary>
    /// Inspector + scene handles for <see cref="NoiseModifierBehaviour"/>. Draws patch-local coverage-extent
    /// sliders (drag a face to resize <c>UnscaledCoverage</c>), mirroring UE <c>UNoiseModifier</c>'s affected
    /// box. Handles operate in the patch frame (this object's transform), so they follow rotation/scale.
    /// </summary>
    [CustomEditor(typeof(NoiseModifierBehaviour))]
    [CanEditMultipleObjects]
    public sealed class NoiseModifierBehaviourEditor : ModifierBehaviourEditorBase
    {
        protected override void DrawHandles(ModifierBehaviour wrapper)
        {
            var w = (NoiseModifierBehaviour)wrapper;
            using (new Handles.DrawingScope(w.transform.localToWorldMatrix))
            {
                Vector3 cov = w.UnscaledCoverage;
                Vector3 half = cov * 0.5f;

                EditorGUI.BeginChangeCheck();
                // Drag the +X / +Z faces to resize coverage in the patch plane (the meaningful axes for noise).
                Handles.color = Color.red;
                float halfX = AxisHalfExtent(new Vector3(half.x, 0, 0), Vector3.right);
                Handles.color = Color.blue;
                float halfZ = AxisHalfExtent(new Vector3(0, 0, half.z), Vector3.forward);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(w, "Edit Noise Coverage");
                    w.UnscaledCoverage = new Vector3(Mathf.Max(0f, halfX * 2f), cov.y, Mathf.Max(0f, halfZ * 2f));
                    w.MarkDirty();
                    NotifyEditedNoUndo();
                }
            }
        }

        // Slider handle at the +axis face; returns the dragged half-extent along that (patch-local) axis.
        static float AxisHalfExtent(Vector3 facePos, Vector3 axis)
        {
            float size = HandleUtility.GetHandleSize(facePos) * 0.1f;
            Vector3 moved = Handles.Slider(facePos, axis, size, Handles.CubeHandleCap, 0f);
            return Vector3.Dot(moved, axis); // axis is unit; dot gives the new extent along it
        }
    }
}
