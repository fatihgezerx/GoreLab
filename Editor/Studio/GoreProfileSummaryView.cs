using System;
using Veneer;
using UnityEditor;
using UnityEngine.UIElements;

namespace GoreLab
{
    /// <summary>
    /// A read-only summary of a <see cref="GoreProfile"/> drawn into a host element (the Cuts and Meshes items of
    /// <see cref="GoreProfileItems"/>). It is filled when it is built and again, coalesced into one rebuild per frame, when the project
    /// changes or Undo / Redo runs, and when the Inspector comes back on screen; never on every repaint, because it walks mesh assets.
    /// The project-change hooks are on only while the host is on a panel.
    /// </summary>
    internal sealed class GoreProfileSummaryView
    {
        private readonly VisualElement host;
        private readonly GoreProfile profile;
        private readonly VeneerBrand brand;
        private readonly Action<VisualElement, GoreProfile, VeneerBrand> fill;
        private readonly Action rebuild;
        private bool scheduled;
        private bool attachedBefore;

        /// <summary>Fills <paramref name="host"/> at once and keeps it up to date while it is on a panel.</summary>
        /// <param name="host">The element the summary is drawn into (cleared and refilled).</param>
        /// <param name="profile">The profile that is summarised.</param>
        /// <param name="brand">The brand of the Inspector.</param>
        /// <param name="fill">Draws the summary into the host.</param>
        internal GoreProfileSummaryView(VisualElement host, GoreProfile profile, VeneerBrand brand, Action<VisualElement, GoreProfile, VeneerBrand> fill)
        {
            this.host = host;
            this.profile = profile;
            this.brand = brand;
            this.fill = fill;
            rebuild = Rebuild;
            host.RegisterCallback<AttachToPanelEvent>(OnAttach);
            host.RegisterCallback<DetachFromPanelEvent>(OnDetach);
            fill(host, profile, brand);
        }

        /// <summary>Draws the list of cuts: one block (title and detail) per cut, or a warning when there is none.</summary>
        internal static void FillCuts(VisualElement host, GoreProfile profile, VeneerBrand brand)
        {
            host.Clear();
            GoreCutInfo[] cuts = GoreProfileSummary.BuildCuts(profile);
            if (cuts.Length == 0)
            {
                host.Add(VeneerStyle.Hint("No cuts: run Tools > Gore Studio.", "warn", brand));
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
                host.Add(block);
            }
        }

        /// <summary>Draws one card per skinned mesh (source, lids, parts, triangles, cuts with lids), or a warning when there is none.</summary>
        internal static void FillMeshes(VisualElement host, GoreProfile profile, VeneerBrand brand)
        {
            host.Clear();
            GoreSetInfo[] sets = GoreProfileSummary.BuildSets(profile);
            if (sets.Length == 0)
            {
                host.Add(VeneerStyle.Hint("No skinned meshes: run Tools > Gore Studio.", "warn", brand));
            }

            for (int i = 0; i < sets.Length; i++)
            {
                VisualElement card = VeneerStyle.Card(sets[i].Name, "layers", brand);
                card.AddToClassList("vn-card--nested");
                AddInfo(card, "Source mesh", sets[i].Source, "The mesh this set was made from.");
                AddInfo(card, "Lids", sets[i].Lids, "Whether the cuts of this mesh get lids (a stump and a lid on the part).");
                AddInfo(card, "Parts with skin", sets[i].Parts, "How many segments have a part of this mesh.");
                AddInfo(card, "Triangles", sets[i].Triangles, "The triangles of all parts together.");
                AddInfo(card, "Cuts with lids", sets[i].CutLids, "How many cuts have a lid on this mesh.");
                host.Add(card);
            }
        }

        private static void AddInfo(VisualElement card, string label, string value, string tooltip)
        {
            var text = new Label(value);
            text.AddToClassList("vn-ellipsis");
            card.Add(VeneerStyle.FieldRow(label, text, tooltip, out _));
        }

        private void OnAttach(AttachToPanelEvent evt)
        {
            // Subtracting first keeps a second attach from adding the hooks twice.
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorApplication.projectChanged += OnProjectChanged;
            Undo.undoRedoPerformed -= OnProjectChanged;
            Undo.undoRedoPerformed += OnProjectChanged;

            // Changes made while the Inspector was off screen were not heard: the first attach follows the build, the later ones refill.
            if (attachedBefore)
            {
                Rebuild();
            }

            attachedBefore = true;
        }

        private void OnDetach(DetachFromPanelEvent evt)
        {
            EditorApplication.projectChanged -= OnProjectChanged;
            Undo.undoRedoPerformed -= OnProjectChanged;
        }

        // Coalesced: several project changes in one frame rebuild the summary once.
        private void OnProjectChanged()
        {
            if (scheduled || host.panel == null)
            {
                return;
            }

            scheduled = true;
            host.schedule.Execute(rebuild);
        }

        private void Rebuild()
        {
            scheduled = false;
            if (profile != null)
            {
                fill(host, profile, brand);
            }
        }
    }
}
