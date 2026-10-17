using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GoreLab
{
    /// <summary>
    /// <c>Tools &gt; Gore Studio</c>: drop a character prefab in, see it in a preview you orbit like in Blender, and see
    /// where every cut will separate its meshes before anything is set up. The left panel holds the target (mesh kind, rig), the skinned meshes
    /// and the cuts; the preview has the selection tools (the toolbar at its top left) and the bar at its bottom that adds the selection to a cut.
    /// Apply sets the prefab up. Nothing is put in a scene. The preview and its tools are in <c>GoreStudioWindow.Preview.cs</c>.
    /// </summary>
    internal sealed partial class GoreStudioWindow : EditorWindow
    {
        private const float MinPanelWidth = 330f;
        private const float MaxPanelWidth = 430f;

        private static readonly Color Background = new Color(0.16f, 0.17f, 0.19f);

        [SerializeField] private GameObject _source;
        [SerializeField] private GoreTool _tool = GoreTool.Vertex;
        [SerializeField] private float _brushRadius = 30f;

        // The way the preview paints: the parts in flat colours (where the cuts separate), or the character as it is (its materials and textures).
        [SerializeField] private bool _materialView;

        // Object mode picks whole objects; edit mode selects vertices, edges and faces of the picked body mesh.
        [SerializeField] private bool _editMode;

        // The coloured parts drawn see-through, and the two overlays of the cuts (the rings and the lids).
        [SerializeField] private bool _xray;
        [SerializeField] private bool _showLines = true;
        [SerializeField] private bool _showLids = true;

        private PreviewRenderUtility _preview;
        private GoreOrbitView _view;
        private GoreStudioModel _model;
        private Material _opaqueMaterial;
        private Material _xrayBodyMaterial;
        private Material _xrayClothMaterial;
        private Material _ringMaterial;
        private Material _lidMaterial;
        private Material _wireMaterial;
        private Material _faceMaterial;
        private Mesh _ringMesh;
        private Mesh _dotMesh;
        private Mesh _edgeMesh;
        private Mesh _faceMesh;
        private readonly List<GoreStudioMeshEdit> _edits = new List<GoreStudioMeshEdit>(16);
        private readonly List<Vector3> _overlayVertices = new List<Vector3>(4096);
        private readonly List<Color32> _overlayColors = new List<Color32>(4096);
        private readonly List<int> _overlayIndices = new List<int>(6144);
        private readonly List<Action> _deferred = new List<Action>(4);
        private Vector2 _scroll;
        private bool _framePending;
        private string _message = string.Empty;
        private MessageType _messageType = MessageType.Info;

        /// <summary>Opens the studio (with <paramref name="character"/> already in it when given).</summary>
        internal static void Open(GameObject character)
        {
            var window = GetWindow<GoreStudioWindow>(false, "Gore Studio", true);
            if (character != null)
            {
                window._source = character;
                window.Defer(() => window.LoadTarget(character));
            }

            window.Show();
            window.Repaint();
        }

        [MenuItem("Tools/Gore Studio")]
        private static void OpenFromMenu() => Open(null);

        #region Lifetime

        private void OnEnable()
        {
            minSize = new Vector2(900f, 560f);
            titleContent = new GUIContent("Gore Studio");
            wantsMouseMove = true;
            wantsMouseEnterLeaveWindow = true;

            _preview = new PreviewRenderUtility();
            _preview.camera.clearFlags = CameraClearFlags.SolidColor;
            _preview.camera.backgroundColor = Background;
            _preview.ambientColor = new Color(0.46f, 0.47f, 0.5f);
            if (_preview.lights.Length > 0)
            {
                _preview.lights[0].intensity = 1.15f;
                _preview.lights[0].transform.rotation = Quaternion.Euler(35f, -140f, 0f);
            }

            if (_preview.lights.Length > 1)
            {
                _preview.lights[1].intensity = 0.55f;
            }

            _view = new GoreOrbitView { AltLeftNavigates = false };
            _model = new GoreStudioModel();
            _static = new GoreStudioStatic(Repaint);
            _opaqueMaterial = MakeMaterial("Gore Studio opaque", BlendMode.One, BlendMode.Zero, CompareFunction.LessEqual, 1, 2000);
            _xrayBodyMaterial = MakeMaterial("Gore Studio X-ray body", BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, CompareFunction.LessEqual, 0, 2900);
            _xrayClothMaterial = MakeMaterial("Gore Studio X-ray clothes", BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, CompareFunction.LessEqual, 0, 2950);
            _model.Opacity = _xray ? XRayOpacity : (byte)255;
            _ringMaterial = MakeMaterial("Gore Studio rings", BlendMode.One, BlendMode.Zero, CompareFunction.LessEqual, 1, 2500);
            _lidMaterial = MakeMaterial("Gore Studio lids", BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, CompareFunction.Always, 0, 4000);
            _wireMaterial = MakeMaterial("Gore Studio wire", BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, CompareFunction.LessEqual, 0, 2400);
            _faceMaterial = MakeMaterial("Gore Studio faces", BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, CompareFunction.LessEqual, 0, 2450);
            _ringMesh = NewOverlayMesh("Gore Studio rings");
            _dotMesh = NewOverlayMesh("Gore Studio dots");
            _edgeMesh = NewOverlayMesh("Gore Studio selected edges");
            _faceMesh = NewOverlayMesh("Gore Studio selected faces");

            Undo.undoRedoPerformed += Repaint;
            if (_source != null)
            {
                var source = _source;
                Defer(() => LoadTarget(source));
            }
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= Repaint;
            if (_model != null)
            {
                var instance = _model.Instance;
                _model.Dispose();
                if (instance != null)
                {
                    DestroyImmediate(instance);
                }

                _model = null;
            }

            if (_static != null)
            {
                _static.Dispose();
                _static = null;
            }

            if (_preview != null)
            {
                _preview.Cleanup();
                _preview = null;
            }

            DestroyAsset(_opaqueMaterial);
            DestroyAsset(_xrayBodyMaterial);
            DestroyAsset(_xrayClothMaterial);
            DestroyAsset(_ringMaterial);
            DestroyAsset(_lidMaterial);
            DestroyAsset(_wireMaterial);
            DestroyAsset(_faceMaterial);
            DestroyAsset(_ringMesh);
            DestroyAsset(_dotMesh);
            DestroyAsset(_edgeMesh);
            DestroyAsset(_faceMesh);
            GoreOrbitView.ReleaseResources();
        }

        private static Mesh NewOverlayMesh(string name) => new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };

        private static void DestroyAsset(Object asset)
        {
            if (asset != null)
            {
                DestroyImmediate(asset);
            }
        }

        // Colours and depth of the overlays: one unlit vertex-colour shader that every pipeline draws.
        private static Material MakeMaterial(string name, BlendMode source, BlendMode destination, CompareFunction depthTest, int depthWrite, int queue)
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            var material = new Material(shader) { name = name, hideFlags = HideFlags.HideAndDontSave };
            material.SetInt("_SrcBlend", (int)source);
            material.SetInt("_DstBlend", (int)destination);
            material.SetInt("_Cull", (int)CullMode.Off);
            material.SetInt("_ZTest", (int)depthTest);
            material.SetInt("_ZWrite", depthWrite);
            material.renderQueue = queue;
            return material;
        }

        // Structural changes (a new character, a cut added) wait until the start of the next layout pass, so the controls of one frame never change between passes.
        private void Defer(Action action)
        {
            _deferred.Add(action);
            Repaint();
        }

        // Puts a working copy of the character in the preview.
        private void LoadTarget(GameObject source)
        {
            // The same character loaded again (after Apply) keeps its clothes on.
            var redress = _dressed && source != null && source == _source;
            var previous = _model.Instance;
            if (previous != null)
            {
                _model.Load(null, null);
                DestroyImmediate(previous);
            }

            _source = source;
            _message = string.Empty;
            ResetInteraction();
            if (source == null)
            {
                _model.Load(null, null);
                return;
            }

            var instance = Instantiate(source);
            instance.name = source.name;
            instance.hideFlags = HideFlags.HideAndDontSave;
            SilenceInstance(instance);
            _preview.AddSingleGO(instance);
            _model.Load(source, instance);
            RestoreWorn();
            _framePending = true;
            _dressed = false;
            if (redress)
            {
                DressWorn();
            }
        }

        // A character may carry a camera, a light or an audio listener: none of them may draw or listen in the preview.
        private static void SilenceInstance(GameObject instance)
        {
            foreach (var camera in instance.GetComponentsInChildren<Camera>(true))
            {
                camera.enabled = false;
            }

            foreach (var light in instance.GetComponentsInChildren<Light>(true))
            {
                light.enabled = false;
            }

            foreach (var listener in instance.GetComponentsInChildren<AudioListener>(true))
            {
                listener.enabled = false;
            }
        }

        #endregion

        #region Panel

        private void OnGUI()
        {
            // With the studio shell attached (GoreStudioWindow.Shell.cs) the shell's IMGUIContainers draw the panel and the preview.
            if (_shellActive)
            {
                return;
            }

            RunLayoutPrologue();

            var panelWidth = Mathf.Clamp(position.width * 0.36f, MinPanelWidth, MaxPanelWidth);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(GUILayout.Width(panelWidth));
            DrawPanel();
            EditorGUILayout.EndVertical();
            var rect = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            EditorGUILayout.EndHorizontal();
            DrawPreview(rect);
        }

        private void RunDeferred()
        {
            if (_deferred.Count == 0)
            {
                return;
            }

            var actions = _deferred.ToArray();
            _deferred.Clear();
            foreach (var action in actions)
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        // The meshes that are cut are drawn as coloured parts instead of themselves; the ones the user switched off are hidden.
        // Whether the skin of a body mesh is drawn: it is shown, and no look hides it (a look's hidden meshes are shown again in Edit mode, where the
        // body is what is being edited). Its lids and cut lines do not depend on this.
        private bool BodyShown(GoreStudioRow row) => row.Show && (!row.HiddenByLook || _editMode);

        private void ApplyVisibility()
        {
            foreach (var row in _model.Rows)
            {
                if (row.Renderer == null)
                {
                    continue;
                }

                var replaced = row.Cut && row.SegmentMesh != null && !_materialView;
                row.Renderer.enabled = BodyShown(row) && !replaced;
            }

            ApplyStaticVisibility();

            // A piece of clothing is drawn by its own renderer in the material view, or when it has no coloured copy to be drawn by.
            foreach (var cloth in _model.Cloth)
            {
                if (cloth.Renderer != null)
                {
                    cloth.Renderer.enabled = cloth.Show && (_materialView || !cloth.Classified);
                }
            }
        }

        private static readonly GUIContent PrefabLabel = new GUIContent("Prefab", "A character prefab (or an object of the scene). Nothing is added to the scene.");
        private static readonly GUIContent MeshTypeLabel = new GUIContent("Mesh type", "Skinned meshes follow bones. Static meshes have none (they break into pieces). Found by itself; change it if it is wrong.");
        private static readonly GUIContent RigLabel = new GUIContent("Rig", "Humanoid gives the thirteen cuts by itself. Generic needs bones picked by hand. Found from the Animator's avatar; change it if it is wrong.");
        private static readonly GUIContent AutoLabel = new GUIContent("Auto", "Go back to what was found by itself.");
        private static readonly GUIContent CutColumnTip = new GUIContent(string.Empty, "Include this mesh: it is cut along the same bones as the others.");
        private static readonly GUIContent LidsColumnTip = new GUIContent(string.Empty, "Use the cut line of this mesh to shape the lids (one lid on the body and one on the part, made from every mesh with Lids on). Skin and the pieces that overlap it: yes. A loose piece that should not widen the lid: no.");
        private static readonly GUIContent ShowColumnTip = new GUIContent(string.Empty, "Show or hide this mesh in the preview only.");
        private static readonly GUIContent CutSwatchTip = new GUIContent(string.Empty, "Show this part on its own in the preview, and choose it for the fill material and the selection.");
        private static readonly GUIContent BoneTip = new GUIContent(string.Empty, "The bone this cut is made at. Press to pick another.");
        private static readonly GUIContent RemoveCutLabel = new GUIContent("x", "Remove this cut.");
        private static readonly GUIContent AddCutLabel = new GUIContent("Add cut", "Pick a bone of the skeleton to cut at.");
        private static readonly GUIContent ClearCutsLabel = new GUIContent("Clear", "Remove every cut.");
        private static readonly GUIContent FillToAllLabel = new GUIContent("To all", "Gives the material of the chosen part to every body part.");
        private static readonly GUIContent FillFieldTip = new GUIContent(string.Empty, "The material that fills the cut of the chosen part: the lid on the part and the stump on the body. Empty = the default red material.");

        // Reused for the vertex counts of the mesh rows (a new GUIContent for every row would allocate on every repaint).
        private static readonly GUIContent Scratch = new GUIContent();

        private const float TargetLabelWidth = 68f;
        private const float AutoButtonWidth = 44f;
        private const float VertexColumnWidth = 78f;
        private const float BoneColumnWidth = 100f;
        private const float RemoveColumnWidth = 22f;
        private const float SwatchColumnWidth = 16f;
        private const float FillNameWidth = 118f;
        private const float ToAllWidth = 46f;

        private void DrawPanel()
        {
            GoreGUI.BeginPage();
            DrawBanner();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawTarget();
            if (_model.HasTarget && _model.Kind == GoreMeshKind.Skinned)
            {
                DrawMeshes();
                DrawCuts();
                DrawCloth();
                DrawEquipment();
            }
            else if (IsStaticTarget)
            {
                DrawStaticPanel();
            }

            EditorGUILayout.EndScrollView();
            if (IsStaticTarget)
            {
                DrawStaticApply();
            }
            else
            {
                DrawApply();
            }

            GoreGUI.EndPage();
        }

        private void DrawBanner()
        {
            if (!_model.HasTarget)
            {
                GoreGUI.Banner("Gore Studio", "Drop a character prefab to begin", GoreGUI.Caution);
            }
            else if (_model.HasError)
            {
                GoreGUI.Banner("Gore Studio", "There is a problem (see the bottom of this panel)", GoreGUI.Bad);
            }
            else if (_model.Kind == GoreMeshKind.Static)
            {
                DrawStaticBanner();
            }
            else
            {
                GoreGUI.Banner("Gore Studio", "Ready: " + GoreGUI.Plural(_model.Cuts.Count, "cut", "cuts") + ", " + _model.Rig + " rig", GoreGUI.Good);
            }
        }

        private void DrawTarget()
        {
            GoreGUI.BeginCard("Target");
            var labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = TargetLabelWidth;

            EditorGUI.BeginChangeCheck();
            var picked = (GameObject)EditorGUILayout.ObjectField(PrefabLabel, _model.Source, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck() && picked != _model.Source)
            {
                Defer(() => LoadTarget(picked));
            }

            if (_model.HasTarget)
            {
                // The mesh kind first; only a skinned character has a rig. The Auto button is always there (greyed when nothing was changed by hand), so the rows line up.
                var line = GoreGUI.Row();
                var autoRect = GoreGUI.TakeRight(ref line, AutoButtonWidth);
                EditorGUI.BeginChangeCheck();
                var kind = (GoreMeshKind)EditorGUI.EnumPopup(line, MeshTypeLabel, _model.Kind);
                if (EditorGUI.EndChangeCheck())
                {
                    _model.Kind = kind;
                    _model.KindOverridden = kind != _model.DetectedKind;
                    _model.Dirty = true;
                }

                using (new EditorGUI.DisabledScope(!_model.KindOverridden))
                {
                    if (GUI.Button(autoRect, AutoLabel))
                    {
                        Defer(() =>
                        {
                            _model.Kind = _model.DetectedKind;
                            _model.KindOverridden = false;
                            _model.Dirty = true;
                        });
                    }
                }

                if (_model.Kind == GoreMeshKind.Static)
                {
                    DrawStaticTarget();
                }

                if (_model.Kind == GoreMeshKind.Skinned)
                {
                    line = GoreGUI.Row();
                    autoRect = GoreGUI.TakeRight(ref line, AutoButtonWidth);
                    EditorGUI.BeginChangeCheck();
                    var rig = (GoreRigType)EditorGUI.EnumPopup(line, RigLabel, _model.Rig);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _model.Rig = rig;
                        _model.RigOverridden = rig != _model.DetectedRig;
                        Defer(RebuildCuts);
                    }

                    using (new EditorGUI.DisabledScope(!_model.RigOverridden))
                    {
                        if (GUI.Button(autoRect, AutoLabel))
                        {
                            Defer(() =>
                            {
                                _model.Rig = _model.DetectedRig;
                                _model.RigOverridden = false;
                                RebuildCuts();
                            });
                        }
                    }
                }
            }

            EditorGUIUtility.labelWidth = labelWidth;
            GoreGUI.EndCard();
        }

        // Makes the cut list again from the rig. The fill materials the user chose are kept by the name of the cut (the humanoid names do not change).
        private void RebuildCuts()
        {
            var kept = new Dictionary<string, Material>(16);
            foreach (var cut in _model.Cuts)
            {
                if (cut.Fill != null && !kept.ContainsKey(cut.Name))
                {
                    kept.Add(cut.Name, cut.Fill);
                }
            }

            _model.FillAutomatic();
            foreach (var cut in _model.Cuts)
            {
                if (kept.TryGetValue(cut.Name, out var fill))
                {
                    cut.Fill = fill;
                }
            }
        }

        private void DrawMeshes()
        {
            GoreGUI.BeginCard("Skinned meshes");
            if (_model.Rows.Count == 0)
            {
                GoreGUI.Note("This character has no skinned mesh.");
                GoreGUI.EndCard();
                return;
            }

            GoreGUI.Note("Tick Cut for every mesh that should come apart.");

            var header = GoreGUI.Row(16f);
            GoreGUI.ColumnHeader(GoreGUI.TakeLeft(ref header, GoreGUI.CheckColumn), "Cut", CutColumnTip.tooltip, TextAnchor.MiddleCenter);
            GoreGUI.ColumnHeader(GoreGUI.TakeLeft(ref header, GoreGUI.CheckColumn), "Lids", LidsColumnTip.tooltip, TextAnchor.MiddleCenter);
            GoreGUI.ColumnHeader(GoreGUI.TakeLeft(ref header, GoreGUI.CheckColumn), "Show", ShowColumnTip.tooltip, TextAnchor.MiddleCenter);
            GoreGUI.ColumnHeader(header, "Mesh", "A skinned mesh of the character.", TextAnchor.MiddleLeft);

            var changed = false;
            for (var index = 0; index < _model.Rows.Count; index++)
            {
                var row = _model.Rows[index];
                var line = GoreGUI.Row();
                var picked = index == _model.SelectedRow;
                if (picked)
                {
                    GoreGUI.Highlight(line);
                }

                var rest = line;
                EditorGUI.BeginChangeCheck();
                row.Cut = GoreGUI.CheckBox(GoreGUI.TakeLeft(ref rest, GoreGUI.CheckColumn), row.Cut, CutColumnTip);
                var lidsRect = GoreGUI.TakeLeft(ref rest, GoreGUI.CheckColumn);
                using (new EditorGUI.DisabledScope(!row.Cut))
                {
                    row.Lids = GoreGUI.CheckBox(lidsRect, row.Lids, LidsColumnTip);
                }

                var show = GoreGUI.CheckBox(GoreGUI.TakeLeft(ref rest, GoreGUI.CheckColumn), row.Show, ShowColumnTip);
                if (EditorGUI.EndChangeCheck())
                {
                    row.Show = show;
                    changed = true;
                }

                Scratch.text = row.VertexText;
                Scratch.tooltip = string.Empty;
                GoreGUI.Detail(GoreGUI.TakeRight(ref rest, VertexColumnWidth), Scratch);

                // The name picks the mesh, like a click on it in the preview.
                EditorGUIUtility.AddCursorRect(rest, MouseCursor.Link);
                if (GUI.Button(rest, row.Label, row.Problem != null || picked ? EditorStyles.boldLabel : EditorStyles.label))
                {
                    var number = index;
                    Defer(() => SelectRow(number));
                }

                if (!row.Cut && !string.IsNullOrEmpty(row.StaysWith))
                {
                    GoreGUI.Note(row.Label.text + ": its skin follows " + row.StaysWith + ", but it is not cut. Tick Cut to take it along.");
                }
            }

            if (changed)
            {
                _model.Dirty = true;
            }

            GoreGUI.EndCard();
        }

        private void DrawCuts()
        {
            GoreGUI.BeginCard("Body parts");
            if (_model.Cuts.Count == 0)
            {
                GoreGUI.Note(_model.Rig == GoreRigType.Humanoid ? "No cuts yet. Press Humanoid cuts." : "No cuts yet. Press Add cut and pick a bone.");
            }
            else
            {
                GoreGUI.Note("The parts that can come off. Click a colour to show one part alone. To move a cut line, select vertices in the preview and press Add to cut.");

                var header = GoreGUI.Row(16f);
                GoreGUI.TakeLeft(ref header, SwatchColumnWidth);
                GoreGUI.TakeRight(ref header, RemoveColumnWidth);
                GoreGUI.ColumnHeader(GoreGUI.TakeRight(ref header, BoneColumnWidth), "Bone", "The bone the cut is made at.", TextAnchor.MiddleLeft);
                GoreGUI.ColumnHeader(header, "Part", "The name of the part (and of the cut).", TextAnchor.MiddleLeft);
            }

            var changed = false;
            for (var i = 0; i < _model.Cuts.Count; i++)
            {
                var cut = _model.Cuts[i];
                var selected = i == _model.SelectedCut;
                var line = GoreGUI.Row();
                if (selected)
                {
                    GoreGUI.Highlight(line);
                }

                var rest = line;
                var swatch = GoreGUI.TakeLeft(ref rest, SwatchColumnWidth);
                var removeRect = GoreGUI.TakeRight(ref rest, RemoveColumnWidth);
                var boneRect = GoreGUI.TakeRight(ref rest, BoneColumnWidth);

                // The selection changes at the start of the next layout, so the controls of one frame never change between passes.
                if (GoreGUI.Swatch(swatch, _model.CutColor(i), selected, CutSwatchTip))
                {
                    var number = i;
                    Defer(() => SelectCut(number));
                }

                EditorGUI.BeginChangeCheck();
                cut.Name = EditorGUI.DelayedTextField(rest, cut.Name);
                if (EditorGUI.EndChangeCheck())
                {
                    changed = true;
                }

                var boneLabel = cut.Bone != null ? cut.Bone.name : "(no bone)";
                Scratch.text = boneLabel;
                Scratch.tooltip = BoneTip.tooltip;
                if (GUI.Button(boneRect, Scratch, EditorStyles.popup))
                {
                    ShowBoneMenu(bone => cut.Bone = bone, true);
                }

                if (GUI.Button(removeRect, RemoveCutLabel))
                {
                    var index = i;
                    Defer(() =>
                    {
                        if (index < _model.Cuts.Count)
                        {
                            _model.Cuts.RemoveAt(index);
                            _model.SelectedCut = -1;
                            _model.Dirty = true;
                        }
                    });
                }
            }

            if (changed)
            {
                _model.Dirty = true;
            }

            GUILayout.Space(2f);
            var buttons = GoreGUI.Row(22f);
            var clearRect = GoreGUI.TakeRight(ref buttons, 56f);
            var addRect = GoreGUI.TakeRight(ref buttons, 72f);
            var canHumanoid = _model.Rig == GoreRigType.Humanoid && _model.Animator != null && _model.Animator.isHuman;
            using (new EditorGUI.DisabledScope(!canHumanoid))
            {
                Scratch.text = "Humanoid cuts";
                Scratch.tooltip = canHumanoid ? "Makes the thirteen cuts of the humanoid rig (it replaces the list)." : "The character has no humanoid rig.";
                if (GUI.Button(buttons, Scratch))
                {
                    if (_model.Cuts.Count == 0 || EditorUtility.DisplayDialog("Humanoid cuts", "This replaces the cuts in the list with the cuts of the humanoid rig.", "Replace", "Cancel"))
                    {
                        Defer(RebuildCuts);
                    }
                }
            }

            if (GUI.Button(addRect, AddCutLabel))
            {
                ShowBoneMenu(bone => AddCut(bone), false);
            }

            using (new EditorGUI.DisabledScope(_model.Cuts.Count == 0))
            {
                if (GUI.Button(clearRect, ClearCutsLabel))
                {
                    Defer(() =>
                    {
                        _model.Cuts.Clear();
                        _model.SelectedCut = -1;
                        _model.Dirty = true;
                    });
                }
            }

            // The studio shell shows the Fill material section as a card of its own, under this list.
            if (!_shellActive)
            {
                DrawFillMaterials();
            }

            GoreGUI.EndCard();
        }

        // Shows one part on its own in the preview (or goes back to all of them when it already was the one).
        private void SelectCut(int index)
        {
            if (index >= _model.Cuts.Count)
            {
                return;
            }

            var selected = index == _model.SelectedCut;
            _cutManual = !selected;
            _model.SelectedCut = selected ? -1 : index;
            _model.SelectedCloth = -1;
            _model.SelectedRow = -1;
            _model.Recolor();
            _selectionKey = -1;
            _faceKey = -1;
        }

        // Picks a body mesh from the list (or lets go of it when it already was the one); only a mesh that is cut and shown can be picked.
        private void SelectRow(int index)
        {
            if (index >= _model.Rows.Count || !_model.Rows[index].Cut || !_model.Rows[index].Show)
            {
                return;
            }

            SelectObject(_model.SelectedRow == index ? -1 : index, -1);
        }

        // A second topic of the card, apart from the list: the material that fills the cut of the chosen part (the lid on the part that comes off and the
        // stump on the body). It always shows the same controls: the chosen part's colour and name, its material, and a button that gives that material to
        // every part. Empty = the default red material. Used when the character is applied.
        private void DrawFillMaterials()
        {
            if (_model.Cuts.Count == 0)
            {
                return;
            }

            var total = _model.Cuts.Count;
            var set = 0;
            foreach (var cut in _model.Cuts)
            {
                if (cut.Fill != null)
                {
                    set++;
                }
            }

            Scratch.text = set + " of " + total + " set";
            GoreGUI.Heading("Fill material", Scratch.text);

            var selected = _model.SelectedCut >= 0 && _model.SelectedCut < total ? _model.Cuts[_model.SelectedCut] : null;
            var line = GoreGUI.Row();
            var rest = line;
            var allRect = GoreGUI.TakeRight(ref rest, ToAllWidth);
            var nameRect = GoreGUI.TakeLeft(ref rest, FillNameWidth);
            if (selected != null)
            {
                var swatch = GoreGUI.TakeLeft(ref nameRect, SwatchColumnWidth - GoreGUI.ColumnGap);
                GoreGUI.SwatchBox(swatch, _model.CutColor(_model.SelectedCut), true);
                GUI.Label(nameRect, selected.Name, EditorStyles.boldLabel);
            }
            else
            {
                GoreGUI.Caption(nameRect, "No part chosen");
            }

            using (new EditorGUI.DisabledScope(selected == null))
            {
                EditorGUI.BeginChangeCheck();
                var picked = (Material)EditorGUI.ObjectField(rest, selected != null ? selected.Fill : null, typeof(Material), false);
                if (EditorGUI.EndChangeCheck() && selected != null)
                {
                    selected.Fill = picked;
                }

                GoreGUI.Tip(rest, FillFieldTip);
                if (GUI.Button(allRect, FillToAllLabel) && selected != null)
                {
                    var fill = selected.Fill;
                    foreach (var cut in _model.Cuts)
                    {
                        cut.Fill = fill;
                    }
                }
            }

            GoreGUI.Note(selected != null ? "Fills the cut: the lid on the part and the stump on the body. Empty = red." : "Click a colour in the list above to choose a part.");
        }

        // A menu of the bones the meshes follow (a submenu for each level of the hierarchy). Bones that already have a cut are left out of the add menu.
        private void ShowBoneMenu(Action<Transform> choose, bool replacing)
        {
            var menu = new GenericMenu();
            var bones = _model.BonesInUse();
            var used = new HashSet<Transform>();
            foreach (var cut in _model.Cuts)
            {
                if (cut.Bone != null)
                {
                    used.Add(cut.Bone);
                }
            }

            var count = 0;
            foreach (var bone in bones)
            {
                if (!replacing && used.Contains(bone))
                {
                    continue;
                }

                var path = AnimationUtility.CalculateTransformPath(bone, _model.Instance.transform);
                var target = bone;
                menu.AddItem(new GUIContent(path), false, () => Defer(() =>
                {
                    choose(target);
                    _model.Dirty = true;
                }));
                count++;
            }

            if (count == 0)
            {
                menu.AddDisabledItem(new GUIContent("No bone left to pick"));
            }

            menu.ShowAsContext();
        }

        private void AddCut(Transform bone)
        {
            var name = bone.name;
            var number = 2;
            while (_model.Cuts.Exists(c => c.Name == name))
            {
                name = bone.name + " " + number++;
            }

            _model.Cuts.Add(new GoreBaker.CutSpec { Name = name, Bone = bone });
            _model.SelectedCut = _model.Cuts.Count - 1;
        }

        private void DrawApply()
        {
            if (_model.HasTarget && _model.HasError)
            {
                GoreGUI.Help(_model.FirstError(), MessageType.Error);
            }

            if (_message.Length > 0)
            {
                GoreGUI.Help(_message, _messageType);
            }

            var label = _model.HasTarget && EditorUtility.IsPersistent(_model.Source) ? "Apply to prefab" : "Set up";
            using (new EditorGUI.DisabledScope(!_model.CanApply))
            {
                if (GUILayout.Button(new GUIContent(label, "Splits the meshes into parts, makes the lids and saves everything into the prefab (the parts are assets next to the profile)."), GUILayout.Height(26f)))
                {
                    Defer(RunApply);
                }
            }
        }

        private void RunApply()
        {
            if (!ConfirmWornReadable())
            {
                return;
            }

            var source = _model.Source;
            var result = GoreStudioApply.Apply(_model);
            StoreWorn(result.Profile);
            AssetDatabase.SaveAssets();
            if (result.Success)
            {
                Debug.Log("[GoreLab] " + result.Message, source);
                _message = result.Message;
                _messageType = MessageType.Info;
            }
            else
            {
                Debug.LogError("[GoreLab] " + result.Message, source);
                _message = result.Message;
                _messageType = MessageType.Error;
            }

            foreach (var warning in result.Warnings)
            {
                Debug.LogWarning("[GoreLab] " + warning, source);
            }

            if (result.Success)
            {
                var message = _message;
                LoadTarget(source);
                _message = message;
                _messageType = MessageType.Info;
                if (result.Profile != null)
                {
                    EditorGUIUtility.PingObject(result.Profile);
                }
            }
        }

        #endregion
    }
}
