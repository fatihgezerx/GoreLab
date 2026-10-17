using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// The static-mesh mode of the Gore Studio (a table, a crate, a statue: no bones): the cards of the left panel (Mesh, Extra triangles, Pieces,
    /// Fill), the setup, and the preview of the pieces. The state is in <see cref="GoreStudioStatic"/>; this file only draws it and reacts to the user.
    /// </summary>
    internal sealed partial class GoreStudioWindow
    {
        private const float StaticLabelWidth = 100f;
        private const float StaticFieldHeight = 20f;
        private const float PreviewButtonWidth = 64f;
        private const float ResetButtonWidth = 46f;
        private const float RerollButtonWidth = 60f;
        private const float CountColumnWidth = 56f;
        private const float AxisCaptionWidth = 120f;

        // Beyond this many hand-drawn pieces the card warns: each piece is a physical object at run time.
        private const int ManyPieces = GorePartitioner.MaxPiecesLimit;

        // The most lines of warnings listed under the Apply button.
        private const int MaxWarningLines = 6;

        private static readonly GUIContent MeshPopupLabel = new GUIContent("Mesh", "The mesh to set up. Each mesh of the prefab has its own settings and its own profile.");
        private static readonly GUIContent MoreLabel = new GUIContent("More triangles", "How many triangles to add before the mesh is divided (0 = none). Press Preview to see them.");
        private static readonly GUIContent PreviewLabel = new GUIContent("Preview", "Adds the triangles and shows the new ones in yellow. The faces selected in Edit mode (Face select) are the ones that get them; none selected = everywhere.");
        private static readonly GUIContent ResetLabel = new GUIContent("Reset", "Takes the extra triangles back and goes to the original triangles, with the faces you had selected selected again. Pieces drawn by hand are dropped.");
        private static readonly GUIContent BreakLabel = new GUIContent("Break", "Surface: pieces are patches of the surface (a front and a back break apart). Through: pieces are columns through the object, so the front and back break together.");
        private static readonly GUIContent AxisLabel = new GUIContent("Axis", "The way the columns go through the object. Auto = the thinnest way (the thickness of a wall).");
        private static readonly GUIContent PiecesSliderLabel = new GUIContent("Pieces", "How many pieces the mesh breaks into. Move the slider to see them in the preview.");
        private static readonly GUIContent SeedLabel = new GUIContent("Seed", "The same seed gives the same pieces.");
        private static readonly GUIContent RerollLabel = new GUIContent("Reroll", "Picks another seed: the pieces are divided another way.");
        private static readonly GUIContent[] ModeLabels =
        {
            new GUIContent("Manual", "You decide the pieces: select faces in Edit mode (Tab) and make pieces of them."),
            new GUIContent("Random", "The studio divides the mesh into the number of pieces you ask for.")
        };

        private static readonly GUIContent NewPieceLabel = new GUIContent("New piece", "Makes a new piece of the selected faces. A face is selected when all its vertices are.");
        private static readonly GUIContent AddPieceLabel = new GUIContent("Add to piece", "Adds the selected faces to the chosen piece (click a colour in the list to choose it).");
        private static readonly GUIContent RemovePieceLabel = new GUIContent("Remove", "Takes the selected faces out of the chosen piece. They become the rest.");
        private static readonly GUIContent DeletePieceLabel = new GUIContent("x", "Delete this piece. Its faces become the rest.");
        private static readonly GUIContent PieceSwatchTip = new GUIContent(string.Empty, "Choose this piece: it is shown on its own and the buttons below work on it.");
        private static readonly GUIContent FillMaterialLabel = new GUIContent("Material", "The material of the broken faces (wood for a table). Empty = the default dark material.");
        private static readonly GUIContent LidScaleLabel = new GUIContent("Lid scale", "Texture tiles per unit across a lid: make it smaller for a coarser grain.");
        private static readonly GUIContent DensityLabel = new GUIContent("Density", "Kilograms per cubic meter: a piece's weight is its volume times this.");
        private static readonly GUIContent DespawnLabel = new GUIContent("Despawn time", "Seconds a broken piece lies there before it is removed. 0 = it stays.");
        private static readonly GUIContent SpinLabel = new GUIContent("Spin", "How hard a piece spins when it breaks off (turns per second at most).");
        private static readonly GUIContent StaticReadableLabel = new GUIContent("Make readable", "Switches Read/Write on in the import settings of the model and reimports it.");

        private GoreStudioStatic _static;
        private readonly List<string> _staticWarnings = new List<string>(4);
        private readonly List<string> _pieceNames = new List<string>(8);

        // What the panel and the preview say, kept until something they depend on changes (a repaint builds no string).
        private int _staticMeshSerial = -1;
        private int _staticTextKey = int.MinValue;
        private string _staticBanner = string.Empty;
        private Color _staticBannerColor = GoreGUI.Caution;
        private string _staticPicked = string.Empty;
        private string _staticAxisCaption = string.Empty;
        private string _staticEditing = string.Empty;
        private string _staticHoverText = string.Empty;
        private int _staticHoverPiece = -2;

        // The argument of an action chosen in the panel, so the deferred call needs no closure (a closure would be made on every repaint).
        private int _staticArg;

        /// <summary>Whether the target is a static mesh (no skinned meshes) that the studio shows.</summary>
        private bool IsStaticTarget => _model != null && _model.HasTarget && _model.Kind == GoreMeshKind.Static;

        #region State

        // Keeps the static state in step with the target at the start of every layout pass: attaches or lets go of the object, makes what is waiting,
        // counts the selected faces and writes the colours when they changed.
        private void RefreshStaticState()
        {
            if (!IsStaticTarget)
            {
                _static.Detach();
                return;
            }

            _static.Attach(_model.Source, _model.Instance);
            _static.Pump();
            if (_static.Opacity != _model.Opacity)
            {
                _static.Opacity = _model.Opacity;
                _static.ColorsDirty = true;
            }

            _static.RefreshSelection();
            if (_static.MeshSerial != _staticMeshSerial)
            {
                // The selection structure was replaced: its selection and undo steps are gone, and the real renderer is shown or hidden again.
                _staticMeshSerial = _static.MeshSerial;
                _undo.Clear();
                _redo.Clear();
                _selectionKey = -1;
                _faceKey = -1;
                _staticHoverPiece = -2;
                _hoverValid = false;
                ApplyVisibility();
            }

            if (_static.ColorsDirty)
            {
                _static.Recolor();
            }

            var key = StaticTextKey();
            if (key != _staticTextKey)
            {
                _staticTextKey = key;
                RebuildStaticTexts();
            }
        }

        private int StaticTextKey()
        {
            var setup = _static.Current;
            var key = _static.Serial;
            key = unchecked(key * 31 + _static.ActiveIndex);
            key = unchecked(key * 31 + (_static.Busy ? 1 : 0));
            key = unchecked(key * 31 + (_static.ExtraStale ? 1 : 0));
            key = unchecked(key * 31 + (_static.Problem != null ? 1 : 0));
            key = unchecked(key * 31 + (Application.isPlaying ? 1 : 0));
            if (setup != null)
            {
                key = unchecked(key * 31 + setup.SelectedPiece);
                key = unchecked(key * 31 + setup.ExtraTriangles);
                key = unchecked(key * 31 + setup.RandomPieces);
                key = unchecked(key * 31 + (int)setup.Mode);
                key = unchecked(key * 31 + (int)setup.Shape * 5 + (int)setup.Axis);
            }

            return key;
        }

        private void RebuildStaticTexts()
        {
            _static.RefreshTexts();
            var setup = _static.Current;
            if (setup == null)
            {
                _staticBanner = "This object has no mesh with a Mesh Filter and a Mesh Renderer";
                _staticBannerColor = GoreGUI.Caution;
                return;
            }

            _staticEditing = "Editing: " + setup.Filter.name;
            _staticAxisCaption = setup.Shape == GoreBreakShape.Through && setup.Axis == GoreBreakAxis.Auto ? "Auto = " + _static.AxisUsed + " (thickness)" : string.Empty;
            _staticPicked = setup.SelectedPiece >= 0 && setup.SelectedPiece < _static.OwnerPieces ? "Picked: " + _static.PieceLabel(setup.SelectedPiece) : string.Empty;
            if (_static.Problem != null)
            {
                _staticBanner = "There is a problem (see the Mesh card)";
                _staticBannerColor = GoreGUI.Bad;
                return;
            }

            var blocker = _static.ApplyBlocker();
            if (blocker.Length > 0)
            {
                _staticBanner = blocker;
                _staticBannerColor = GoreGUI.Caution;
                return;
            }

            _staticBanner = "Ready: " + GoreGUI.Plural(_static.OwnerPieces + (setup.Mode == GoreBreakMode.Manual && _static.Counts[_static.Counts.Length - 1] > 0 ? 1 : 0), "piece", "pieces") + ", " + (_static.Closed ? "closed mesh" : "open mesh");
            _staticBannerColor = GoreGUI.Good;
        }

        // The real renderer of the mesh gives way to the coloured pieces (and comes back in the material view).
        private void ApplyStaticVisibility()
        {
            if (!IsStaticTarget)
            {
                return;
            }

            var setup = _static.Current;
            if (setup != null && setup.Renderer != null)
            {
                setup.Renderer.enabled = _materialView || !_static.HasSoup;
            }
        }

        private void DrawStaticBanner()
        {
            GoreGUI.Banner("Gore Studio", _staticBanner, _staticBannerColor);
        }

        #endregion

        #region Panel

        // The Mesh popup of the Target card, for a prefab with several meshes.
        private void DrawStaticTarget()
        {
            if (!_static.HasSetups || _static.Setups.Count < 2)
            {
                return;
            }

            EditorGUI.BeginChangeCheck();
            var index = EditorGUI.Popup(EditorGUI.PrefixLabel(GoreGUI.Row(StaticFieldHeight), MeshPopupLabel), _static.ActiveIndex, _static.MeshNames);
            if (EditorGUI.EndChangeCheck())
            {
                _staticArg = index;
                Defer(ActivateStaticMesh);
            }
        }

        private void ActivateStaticMesh()
        {
            ResetInteraction();
            _static.Activate(_staticArg);
            _staticTextKey = int.MinValue;
        }

        private void DrawStaticPanel()
        {
            if (!_static.HasSetups)
            {
                GoreGUI.BeginCard("Mesh");
                GoreGUI.Note("This object has no mesh with a Mesh Filter and a Mesh Renderer.");
                GoreGUI.EndCard();
                return;
            }

            var labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = StaticLabelWidth;
            DrawStaticMeshCard();
            if (_static.Problem == null && _static.Working != null)
            {
                DrawStaticExtraCard();
                DrawStaticPiecesCard();
                DrawStaticFillCard();
            }

            EditorGUIUtility.labelWidth = labelWidth;
        }

        private static void StaticRow(string label, string value)
        {
            var line = GoreGUI.Row();
            GoreGUI.Caption(GoreGUI.TakeLeft(ref line, StaticLabelWidth), label);
            GUI.Label(line, value);
        }

        private void DrawStaticMeshCard()
        {
            GoreGUI.BeginCard("Mesh");
            var setup = _static.Current;
            if (_static.Problem != null)
            {
                GoreGUI.Help(_static.Problem, MessageType.Error);
                if (_static.CanMakeReadable)
                {
                    GoreGUI.Note("The editor cannot read this mesh. Read/Write has to be on in the import settings of its model.");
                    if (GUILayout.Button(StaticReadableLabel))
                    {
                        var mesh = setup.Mesh;
                        var path = AssetDatabase.GetAssetPath(mesh);
                        if (EditorUtility.DisplayDialog("Make readable", "This switches Read/Write on in the import settings of '" + path + "' and reimports the model. It can take a moment.", "Reimport", "Cancel"))
                        {
                            Defer(() => MakeReadableAndReload(mesh));
                        }
                    }
                }

                GoreGUI.EndCard();
                return;
            }

            StaticRow("Triangles", _static.TrianglesText);
            StaticRow("Pieces", _static.PiecesText);
            StaticRow("Shape", _static.Closed ? "Closed" : "Open: thin shards, no lids");
            if (setup.Note.Length > 0)
            {
                GoreGUI.Note(setup.Note);
            }

            GoreGUI.EndCard();
        }

        private void MakeReadableAndReload(Mesh mesh)
        {
            if (!GoreBreakBaker.MakeReadable(mesh))
            {
                _message = "'" + mesh.name + "' is not from a model file, so Read/Write cannot be switched on here.";
                _messageType = MessageType.Warning;
                return;
            }

            // The model was reimported: the working copy is made again from the asset.
            LoadTarget(_model.Source);
        }

        private void DrawStaticExtraCard()
        {
            GoreGUI.BeginCard("Extra triangles");
            var setup = _static.Current;
            var line = GoreGUI.Row(StaticFieldHeight);
            var resetRect = GoreGUI.TakeRight(ref line, ResetButtonWidth);
            var previewRect = GoreGUI.TakeRight(ref line, PreviewButtonWidth);
            EditorGUI.BeginChangeCheck();
            var count = EditorGUI.IntField(line, MoreLabel, setup.ExtraTriangles);
            if (EditorGUI.EndChangeCheck())
            {
                _static.SetExtra(count);
            }

            var background = GUI.backgroundColor;
            if (_static.ExtraStale)
            {
                GUI.backgroundColor = GoreGUI.Caution;
            }

            using (new EditorGUI.DisabledScope(_static.Busy))
            {
                if (GUI.Button(previewRect, PreviewLabel))
                {
                    Defer(() => _static.Preview());
                }
            }

            GUI.backgroundColor = background;
            using (new EditorGUI.DisabledScope(_static.Busy || (setup.CommittedExtra == 0 && setup.ExtraTriangles == 0)))
            {
                if (GUI.Button(resetRect, ResetLabel))
                {
                    Defer(() => _static.ResetExtra());
                }
            }

            GoreGUI.Note(_static.ExtraHint);
            if (setup.Mode == GoreBreakMode.Random && setup.Shape == GoreBreakShape.Through)
            {
                GoreGUI.Note("Not needed for Through: triangles are cut along the borders.");
            }

            GoreGUI.EndCard();
        }

        private void DrawStaticPiecesCard()
        {
            GoreGUI.BeginCard("Pieces");
            var setup = _static.Current;
            EditorGUI.BeginChangeCheck();
            var mode = GUI.Toolbar(GoreGUI.Row(22f), (int)setup.Mode, ModeLabels);
            if (EditorGUI.EndChangeCheck())
            {
                _staticArg = mode;
                Defer(SwitchStaticMode);
            }

            DrawStaticBreak(setup);
            if (setup.Mode == GoreBreakMode.Random)
            {
                DrawStaticRandom(setup);
            }
            else
            {
                DrawStaticManual(setup);
            }

            foreach (var note in _static.Notes)
            {
                GoreGUI.Note(note);
            }

            GoreGUI.EndCard();
        }

        // The Break row (both modes: the lids depend on it) and, for Through, the Axis row with the axis Auto picked.
        private void DrawStaticBreak(GoreStaticSetup setup)
        {
            EditorGUI.BeginChangeCheck();
            var shape = (GoreBreakShape)EditorGUI.EnumPopup(GoreGUI.Row(StaticFieldHeight), BreakLabel, setup.Shape);
            if (EditorGUI.EndChangeCheck())
            {
                _staticArg = (int)shape;
                Defer(SwitchStaticShape);
            }

            if (setup.Shape == GoreBreakShape.Through)
            {
                var line = GoreGUI.Row(StaticFieldHeight);
                var caption = GoreGUI.TakeRight(ref line, AxisCaptionWidth);
                EditorGUI.BeginChangeCheck();
                var axis = (GoreBreakAxis)EditorGUI.EnumPopup(line, AxisLabel, setup.Axis);
                if (EditorGUI.EndChangeCheck())
                {
                    _staticArg = (int)axis;
                    Defer(SwitchStaticAxis);
                }

                GoreGUI.Caption(caption, _staticAxisCaption);
                GoreGUI.Note("Pieces are columns through the object: front and back break together.");
            }
            else
            {
                GoreGUI.Note("Pieces are patches of the surface.");
            }
        }

        private void SwitchStaticShape()
        {
            _static.SetShape((GoreBreakShape)_staticArg);
            _static.SetShowNew(false);
        }

        private void SwitchStaticAxis()
        {
            _static.SetAxis((GoreBreakAxis)_staticArg);
            _static.SetShowNew(false);
        }

        private void SwitchStaticMode()
        {
            _static.SetMode((GoreBreakMode)_staticArg);
            _static.SetShowNew(false);
        }

        private void ChooseStaticPiece()
        {
            _static.SetShowNew(false);
            _static.SelectPiece(_staticArg == _static.Current.SelectedPiece ? -1 : _staticArg);
        }

        private void DeleteStaticPiece()
        {
            StaticPieceAction(3, _staticArg);
        }

        private static void DetailText(Rect column, string text)
        {
            Scratch.text = text;
            Scratch.tooltip = string.Empty;
            GoreGUI.Detail(column, Scratch);
        }

        private void DrawStaticRandom(GoreStaticSetup setup)
        {
            var maximum = _static.MaxRandomPieces();
            if (maximum > GoreStudioStatic.MinRandomPieces)
            {
                EditorGUI.BeginChangeCheck();
                var pieces = EditorGUI.IntSlider(GoreGUI.Row(StaticFieldHeight), PiecesSliderLabel, Mathf.Min(setup.RandomPieces, maximum), GoreStudioStatic.MinRandomPieces, maximum);
                if (EditorGUI.EndChangeCheck())
                {
                    _static.SetRandomPieces(pieces);
                    _static.SetShowNew(false);
                }
            }
            else
            {
                GoreGUI.Note("The mesh has too few triangles to divide. Add triangles above.");
            }

            var line = GoreGUI.Row(StaticFieldHeight);
            var rerollRect = GoreGUI.TakeRight(ref line, RerollButtonWidth);
            EditorGUI.BeginChangeCheck();
            var seed = EditorGUI.IntField(line, SeedLabel, setup.RandomSeed);
            if (EditorGUI.EndChangeCheck())
            {
                _static.SetSeed(seed);
                _static.SetShowNew(false);
            }

            if (GUI.Button(rerollRect, RerollLabel))
            {
                _static.SetSeed(Random.Range(1, 1000000));
                _static.SetShowNew(false);
            }

            GoreGUI.Note("Each piece is a physical object at run time; at most " + GorePartitioner.MaxPiecesLimit + ".");
        }

        private string PieceName(int piece)
        {
            while (_pieceNames.Count <= piece)
            {
                _pieceNames.Add("Piece " + (_pieceNames.Count + 1));
            }

            return _pieceNames[piece];
        }

        private void DrawStaticManual(GoreStaticSetup setup)
        {
            if (setup.ManualPieces == 0)
            {
                GoreGUI.Note("No piece yet. Press Tab, select faces, then New piece. What is left over is the rest.");
            }

            var drawn = Mathf.Min(setup.ManualPieces, _static.CountTexts.Length - 1);
            for (var piece = 0; piece < drawn; piece++)
            {
                var line = GoreGUI.Row();
                var chosen = piece == setup.SelectedPiece;
                if (chosen)
                {
                    GoreGUI.Highlight(line);
                }

                var rest = line;
                var swatch = GoreGUI.TakeLeft(ref rest, GoreGUI.CheckColumn * 0.5f);
                var removeRect = GoreGUI.TakeRight(ref rest, RemoveColumnWidth);
                var countRect = GoreGUI.TakeRight(ref rest, CountColumnWidth);
                if (GoreGUI.Swatch(swatch, GoreStaticPalette.Of(piece), chosen, PieceSwatchTip))
                {
                    _staticArg = piece;
                    Defer(ChooseStaticPiece);
                }

                GUI.Label(rest, PieceName(piece), chosen ? EditorStyles.boldLabel : EditorStyles.label);
                DetailText(countRect, _static.CountTexts[piece]);
                if (GUI.Button(removeRect, DeletePieceLabel))
                {
                    _staticArg = piece;
                    Defer(DeleteStaticPiece);
                }
            }

            var restCount = _static.Counts.Length - 1;
            var restLine = GoreGUI.Row();
            var restRest = restLine;
            GoreGUI.SwatchBox(GoreGUI.TakeLeft(ref restRest, GoreGUI.CheckColumn * 0.5f), GoreStaticPalette.RestColor, false);
            GoreGUI.TakeRight(ref restRest, RemoveColumnWidth);
            DetailText(GoreGUI.TakeRight(ref restRest, CountColumnWidth), _static.CountTexts[restCount]);
            GUI.Label(restRest, "Rest");

            GoreGUI.Note(_static.FacesText + ". Select faces in Edit mode (Tab).");
            var buttons = GoreGUI.Row(22f);
            var third = (buttons.width - GoreGUI.ColumnGap * 2f) / 3f;
            var newRect = new Rect(buttons.x, buttons.y, third, buttons.height);
            var addRect = new Rect(newRect.xMax + GoreGUI.ColumnGap, buttons.y, third, buttons.height);
            var takeRect = new Rect(addRect.xMax + GoreGUI.ColumnGap, buttons.y, third, buttons.height);
            var hasFaces = _static.FaceCount > 0 && _static.CanDraw;
            var hasPiece = hasFaces && setup.SelectedPiece >= 0;
            using (new EditorGUI.DisabledScope(!hasFaces))
            {
                if (GUI.Button(newRect, NewPieceLabel))
                {
                    Defer(() => StaticPieceAction(0, -1));
                }
            }

            using (new EditorGUI.DisabledScope(!hasPiece))
            {
                if (GUI.Button(addRect, AddPieceLabel))
                {
                    Defer(() => StaticPieceAction(1, -1));
                }

                if (GUI.Button(takeRect, RemovePieceLabel))
                {
                    Defer(() => StaticPieceAction(2, -1));
                }
            }

            if (setup.ManualPieces > ManyPieces)
            {
                GoreGUI.Note("More than " + ManyPieces + " pieces: each is a physical object at run time.");
            }
        }

        // 0 = a new piece from the selection, 1 = add the selection to the chosen piece, 2 = take it out of it, 3 = delete the piece <number>. One undo step each.
        private void StaticPieceAction(int action, int number)
        {
            var setup = _static.Current;
            if (setup == null)
            {
                return;
            }

            _static.RefreshSelection();
            var before = CaptureAll();
            before.Owners = _static.Snapshot();
            var piece = setup.SelectedPiece;
            _static.SetShowNew(false);
            var moved = 0;
            string message;
            switch (action)
            {
                case 0:
                    moved = _static.NewPiece();
                    message = moved > 0 ? "Piece " + setup.ManualPieces + " made of " + moved.ToString("N0") + " triangles." : "No face selected.";
                    break;
                case 1:
                    moved = _static.AssignTo(piece);
                    message = moved + " triangles added to piece " + (piece + 1) + ".";
                    break;
                case 2:
                    moved = _static.RemoveFrom(piece);
                    message = moved + " triangles taken out of piece " + (piece + 1) + ".";
                    break;
                default:
                    _static.DeletePiece(number);
                    moved = 1;
                    message = "Piece " + (number + 1) + " deleted; its triangles are the rest.";
                    break;
            }

            if (moved > 0)
            {
                PushUndo(before);
                if (action != 3)
                {
                    _static.ClearSelection();
                }
            }

            // The selection changed: count it again first, so the message is not taken for one about an older selection.
            _selectionKey = -1;
            RefreshSelectionStats();
            _toast = message;
            _toastKey = _selectionKey;
            _staticTextKey = int.MinValue;
            Repaint();
        }

        private void DrawStaticFillCard()
        {
            GoreGUI.BeginCard("Fill");
            var setup = _static.Current;
            setup.Fill = (Material)EditorGUI.ObjectField(GoreGUI.Row(StaticFieldHeight), FillMaterialLabel, setup.Fill, typeof(Material), false);
            setup.LidUvScale = Mathf.Max(0.01f, EditorGUI.FloatField(GoreGUI.Row(StaticFieldHeight), LidScaleLabel, setup.LidUvScale));
            setup.Density = Mathf.Max(0.1f, EditorGUI.FloatField(GoreGUI.Row(StaticFieldHeight), DensityLabel, setup.Density));
            setup.DespawnTime = Mathf.Max(0f, EditorGUI.FloatField(GoreGUI.Row(StaticFieldHeight), DespawnLabel, setup.DespawnTime));
            setup.Spin = Mathf.Max(0f, EditorGUI.FloatField(GoreGUI.Row(StaticFieldHeight), SpinLabel, setup.Spin));
            GoreGUI.Note(_static.Closed ? "Broken faces get a lid with the fill material when you Apply." : "An open mesh has no lids: the fill material is not used.");
            GoreGUI.EndCard();
        }

        #endregion

        #region Apply

        private void DrawStaticApply()
        {
            if (_model.HasTarget && _model.HasError)
            {
                GoreGUI.Help(_model.FirstError(), MessageType.Error);
            }

            if (_message.Length > 0)
            {
                GoreGUI.Help(_message, _messageType);
            }

            for (var i = 0; i < _staticWarnings.Count && i < MaxWarningLines; i++)
            {
                GoreGUI.Help(_staticWarnings[i], MessageType.Warning);
            }

            var label = _model.HasTarget && EditorUtility.IsPersistent(_model.Source) ? "Apply to prefab" : "Set up";
            var ready = _static.HasSetups && _static.ApplyBlocker().Length == 0;
            using (new EditorGUI.DisabledScope(!ready))
            {
                if (GUILayout.Button(new GUIContent(label, "Divides the mesh into pieces, makes the lids and saves everything into the prefab (the pieces are assets next to the profile)."), GUILayout.Height(26f)))
                {
                    Defer(RunStaticApply);
                }
            }
        }

        private void RunStaticApply()
        {
            var source = _model.Source;
            var key = _static.Current.Key;
            var result = GoreStaticApply.Apply(_static, source, _model.Instance);
            AssetDatabase.SaveAssets();
            _staticWarnings.Clear();
            _staticWarnings.AddRange(result.Warnings);
            if (result.Success)
            {
                Debug.Log("[GoreLab] " + result.Message, source);
            }
            else
            {
                Debug.LogError("[GoreLab] " + result.Message, source);
            }

            foreach (var warning in result.Warnings)
            {
                Debug.LogWarning("[GoreLab] " + warning, source);
            }

            if (!result.Success)
            {
                _message = result.Message;
                _messageType = MessageType.Error;
                return;
            }

            // The working copy is made again from what was saved, so the studio shows what the profile holds.
            _static.Forget(key);
            LoadTarget(source);
            _message = result.Message;
            _messageType = MessageType.Info;
            if (result.Profile != null)
            {
                EditorGUIUtility.PingObject(result.Profile);
            }
        }

        #endregion

        #region Preview

        // The coloured pieces, the edges of the new triangles, and (Lines on, object mode) every edge of the mesh. Edit mode draws its own wire.
        private void DrawStaticScene()
        {
            if (_static.SoupMesh == null)
            {
                return;
            }

            if (!_materialView)
            {
                _preview.DrawMesh(_static.SoupMesh, Matrix4x4.identity, _xray ? _xrayBodyMaterial : _opaqueMaterial, 0);
            }

            var bias = Matrix4x4.Translate(-_view.Forward * (_view.Distance * 0.0015f));
            if (_static.ShowNew && _static.NewEdgeMesh != null)
            {
                _preview.DrawMesh(_static.NewEdgeMesh, bias, _wireMaterial, 0);
            }

            if (_showLines && !_editMode && _static.WireMesh != null)
            {
                _preview.DrawMesh(_static.WireMesh, bias, _wireMaterial, 0);
            }
        }

        // The bar at the bottom in Edit mode: how many faces are selected, and (Manual) the buttons that make pieces of them.
        private void DrawStaticHud(Rect rect)
        {
            var box = new Rect(rect.center.x - HudWidth * 0.5f, rect.yMax - HudHeight - 16f, HudWidth, HudHeight);
            _uiRects.Add(box);
            if (_toast.Length > 0)
            {
                var toast = new Rect(box.x - 70f, box.y - 44f, box.width + 140f, 38f);
                _uiRects.Add(toast);
                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(toast, new Color(0.14f, 0.33f, 0.2f, 0.95f));
                    if (_noticeStyle == null)
                    {
                        _noticeStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, alignment = TextAnchor.MiddleCenter };
                        _noticeStyle.normal.textColor = Color.white;
                    }

                    GUI.Label(new Rect(toast.x + 6f, toast.y, toast.width - 12f, toast.height), _toast, _noticeStyle);
                }
            }

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(box, HudBackground);
            }

            GUI.Label(new Rect(box.x + 10f, box.y + 7f, box.width - 20f, 20f), new GUIContent(_static.FacesText, "A face is selected when all its vertices are. Use Face select (3) to pick faces."), EditorStyles.boldLabel);
            var setup = _static.Current;
            if (setup != null && setup.Mode == GoreBreakMode.Manual && _static.CanDraw)
            {
                var y = box.y + 33f;
                var third = (box.width - 20f - GoreGUI.ColumnGap * 2f) / 3f;
                var hasFaces = _static.FaceCount > 0;
                using (new EditorGUI.DisabledScope(!hasFaces))
                {
                    if (GUI.Button(new Rect(box.x + 10f, y, third, 22f), NewPieceLabel))
                    {
                        Defer(() => StaticPieceAction(0, -1));
                    }
                }

                using (new EditorGUI.DisabledScope(!hasFaces || setup.SelectedPiece < 0))
                {
                    if (GUI.Button(new Rect(box.x + 10f + third + GoreGUI.ColumnGap, y, third, 22f), AddPieceLabel))
                    {
                        Defer(() => StaticPieceAction(1, -1));
                    }

                    if (GUI.Button(new Rect(box.x + 10f + (third + GoreGUI.ColumnGap) * 2f, y, third, 22f), RemovePieceLabel))
                    {
                        Defer(() => StaticPieceAction(2, -1));
                    }
                }
            }
            else
            {
                GUI.Label(new Rect(box.x + 10f, box.y + 33f, box.width - 20f, 22f), setup != null && setup.Mode == GoreBreakMode.Random ? "Switch to Manual to draw pieces by hand." : "Selected faces get the extra triangles.", EditorStyles.miniLabel);
            }
        }

        // Object mode, static mesh: the piece under the mouse and the picked piece.
        private void DrawStaticLabel(Rect rect)
        {
            var lines = 0;
            if (_editMode)
            {
                LabelLines[lines++] = _staticEditing;
            }
            else
            {
                if (_staticHoverText.Length > 0)
                {
                    LabelLines[lines++] = _staticHoverText;
                }

                if (_staticPicked.Length > 0)
                {
                    LabelLines[lines++] = _staticPicked;
                }
            }

            DrawLabelBox(rect, lines);
        }

        // A click without a drag picks the piece under the mouse; a click on nothing (or on the rest) lets go. Moving the mouse finds the piece under it.
        private void HandleStaticObjectPick(Rect rect, Event current, bool overUi)
        {
            var mouse = current.mousePosition;
            switch (current.type)
            {
                case EventType.MouseMove:
                    UpdateStaticHover(rect, mouse, overUi);
                    break;
                case EventType.MouseLeaveWindow:
                    _hoverValid = false;
                    SetStaticHover(-2);
                    break;
                case EventType.MouseDown:
                    if (current.button == 0 && rect.Contains(mouse) && !overUi)
                    {
                        GUIUtility.keyboardControl = 0;
                        _objectDown = true;
                        _objectStart = mouse;
                        current.Use();
                    }

                    break;
                case EventType.MouseDrag:
                    if (_objectDown)
                    {
                        // A drag is not a pick.
                        if ((mouse - _objectStart).sqrMagnitude > DragThreshold * DragThreshold)
                        {
                            _objectDown = false;
                        }

                        current.Use();
                    }

                    break;
                case EventType.MouseUp:
                    if (_objectDown && current.button == 0)
                    {
                        _objectDown = false;
                        var triangle = PickStaticTriangle(rect, mouse);
                        var piece = triangle >= 0 ? _static.Owner[triangle] : -1;
                        Defer(() =>
                        {
                            _static.SetShowNew(false);
                            _static.SelectPiece(piece);
                        });
                        current.Use();
                    }

                    break;
            }
        }

        private void UpdateStaticHover(Rect rect, Vector2 mouse, bool overUi)
        {
            if (overUi || !rect.Contains(mouse))
            {
                _hoverValid = false;
                SetStaticHover(-2);
                return;
            }

            if (_hoverValid && (mouse - _hoverTested).sqrMagnitude <= HoverMoveThreshold * HoverMoveThreshold)
            {
                return;
            }

            _hoverTested = mouse;
            _hoverValid = true;
            if (_static.DisplayTriangleCount > PickTriangleLimit)
            {
                return;
            }

            var triangle = PickStaticTriangle(rect, mouse);
            SetStaticHover(triangle >= 0 ? _static.Owner[triangle] : -2);
        }

        // -2 = nothing under the mouse, -1 = the rest, 0 and up = a piece.
        private void SetStaticHover(int piece)
        {
            if (piece == _staticHoverPiece)
            {
                return;
            }

            _staticHoverPiece = piece;
            _staticHoverText = piece == -2 ? string.Empty : "Under mouse: " + _static.PieceLabel(piece);
            Repaint();
        }

        // The nearest triangle of the working mesh along a ray through the mouse, or -1. No allocation: it reads the arrays the state holds.
        private int PickStaticTriangle(Rect rect, Vector2 mouse)
        {
            if (_static.Edit == null)
            {
                return -1;
            }

            var ray = _view.ScreenRay(rect, mouse);
            var world = _static.DisplayWorld;
            var corners = _static.DisplayCorners;
            var best = float.PositiveInfinity;
            var picked = -1;
            for (var t = 0; t * 3 + 2 < corners.Length; t++)
            {
                if (RayTriangle(ray.origin, ray.direction, world[corners[t * 3]], world[corners[t * 3 + 1]], world[corners[t * 3 + 2]], out var distance) && distance < best)
                {
                    best = distance;
                    picked = t;
                }
            }

            return picked;
        }

        #endregion
    }
}
