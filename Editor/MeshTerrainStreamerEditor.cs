using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Fca.MeshTerrain.Streaming;

namespace Fca.MeshTerrain.EditorTools
{
    /// <summary>
    /// Custom inspector for <see cref="MeshTerrainStreamer"/> adding the UE-style modifier <b>stack panel</b>:
    /// a reorderable list of the child <see cref="ModifierBehaviour"/>s in applied order (base pinned first),
    /// with enable/disable toggles, add/remove, and live streaming diagnostics. Every structural change routes
    /// through the streamer's incremental-rebuild API so the terrain re-cooks only what changed.
    /// </summary>
    [CustomEditor(typeof(MeshTerrainStreamer))]
    public sealed class MeshTerrainStreamerEditor : UnityEditor.Editor
    {
        ReorderableList _list;
        readonly List<ModifierBehaviour> _modifiers = new();

        MeshTerrainStreamer Streamer => (MeshTerrainStreamer)target;

        void OnEnable()
        {
            RefreshModifiers();
            _list = new ReorderableList(_modifiers, typeof(ModifierBehaviour), true, true, false, false)
            {
                drawHeaderCallback = r => EditorGUI.LabelField(r, "Modifier Stack (applied order — base first)"),
                elementHeight = EditorGUIUtility.singleLineHeight + 4f,
                drawElementCallback = DrawElement,
                onReorderCallback = OnReorder,
            };
        }

        void RefreshModifiers()
        {
            _modifiers.Clear();
            var s = Streamer;
            if (s == null) return;
            s.GetComponentsInChildren<ModifierBehaviour>(true, _modifiers);
            // Show them in the same applied order the streamer assembles: base first, then priority/sub-priority,
            // then sibling. We only have the wrappers here, so mirror that ordering for display + reorder.
            _modifiers.Sort((a, b) =>
            {
                if (a.IsBaseModifier != b.IsBaseModifier) return a.IsBaseModifier ? -1 : 1;
                int p = a.PriorityLayer.CompareTo(b.PriorityLayer);
                if (p != 0) return p;
                int sp = a.SubPriority.CompareTo(b.SubPriority);
                if (sp != 0) return sp;
                return a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex());
            });
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var s = Streamer;
            if (!s.UseSceneModifiers)
            {
                EditorGUILayout.HelpBox("Use Scene Modifiers is off — the stack is supplied in code " +
                    "(SetModifierStack). Enable it to author modifiers as child objects here.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space();
            RefreshModifiers();
            _list.DoLayoutList();
            DrawAddRemoveBar();
            DrawDiagnostics();
        }

        void DrawElement(Rect rect, int index, bool active, bool focused)
        {
            if (index < 0 || index >= _modifiers.Count) return;
            var m = _modifiers[index];
            if (m == null) return;

            rect.y += 2f;
            float h = EditorGUIUtility.singleLineHeight;

            // Enable/disable toggle (UE bIsDisabled) — checked == enabled.
            var toggleRect = new Rect(rect.x, rect.y, 18f, h);
            EditorGUI.BeginChangeCheck();
            bool enabled = EditorGUI.Toggle(toggleRect, !m.IsDisabled);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(m, "Toggle Modifier");
                m.IsDisabled = !enabled;
                m.MarkDirty();
                EditorUtility.SetDirty(m);
                Streamer.NotifyModifierStackChanged(); // membership/enable change → re-resolve
            }

            // Name + type, dim when disabled; "(base)" tag for the base modifier.
            var labelRect = new Rect(rect.x + 22f, rect.y, rect.width - 22f, h);
            var prev = GUI.color;
            if (m.IsDisabled) GUI.color = new Color(prev.r, prev.g, prev.b, 0.5f);
            string tag = m.IsBaseModifier ? "  (base)" : "";
            EditorGUI.LabelField(labelRect, $"{m.gameObject.name}{tag}", EditorStyles.boldLabel);
            GUI.color = prev;

            // Click selects the modifier object so its own inspector/handles show.
            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition) &&
                Event.current.button == 0 && Event.current.clickCount == 1)
            {
                Selection.activeGameObject = m.gameObject;
            }
        }

        void OnReorder(ReorderableList list)
        {
            // Persist the new applied order via sibling index + a monotonically increasing SubPriority, so the
            // streamer's CollectSceneModifiers reproduces exactly what the user sees. Base stays first.
            Undo.SetCurrentGroupName("Reorder Modifiers");
            for (int i = 0; i < _modifiers.Count; i++)
            {
                var m = _modifiers[i];
                if (m == null) continue;
                Undo.RecordObject(m, "Reorder Modifiers");
                Undo.RecordObject(m.transform, "Reorder Modifiers");
                m.transform.SetSiblingIndex(i);
                m.SubPriority = i; // tiebreak-free explicit order (base still sorts first via IsBaseModifier)
                m.MarkDirty();
                EditorUtility.SetDirty(m);
            }
            Streamer.NotifyModifierStackChanged();
        }

        void DrawAddRemoveBar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Modifier", EditorStyles.miniButtonLeft))
                    ShowAddMenu();

                using (new EditorGUI.DisabledScope(_list.index < 0 || _list.index >= _modifiers.Count))
                {
                    if (GUILayout.Button("Remove", EditorStyles.miniButtonRight))
                        RemoveSelected();
                }
            }
        }

        void ShowAddMenu()
        {
            var menu = new GenericMenu();
            foreach (var entry in ModifierTypeRegistry.Entries)
            {
                var type = entry.Type;
                menu.AddItem(new GUIContent(entry.DisplayName), false, () => AddModifier(type));
            }
            menu.ShowAsContext();
        }

        void AddModifier(System.Type type)
        {
            var go = new GameObject(DisplayNameFor(type));
            Undo.RegisterCreatedObjectUndo(go, "Add Modifier");
            Undo.SetTransformParent(go.transform, Streamer.transform, "Add Modifier");
            go.transform.localPosition = Vector3.zero;
            Undo.AddComponent(go, type);
            Streamer.NotifyModifierStackChanged();
            Selection.activeGameObject = go;
            RefreshModifiers();
        }

        static string DisplayNameFor(System.Type type)
        {
            foreach (var e in ModifierTypeRegistry.Entries)
                if (e.Type == type) return e.DisplayName;
            return type.Name;
        }

        void RemoveSelected()
        {
            int i = _list.index;
            if (i < 0 || i >= _modifiers.Count) return;
            var m = _modifiers[i];
            if (m == null) return;
            var go = m.gameObject;
            // Destroy the whole child GO if Add created it (only the modifier + Transform); otherwise just the
            // component, so we never delete an object the user is using for other things.
            bool dedicatedChild = go != Streamer.gameObject && go.GetComponents<Component>().Length <= 2;
            if (dedicatedChild) Undo.DestroyObjectImmediate(go);
            else Undo.DestroyObjectImmediate(m);
            Streamer.NotifyModifierStackChanged();
            RefreshModifiers();
        }

        void DrawDiagnostics()
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Streaming", EditorStyles.boldLabel);
                var s = Streamer;
                EditorGUILayout.LabelField($"Resident: {s.residentCount}    Pending: {s.pendingCount}    Queued: {s.queuedCount}");
                EditorGUILayout.LabelField($"Cooks: {StreamingDiagnostics.Cooks}    Hits: {StreamingDiagnostics.CacheHits}    Misses: {StreamingDiagnostics.CacheMisses}");
            }
            // Keep diagnostics live while sections stream.
            if (Streamer.pendingCount > 0 || Streamer.queuedCount > 0) Repaint();
        }
    }
}
