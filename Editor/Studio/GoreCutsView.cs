using System;
using System.Collections.Generic;
using Veneer;
using UnityEngine;
using UnityEngine.UIElements;

namespace GoreLab
{
    /// <summary>
    /// The list of cuts of a <see cref="GoreCharacter"/> Inspector (the Cuts item of <see cref="GoreCharacterItems"/>): one row per cut
    /// with its limb and an On / Severed pill, a Sever button for the cuts still on (Play Mode), a Reset character button (Play Mode) and
    /// a note outside Play Mode. While the character is not set up the item is hidden, and so is its card when nothing else in the card is
    /// shown. Built once; rows are made again only when the cuts or the profile change, and a row is touched only when its state changed.
    /// </summary>
    internal sealed class GoreCutsView
    {
        private const string CardClass = "vn-card";
        private const string CardTitleClass = "vn-card__title";

        private static readonly string[] LimbNames = Enum.GetNames(typeof(GoreLimb));

        private readonly List<CutRow> rows = new List<CutRow>(16);
        private readonly GoreCharacter character;
        private readonly VeneerBrand brand;
        private readonly VisualElement host;
        private VisualElement cutsHost;
        private VisualElement resetButton;
        private VisualElement playNote;
        private GoreProfile builtProfile;
        private int builtCuts = -1;
        private int shownReady = -1;

        private sealed class CutRow
        {
            public int Index;
            public Label State;
            public Button Sever;
            public int Shown = -1;
        }

        private GoreCutsView(GoreCharacter character, VeneerBrand brand)
        {
            this.character = character;
            this.brand = brand;
            host = new VisualElement();
        }

        /// <summary>Builds the item into the builder's current container (the card) and starts its polling; used as the item's <c>Build</c>.</summary>
        /// <param name="context">The context the item is built with.</param>
        internal static VisualElement Build(VeneerCustomItemContext context)
        {
            var character = context.SerializedObject.targetObject as GoreCharacter;
            if (character == null)
            {
                return null;
            }

            var view = new GoreCutsView(character, context.Brand);
            view.Create(context.Builder);
            view.Refresh();
            context.Poll(view.Refresh);
            return null;
        }

        private void Create(VeneerInspectorBuilder b)
        {
            b.Add(host);
            using (b.Into(host))
            {
                cutsHost = new VisualElement();
                b.Add(cutsHost);
                resetButton = b.Button(
                    "Reset character", "refresh", () => character.ResetCharacter(),
                    "Puts every part back and removes the parts that were cut off (play mode only).");
                playNote = b.Hint("The Sever and Reset buttons appear in Play Mode.", "info");
            }
        }

        private void Refresh()
        {
            if (character == null)
            {
                return;
            }

            bool ready = character.IsReady;
            int readyFlag = ready ? 1 : 0;
            if (readyFlag != shownReady)
            {
                shownReady = readyFlag;
                ApplyReady(ready);
            }

            if (!ready)
            {
                return;
            }

            int count = character.CutCount;
            if (count != builtCuts || character.Profile != builtProfile)
            {
                RebuildRows(count);
            }

            bool live = GoreCharacterItems.IsLive(character);
            VeneerStyle.SetDisplay(resetButton, live);
            VeneerStyle.SetDisplay(playNote, !live);
            for (int i = 0; i < rows.Count; i++)
            {
                CutRow row = rows[i];
                bool severed = character.IsSevered(row.Index);
                int shown = (severed ? 1 : 0) | (live ? 2 : 0);
                if (row.Shown == shown)
                {
                    continue;
                }

                row.Shown = shown;
                VeneerStyle.SetPill(row.State, severed ? "warn" : "ok", severed ? "Severed" : "On");

                // Hidden, not removed, so the row keeps its width and the layout does not change under a click.
                row.Sever.style.visibility = live && !severed ? Visibility.Visible : Visibility.Hidden;
            }
        }

        // Hides the list while the character is not set up, and the card that holds it when nothing else in the card is shown.
        private void ApplyReady(bool ready)
        {
            VeneerStyle.SetDisplay(host, ready);
            VisualElement card = FindCard();
            if (card != null)
            {
                VeneerStyle.SetDisplay(card, ready || HasOtherVisibleContent(card));
            }
        }

        private VisualElement FindCard()
        {
            VisualElement element = host.parent;
            for (int i = 0; i < 3 && element != null; i++)
            {
                if (element.ClassListContains(CardClass))
                {
                    return element;
                }

                element = element.parent;
            }

            return null;
        }

        private bool HasOtherVisibleContent(VisualElement card)
        {
            for (int i = 0; i < card.childCount; i++)
            {
                VisualElement child = card[i];
                if (child != host && !child.ClassListContains(CardTitleClass) && child.style.display.value != DisplayStyle.None)
                {
                    return true;
                }
            }

            return false;
        }

        private void RebuildRows(int count)
        {
            builtCuts = count;
            builtProfile = character.Profile;
            rows.Clear();
            cutsHost.Clear();
            for (int i = 0; i < count; i++)
            {
                int index = i;
                var line = new VisualElement();
                line.AddToClassList("vn-list-row");
                if ((i & 1) == 1)
                {
                    line.AddToClassList("vn-list-row--alt");
                }

                var name = new Label(character.CutName(i));
                name.AddToClassList("gore-cut-name");
                line.Add(name);

                var limb = new Label(LimbName(character.CutLimb(i)));
                limb.AddToClassList("gore-cut-limb");
                line.Add(limb);

                Label state = VeneerStyle.Pill("On", "ok");
                state.AddToClassList("gore-cut-state");
                line.Add(state);

                Button sever = VeneerStyle.Button(
                    "Sever", null, "Takes this part off with a small throw forward and up (play mode only).",
                    () => character.Sever(index, character.transform.forward * 4f + Vector3.up * 2f), false, brand);
                sever.AddToClassList("gore-cut-button");
                line.Add(sever);

                cutsHost.Add(line);
                rows.Add(new CutRow { Index = i, State = state, Sever = sever });
            }
        }

        private static string LimbName(GoreLimb limb)
        {
            int index = (int)limb;
            return index >= 0 && index < LimbNames.Length ? LimbNames[index] : limb.ToString();
        }
    }
}
