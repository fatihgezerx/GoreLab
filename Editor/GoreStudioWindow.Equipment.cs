using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GoreLab
{
    // The Worn equipment card: what the character can wear (armour, hair, boots put on at run time) is cut too, but those meshes are split at run
    // time and Unity only lets the run time read a mesh with Read/Write on. The user drops what can be worn; the studio finds the skinned
    // meshes, says which are not readable, switches them on, and warns before Apply when some still are.
    internal sealed partial class GoreStudioWindow
    {
        private const int ListedNames = 6;

        private readonly List<Object> _worn = new List<Object>(8);
        private List<GoreWornMesh> _wornMeshes = new List<GoreWornMesh>();
        private readonly List<GameObject> _wornPrefabs = new List<GameObject>(8);
        private readonly List<string> _wornNotes = new List<string>(4);

        private int WornUnreadable
        {
            get
            {
                var count = 0;
                foreach (var mesh in _wornMeshes)
                {
                    if (!mesh.Readable)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        private static readonly GUIContent DropLabel = new GUIContent("Drop prefabs or folders here", "What the character can wear at run time: prefabs, a folder of prefabs, or any ScriptableObject whose fields are marked with [GoreWorn]. The studio finds the skinned meshes in them and tells which of them are not readable.");
        private static readonly GUIContent ForgetLabel = new GUIContent("x", "Forget this one.");
        private static readonly GUIContent ScanLabel = new GUIContent("Scan", "Looks at the dropped assets again (after changing them).");
        private static readonly GUIContent DressLabel = new GUIContent("Dress", "Puts the dropped clothes on the naked character in the preview, so each piece can be set up in Cloth parts.");
        private static readonly GUIContent MakeWornReadableLabel = new GUIContent("Make readable", "Ticks Read/Write in the import settings of their models and reimports them.");

        private void DrawEquipment()
        {
            GoreGUI.BeginCard("Worn equipment");
            GoreGUI.Note("What the character can wear: armour, hair, boots. Drop it here, press Dress, then set each piece up in Cloth parts.");

            var box = GoreGUI.Row(34f);
            GUI.Box(box, DropLabel, EditorStyles.helpBox);
            HandleWornDrop(box);

            var remove = -1;
            for (var i = 0; i < _worn.Count; i++)
            {
                var line = GoreGUI.Row();
                var forget = GoreGUI.TakeRight(ref line, 22f);
                EditorGUI.ObjectField(line, GUIContent.none, _worn[i], typeof(Object), false);
                if (GUI.Button(forget, ForgetLabel))
                {
                    remove = i;
                }
            }

            if (remove >= 0)
            {
                var index = remove;
                Defer(() => RemoveWorn(index));
            }

            if (_worn.Count > 0)
            {
                DrawWornSummary();
            }

            GoreGUI.EndCard();
        }

        private void DrawWornSummary()
        {
            var unreadable = WornUnreadable;
            foreach (var note in _wornNotes)
            {
                GoreGUI.Help(note, MessageType.Info);
            }

            if (_wornMeshes.Count == 0)
            {
                GoreGUI.Note("No skinned mesh found in these.");
            }
            else if (unreadable == 0)
            {
                GoreGUI.Note(GoreGUI.Plural(_wornMeshes.Count, "mesh", "meshes") + " found, all readable.");
            }
            else
            {
                var text = unreadable + " of " + GoreGUI.Plural(_wornMeshes.Count, "mesh is", "meshes are") + " not readable. Each goes whole with a part and cannot be cut: fine for hair, a helmet or a cape. A glove, a sleeve or armour needs Read/Write to be cut.";
                var listed = 0;
                var unfixable = 0;
                foreach (var mesh in _wornMeshes)
                {
                    if (mesh.Readable)
                    {
                        continue;
                    }

                    unfixable += mesh.Importer == null ? 1 : 0;
                    if (listed++ < ListedNames)
                    {
                        text += "\n- " + mesh.Mesh.name;
                    }
                }

                if (unreadable > ListedNames)
                {
                    text += "\n- and " + (unreadable - ListedNames) + " more";
                }

                if (unfixable > 0)
                {
                    text += "\n" + unfixable + " of them are not in a model file: switch Read/Write on where they come from.";
                }

                GoreGUI.Help(text, MessageType.Info);
                if (GUILayout.Button(MakeWornReadableLabel))
                {
                    Defer(MakeWornReadable);
                }
            }

            GUILayout.Space(2f);
            var buttons = GoreGUI.Row(22f);
            if (GUI.Button(GoreGUI.TakeLeft(ref buttons, 64f), ScanLabel))
            {
                Defer(ScanWorn);
            }

            using (new EditorGUI.DisabledScope(_wornPrefabs.Count == 0 || !_model.HasTarget))
            {
                if (GUI.Button(buttons, DressLabel))
                {
                    Defer(DressWorn);
                }
            }
        }

        private void HandleWornDrop(Rect box)
        {
            var e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !box.Contains(e.mousePosition))
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                var dropped = DragAndDrop.objectReferences;
                Defer(() => AddWorn(dropped));
            }

            e.Use();
        }

        private void AddWorn(Object[] dropped)
        {
            foreach (var asset in dropped)
            {
                if (asset != null && !_worn.Contains(asset))
                {
                    _worn.Add(asset);
                }
            }

            ScanWorn();
        }

        private void RemoveWorn(int index)
        {
            if (index >= 0 && index < _worn.Count)
            {
                _worn.RemoveAt(index);
                ScanWorn();
            }
        }

        private void ScanWorn()
        {
            _wornMeshes = GoreWornScan.Collect(_worn, _wornNotes, _wornPrefabs);
            Repaint();
        }

        private void MakeWornReadable()
        {
            var changed = GoreWornScan.MakeReadable(_wornMeshes);
            ScanWorn();
            _message = changed > 0 ? GoreGUI.Plural(changed, "model was", "models were") + " set to Read/Write." : "Nothing could be changed here.";
            _messageType = MessageType.Info;
        }

        // What was dropped for this character is kept in its profile (editor only), so the card is the same next time.
        private void RestoreWorn()
        {
            _worn.Clear();
            var profile = _model != null && _model.Existing != null ? _model.Existing.Profile : null;
            if (profile != null && profile.WornSources != null)
            {
                foreach (var asset in profile.WornSources)
                {
                    if (asset != null)
                    {
                        _worn.Add(asset);
                    }
                }
            }

            ScanWorn();
        }

        private void StoreWorn(GoreProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            profile.WornSources = _worn.ToArray();
            EditorUtility.SetDirty(profile);
        }

        // Nothing has to be asked before Apply: a worn mesh that is not readable still goes with the part it mostly belongs to.
        private static bool ConfirmWornReadable() => true;
    }
}
