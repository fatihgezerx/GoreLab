using Veneer;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GoreLab
{
    /// <summary>
    /// The studio-look Inspector of a <see cref="GoreProfile"/> (replaces the IMGUI one when Veneer is installed). Three tabs:
    /// Settings (what a cut-off part does, and the blood), Cuts (read only: the cuts the setup made and how they are nested) and Meshes
    /// (read only: what was made from every skinned mesh). The two read-only summaries are worked out when the Inspector opens and when
    /// the project changes or Undo / Redo runs, never on every repaint.
    /// </summary>
    [CustomEditor(typeof(GoreProfile))]
    internal sealed class GoreProfileStudioEditor : VeneerInspector<GoreProfile>
    {
        private VisualElement cutsHost;
        private VisualElement meshesHost;
        private Label subtitle;
        private Label rulesNote;
        private bool dirty = true;
        private bool rebuildScheduled;
        private bool empty;
        private string counts = string.Empty;

        protected override VeneerBrand Brand => GoreBrand.Value;

        private void OnEnable()
        {
            EditorApplication.projectChanged += OnProjectChanged;
            Undo.undoRedoPerformed += OnProjectChanged;
            dirty = true;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= OnProjectChanged;
            Undo.undoRedoPerformed -= OnProjectChanged;
        }

        protected override void Build(VeneerInspectorBuilder b)
        {
            if (TargetCount > 1)
            {
                b.Header("Gore Profile", "Several profiles selected");
                BuildSettings(b);
                return;
            }

            Refresh();
            VisualElement header = b.Header("Gore Profile", counts, null);
            subtitle = header.Q<Label>(className: "vn-subtitle");
            b.StatePill(ReadPill);

            VisualElement[] tabs = b.Tabs(("Settings", "sliders"), ("Cuts", "list"), ("Meshes", "layers"));
            using (b.Into(tabs[0]))
            {
                BuildSettings(b);
            }

            using (b.Into(tabs[1]))
            {
                b.Card("Cuts", "list", () =>
                {
                    cutsHost = new VisualElement();
                    b.Add(cutsHost);
                });
            }

            using (b.Into(tabs[2]))
            {
                meshesHost = new VisualElement();
                b.Add(meshesHost);
            }

            Populate();
        }

        private void BuildSettings(VeneerInspectorBuilder b)
        {
            b.Card("Cut-off parts", "sword", () =>
            {
                b.Number(nameof(GoreProfile.DespawnTime), "Despawn time", "s");
                b.Number(nameof(GoreProfile.Density), "Density", "kg/m3");
                b.Number(nameof(GoreProfile.Spin), "Spin", "turns/s");
                b.Chip(nameof(GoreProfile.CutEquipment), "Cut worn equipment", "layers");
                rulesNote = VeneerStyle.MutedLabel(RulesText());
                rulesNote.style.whiteSpace = WhiteSpace.Normal;
                b.Add(rulesNote);
            });

            b.Card("Blood", "heart", () =>
            {
                b.Object(nameof(GoreProfile.BloodPrefab), "Blood prefab", typeof(GameObject));
                VisualElement dry = b.Hint("No blood: the cuts stay dry.", "info");
                VisualElement defaultButton = b.Button(
                    "Use the default blood", "plus", UseDefaultBlood,
                    "Fills the slot with the blood effect that comes with GoreLab.");
                VisualElement bleed = b.Number(nameof(GoreProfile.BleedTime), "Bleed time", "s");
                VisualElement scale = b.Slider(nameof(GoreProfile.BloodScale), "Blood scale", "Thin", "Thick", 0.01f, 4f);
                b.VisibleWhen(dry, nameof(GoreProfile.BloodPrefab), null);
                b.VisibleWhen(defaultButton, nameof(GoreProfile.BloodPrefab), null);
                b.VisibleWhen(bleed, nameof(GoreProfile.BloodPrefab), null, true);
                b.VisibleWhen(scale, nameof(GoreProfile.BloodPrefab), null, true);
            });
        }

        private void UseDefaultBlood()
        {
            SerializedProperty blood = serializedObject.FindProperty(nameof(GoreProfile.BloodPrefab));
            serializedObject.Update();
            blood.objectReferenceValue = GoreBloodAssets.DefaultBlood();
            serializedObject.ApplyModifiedProperties();
        }

        private string RulesText()
        {
            GoreClothRule[] rules = Target.ClothRules;
            return rules != null && rules.Length > 0
                ? GoreGUI.Plural(rules.Length, "worn mesh has", "worn meshes have") + " a rule of its own (set in the Cloth parts card of the Gore Studio); the others follow the automatic rules."
                : "Every worn mesh follows the automatic rules. Set a piece up by hand in the Cloth parts card of the Gore Studio.";
        }

        private (string text, string kind) ReadPill()
        {
            if (rulesNote != null)
            {
                string rules = RulesText();
                if (rulesNote.text != rules)
                {
                    rulesNote.text = rules;
                }
            }

            return empty ? ("Not set up", "warn") : ("Ready", "ok");
        }

        private void OnProjectChanged()
        {
            dirty = true;
            if (cutsHost == null || cutsHost.panel == null || rebuildScheduled)
            {
                return;
            }

            // Coalesced: several project changes in one frame rebuild the summaries once.
            rebuildScheduled = true;
            cutsHost.schedule.Execute(() =>
            {
                rebuildScheduled = false;
                if (target != null && dirty)
                {
                    Populate();
                }
            });
        }

        private void Refresh()
        {
            dirty = false;
            GoreProfile profile = Target;
            empty = GoreProfileSummary.IsEmpty(profile);
            counts = GoreProfileSummary.Counts(profile);
        }

        // Rebuilds the two read-only lists from a fresh summary.
        private void Populate()
        {
            Refresh();
            GoreProfile profile = Target;
            if (subtitle != null)
            {
                subtitle.text = counts;
            }

            cutsHost.Clear();
            GoreCutInfo[] cuts = GoreProfileSummary.BuildCuts(profile);
            if (cuts.Length == 0)
            {
                cutsHost.Add(VeneerStyle.Hint("No cuts: run Tools > Gore Studio.", "warn", GoreBrand.Value));
            }

            for (int i = 0; i < cuts.Length; i++)
            {
                var block = new VisualElement();
                block.AddToClassList("gore-cut-block");
                if ((i & 1) == 1)
                {
                    block.AddToClassList("gore-cut-block--alt");
                }

                var title = new Label(cuts[i].Title);
                title.AddToClassList("gore-cut-title");
                block.Add(title);
                var detail = new Label(cuts[i].Detail);
                detail.AddToClassList("gore-cut-detail");
                block.Add(detail);
                cutsHost.Add(block);
            }

            meshesHost.Clear();
            GoreSetInfo[] sets = GoreProfileSummary.BuildSets(profile);
            if (sets.Length == 0)
            {
                meshesHost.Add(VeneerStyle.Hint("No skinned meshes: run Tools > Gore Studio.", "warn", GoreBrand.Value));
            }

            for (int i = 0; i < sets.Length; i++)
            {
                VisualElement card = VeneerStyle.Card(sets[i].Name, "layers", GoreBrand.Value);
                AddInfo(card, "Source mesh", sets[i].Source, "The mesh this set was made from.");
                AddInfo(card, "Lids", sets[i].Lids, "Whether the cuts of this mesh get lids (a stump and a lid on the part).");
                AddInfo(card, "Parts with skin", sets[i].Parts, "How many segments have a part of this mesh.");
                AddInfo(card, "Triangles", sets[i].Triangles, "The triangles of all parts together.");
                AddInfo(card, "Cuts with lids", sets[i].CutLids, "How many cuts have a lid on this mesh.");
                meshesHost.Add(card);
            }
        }

        private static void AddInfo(VisualElement card, string label, string value, string tooltip)
        {
            var text = new Label(value);
            text.AddToClassList("vn-ellipsis");
            card.Add(VeneerStyle.FieldRow(label, text, tooltip, out _));
        }
    }
}
