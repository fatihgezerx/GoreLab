using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GoreLab
{
    /// <summary>The panel pages of the Gore Studio that are still drawn with IMGUI inside the studio shell.</summary>
    internal enum GoreStudioPage
    {
        /// <summary>The Skinned meshes card.</summary>
        Meshes,

        /// <summary>The Body parts card (the cut list; its Fill material section is a UI Toolkit card of the shell).</summary>
        Cuts,

        /// <summary>The Cloth parts and Worn equipment cards.</summary>
        Cloth,

        /// <summary>The panels of a static (unskinned) target.</summary>
        Static
    }

    /// <summary>
    /// The hooks between the Gore Studio window and its optional UI Toolkit shell. The window itself needs no Veneer: it keeps
    /// drawing everything with IMGUI (<c>OnGUI</c>) until a shell is attached. The shell lives in the <c>GoreLab.Editor.Studio</c>
    /// assembly (it needs the kit), registers <see cref="ShellFactory"/> and then hosts the heavy IMGUI parts (the panel pages and the
    /// 3D preview) in <c>IMGUIContainer</c>s by calling the <c>ShellDraw*</c> methods below, so the behaviour of those parts is unchanged.
    /// </summary>
    internal sealed partial class GoreStudioWindow
    {
        /// <summary>Set by the studio assembly: builds the shell for a window, or returns null to keep the IMGUI window.</summary>
        internal static Func<GoreStudioWindow, VisualElement> ShellFactory;

        private readonly Vector2[] _pageScroll = new Vector2[4];
        private bool _shellActive;

        /// <summary>Called by Unity when the window's UI Toolkit tree is made (also again after a domain reload or a re-dock).</summary>
        public void CreateGUI()
        {
            _shellActive = false;
            rootVisualElement.Clear();
            TryAttachShell();
        }

        /// <summary>
        /// Puts the shell into the window if the studio assembly registered one and none is attached yet. If building it fails, the exception is
        /// logged and the window keeps drawing with IMGUI, so it never goes blank.
        /// </summary>
        internal void TryAttachShell()
        {
            if (_shellActive || ShellFactory == null || _model == null)
            {
                return;
            }

            VisualElement shell;
            try
            {
                shell = ShellFactory(this);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return;
            }

            if (shell == null)
            {
                return;
            }

            rootVisualElement.Clear();
            rootVisualElement.style.flexGrow = 1f;
            rootVisualElement.Add(shell);
            _shellActive = true;
            Repaint();
        }

        /// <summary>Whether the shell is attached (the window's own <c>OnGUI</c> then draws nothing).</summary>
        internal bool ShellActive => _shellActive;

        /// <summary>The model of the character in the preview.</summary>
        internal GoreStudioModel Model => _model;

        /// <summary>The tools of a static target (null when the window is not enabled).</summary>
        internal GoreStudioStatic StaticTools => _static;

        /// <summary>Whether the target is a skinned character.</summary>
        internal bool HasSkinnedTarget => _model != null && _model.HasTarget && _model.Kind == GoreMeshKind.Skinned;

        /// <summary>Whether the target is a static mesh.</summary>
        internal bool HasStaticTarget => IsStaticTarget;

        /// <summary>The result line of the last Apply (empty for none).</summary>
        internal string ResultMessage => _message;

        /// <summary>The kind of <see cref="ResultMessage"/>.</summary>
        internal MessageType ResultKind => _messageType;

        /// <summary>The warnings of the last static Apply.</summary>
        internal IReadOnlyList<string> StaticWarnings => _staticWarnings;

        /// <summary>How many static warnings the panel shows at most.</summary>
        internal const int ShellMaxWarnings = MaxWarningLines;

        /// <summary>The width the left panel has in a window as wide as <paramref name="windowWidth"/>.</summary>
        internal static float PanelWidth(float windowWidth) => Mathf.Clamp(windowWidth * 0.36f, MinPanelWidth, MaxPanelWidth);

        /// <summary>
        /// The one line that says how the studio stands: <paramref name="level"/> is 0 for fine, 1 for needs attention, 2 for broken, 3 for working
        /// (the same states the IMGUI banner shows with its colours).
        /// </summary>
        internal void ShellStatus(out string text, out int level)
        {
            if (_model == null || !_model.HasTarget)
            {
                text = "Drop a character prefab to begin";
                level = 1;
            }
            else if (_model.HasError)
            {
                text = "There is a problem (see the bottom of this panel)";
                level = 2;
            }
            else if (_model.Kind == GoreMeshKind.Static)
            {
                text = _staticBanner;
                level = _staticBannerColor == GoreGUI.Good ? 0 : _staticBannerColor == GoreGUI.Bad ? 2 : 1;
                if (_static != null && _static.Busy)
                {
                    level = 3;
                }
            }
            else
            {
                text = "Ready: " + GoreGUI.Plural(_model.Cuts.Count, "cut", "cuts") + ", " + _model.Rig + " rig";
                level = 0;
            }
        }

        /// <summary>The text of the Apply button: a prefab asset is applied to, an object of a scene is set up.</summary>
        internal string ShellApplyLabel => _model != null && _model.HasTarget && EditorUtility.IsPersistent(_model.Source) ? "Apply to prefab" : "Set up";

        /// <summary>Whether the Apply button of a static target can be pressed.</summary>
        internal bool ShellStaticApplyReady => _static != null && _static.HasSetups && _static.ApplyBlocker().Length == 0;

        /// <summary>Picks another prefab (loaded at the start of the next layout pass, like every structural change).</summary>
        internal void ShellSetSource(GameObject picked)
        {
            if (_model != null && picked != _model.Source)
            {
                Defer(() => LoadTarget(picked));
            }
        }

        /// <summary>Sets the mesh type by hand.</summary>
        internal void ShellSetKind(GoreMeshKind kind)
        {
            _model.Kind = kind;
            _model.KindOverridden = kind != _model.DetectedKind;
            _model.Dirty = true;
            Repaint();
        }

        /// <summary>Goes back to the mesh type that was found by itself.</summary>
        internal void ShellResetKind()
        {
            Defer(() =>
            {
                _model.Kind = _model.DetectedKind;
                _model.KindOverridden = false;
                _model.Dirty = true;
            });
        }

        /// <summary>Sets the rig by hand (the cuts are made again from it).</summary>
        internal void ShellSetRig(GoreRigType rig)
        {
            _model.Rig = rig;
            _model.RigOverridden = rig != _model.DetectedRig;
            Defer(RebuildCuts);
        }

        /// <summary>Goes back to the rig that was found by itself.</summary>
        internal void ShellResetRig()
        {
            Defer(() =>
            {
                _model.Rig = _model.DetectedRig;
                _model.RigOverridden = false;
                RebuildCuts();
            });
        }

        /// <summary>Chooses another mesh of a prefab with several static meshes.</summary>
        internal void ShellActivateStaticMesh(int index)
        {
            _staticArg = index;
            Defer(ActivateStaticMesh);
        }

        /// <summary>Applies a skinned character (at the start of the next layout pass).</summary>
        internal void ShellApply() => Defer(RunApply);

        /// <summary>Applies a static mesh (at the start of the next layout pass).</summary>
        internal void ShellApplyStatic() => Defer(RunStaticApply);

        /// <summary>Draws one panel page with IMGUI, in a scroll view of its own. Called from the <c>onGUIHandler</c> of an <c>IMGUIContainer</c>.</summary>
        internal void ShellDrawPage(GoreStudioPage page)
        {
            if (_model == null || _static == null)
            {
                return;
            }

            RunLayoutPrologue();
            var index = (int)page;
            GoreGUI.BeginPage();
            _pageScroll[index] = EditorGUILayout.BeginScrollView(_pageScroll[index]);
            switch (page)
            {
                case GoreStudioPage.Meshes when HasSkinnedTarget:
                    DrawMeshes();
                    break;
                case GoreStudioPage.Cuts when HasSkinnedTarget:
                    DrawCuts();
                    break;
                case GoreStudioPage.Cloth when HasSkinnedTarget:
                    DrawCloth();
                    DrawEquipment();
                    break;
                case GoreStudioPage.Static when IsStaticTarget:
                    DrawStaticPanel();
                    break;
            }

            EditorGUILayout.EndScrollView();
            GoreGUI.EndPage();
        }

        /// <summary>Draws the 3D preview and its tools with IMGUI into the whole area of the calling <c>IMGUIContainer</c>.</summary>
        internal void ShellDrawPreview()
        {
            if (_model == null || _preview == null)
            {
                return;
            }

            RunLayoutPrologue();
            var rect = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            DrawPreview(rect);
        }

        // The work that must happen once at the start of each layout pass, before anything is drawn (idempotent: every IMGUI part of the
        // window may call it).
        private void RunLayoutPrologue()
        {
            if (Event.current.type != EventType.Layout)
            {
                return;
            }

            RunDeferred();
            if (_model.Dirty)
            {
                _model.Rebuild();
                _model.Recolor();
                ApplyVisibility();
            }

            RefreshSelectionStats();
            RefreshStaticState();
        }
    }
}
