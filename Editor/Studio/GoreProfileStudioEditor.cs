using Veneer;
using UnityEditor;

namespace GoreLab
{
    /// <summary>
    /// The studio-look Inspector of a <see cref="GoreProfile"/> (replaces the IMGUI one when Veneer is installed), drawn from a Veneer
    /// layout: the project's own copy made in the Inspector Designer when there is one, otherwise <see cref="GoreProfileDefaultLayout"/>
    /// (three tabs: Settings, Cuts and Meshes). The state pill, the rules note, the default-blood button and the two read-only summaries
    /// are custom items (<see cref="GoreProfileItems"/>). A selection of several profiles shows only the Settings cards, drawn from a
    /// short layout made here for the occasion.
    /// </summary>
    [CustomEditor(typeof(GoreProfile))]
    internal sealed class GoreProfileStudioEditor : VeneerLayoutDrivenInspector<GoreProfile>
    {
        /// <inheritdoc />
        protected override VeneerBrand Brand => GoreBrand.Value;

        /// <inheritdoc />
        protected override bool BuildMultiSelection(VeneerInspectorBuilder b)
        {
            // A layout that is used once and freed: the renderer only reads it while it builds.
            VeneerLayout layout = GoreProfileDefaultLayout.CreateMultiSelectionLayout();
            try
            {
                VeneerLayoutRenderer.Build(b, layout, serializedObject);
            }
            finally
            {
                DestroyImmediate(layout);
            }

            return true;
        }
    }
}
