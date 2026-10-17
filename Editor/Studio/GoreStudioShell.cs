using System.Collections.Generic;
using Veneer;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace GoreLab
{
    /// <summary>
    /// The UI Toolkit shell of the Gore Studio window (the studio look): a title bar with the logo and a state pill, tabs for the panel pages,
    /// a two-pane layout (the panel at the left, the 3D preview at the right), and the simple parts of the panel rebuilt as cards: Target,
    /// Fill material and Apply. The heavy parts (the Skinned meshes, Body parts, Cloth parts, Worn equipment and static panels, and the 3D
    /// preview with its tools) are still IMGUI, hosted in <c>IMGUIContainer</c>s by calling the window's <c>ShellDraw*</c> methods, so their
    /// behaviour (selection, Undo, hotkeys, orbit view) is unchanged. The window owns all the state; the shell reads it five to ten times a
    /// second while it is on a panel and writes only through the window's <c>Shell*</c> methods.
    /// </summary>
    internal sealed class GoreStudioShell
    {
        private const long RefreshMilliseconds = 100;
        private const string TabKey = "GoreLab.GoreStudio.Tab";

        private static readonly string[] TabLabels = { "Meshes", "Parts", "Cloth", "Static" };
        private static readonly string[] TabIcons = { "layers", "sword", "shield", "cube" };

        private static readonly string[] TabTips =
        {
            "The skinned meshes of the character: which ones are cut, get lids and are shown.",
            "The body parts that can come off, their bones and their fill material.",
            "The worn equipment and what each piece does when a part is cut off.",
            "The panels of a static object: how it breaks into pieces."
        };

        private readonly GoreStudioWindow window;
        private readonly VisualElement root;
        private readonly VisualElement[] pageElements = new VisualElement[4];
        private readonly List<string> meshChoices = new List<string>(4);
        private readonly Hint[] warningHints = new Hint[GoreStudioWindow.ShellMaxWarnings];

        private VisualElement left;
        private VisualElement tabBar;
        private Label pill;
        private Label status;

        private ObjectField prefabField;
        private VisualElement kindRow;
        private EnumField kindField;
        private Button kindAuto;
        private VisualElement rigRow;
        private EnumField rigField;
        private Button rigAuto;
        private VisualElement meshRow;
        private DropdownField meshField;
        private string[] meshNamesShown;

        private VisualElement fillCard;
        private Label fillCount;
        private VisualElement fillSwatch;
        private Label fillName;
        private ObjectField fillField;
        private Button fillAll;
        private Label fillNote;

        private Button applyButton;
        private Hint errorHint;
        private readonly Hint[] resultHints = new Hint[3];

        private IVisualElementScheduledItem tick;
        private bool sourcePending;
        private int sourcePendingTicks;
        private int selectedTab;
        private VeneerStyle.TabSwitcher pageSwitcher;
        private string shownPillText;
        private string shownPillKind;
        private string shownStatus;

        private struct Hint
        {
            public VisualElement Element;
            public Label Text;
        }

        private GoreStudioShell(GoreStudioWindow window)
        {
            this.window = window;
            VeneerBrand brand = GoreBrand.Value;

            // The window sets its plain title in OnEnable; the logo before the name on the tab comes with the shell, like the other studio windows.
            window.titleContent = new GUIContent("Gore Studio", brand.Logo);
            root = VeneerStyle.Root(brand, "vn-window");
            root.AddToClassList("gore-shell");
            root.style.flexGrow = 1f;

            root.Add(BuildTitleBar(brand));
            status = VeneerStyle.Subtitle(string.Empty);
            status.style.marginBottom = 6f;
            root.Add(status);

            var body = new VisualElement();
            body.AddToClassList("gore-body");
            root.Add(body);

            left = new VisualElement();
            left.AddToClassList("gore-left");
            body.Add(left);
            BuildLeft(brand);

            var right = new VisualElement();
            right.AddToClassList("gore-right");
            body.Add(right);
            var preview = new IMGUIContainer(window.ShellDrawPreview);
            preview.AddToClassList("gore-ims");
            preview.focusable = true;

            // The hotkeys of the preview (Tab, M, A, Z...) work while the mouse is over it, as before: take the keyboard focus on entering,
            // unless another control (a text field of the panel) has it.
            preview.RegisterCallback<PointerEnterEvent>(evt =>
            {
                if (preview.panel != null && preview.panel.focusController.focusedElement == null)
                {
                    preview.Focus();
                }
            });
            right.Add(preview);

            root.RegisterCallback<GeometryChangedEvent>(evt => left.style.width = GoreStudioWindow.PanelWidth(root.layout.width));
            root.RegisterCallback<AttachToPanelEvent>(evt => tick?.Resume());
            root.RegisterCallback<DetachFromPanelEvent>(evt => tick?.Pause());
            tick = root.schedule.Execute(Refresh).Every(RefreshMilliseconds);
            Refresh();
        }

        /// <summary>The root of the shell, to be put into the window.</summary>
        private VisualElement Root => root;

        [InitializeOnLoadMethod]
        private static void Register()
        {
            GoreStudioWindow.ShellFactory = window => new GoreStudioShell(window).Root;

            // A window that was open before the scripts reloaded may have built its tree before this ran: give it the shell now.
            EditorApplication.delayCall += AttachToOpenWindows;
        }

        private static void AttachToOpenWindows()
        {
            GoreStudioWindow[] windows = Resources.FindObjectsOfTypeAll<GoreStudioWindow>();
            for (int i = 0; i < windows.Length; i++)
            {
                windows[i].TryAttachShell();
            }
        }

        // ------------------------------------------------------------------ building

        private VisualElement BuildTitleBar(VeneerBrand brand)
        {
            var bar = new VisualElement();
            bar.AddToClassList("vn-titlebar");
            Texture2D logo = brand.Logo;
            if (logo != null)
            {
                var logoElement = new VisualElement();
                logoElement.AddToClassList("vn-titlebar__logo");
                logoElement.pickingMode = PickingMode.Ignore;
                logoElement.style.backgroundImage = new StyleBackground(logo);
                bar.Add(logoElement);
            }

            Label title = VeneerStyle.Title("Gore Studio");
            title.style.fontSize = 15f;
            title.AddToClassList("vn-ellipsis");
            bar.Add(title);
            pill = VeneerStyle.Pill(string.Empty, "info");
            bar.Add(pill);
            bar.Add(VeneerStyle.Spacer());
            return bar;
        }

        private void BuildLeft(VeneerBrand brand)
        {
            left.Add(BuildTargetCard(brand));

            tabBar = VeneerStyle.Tabs(TabIcons, TabLabels, TabTips, selectedTab = SessionState.GetInt(TabKey, 0), OnTabChanged, brand);
            tabBar.AddToClassList("gore-modes");
            left.Add(tabBar);

            var pages = new VisualElement();
            pages.style.flexGrow = 1f;
            pages.style.flexShrink = 1f;
            pages.style.minHeight = 0f;
            left.Add(pages);
            for (int i = 0; i < pageElements.Length; i++)
            {
                GoreStudioPage page = (GoreStudioPage)i;
                var element = new VisualElement();
                element.style.flexGrow = 1f;
                element.style.flexShrink = 1f;
                element.style.minHeight = 0f;
                VisualElement host = element;
                var container = new IMGUIContainer(() => DrawPageFaded(host, page));
                container.AddToClassList("gore-ims");
                element.Add(container);
                if (page == GoreStudioPage.Cuts)
                {
                    element.Add(BuildFillCard(brand));
                }

                pageElements[i] = element;
                pages.Add(element);
            }

            pageSwitcher = new VeneerStyle.TabSwitcher(pages, pageElements);
            pageSwitcher.Initialize(tabBar, -1);

            left.Add(BuildApplyCard(brand));
        }

        // An IMGUIContainer ignores the opacity of its parents, so the tab switch fade would show only as a slide and the page would pop
        // in and out. The page element carries the animated opacity; the IMGUI draw reads it and tints everything with GUI.color.
        private void DrawPageFaded(VisualElement host, GoreStudioPage page)
        {
            float alpha = host.resolvedStyle.opacity;
            if (alpha >= 0.999f)
            {
                window.ShellDrawPage(page);
                return;
            }

            Color previous = GUI.color;
            GUI.color = new Color(previous.r, previous.g, previous.b, previous.a * Mathf.Clamp01(alpha));
            try
            {
                window.ShellDrawPage(page);
            }
            finally
            {
                GUI.color = previous;
            }
        }

        private VisualElement BuildTargetCard(VeneerBrand brand)
        {
            VisualElement card = VeneerStyle.Card("Target", "target", brand);

            prefabField = new ObjectField { objectType = typeof(GameObject), allowSceneObjects = true };
            prefabField.RegisterValueChangedCallback(evt =>
            {
                sourcePending = true;
                sourcePendingTicks = 0;
                window.ShellSetSource(evt.newValue as GameObject);
            });
            card.Add(VeneerStyle.FieldRow("Prefab", prefabField, "A character prefab (or an object of the scene). Nothing is added to the scene.", out _));

            kindField = new EnumField(GoreMeshKind.Skinned);
            kindField.RegisterValueChangedCallback(evt => window.ShellSetKind((GoreMeshKind)evt.newValue));
            kindAuto = VeneerStyle.Button("Auto", null, "Go back to what was found by itself.", window.ShellResetKind, false, brand);
            kindRow = ValueWithAuto("Mesh type", kindField, kindAuto,
                "Skinned meshes follow bones. Static meshes have none (they break into pieces). Found by itself; change it if it is wrong.");
            card.Add(kindRow);

            rigField = new EnumField(GoreRigType.Humanoid);
            rigField.RegisterValueChangedCallback(evt => window.ShellSetRig((GoreRigType)evt.newValue));
            rigAuto = VeneerStyle.Button("Auto", null, "Go back to what was found by itself.", window.ShellResetRig, false, brand);
            rigRow = ValueWithAuto("Rig", rigField, rigAuto,
                "Humanoid gives the thirteen cuts by itself. Generic needs bones picked by hand. Found from the Animator's avatar; change it if it is wrong.");
            card.Add(rigRow);

            meshField = new DropdownField(new List<string> { "-" }, 0);
            meshField.RegisterValueChangedCallback(evt =>
            {
                if (meshField.index >= 0)
                {
                    window.ShellActivateStaticMesh(meshField.index);
                }
            });
            meshRow = VeneerStyle.FieldRow("Mesh", meshField, "The mesh to set up. Each mesh of the prefab has its own settings and its own profile.", out _);
            card.Add(meshRow);
            return card;
        }

        private static VisualElement ValueWithAuto(string label, VisualElement field, Button auto, string tooltip)
        {
            var inner = VeneerStyle.Row();
            field.style.flexGrow = 1f;
            field.style.flexShrink = 1f;
            field.style.marginLeft = 0f;
            field.style.marginRight = 0f;
            inner.Add(field);
            auto.style.width = 44f;
            auto.style.flexShrink = 0f;
            inner.Add(auto);
            return VeneerStyle.FieldRow(label, inner, tooltip, out _);
        }

        private VisualElement BuildFillCard(VeneerBrand brand)
        {
            fillCard = VeneerStyle.Card("Fill material", "layers", brand);
            fillCard.style.flexShrink = 0f;
            fillCard.style.marginTop = 6f;
            fillCount = VeneerStyle.MutedLabel(string.Empty);
            fillCard[0].Add(VeneerStyle.Spacer());
            fillCard[0].Add(fillCount);

            VisualElement line = VeneerStyle.Row();
            line.style.alignItems = Align.Center;
            fillSwatch = new VisualElement();
            fillSwatch.style.width = 14f;
            fillSwatch.style.height = 14f;
            fillSwatch.style.flexShrink = 0f;
            fillSwatch.style.borderTopWidth = fillSwatch.style.borderBottomWidth = fillSwatch.style.borderLeftWidth = fillSwatch.style.borderRightWidth = 1f;
            fillSwatch.style.borderTopColor = fillSwatch.style.borderBottomColor = fillSwatch.style.borderLeftColor = fillSwatch.style.borderRightColor = new Color(1f, 1f, 1f, 0.8f);
            line.Add(fillSwatch);
            fillName = new Label("No part chosen");
            fillName.AddToClassList("vn-ellipsis");
            fillName.style.width = 110f;
            fillName.style.flexShrink = 0f;
            fillName.style.unityFontStyleAndWeight = FontStyle.Bold;
            line.Add(fillName);

            fillField = new ObjectField { objectType = typeof(Material), allowSceneObjects = false };
            fillField.tooltip = "The material that fills the cut of the chosen part: the lid on the part and the stump on the body. Empty = the default red material.";
            fillField.style.flexGrow = 1f;
            fillField.style.flexShrink = 1f;
            fillField.RegisterValueChangedCallback(evt =>
            {
                GoreBaker.CutSpec cut = SelectedCut();
                if (cut != null)
                {
                    cut.Fill = evt.newValue as Material;
                }
            });
            line.Add(fillField);

            fillAll = VeneerStyle.Button("To all", null, "Gives the material of the chosen part to every body part.", () =>
            {
                GoreBaker.CutSpec cut = SelectedCut();
                if (cut == null)
                {
                    return;
                }

                Material fill = cut.Fill;
                foreach (GoreBaker.CutSpec other in window.Model.Cuts)
                {
                    other.Fill = fill;
                }
            }, false, brand);
            fillAll.style.flexShrink = 0f;
            line.Add(fillAll);
            fillCard.Add(line);

            fillNote = VeneerStyle.MutedLabel(string.Empty);
            fillNote.style.whiteSpace = WhiteSpace.Normal;
            fillNote.style.marginTop = 4f;
            fillNote.style.marginBottom = 4f;
            fillCard.Add(fillNote);
            return fillCard;
        }

        private VisualElement BuildApplyCard(VeneerBrand brand)
        {
            var area = new VisualElement();
            area.style.flexShrink = 0f;
            area.style.marginTop = 6f;

            errorHint = MakeHint(area, "error", brand);
            resultHints[0] = MakeHint(area, "info", brand);
            resultHints[1] = MakeHint(area, "warn", brand);
            resultHints[2] = MakeHint(area, "error", brand);
            for (int i = 0; i < warningHints.Length; i++)
            {
                warningHints[i] = MakeHint(area, "warn", brand);
            }

            applyButton = VeneerStyle.Button("Set up", "play", string.Empty, OnApply, true, brand);
            applyButton.style.height = 28f;
            area.Add(applyButton);
            return area;
        }

        private static Hint MakeHint(VisualElement parent, string kind, VeneerBrand brand)
        {
            VisualElement element = VeneerStyle.Hint(string.Empty, kind, out Label text, brand);
            VeneerStyle.SetDisplay(element, false);
            parent.Add(element);
            return new Hint { Element = element, Text = text };
        }

        // ------------------------------------------------------------------ events

        private void OnTabChanged(int index)
        {
            selectedTab = index;
            SessionState.SetInt(TabKey, index);
            GoreStudioModel model = window.Model;
            bool usable = model != null && model.HasTarget
                && (index == (int)GoreStudioPage.Static ? window.HasStaticTarget : window.HasSkinnedTarget);
            if (usable && pageSwitcher != null)
            {
                pageSwitcher.Switch(index);
            }
            else
            {
                ShowPages();
            }

            window.Repaint();
        }

        private void OnApply()
        {
            if (window.HasStaticTarget)
            {
                window.ShellApplyStatic();
            }
            else
            {
                window.ShellApply();
            }
        }

        private GoreBaker.CutSpec SelectedCut()
        {
            GoreStudioModel model = window.Model;
            return model != null && model.SelectedCut >= 0 && model.SelectedCut < model.Cuts.Count ? model.Cuts[model.SelectedCut] : null;
        }

        // ------------------------------------------------------------------ refresh

        // Runs every 100 ms while the window is on a panel: copies what the window knows into the cards. Every write is guarded by a
        // comparison, so an idle studio changes nothing (and so repaints nothing).
        private void Refresh()
        {
            if (window == null || window.Model == null)
            {
                return;
            }

            GoreStudioModel model = window.Model;
            bool has = model.HasTarget;
            bool isStatic = window.HasStaticTarget;
            bool skinned = window.HasSkinnedTarget;

            window.ShellStatus(out string statusText, out int level);
            string pillKind = level == 0 ? "ok" : level == 1 ? "warn" : level == 2 ? "error" : "special";
            string pillText = !has ? "Nothing selected" : level == 2 ? "Problem" : level == 3 ? "Baking" : "Target selected";
            if (pillText != shownPillText || pillKind != shownPillKind)
            {
                shownPillText = pillText;
                shownPillKind = pillKind;
                VeneerStyle.SetPill(pill, pillKind, pillText);
            }

            if (statusText != shownStatus)
            {
                shownStatus = statusText;
                status.text = statusText;
            }

            RefreshTarget(model, has, isStatic, skinned);
            RefreshTabs(has, isStatic, skinned);
            RefreshFill(model, skinned);
            RefreshApply(model, has, isStatic);
        }

        private void RefreshTarget(GoreStudioModel model, bool has, bool isStatic, bool skinned)
        {
            // A prefab that was just picked is loaded at the start of the next layout pass: do not put the old one back meanwhile.
            if (sourcePending && (model.Source == prefabField.value || ++sourcePendingTicks > 5))
            {
                sourcePending = false;
            }

            if (!sourcePending && prefabField.value != model.Source)
            {
                prefabField.SetValueWithoutNotify(model.Source);
            }

            VeneerStyle.SetDisplay(kindRow, has);
            VeneerStyle.SetDisplay(rigRow, skinned);
            if (has)
            {
                if (!Equals(kindField.value, model.Kind))
                {
                    kindField.SetValueWithoutNotify(model.Kind);
                }

                kindAuto.SetEnabled(model.KindOverridden);
            }

            if (skinned)
            {
                if (!Equals(rigField.value, model.Rig))
                {
                    rigField.SetValueWithoutNotify(model.Rig);
                }

                rigAuto.SetEnabled(model.RigOverridden);
            }

            GoreStudioStatic tools = window.StaticTools;
            bool showMesh = isStatic && tools != null && tools.HasSetups && tools.Setups.Count >= 2;
            VeneerStyle.SetDisplay(meshRow, showMesh);
            if (showMesh)
            {
                if (!ReferenceEquals(meshNamesShown, tools.MeshNames))
                {
                    meshNamesShown = tools.MeshNames;
                    meshChoices.Clear();
                    meshChoices.AddRange(meshNamesShown);
                    meshField.choices = meshChoices;
                }

                int active = tools.ActiveIndex;
                if (active >= 0 && active < meshChoices.Count && meshField.index != active)
                {
                    meshField.SetValueWithoutNotify(meshChoices[active]);
                }
            }
        }

        private void RefreshTabs(bool has, bool isStatic, bool skinned)
        {
            VeneerStyle.SetDisplay(tabBar, has);
            for (int i = 0; i < tabBar.childCount; i++)
            {
                bool visible = i == (int)GoreStudioPage.Static ? isStatic : skinned;
                VeneerStyle.SetDisplay(tabBar[i], visible);
            }

            // The selected tab must be one of the visible ones: a static target has only its own tab, a skinned one the other three.
            int wanted = isStatic ? (int)GoreStudioPage.Static : selectedTab == (int)GoreStudioPage.Static ? 0 : selectedTab;
            if (has && wanted != VeneerStyle.GetSegmentedSelected(tabBar))
            {
                VeneerStyle.SetSegmentedSelected(tabBar, wanted);
            }

            ShowPages(has, isStatic, skinned, wanted);
        }

        private void ShowPages()
        {
            GoreStudioModel model = window.Model;
            if (model == null)
            {
                return;
            }

            bool isStatic = window.HasStaticTarget;
            bool skinned = window.HasSkinnedTarget;
            int wanted = isStatic ? (int)GoreStudioPage.Static : selectedTab == (int)GoreStudioPage.Static ? 0 : selectedTab;
            ShowPages(model.HasTarget, isStatic, skinned, wanted);
        }

        private void ShowPages(bool has, bool isStatic, bool skinned, int selected)
        {
            // The switcher owns the pages' display while it fades; it ends on the tab bar's selection, and the next refresh checks again.
            if (pageSwitcher != null && pageSwitcher.Animating)
            {
                return;
            }

            for (int i = 0; i < pageElements.Length; i++)
            {
                bool usable = has && (i == (int)GoreStudioPage.Static ? isStatic : skinned);
                VeneerStyle.SetDisplay(pageElements[i], usable && i == selected);
            }
        }

        private void RefreshFill(GoreStudioModel model, bool skinned)
        {
            bool show = skinned && model.Cuts.Count > 0;
            VeneerStyle.SetDisplay(fillCard, show);
            if (!show)
            {
                return;
            }

            int set = 0;
            for (int i = 0; i < model.Cuts.Count; i++)
            {
                if (model.Cuts[i].Fill != null)
                {
                    set++;
                }
            }

            SetText(fillCount, set + " of " + model.Cuts.Count + " set");
            GoreBaker.CutSpec selected = SelectedCut();
            fillField.SetEnabled(selected != null);
            fillAll.SetEnabled(selected != null);
            if (selected != null)
            {
                fillSwatch.style.backgroundColor = (Color)model.CutColor(model.SelectedCut);
                VeneerStyle.SetDisplay(fillSwatch, true);
                SetText(fillName, selected.Name);
                if (fillField.value != selected.Fill)
                {
                    fillField.SetValueWithoutNotify(selected.Fill);
                }

                SetText(fillNote, "Fills the cut: the lid on the part and the stump on the body. Empty = red.");
            }
            else
            {
                VeneerStyle.SetDisplay(fillSwatch, false);
                SetText(fillName, "No part chosen");
                if (fillField.value != null)
                {
                    fillField.SetValueWithoutNotify(null);
                }

                SetText(fillNote, "Click a colour in the Parts list above to choose a part.");
            }
        }

        private void RefreshApply(GoreStudioModel model, bool has, bool isStatic)
        {
            ShowHint(errorHint, has && model.HasError ? model.FirstError() : null);

            string message = window.ResultMessage;
            MessageType kind = window.ResultKind;
            ShowHint(resultHints[0], !string.IsNullOrEmpty(message) && kind != MessageType.Warning && kind != MessageType.Error ? message : null);
            ShowHint(resultHints[1], !string.IsNullOrEmpty(message) && kind == MessageType.Warning ? message : null);
            ShowHint(resultHints[2], !string.IsNullOrEmpty(message) && kind == MessageType.Error ? message : null);

            IReadOnlyList<string> warnings = window.StaticWarnings;
            for (int i = 0; i < warningHints.Length; i++)
            {
                ShowHint(warningHints[i], isStatic && i < warnings.Count ? warnings[i] : null);
            }

            SetText(applyButton.Q<Label>(className: "vn-button__label") ?? applyButton.Q<Label>(), window.ShellApplyLabel);
            applyButton.tooltip = isStatic
                ? "Divides the mesh into pieces, makes the lids and saves everything into the prefab (the pieces are assets next to the profile)."
                : "Splits the meshes into parts, makes the lids and saves everything into the prefab (the parts are assets next to the profile).";
            applyButton.SetEnabled(isStatic ? window.ShellStaticApplyReady : model.CanApply);
        }

        private static void ShowHint(Hint hint, string text)
        {
            bool show = !string.IsNullOrEmpty(text);
            VeneerStyle.SetDisplay(hint.Element, show);
            if (show)
            {
                SetText(hint.Text, text);
            }
        }

        private static void SetText(Label label, string text)
        {
            if (label != null && label.text != text)
            {
                label.text = text;
            }
        }
    }
}
