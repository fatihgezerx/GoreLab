using System;
using Veneer;
using UnityEngine;
using UnityEngine.UIElements;

namespace GoreLab
{
    /// <summary>
    /// The pieces of the <see cref="GoreCharacter"/> Inspector that are not fields, registered with Veneer so the default layout (and a
    /// user's copy of it) can place them: the header state pill (which also keeps the header's subtitle current), the Play Mode strip
    /// that counts the parts off, the Open Gore Studio button, the "not set up" hint and the list of cuts. They can be moved or hidden in
    /// the Inspector Designer, not changed.
    /// </summary>
    internal static class GoreCharacterItems
    {
        /// <summary>Property path of the profile slot.</summary>
        internal const string ProfilePath = "_profile";

        /// <summary>Tooltip of the profile slot.</summary>
        internal const string ProfileTip = "What the setup worked out for this character: the cuts, the parts of its meshes and what a cut-off part does. Shared by every character with the same meshes and bones.";

        /// <summary>Text of the hint shown while the profile is set but the character is not ready.</summary>
        internal const string NotSetUp = "Nothing can be cut yet. Open Tools > Gore Studio, drop this character's prefab in and press Apply to split the skinned meshes of this character into parts.";

        /// <summary>Text of the hint shown while there is no profile.</summary>
        internal const string NoProfile = "No profile yet. Open the Gore Studio, drop this character's prefab in and press Apply: it makes the profile and fills this slot.";

        /// <summary>Id of the header state pill (header badge).</summary>
        internal const string StateId = "gore.character.state";

        /// <summary>Id of the Play Mode strip "n of n parts off" (card item).</summary>
        internal const string PartsOffId = "gore.character.parts-off";

        /// <summary>Id of the Open Gore Studio button (card item).</summary>
        internal const string OpenStudioId = "gore.character.open-studio";

        /// <summary>Id of the "Nothing can be cut yet" hint (card item).</summary>
        internal const string NotSetUpId = "gore.character.not-set-up";

        /// <summary>Id of the list of cuts with the Sever and Reset buttons (card item).</summary>
        internal const string CutsId = "gore.character.cuts";

        private const string SubtitleClass = "vn-subtitle";

        /// <summary>Registers the custom items of <see cref="GoreCharacter"/>. Safe to call again (items are replaced).</summary>
        internal static void Register()
        {
            Type type = typeof(GoreCharacter);

            VeneerCustomItems.Register(type, new VeneerCustomItem
            {
                Id = StateId,
                Label = "State",
                Icon = "check-circle",
                Description = "A pill in the header: Not set up, Ready or (in Play Mode) Parts off. It also keeps the subtitle current (n cuts, n skinned meshes).",
                Slot = VeneerCustomItemSlot.HeaderBadge,
                Build = BuildState,
            });

            VeneerCustomItems.Register(type, new VeneerCustomItem
            {
                Id = PartsOffId,
                Label = "Parts off strip",
                Icon = "eye",
                Description = "A strip that shows only in Play Mode: how many parts are off.",
                Slot = VeneerCustomItemSlot.Item,
                Build = BuildPartsOff,
            });

            VeneerCustomItems.Register(type, new VeneerCustomItem
            {
                Id = OpenStudioId,
                Label = "Open Gore Studio button",
                Icon = "wrench",
                Description = "Opens Tools > Gore Studio, where the character is split into parts and the cuts are set up.",
                Slot = VeneerCustomItemSlot.Item,
                Build = BuildOpenStudio,
            });

            VeneerCustomItems.Register(type, new VeneerCustomItem
            {
                Id = NotSetUpId,
                Label = "Not set up hint",
                Icon = "warning",
                Description = "A warning while the character has a profile but is not ready to be cut.",
                Slot = VeneerCustomItemSlot.Item,
                Build = BuildNotSetUp,
            });

            VeneerCustomItems.Register(type, new VeneerCustomItem
            {
                Id = CutsId,
                Label = "Cuts",
                Icon = "list",
                Description = "The cuts of the character with Sever buttons and Reset character (Play Mode). Shows only while the character is set up; its card is hidden otherwise.",
                Slot = VeneerCustomItemSlot.Item,
                Build = GoreCutsView.Build,
            });
        }

        /// <summary>True for an object of a scene that is running (not for a prefab asset): only then can parts be taken off.</summary>
        /// <param name="character">A live character (not destroyed).</param>
        internal static bool IsLive(GoreCharacter character)
        {
            return Application.isPlaying && character.gameObject.scene.IsValid();
        }

        private static GoreCharacter CharacterOf(VeneerCustomItemContext context)
        {
            return context.SerializedObject.targetObject as GoreCharacter;
        }

        private static VisualElement BuildState(VeneerCustomItemContext context)
        {
            GoreCharacter character = CharacterOf(context);
            if (character == null)
            {
                return null;
            }

            Label pill = VeneerStyle.Pill(string.Empty, "idle");
            var view = new StateView(character, pill, FindSubtitle(context.Builder.Current));

            // The first run of Poll is on the first tick: show the right state at once.
            view.Refresh();
            context.Poll(view.Refresh);
            return pill;
        }

        // The header's subtitle label, found from the badge container (the header's right end: its card is two levels up). Null when the
        // header has none (a header drawn over a banner picture, or no subtitle): the subtitle is then left as the layout wrote it.
        private static Label FindSubtitle(VisualElement container)
        {
            VisualElement scope = container;
            for (int i = 0; i < 2 && scope != null && scope.parent != null; i++)
            {
                scope = scope.parent;
            }

            return scope != null ? scope.Q<Label>(className: SubtitleClass) : null;
        }

        private static VisualElement BuildPartsOff(VeneerCustomItemContext context)
        {
            GoreCharacter character = CharacterOf(context);
            if (character == null)
            {
                return null;
            }

            var text = new PartsOffText(character);
            context.Builder.Live(text.Read);
            return null;
        }

        private static VisualElement BuildOpenStudio(VeneerCustomItemContext context)
        {
            context.Builder.Space(2f);
            context.Builder.Button(
                "Open Gore Studio", "wrench", () => GoreStudioWindow.Open(null),
                "Opens Tools > Gore Studio, where the character is split into parts and the cuts are set up.", true);
            return null;
        }

        private static VisualElement BuildNotSetUp(VeneerCustomItemContext context)
        {
            GoreCharacter character = CharacterOf(context);
            if (character == null)
            {
                return null;
            }

            context.Builder.ConditionalHint(() => character != null && character.Profile != null && !character.IsReady ? NotSetUp : null, "warn", true);
            return null;
        }

        /// <summary>Keeps the header pill and the subtitle in step with the character; writes only when something changed.</summary>
        private sealed class StateView
        {
            private readonly GoreCharacter character;
            private readonly Label pill;
            private readonly Label subtitle;
            private string subtitleText = string.Empty;
            private int shownCuts = -1;
            private int shownSets = -1;
            private int shownReady = -1;

            public StateView(GoreCharacter character, Label pill, Label subtitle)
            {
                this.character = character;
                this.pill = pill;
                this.subtitle = subtitle;
            }

            public void Refresh()
            {
                if (character == null)
                {
                    return;
                }

                bool ready = character.IsReady;
                int cuts = ready ? character.CutCount : 0;
                int sets = ready ? character.Sets.Count : 0;
                int readyFlag = ready ? 1 : 0;
                if (cuts != shownCuts || sets != shownSets || readyFlag != shownReady)
                {
                    shownCuts = cuts;
                    shownSets = sets;
                    shownReady = readyFlag;
                    subtitleText = ready
                        ? GoreGUI.Plural(cuts, "cut", "cuts") + ", " + GoreGUI.Plural(sets, "skinned mesh", "skinned meshes")
                        : "Not set up";
                    if (subtitle != null)
                    {
                        subtitle.text = subtitleText;
                    }
                }

                if (!ready)
                {
                    VeneerStyle.SetPill(pill, "warn", "Not set up");
                }
                else if (IsLive(character) && character.IsCut)
                {
                    VeneerStyle.SetPill(pill, "special", "Parts off");
                }
                else
                {
                    VeneerStyle.SetPill(pill, "ok", "Ready");
                }
            }
        }

        /// <summary>The text of the Play Mode strip; the string is made again only when a number changed.</summary>
        private sealed class PartsOffText
        {
            private readonly GoreCharacter character;
            private string text = string.Empty;
            private int shownOff = -1;
            private int shownCount = -1;

            public PartsOffText(GoreCharacter character)
            {
                this.character = character;
            }

            public string Read()
            {
                if (character == null)
                {
                    return text;
                }

                int count = character.CutCount;
                int off = 0;
                for (int i = 0; i < count; i++)
                {
                    if (character.IsSevered(i))
                    {
                        off++;
                    }
                }

                if (off != shownOff || count != shownCount)
                {
                    shownOff = off;
                    shownCount = count;
                    text = off + " of " + count + " parts off";
                }

                return text;
            }
        }
    }
}
