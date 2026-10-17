using System;
using Veneer;

namespace GoreLab
{
    /// <summary>
    /// The design of the <see cref="GoreCharacter"/> Inspector that GoreLab ships: a Profile card (the profile slot with the Open Gore
    /// Studio button and the hints while the character is not set up, plus a strip that counts the parts off in Play Mode) and a Cuts
    /// card (the cuts with a Sever button each in Play Mode, and Reset character). With Veneer installed a project can copy it in the
    /// Inspector Designer and change the copy; this default is never saved as an asset.
    /// </summary>
    internal sealed class GoreCharacterDefaultLayout : IVeneerDefaultLayoutProvider
    {
        /// <inheritdoc />
        public Type TargetType => typeof(GoreCharacter);

        /// <inheritdoc />
        public VeneerLayout CreateLayout()
        {
            // One page: no tab bar. The subtitle is only a fallback: the state item writes the live "n cuts, n skinned meshes" over it.
            VeneerLayout layout = new VeneerLayoutBuilder(typeof(GoreCharacter), "Gore Character", "Cuts and parts of a character")
                .Page("Character", "list")
                .Card("Profile", "database")
                .Custom(GoreCharacterItems.PartsOffId)
                .Field(GoreCharacterItems.ProfilePath, "Profile", GoreCharacterItems.ProfileTip, widget: VeneerWidget.Object)
                .Custom(GoreCharacterItems.OpenStudioId)
                .Note(GoreCharacterItems.NoProfile, VeneerNoteKind.Warning)
                .ShowIf(GoreCharacterItems.ProfilePath, VeneerCondition.IsNull)
                .Custom(GoreCharacterItems.NotSetUpId)
                .Custom(GoreCharacterItems.StateId)
                .Card("Cuts", "list")
                .Custom(GoreCharacterItems.CutsId)
                .Build();

            // The look of the system (accent, style sheet). UseThemeLogo stays off: the builder already shows the brand logo in the header.
            layout.ThemeBrand = GoreBrand.Value.Name;
            return layout;
        }

        /// <inheritdoc />
        public void RegisterItems()
        {
            GoreCharacterItems.Register();
        }
    }
}
