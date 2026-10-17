#if !HAS_VENEER
using System;
using UnityEditor;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// The fallback Inspector of a <see cref="GoreCharacter"/>, used only while Veneer is not in the project: a banner that says
    /// whether the character is set up (and, while playing, whether any part is off), the profile, and the list of cuts. In play mode every
    /// cut that is still on has a Sever button, and the character can be reset. With the kit installed,
    /// <c>GoreCharacterStudioEditor</c> (in the <c>Studio</c> folder) replaces it.
    /// </summary>
    [CustomEditor(typeof(GoreCharacter))]
    internal sealed class GoreCharacterEditor : UnityEditor.Editor
    {
        private static readonly string[] LimbNames = Enum.GetNames(typeof(GoreLimb));
        private static readonly GUIContent ProfileLabel = new GUIContent("Profile", "What the setup worked out for this character: the cuts, the parts of its meshes and what a cut-off part does. Shared by every character with the same meshes and bones.");
        private static readonly GUIContent SeverLabel = new GUIContent("Sever", "Takes this part off with a small throw forward and up (play mode only).");
        private static readonly GUIContent ResetLabel = new GUIContent("Reset character", "Puts every part back and removes the parts that were cut off (play mode only).");

        private SerializedProperty _profile;
        private string _bannerText = string.Empty;
        private int _bannerCuts = -1;
        private int _bannerSets = -1;
        private int _bannerState = -1;

        /// <inheritdoc />
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        private void OnEnable() => _profile = serializedObject.FindProperty("_profile");

        /// <inheritdoc />
        public override void OnInspectorGUI()
        {
            var character = (GoreCharacter)target;
            var ready = character.IsReady;

            // Play mode buttons only for an object of a scene that is running (not for a prefab asset).
            var live = Application.isPlaying && character.gameObject.scene.IsValid();

            serializedObject.Update();
            DrawBanner(character, ready, live);

            EditorGUILayout.PropertyField(_profile, ProfileLabel);
            serializedObject.ApplyModifiedProperties();

            if (ready)
            {
                DrawCuts(character, live);
            }
            else
            {
                GoreGUI.Help("Nothing can be cut yet. Open Tools > Gore Studio, drop this character's prefab in and press Apply to split the skinned meshes of this character into parts.", MessageType.Info);
            }
        }

        private void DrawBanner(GoreCharacter character, bool ready, bool live)
        {
            string text;
            Color color;
            if (!ready)
            {
                text = "Not set up: open Tools > Gore Studio";
                color = GoreGUI.Caution;
            }
            else
            {
                var cuts = character.CutCount;
                var sets = character.Sets.Count;
                var state = live ? (character.IsCut ? 1 : 0) : -1;
                if (_bannerCuts != cuts || _bannerSets != sets || _bannerState != state)
                {
                    _bannerCuts = cuts;
                    _bannerSets = sets;
                    _bannerState = state;
                    _bannerText = "Ready: " + GoreGUI.Plural(cuts, "cut", "cuts") + ", " + GoreGUI.Plural(sets, "skinned mesh", "skinned meshes")
                        + (state == 1 ? ". Parts are off." : state == 0 ? ". All parts are on." : string.Empty);
                }

                text = _bannerText;
                color = state == 1 ? GoreGUI.Accent : GoreGUI.Good;
            }

            GoreGUI.Banner("Gore Character", text, color);
        }

        private static void DrawCuts(GoreCharacter character, bool live)
        {
            GoreGUI.BeginCard("Cuts");
            var count = character.CutCount;
            var impulse = character.transform.forward * 4f + Vector3.up * 2f;
            for (var i = 0; i < count; i++)
            {
                var severed = character.IsSevered(i);
                GUILayout.BeginHorizontal();
                GUILayout.Label(character.CutName(i), GUILayout.MinWidth(60f), GUILayout.ExpandWidth(true));
                GUILayout.Label(LimbName(character.CutLimb(i)), EditorStyles.miniLabel, GUILayout.Width(110f));

                var previous = GUI.contentColor;
                GUI.contentColor = severed ? GoreGUI.Caution : GoreGUI.Good;
                GUILayout.Label(severed ? "Severed: yes" : "Severed: no", EditorStyles.miniLabel, GUILayout.Width(80f));
                GUI.contentColor = previous;

                if (live)
                {
                    // One control in the same place whether the cut is off or not, so the layout does not change under a click.
                    if (severed)
                    {
                        GUILayout.Label(GUIContent.none, GUILayout.Width(60f));
                    }
                    else if (GUILayout.Button(SeverLabel, GUILayout.Width(60f)))
                    {
                        character.Sever(i, impulse);
                    }
                }

                GUILayout.EndHorizontal();
            }

            if (live)
            {
                GUILayout.Space(4f);
                if (GUILayout.Button(ResetLabel))
                {
                    character.ResetCharacter();
                }
            }
            else
            {
                GoreGUI.Note("The Sever and Reset buttons appear in Play Mode.");
            }

            GoreGUI.EndCard();
        }

        private static string LimbName(GoreLimb limb)
        {
            var index = (int)limb;
            return index >= 0 && index < LimbNames.Length ? LimbNames[index] : limb.ToString();
        }
    }
}
#endif
