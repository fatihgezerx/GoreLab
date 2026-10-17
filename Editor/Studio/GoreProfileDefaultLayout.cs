using System;
using Veneer;

namespace GoreLab
{
    /// <summary>
    /// The design of the <see cref="GoreProfile"/> Inspector that GoreLab ships: three tabs. Settings (the Cut-off parts card: despawn
    /// time, density, spin, cut worn equipment and the rules note; the Blood card: prefab, the default-blood button, bleed time and
    /// scale), Cuts (read only: the cuts the setup made) and Meshes (read only: what was made from every skinned mesh). The data arrays
    /// the setup writes (cuts, mesh sets, sections, cloth rules) are hidden: the two summaries show them. With Veneer installed a project
    /// can copy the layout in the Inspector Designer and change the copy; this default is never saved as an asset.
    /// </summary>
    internal sealed class GoreProfileDefaultLayout : IVeneerDefaultLayoutProvider
    {
        private static readonly string[] DataPaths =
        {
            nameof(GoreProfile.Cuts),
            nameof(GoreProfile.MeshSets),
            nameof(GoreProfile.Sections),
            nameof(GoreProfile.ClothRules),
        };

        /// <inheritdoc />
        public Type TargetType => typeof(GoreProfile);

        /// <inheritdoc />
        public VeneerLayout CreateLayout()
        {
            // The subtitle is only a fallback: the state item writes the live "n cuts, n skinned meshes" over it.
            VeneerLayoutBuilder builder = new VeneerLayoutBuilder(typeof(GoreProfile), "Gore Profile", "Cuts and meshes of a character")
                .Page("Settings", "sliders");
            AddSettingsCards(builder)
                .Page("Cuts", "list")
                .Card("Cuts", "list")
                .Custom(GoreProfileItems.CutsId)
                .Custom(GoreProfileItems.StateId)
                .Page("Meshes", "layers")
                .Card("Meshes", "layers", false)
                .Custom(GoreProfileItems.MeshesId);
            return Finish(builder.Build());
        }

        /// <inheritdoc />
        public void RegisterItems()
        {
            GoreProfileItems.Register();
        }

        /// <summary>
        /// The layout of a selection of several profiles: the Settings cards on one page (no tabs, no state pill, no summaries), under the
        /// subtitle "Several profiles selected". A new in-memory layout each call: the caller frees it with <c>DestroyImmediate</c>.
        /// </summary>
        internal static VeneerLayout CreateMultiSelectionLayout()
        {
            VeneerLayoutBuilder builder = new VeneerLayoutBuilder(typeof(GoreProfile), "Gore Profile", "Several profiles selected")
                .Page("Settings", "sliders");
            AddSettingsCards(builder);
            return Finish(builder.Build());
        }

        // The two cards of the Settings tab. Blood scale has no layout slider range (a layout slider is 0..1 without [Range]), so a custom
        // widget of GoreProfileItems draws it with its own 0.01..4 range.
        private static VeneerLayoutBuilder AddSettingsCards(VeneerLayoutBuilder builder)
        {
            return builder
                .Card("Cut-off parts", "sword")
                .Field(nameof(GoreProfile.DespawnTime), "Despawn time", unit: "s", widget: VeneerWidget.Number)
                .Field(nameof(GoreProfile.Density), "Density", unit: "kg/m3", widget: VeneerWidget.Number)
                .Field(nameof(GoreProfile.Spin), "Spin", unit: "turns/s", widget: VeneerWidget.Number)
                .Field(nameof(GoreProfile.CutEquipment), "Cut worn equipment", widget: VeneerWidget.Toggle)
                .Icon("layers")
                .Custom(GoreProfileItems.RulesId)
                .Card("Blood", "heart")
                .Field(nameof(GoreProfile.BloodPrefab), "Blood prefab", widget: VeneerWidget.Object)
                .Note("No blood: the cuts stay dry.")
                .ShowIf(nameof(GoreProfile.BloodPrefab), VeneerCondition.IsNull)
                .Custom(GoreProfileItems.DefaultBloodId)
                .ShowIf(nameof(GoreProfile.BloodPrefab), VeneerCondition.IsNull)
                .Field(nameof(GoreProfile.BleedTime), "Bleed time", unit: "s", widget: VeneerWidget.Number)
                .ShowIf(nameof(GoreProfile.BloodPrefab), VeneerCondition.NotNull)
                .Field(nameof(GoreProfile.BloodScale), "Blood scale")
                .ShowIf(nameof(GoreProfile.BloodPrefab), VeneerCondition.NotNull);
        }

        private static VeneerLayout Finish(VeneerLayout layout)
        {
            // The raw data arrays are not fields to edit: they are hidden on purpose (not lost into an Other card); the Designer can show them.
            for (int i = 0; i < DataPaths.Length; i++)
            {
                layout.HiddenPaths.Add(DataPaths[i]);
            }

            // The look of the system (accent, style sheet). UseThemeLogo stays off: the builder already shows the brand logo in the header.
            layout.ThemeBrand = GoreBrand.Value.Name;
            return layout;
        }
    }
}
