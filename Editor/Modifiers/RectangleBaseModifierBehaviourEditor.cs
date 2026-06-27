using UnityEditor;

namespace Fca.MeshTerrain.EditorTools
{
    /// <summary>
    /// Inspector for <see cref="RectangleBaseModifierBehaviour"/>. No special scene handles (the base grid is
    /// sized via Size/Resolution fields and placed by the transform); inherits field-edit and transform-drag
    /// auto-rebuild routing from the base.
    /// </summary>
    [CustomEditor(typeof(RectangleBaseModifierBehaviour))]
    [CanEditMultipleObjects]
    public sealed class RectangleBaseModifierBehaviourEditor : ModifierBehaviourEditorBase
    {
    }
}
