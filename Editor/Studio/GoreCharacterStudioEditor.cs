using Veneer;
using UnityEditor;

namespace GoreLab
{
    /// <summary>
    /// The studio-look Inspector of a <see cref="GoreCharacter"/> (replaces the IMGUI one when Veneer is installed), drawn from a Veneer
    /// layout: the project's own copy made in the Inspector Designer when there is one, otherwise <see cref="GoreCharacterDefaultLayout"/>
    /// (a header with a state pill, the profile slot with a button that opens the Gore Studio, hints while the character is not set up,
    /// and the list of cuts with Sever buttons in Play Mode). The pieces that are not fields are custom items
    /// (<see cref="GoreCharacterItems"/>). A selection of several characters gets its own short view built here.
    /// </summary>
    [CustomEditor(typeof(GoreCharacter))]
    internal sealed class GoreCharacterStudioEditor : VeneerLayoutDrivenInspector<GoreCharacter>
    {
        /// <inheritdoc />
        protected override VeneerBrand Brand => GoreBrand.Value;

        /// <inheritdoc />
        protected override bool BuildMultiSelection(VeneerInspectorBuilder b)
        {
            b.Header("Gore Character", "Several characters selected");
            b.Card("Profile", "database", () => b.Object(GoreCharacterItems.ProfilePath, "Profile", typeof(GoreProfile)));
            b.Hint("Select one Gore Character at a time to see its cuts.", "info");
            return true;
        }
    }
}
