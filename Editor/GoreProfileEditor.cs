#if !HAS_VENEER
using UnityEditor;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// The fallback Inspector of a <see cref="GoreProfile"/>, used only while Veneer is not in the project: what the setup made,
    /// read only (a summary of every skinned mesh and the list of cuts), and the settings of a cut-off part. The summary is worked out when
    /// the Inspector opens and when the project changes, not on every repaint. With the kit installed,
    /// <c>GoreProfileStudioEditor</c> (in the <c>Studio</c> folder) replaces it.
    /// </summary>
    [CustomEditor(typeof(GoreProfile))]
    internal sealed class GoreProfileEditor : UnityEditor.Editor
    {
        private SerializedProperty _despawnTime;
        private SerializedProperty _density;
        private SerializedProperty _spin;
        private SerializedProperty _cutEquipment;
        private SerializedProperty _bloodPrefab;
        private SerializedProperty _bleedTime;
        private SerializedProperty _bloodScale;
        private GoreSetInfo[] _sets = new GoreSetInfo[0];
        private GoreCutInfo[] _cuts = new GoreCutInfo[0];
        private string _bannerText = string.Empty;
        private bool _empty;
        private bool _dirty = true;

        private void OnEnable()
        {
            _despawnTime = serializedObject.FindProperty(nameof(GoreProfile.DespawnTime));
            _density = serializedObject.FindProperty(nameof(GoreProfile.Density));
            _spin = serializedObject.FindProperty(nameof(GoreProfile.Spin));
            _cutEquipment = serializedObject.FindProperty(nameof(GoreProfile.CutEquipment));
            _bloodPrefab = serializedObject.FindProperty(nameof(GoreProfile.BloodPrefab));
            _bleedTime = serializedObject.FindProperty(nameof(GoreProfile.BleedTime));
            _bloodScale = serializedObject.FindProperty(nameof(GoreProfile.BloodScale));
            EditorApplication.projectChanged += OnProjectChanged;
            Undo.undoRedoPerformed += OnProjectChanged;
            _dirty = true;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= OnProjectChanged;
            Undo.undoRedoPerformed -= OnProjectChanged;
        }

        private void OnProjectChanged()
        {
            _dirty = true;
            Repaint();
        }

        /// <inheritdoc />
        public override void OnInspectorGUI()
        {
            // Changed on the layout pass only, so the layout and repaint passes of one frame draw the same controls.
            if (_dirty && Event.current.type == EventType.Layout)
            {
                Rebuild((GoreProfile)target);
            }

            serializedObject.Update();
            GoreGUI.Banner("Gore Profile", _bannerText, _empty ? GoreGUI.Caution : GoreGUI.Good);

            for (var i = 0; i < _sets.Length; i++)
            {
                DrawSet(_sets[i]);
            }

            DrawCuts();

            GoreGUI.BeginCard("Cut-off parts");
            EditorGUILayout.PropertyField(_despawnTime);
            EditorGUILayout.PropertyField(_density);
            EditorGUILayout.PropertyField(_spin);
            EditorGUILayout.PropertyField(_cutEquipment);
            var rules = ((GoreProfile)target).ClothRules;
            GoreGUI.Note(rules != null && rules.Length > 0
                ? GoreGUI.Plural(rules.Length, "worn mesh has", "worn meshes have") + " a rule of its own (set in the Cloth parts card of the Gore Studio); the others follow the automatic rules."
                : "Every worn mesh follows the automatic rules. Set a piece up by hand in the Cloth parts card of the Gore Studio.");
            GoreGUI.EndCard();

            GoreGUI.BeginCard("Blood");
            EditorGUILayout.PropertyField(_bloodPrefab);
            if (_bloodPrefab.objectReferenceValue == null)
            {
                GoreGUI.Note("No blood: the cuts stay dry.");
                if (GUILayout.Button("Use the default blood"))
                {
                    _bloodPrefab.objectReferenceValue = GoreBloodAssets.DefaultBlood();
                }
            }
            else
            {
                EditorGUILayout.PropertyField(_bleedTime);
                EditorGUILayout.PropertyField(_bloodScale);
            }

            GoreGUI.EndCard();
            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawSet(GoreSetInfo info)
        {
            GoreGUI.BeginCard(info.Name);
            EditorGUILayout.LabelField("Source mesh", info.Source);
            EditorGUILayout.LabelField("Lids", info.Lids);
            EditorGUILayout.LabelField("Parts with skin", info.Parts);
            EditorGUILayout.LabelField("Triangles", info.Triangles);
            EditorGUILayout.LabelField("Cuts with lids", info.CutLids);
            GoreGUI.EndCard();
        }

        private void DrawCuts()
        {
            GoreGUI.BeginCard("Cuts");
            if (_cuts.Length == 0)
            {
                GoreGUI.Note("No cuts: run Tools > Gore Studio.");
            }

            for (var i = 0; i < _cuts.Length; i++)
            {
                GUILayout.Label(_cuts[i].Title, EditorStyles.boldLabel);
                GoreGUI.Note(_cuts[i].Detail);
            }

            GoreGUI.EndCard();
        }

        private void Rebuild(GoreProfile profile)
        {
            _dirty = false;
            _empty = GoreProfileSummary.IsEmpty(profile);
            _bannerText = GoreProfileSummary.Counts(profile);
            _sets = GoreProfileSummary.BuildSets(profile);
            _cuts = GoreProfileSummary.BuildCuts(profile);
        }
    }
}
#endif
