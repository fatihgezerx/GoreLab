using System;
using System.Collections.Generic;
using Veneer;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GoreLab
{
    /// <summary>
    /// The studio-look Inspector of a <see cref="GoreCharacter"/> (replaces the IMGUI one when Veneer is installed). A header
    /// with a state pill (not set up / ready / parts off), the profile slot with a button that opens the Gore Studio, hints while the
    /// character is not set up, and the list of cuts. In Play Mode every cut that is still on has a Sever button and the character can be reset.
    /// </summary>
    [CustomEditor(typeof(GoreCharacter))]
    internal sealed class GoreCharacterStudioEditor : VeneerInspector<GoreCharacter>
    {
        private const string ProfilePath = "_profile";
        private const string ProfileTip = "What the setup worked out for this character: the cuts, the parts of its meshes and what a cut-off part does. Shared by every character with the same meshes and bones.";
        private const string NotSetUp = "Nothing can be cut yet. Open Tools > Gore Studio, drop this character's prefab in and press Apply to split the skinned meshes of this character into parts.";
        private const string NoProfile = "No profile yet. Open the Gore Studio, drop this character's prefab in and press Apply: it makes the profile and fills this slot.";

        private static readonly string[] LimbNames = Enum.GetNames(typeof(GoreLimb));

        private readonly List<CutRow> rows = new List<CutRow>(16);
        private GoreCharacter character;
        private Label subtitle;
        private VisualElement cutsCard;
        private VisualElement cutsHost;
        private VisualElement resetButton;
        private VisualElement playNote;
        private GoreProfile builtProfile;
        private int builtCuts = -1;

        private sealed class CutRow
        {
            public int Index;
            public Label State;
            public Button Sever;
            public int Shown = -1;
        }

        protected override VeneerBrand Brand => GoreBrand.Value;

        protected override void Build(VeneerInspectorBuilder b)
        {
            if (TargetCount > 1)
            {
                b.Header("Gore Character", "Several characters selected");
                b.Card("Profile", "database", () => b.Object(ProfilePath, "Profile", typeof(GoreProfile)));
                b.Hint("Select one Gore Character at a time to see its cuts.", "info");
                return;
            }

            character = Target;
            VisualElement header = b.Header("Gore Character", SubtitleText(), null);
            subtitle = header.Q<Label>(className: "vn-subtitle");
            b.StatePill(ReadPill);
            b.Live(() => PartsOffText());

            b.Card("Profile", "database", () =>
            {
                VisualElement slot = b.Object(ProfilePath, "Profile", typeof(GoreProfile));
                slot.tooltip = ProfileTip;
                b.Space(2f);
                b.Button(
                    "Open Gore Studio", "wrench", () => GoreStudioWindow.Open(null),
                    "Opens Tools > Gore Studio, where the character is split into parts and the cuts are set up.", true);
            });

            b.ConditionalHint(() => character.Profile == null ? NoProfile : null, "warn", true);
            b.ConditionalHint(() => character.Profile != null && !character.IsReady ? NotSetUp : null, "warn", true);

            cutsCard = b.Card("Cuts", "list");
            using (b.Into(cutsCard))
            {
                cutsHost = new VisualElement();
                b.Add(cutsHost);
                resetButton = b.Button(
                    "Reset character", "refresh", () => character.ResetCharacter(),
                    "Puts every part back and removes the parts that were cut off (play mode only).");
                playNote = b.Hint("The Sever and Reset buttons appear in Play Mode.", "info");
            }

            RefreshCuts();
        }

        // True for an object of a scene that is running (not for a prefab asset): only then can parts be taken off.
        private bool IsLive => Application.isPlaying && character.gameObject.scene.IsValid();

        private string SubtitleText() =>
            character.IsReady ? GoreGUI.Plural(character.CutCount, "cut", "cuts") + ", " + GoreGUI.Plural(character.Sets.Count, "skinned mesh", "skinned meshes") : "Not set up";

        // Run by the builder's 200 ms scheduler (paused while the Inspector is not shown): the pill provider is also the refresh hook
        // of the subtitle and the cut rows, so this Inspector needs no scheduler of its own.
        private (string text, string kind) ReadPill()
        {
            string text = SubtitleText();
            if (subtitle != null && subtitle.text != text)
            {
                subtitle.text = text;
            }

            RefreshCuts();
            if (!character.IsReady)
            {
                return ("Not set up", "warn");
            }

            return IsLive && character.IsCut ? ("Parts off", "special") : ("Ready", "ok");
        }

        private string PartsOffText()
        {
            int count = character.CutCount;
            int off = 0;
            for (int i = 0; i < count; i++)
            {
                if (character.IsSevered(i))
                {
                    off++;
                }
            }

            return off + " of " + count + " parts off";
        }

        private void RefreshCuts()
        {
            bool ready = character.IsReady;
            VeneerStyle.SetDisplay(cutsCard, ready);
            if (!ready)
            {
                return;
            }

            int count = character.CutCount;
            if (count != builtCuts || character.Profile != builtProfile)
            {
                RebuildRows(count);
            }

            bool live = IsLive;
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
                    () => character.Sever(index, character.transform.forward * 4f + Vector3.up * 2f), false, GoreBrand.Value);
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
