using System;
using Veneer;
using UnityEditor;
using UnityEngine.UIElements;

namespace GoreLab
{
    /// <summary>
    /// The pieces of the <see cref="GoreProfile"/> Inspector that are not plain fields, registered with Veneer so the default layout (and
    /// a user's copy of it) can place them: the header state pill (which also keeps the subtitle current), the note about worn-mesh
    /// rules, the Use the default blood button, the two read-only summaries (cuts, meshes) and the widget of Blood scale. They can be
    /// moved or hidden in the Inspector Designer, not changed.
    /// </summary>
    internal static class GoreProfileItems
    {
        /// <summary>Id of the header state pill (header badge).</summary>
        internal const string StateId = "gore.profile.state";

        /// <summary>Id of the note that says how many worn meshes have a rule of their own (card item).</summary>
        internal const string RulesId = "gore.profile.rules";

        /// <summary>Id of the Use the default blood button (card item).</summary>
        internal const string DefaultBloodId = "gore.profile.default-blood";

        /// <summary>Id of the read-only list of cuts (card item).</summary>
        internal const string CutsId = "gore.profile.cuts";

        /// <summary>Id of the read-only cards of skinned meshes (card item).</summary>
        internal const string MeshesId = "gore.profile.meshes";

        private const string SubtitleClass = "vn-subtitle";

        /// <summary>Registers the custom items and the Blood scale widget of <see cref="GoreProfile"/>. Safe to call again (items are replaced).</summary>
        internal static void Register()
        {
            Type type = typeof(GoreProfile);

            VeneerCustomItems.Register(type, new VeneerCustomItem
            {
                Id = StateId,
                Label = "State",
                Icon = "check-circle",
                Description = "A pill in the header: Not set up or Ready. It also keeps the subtitle current (n cuts, n skinned meshes).",
                Slot = VeneerCustomItemSlot.HeaderBadge,
                Build = BuildState,
            });

            VeneerCustomItems.Register(type, new VeneerCustomItem
            {
                Id = RulesId,
                Label = "Worn mesh rules note",
                Icon = "layers",
                Description = "A line under Cut worn equipment: how many worn meshes have a rule of their own (set in the Gore Studio).",
                Slot = VeneerCustomItemSlot.Item,
                Build = BuildRules,
            });

            VeneerCustomItems.Register(type, new VeneerCustomItem
            {
                Id = DefaultBloodId,
                Label = "Use the default blood button",
                Icon = "plus",
                Description = "Fills Blood prefab with the blood effect that comes with GoreLab.",
                Slot = VeneerCustomItemSlot.Item,
                Build = BuildDefaultBlood,
            });

            VeneerCustomItems.Register(type, new VeneerCustomItem
            {
                Id = CutsId,
                Label = "Cuts summary",
                Icon = "list",
                Description = "Read only: every cut the setup made, with its parent and the cuts below it. Worked out again when the project changes.",
                Slot = VeneerCustomItemSlot.Item,
                Build = BuildCuts,
            });

            VeneerCustomItems.Register(type, new VeneerCustomItem
            {
                Id = MeshesId,
                Label = "Meshes summary",
                Icon = "layers",
                Description = "Read only: one card per skinned mesh with its source, lids, parts and triangles. Worked out again when the project changes.",
                Slot = VeneerCustomItemSlot.Item,
                Build = BuildMeshes,
            });

            // A layout slider without [Range] is 0..1; the blood scale goes from 0.01 to 4.
            VeneerCustomItems.RegisterWidget(
                type,
                nameof(GoreProfile.BloodScale),
                (context, property) => context.Builder.Slider(property.propertyPath, "Blood scale", "Thin", "Thick", 0.01f, 4f));
        }

        private static GoreProfile ProfileOf(VeneerCustomItemContext context)
        {
            return context.SerializedObject.targetObject as GoreProfile;
        }

        private static VisualElement BuildState(VeneerCustomItemContext context)
        {
            GoreProfile profile = ProfileOf(context);
            if (profile == null)
            {
                return null;
            }

            Label pill = VeneerStyle.Pill(string.Empty, "idle");
            var view = new StateView(profile, pill, FindSubtitle(context.Builder.Current));

            // The first run of Poll is on the first tick: show the right state at once.
            view.Refresh();
            context.Poll(view.Refresh);
            return pill;
        }

        // The header's subtitle label, found from the badge container (the header's right end: its card is two levels up). Null when the
        // header has none (a header drawn over a banner picture): the subtitle is then left as the layout wrote it.
        private static Label FindSubtitle(VisualElement container)
        {
            VisualElement scope = container;
            for (int i = 0; i < 2 && scope != null && scope.parent != null; i++)
            {
                scope = scope.parent;
            }

            return scope != null ? scope.Q<Label>(className: SubtitleClass) : null;
        }

        private static VisualElement BuildRules(VeneerCustomItemContext context)
        {
            GoreProfile profile = ProfileOf(context);
            if (profile == null)
            {
                return null;
            }

            Label note = VeneerStyle.MutedLabel(string.Empty);
            note.style.whiteSpace = WhiteSpace.Normal;
            var view = new RulesView(profile, note);
            view.Refresh();
            context.Poll(view.Refresh);
            return note;
        }

        private static VisualElement BuildDefaultBlood(VeneerCustomItemContext context)
        {
            SerializedObject serializedObject = context.SerializedObject;
            context.Builder.Button(
                "Use the default blood", "plus", () => UseDefaultBlood(serializedObject),
                "Fills the slot with the blood effect that comes with GoreLab.");
            return null;
        }

        private static void UseDefaultBlood(SerializedObject serializedObject)
        {
            SerializedProperty blood = serializedObject.FindProperty(nameof(GoreProfile.BloodPrefab));
            serializedObject.Update();
            blood.objectReferenceValue = GoreBloodAssets.DefaultBlood();
            serializedObject.ApplyModifiedProperties();
        }

        private static VisualElement BuildCuts(VeneerCustomItemContext context)
        {
            return BuildSummary(context, GoreProfileSummaryView.FillCuts);
        }

        private static VisualElement BuildMeshes(VeneerCustomItemContext context)
        {
            return BuildSummary(context, GoreProfileSummaryView.FillMeshes);
        }

        // A host element that the summary view fills now and again when the project changes; the view lives as long as the host.
        private static VisualElement BuildSummary(VeneerCustomItemContext context, Action<VisualElement, GoreProfile, VeneerBrand> fill)
        {
            GoreProfile profile = ProfileOf(context);
            if (profile == null)
            {
                return null;
            }

            var host = new VisualElement();
            context.Builder.Add(host);
            new GoreProfileSummaryView(host, profile, context.Brand, fill);
            return null;
        }

        /// <summary>Keeps the header pill and the subtitle in step with the profile; reads two counts, writes only when something changed.</summary>
        private sealed class StateView
        {
            private readonly GoreProfile profile;
            private readonly Label pill;
            private readonly Label subtitle;
            private int shownCuts = -1;
            private int shownSets = -1;

            public StateView(GoreProfile profile, Label pill, Label subtitle)
            {
                this.profile = profile;
                this.pill = pill;
                this.subtitle = subtitle;
            }

            public void Refresh()
            {
                if (profile == null)
                {
                    return;
                }

                int cuts = profile.CutCount;
                int sets = profile.SetCount;
                if (cuts != shownCuts || sets != shownSets)
                {
                    shownCuts = cuts;
                    shownSets = sets;
                    if (subtitle != null)
                    {
                        subtitle.text = GoreProfileSummary.Counts(profile);
                    }
                }

                bool empty = cuts == 0 || sets == 0;
                VeneerStyle.SetPill(pill, empty ? "warn" : "ok", empty ? "Not set up" : "Ready");
            }
        }

        /// <summary>The rules note: its text is made again only when the number of rules changed.</summary>
        private sealed class RulesView
        {
            private readonly GoreProfile profile;
            private readonly Label note;
            private int shownRules = -1;

            public RulesView(GoreProfile profile, Label note)
            {
                this.profile = profile;
                this.note = note;
            }

            public void Refresh()
            {
                if (profile == null)
                {
                    return;
                }

                GoreClothRule[] rules = profile.ClothRules;
                int count = rules != null ? rules.Length : 0;
                if (count == shownRules)
                {
                    return;
                }

                shownRules = count;
                note.text = count > 0
                    ? GoreGUI.Plural(count, "worn mesh has", "worn meshes have") + " a rule of its own (set in the Cloth parts card of the Gore Studio); the others follow the automatic rules."
                    : "Every worn mesh follows the automatic rules. Set a piece up by hand in the Cloth parts card of the Gore Studio.";
            }
        }
    }
}
