using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// The preview of the Gore Studio and the tools on it: the toolbar at its top left (click or paint select; vertex, edge or face),
    /// the bar at its bottom that adds the selection to a cut, the hotkeys, the selection input and the overlays the tools draw.
    /// </summary>
    internal sealed partial class GoreStudioWindow
    {
        private const float RingPixels = 3f;
        private const float SelectedRingPixels = 7f;
        private const float DotPixels = 3.5f;
        private const float SelectedDotPixels = 7f;
        private const float DecidedDotPixels = 5.5f;
        private const float SelectedEdgePixels = 3.5f;
        private const float DragThreshold = 4f;
        private const float MinBrush = 6f;
        private const float MaxBrush = 240f;

        // The alpha of the coloured parts in the X-ray view (115 of 255).
        private const byte XRayOpacity = 115;

        // Above this many triangles the object under the mouse is not worked out on every move (a click still picks).
        private const int PickTriangleLimit = 150000;
        private const float HoverMoveThreshold = 3f;
        private const string EditOnlyTip = "Edit mode only. Press Tab to leave object mode.";
        private const string ClothNotice = "Cloth pieces are set up in the Cloth parts card. Select a body mesh to edit it.";

        private const float ButtonWidth = 32f;
        private const float ButtonHeight = 28f;
        private const float HudWidth = 330f;
        private const float HudHeight = 62f;

        private static readonly Color32 SelectedRingColor = new Color32(255, 255, 255, 255);
        private static readonly Color32 IncludeColor = new Color32(70, 220, 100, 255);
        private static readonly Color32 ExcludeColor = new Color32(235, 64, 64, 255);
        private static readonly Color32 DotColor = new Color32(12, 12, 14, 255);
        private static readonly Color32 SelectionColor = new Color32(255, 150, 24, 255);
        private static readonly Color32 SelectedFaceColor = new Color32(255, 150, 24, 120);

        private static readonly Color ButtonOff = new Color(0.20f, 0.20f, 0.21f, 0.94f);
        private static readonly Color ButtonHover = new Color(0.29f, 0.29f, 0.31f, 0.96f);
        private static readonly Color ButtonOn = new Color(0.62f, 0.11f, 0.30f, 0.98f);
        private static readonly Color ButtonDimmed = new Color(0.12f, 0.12f, 0.13f, 0.6f);
        private static readonly Color HudBackground = new Color(0.10f, 0.10f, 0.11f, 0.93f);

        // The icons are drawn with Handles (vector, sharp at any scale); these arrays are reused so a repaint does not allocate.
        private static readonly Vector3[] IconSquare = new Vector3[5];
        private static readonly Vector3[] IconLine = new Vector3[2];
        private static readonly Vector3[] IconPolygon = new Vector3[4];
        private static readonly Vector3[] IconTriangle = new Vector3[3];
        private static readonly Vector3[] IconHexagon = new Vector3[7];
        private static readonly Vector3[] IconEllipse = new Vector3[17];
        private static readonly GUIContent LabelContent = new GUIContent();
        private static readonly string[] LabelLines = new string[2];

        private readonly List<Rect> _uiRects = new List<Rect>(4);
        private int _selectionKey = -1;
        private int _faceKey = -1;
        private int _selectedVertices;
        private bool _paint;
        private bool _painting;
        private Vector2 _paintLast;
        private bool _selectionDown;
        private bool _selectionBox;
        private Vector2 _selectionStart;
        private Vector2 _selectionNow;
        private string _toast = string.Empty;
        private int _toastKey;
        private bool _wasOverUi;

        // Object mode: where the toolbar ends (the label goes right of it), the press that may become a pick, and the object under the mouse.
        private float _toolbarRight;
        private bool _objectDown;
        private Vector2 _objectStart;
        private Vector2 _hoverTested;
        private bool _hoverValid;
        private int _hoverRow = -1;
        private int _hoverCloth = -1;
        private GUIStyle _labelStyle;
        private GUIStyle _noticeStyle;

        // Until the user picks a cut by hand, the bar uses the one nearest to the selection.
        private bool _cutManual;

        // A new character: nothing is selected, no stroke or box is under way.
        private void ResetInteraction()
        {
            _selectionKey = -1;
            _faceKey = -1;
            _selectionDown = false;
            _selectionBox = false;
            _painting = false;
            _toast = string.Empty;
            _undo.Clear();
            _redo.Clear();
            _infoOpen = false;
            _cutManual = false;
            _objectDown = false;
            _hoverValid = false;
            _hoverRow = -1;
            _hoverCloth = -1;
            _staticHoverPiece = -2;
            _staticHoverText = string.Empty;
        }

        #region Drawing

        private void DrawPreview(Rect rect)
        {
            if (rect.width < 16f || rect.height < 16f)
            {
                return;
            }

            var current = Event.current;
            _uiRects.Clear();
            if (_framePending && current.type != EventType.Layout)
            {
                _model.RefreshBounds();
                _view.Frame(_model.Bounds);
                _framePending = false;
            }

            var interactive = _model.HasTarget && (_model.Kind == GoreMeshKind.Skinned || _model.Kind == GoreMeshKind.Static);
            var changed = false;
            if (current.type == EventType.Repaint)
            {
                DrawScene(rect, interactive);
            }

            if (interactive)
            {
                // The info button is the first thing a press can hit.
                if (current.type == EventType.MouseDown && current.button == 0 && HandleInfoPress(rect, current.mousePosition))
                {
                    current.Use();
                    changed = true;
                }

                DrawToolbar(rect);
                DrawObjectLabel(rect);
                if (_editMode)
                {
                    if (_model.EditsNothing)
                    {
                        DrawClothNotice(rect);
                    }
                    else if (IsStaticTarget)
                    {
                        DrawStaticHud(rect);
                    }
                    else
                    {
                        DrawHud(rect);
                    }
                }

                DrawInfo(rect);
            }

            changed |= _view.DrawNavigationGizmo(rect, current);

            // In paint mode the wheel sets the size of the brush instead of zooming.
            if (interactive && _editMode && _paint && current.type == EventType.ScrollWheel && rect.Contains(current.mousePosition) && !InUi(current.mousePosition))
            {
                _brushRadius = Mathf.Clamp(_brushRadius * (current.delta.y > 0f ? 0.9f : 1.1f), MinBrush, MaxBrush);
                current.Use();
                changed = true;
            }
            else
            {
                // The view moved under a still mouse: what is under it must be worked out again at the next move.
                var viewChanged = _view.HandleInput(rect, current);
                if (viewChanged)
                {
                    _hoverValid = false;
                }

                changed |= viewChanged;
            }

            if (interactive)
            {
                HandleHotkeys(rect, current);
                HandleSelection(rect, current);
                // The brush follows the mouse, and a button under it lights up; elsewhere a move needs no repaint of the preview.
                if (current.type == EventType.MouseMove || current.type == EventType.MouseLeaveWindow)
                {
                    var overUi = current.type == EventType.MouseMove && InUi(current.mousePosition);
                    if ((_editMode && _paint) || overUi || _wasOverUi)
                    {
                        changed = true;
                    }

                    _wasOverUi = overUi;
                }
            }

            if (current.type == EventType.Repaint)
            {
                if (interactive)
                {
                    DrawPaintCursor(rect);
                    DrawSelectionBox();
                }
                else if (!_model.HasTarget)
                {
                    var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 13 };
                    GUI.Label(rect, "Drop a character prefab into the Prefab field", style);
                }
            }

            if (changed)
            {
                Repaint();
            }
        }

        private bool InUi(Vector2 point)
        {
            foreach (var area in _uiRects)
            {
                if (area.Contains(point))
                {
                    return true;
                }
            }

            return false;
        }

        private void DrawScene(Rect rect, bool interactive)
        {
            _preview.BeginPreview(rect, GUIStyle.none);
            var camera = _preview.camera;
            _view.Apply(camera, rect.width / rect.height);

            if (_model.HasTarget)
            {
                foreach (var row in _model.Rows)
                {
                    if (!row.Cut || !row.Show)
                    {
                        continue;
                    }

                    if (row.SegmentMesh != null && !_materialView && BodyShown(row))
                    {
                        _preview.DrawMesh(row.SegmentMesh, Matrix4x4.identity, _xray ? _xrayBodyMaterial : _opaqueMaterial, 0);
                    }

                    if (_showLids && row.LidMesh != null)
                    {
                        _preview.DrawMesh(row.LidMesh, Matrix4x4.identity, _lidMaterial, 0);
                    }
                }

                if (!_materialView)
                {
                    foreach (var cloth in _model.Cloth)
                    {
                        if (cloth.Show && cloth.Classified && cloth.SegmentMesh != null)
                        {
                            _preview.DrawMesh(cloth.SegmentMesh, Matrix4x4.identity, _xray ? _xrayClothMaterial : _opaqueMaterial, 0);
                        }
                    }
                }

                if (IsStaticTarget)
                {
                    DrawStaticScene();
                }

                if (_showLines && BuildRings(rect))
                {
                    _preview.DrawMesh(_ringMesh, Matrix4x4.identity, _ringMaterial, 0);
                }

                if (interactive && _editMode)
                {
                    DrawEditOverlays(rect);
                }
            }

            // true: the scriptable pipeline renders it (the character's materials are URP); false: keep the camera settings of the view.
            _preview.Render(true, false);
            GUI.DrawTexture(rect, _preview.EndPreview(), ScaleMode.StretchToFill, false);
        }

        // The tools' view: every edge as a thin line, the selected faces and edges, and the vertices as dots (all of them in vertex mode, the
        // selected ones and the ones decided for the active cut always).
        private void DrawEditOverlays(Rect rect)
        {
            CollectEdits();
            var forward = _view.Forward;
            var cameraPosition = _view.Pivot - forward * _view.Distance;

            // Everything is nudged towards the camera as a whole, so the lines and dots sit in front of the skin instead of fighting it for depth.
            var bias = Matrix4x4.Translate(-forward * (_view.Distance * 0.0015f));
            foreach (var edit in _edits)
            {
                _preview.DrawMesh(edit.Wire, bias, _wireMaterial, 0);
            }

            if (BuildSelectedFaces())
            {
                _preview.DrawMesh(_faceMesh, bias, _faceMaterial, 0);
            }

            var worldPerPixel = 2f * _view.Distance * Mathf.Tan(15f * Mathf.Deg2Rad) / Mathf.Max(1f, rect.height);
            var right = _view.Rotation * Vector3.right;
            var up = _view.Rotation * Vector3.up;

            ClearOverlay();
            foreach (var edit in _edits)
            {
                for (var e = 0; e < edit.EdgeCount; e++)
                {
                    var a = edit.EdgeA[e];
                    var b = edit.EdgeB[e];
                    if (!edit.Selected[a] || !edit.Selected[b])
                    {
                        continue;
                    }

                    if (!edit.Visible(a, cameraPosition, forward, _view.Orthographic) && !edit.Visible(b, cameraPosition, forward, _view.Orthographic))
                    {
                        continue;
                    }

                    var from = edit.GroupPosition[a];
                    var to = edit.GroupPosition[b];
                    var side = Vector3.Cross(to - from, forward);
                    if (side.sqrMagnitude < 1e-14f)
                    {
                        continue;
                    }

                    AddRibbon(from, to, side.normalized * (worldPerPixel * SelectedEdgePixels * 0.5f), SelectionColor);
                }
            }

            if (UploadOverlay(_edgeMesh))
            {
                _preview.DrawMesh(_edgeMesh, bias, _ringMaterial, 0);
            }

            ClearOverlay();
            var dot = right * (worldPerPixel * DotPixels * 0.5f);
            var dotUp = up * (worldPerPixel * DotPixels * 0.5f);
            var big = right * (worldPerPixel * SelectedDotPixels * 0.5f);
            var bigUp = up * (worldPerPixel * SelectedDotPixels * 0.5f);
            var decided = right * (worldPerPixel * DecidedDotPixels * 0.5f);
            var decidedUp = up * (worldPerPixel * DecidedDotPixels * 0.5f);
            var activeBone = ActiveCutBone();
            foreach (var edit in _edits)
            {
                var kinds = edit.OverridesOf(activeBone);
                for (var g = 0; g < edit.GroupCount; g++)
                {
                    var selected = edit.Selected[g];
                    var kind = kinds != null ? kinds[g] : (byte)0;
                    if (!selected && kind == 0 && _tool != GoreTool.Vertex)
                    {
                        continue;
                    }

                    if (!edit.Visible(g, cameraPosition, forward, _view.Orthographic))
                    {
                        continue;
                    }

                    if (selected)
                    {
                        AddQuad(edit.GroupPosition[g], big, bigUp, SelectionColor);
                    }
                    else if (kind != 0)
                    {
                        AddQuad(edit.GroupPosition[g], decided, decidedUp, kind == (byte)GoreOverrideKind.Include ? IncludeColor : ExcludeColor);
                    }
                    else
                    {
                        AddQuad(edit.GroupPosition[g], dot, dotUp, DotColor);
                    }
                }
            }

            if (UploadOverlay(_dotMesh))
            {
                _preview.DrawMesh(_dotMesh, bias, _ringMaterial, 0);
            }
        }

        private void ClearOverlay()
        {
            _overlayVertices.Clear();
            _overlayColors.Clear();
            _overlayIndices.Clear();
        }

        private void AddQuad(Vector3 centre, Vector3 right, Vector3 up, Color32 color)
        {
            var start = _overlayVertices.Count;
            _overlayVertices.Add(centre - right - up);
            _overlayVertices.Add(centre + right - up);
            _overlayVertices.Add(centre + right + up);
            _overlayVertices.Add(centre - right + up);
            AddQuadIndices(start, color);
        }

        private void AddRibbon(Vector3 from, Vector3 to, Vector3 side, Color32 color)
        {
            var start = _overlayVertices.Count;
            _overlayVertices.Add(from - side);
            _overlayVertices.Add(from + side);
            _overlayVertices.Add(to + side);
            _overlayVertices.Add(to - side);
            AddQuadIndices(start, color);
        }

        private void AddQuadIndices(int start, Color32 color)
        {
            for (var k = 0; k < 4; k++)
            {
                _overlayColors.Add(color);
            }

            _overlayIndices.Add(start);
            _overlayIndices.Add(start + 1);
            _overlayIndices.Add(start + 2);
            _overlayIndices.Add(start);
            _overlayIndices.Add(start + 2);
            _overlayIndices.Add(start + 3);
        }

        // Puts what AddQuad / AddRibbon collected into a mesh; false when there was nothing.
        private bool UploadOverlay(Mesh mesh)
        {
            if (_overlayVertices.Count == 0)
            {
                return false;
            }

            mesh.Clear();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(_overlayVertices);
            mesh.SetColors(_overlayColors);
            mesh.SetIndices(_overlayIndices, MeshTopology.Triangles, 0, false);
            mesh.bounds = new Bounds(_model.Bounds.center, _model.Bounds.size * 4f + Vector3.one);
            return true;
        }

        // The selected faces (all three vertices selected) as one translucent mesh, rebuilt only when a selection changed.
        private bool BuildSelectedFaces()
        {
            var key = _edits.Count;
            foreach (var edit in _edits)
            {
                key = unchecked(key * 31 + edit.Version);
            }

            if (key != _faceKey)
            {
                _faceKey = key;
                ClearOverlay();
                foreach (var edit in _edits)
                {
                    for (var t = 0; t < edit.TriangleCount; t++)
                    {
                        var a = edit.TriangleGroups[t * 3];
                        var b = edit.TriangleGroups[t * 3 + 1];
                        var c = edit.TriangleGroups[t * 3 + 2];
                        if (!edit.Selected[a] || !edit.Selected[b] || !edit.Selected[c])
                        {
                            continue;
                        }

                        for (var k = 0; k < 3; k++)
                        {
                            _overlayIndices.Add(_overlayVertices.Count);
                            _overlayVertices.Add(edit.GroupPosition[edit.TriangleGroups[t * 3 + k]]);
                            _overlayColors.Add(SelectedFaceColor);
                        }
                    }
                }

                _faceMesh.Clear();
                _faceMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                if (_overlayVertices.Count > 0)
                {
                    _faceMesh.SetVertices(_overlayVertices);
                    _faceMesh.SetColors(_overlayColors);
                    _faceMesh.SetIndices(_overlayIndices, MeshTopology.Triangles, 0, false);
                    _faceMesh.bounds = new Bounds(_model.Bounds.center, _model.Bounds.size * 4f + Vector3.one);
                }

                _faceHasTriangles = _overlayVertices.Count > 0;
            }

            return _faceHasTriangles;
        }

        private bool _faceHasTriangles;

        // The cut rings as thin flat ribbons that face the camera, a few pixels wide, nudged towards the camera so the skin does not hide them.
        private bool BuildRings(Rect rect)
        {
            var count = _model.LineFrom.Count;
            if (count == 0)
            {
                return false;
            }

            ClearOverlay();
            var forward = _view.Forward;
            var worldPerPixel = 2f * _view.Distance * Mathf.Tan(15f * Mathf.Deg2Rad) / Mathf.Max(1f, rect.height);
            var towardCamera = -forward * (_view.Distance * 0.004f);
            for (var i = 0; i < count; i++)
            {
                var from = _model.LineFrom[i] + towardCamera;
                var to = _model.LineTo[i] + towardCamera;
                var side = Vector3.Cross(to - from, forward);
                if (side.sqrMagnitude < 1e-14f)
                {
                    continue;
                }

                var cut = _model.LineCut[i];
                var selected = cut == _model.SelectedCut;
                side = side.normalized * (worldPerPixel * (selected ? SelectedRingPixels : RingPixels) * 0.5f);
                AddRibbon(from, to, side, selected ? SelectedRingColor : _model.CutColor(cut));
            }

            return UploadOverlay(_ringMesh);
        }

        #endregion

        #region Toolbar and bar

        // Top left, like Blender's header: the mode (object or edit), then the way to select (click, paint) and what to select (vertex, edge, face) -
        // both for edit mode only - then the view (colours, materials, X-ray) and the overlays of the cuts (lines, lids).
        private void DrawToolbar(Rect rect)
        {
            var x = rect.x + 8f;
            var y = rect.y + 8f;
            var modeObject = new Rect(x, y, ButtonWidth, ButtonHeight);
            var modeEdit = new Rect(modeObject.xMax + 2f, y, ButtonWidth, ButtonHeight);
            var modeClick = new Rect(modeEdit.xMax + 18f, y, ButtonWidth, ButtonHeight);
            var modePaint = new Rect(modeClick.xMax + 2f, y, ButtonWidth, ButtonHeight);
            var vertex = new Rect(modePaint.xMax + 18f, y, ButtonWidth, ButtonHeight);
            var edge = new Rect(vertex.xMax + 2f, y, ButtonWidth, ButtonHeight);
            var face = new Rect(edge.xMax + 2f, y, ButtonWidth, ButtonHeight);
            var viewSegments = new Rect(face.xMax + 18f, y, ButtonWidth, ButtonHeight);
            var viewMaterials = new Rect(viewSegments.xMax + 2f, y, ButtonWidth, ButtonHeight);
            var viewXray = new Rect(viewMaterials.xMax + 2f, y, ButtonWidth, ButtonHeight);
            var showLines = new Rect(viewXray.xMax + 18f, y, ButtonWidth, ButtonHeight);
            var showLids = new Rect(showLines.xMax + 2f, y, ButtonWidth, ButtonHeight);
            _toolbarRight = showLids.xMax;
            _uiRects.Add(new Rect(x, y, showLids.xMax - x, ButtonHeight));

            if (ToolButton(modeObject, !_editMode, IsStaticTarget ? "Object mode: a click picks a piece (its colour). Tab switches between object and edit mode." : "Object mode: a click picks a whole object (a body mesh or a piece of clothing). Tab switches between object and edit mode.", 7))
            {
                Defer(() => SetEditMode(false));
            }

            if (ToolButton(modeEdit, _editMode, IsStaticTarget ? "Edit mode: select the vertices, edges and faces of the mesh to make pieces of them, or to choose where the extra triangles go. Tab switches." : "Edit mode: select the vertices, edges and faces of the picked body mesh (all shown meshes when none is picked) to move the cut lines. Tab switches.", 8))
            {
                Defer(() => SetEditMode(true));
            }

            if (ToolButton(modeClick, !_paint, _editMode ? "Select by clicking (C switches between click and paint). Click = select, Ctrl + click = add or remove one, drag = box, Shift + drag = take out, Alt + click = edge loop." : EditOnlyTip, 0, _editMode))
            {
                _paint = false;
                _painting = false;
            }

            if (ToolButton(modePaint, _paint, _editMode ? "Select by painting with a round brush (C). Drag = select, Shift + drag = deselect, mouse wheel = brush size, C or Esc = back to clicking." : EditOnlyTip, 1, _editMode))
            {
                _paint = true;
                _selectionDown = false;
                _selectionBox = false;
            }

            if (ToolButton(vertex, _tool == GoreTool.Vertex, _editMode ? "Select vertices (1)." : EditOnlyTip, 2, _editMode))
            {
                _tool = GoreTool.Vertex;
            }

            if (ToolButton(edge, _tool == GoreTool.Edge, _editMode ? "Select edges (2)." : EditOnlyTip, 3, _editMode))
            {
                _tool = GoreTool.Edge;
            }

            if (ToolButton(face, _tool == GoreTool.Face, _editMode ? "Select faces (3)." : EditOnlyTip, 4, _editMode))
            {
                _tool = GoreTool.Face;
            }

            if (ToolButton(viewSegments, !_materialView, IsStaticTarget ? "Show the pieces in flat colours (M switches between the two views)." : "Show the parts in flat colours: where every cut separates the body and the clothes (M switches between the two views).", 5))
            {
                SetMaterialView(false);
            }

            if (ToolButton(viewMaterials, _materialView, IsStaticTarget ? "Show the mesh as it is, with its materials and textures (M)." : "Show the character as it is, with its materials and textures (M).", 6))
            {
                SetMaterialView(true);
            }

            if (ToolButton(viewXray, _xray && !_materialView, _materialView ? "X-ray works in the coloured view only. Press M to go back to it." : "X-ray: draw the coloured parts see-through, so the body shows under the clothes.", 9, !_materialView))
            {
                Defer(() => SetXRay(!_xray));
            }

            if (ToolButton(showLines, _showLines, IsStaticTarget ? "Lines: every edge of the mesh. The edges of the new triangles are drawn in yellow." : "Lines: the rings where the cuts will separate the parts.", 10))
            {
                _showLines = !_showLines;
                Repaint();
            }

            if (ToolButton(showLids, _showLids, IsStaticTarget ? "Lids are made when you Apply, so they are not shown here." : "Lids: the translucent red discs that will close the cut parts.", 11, !IsStaticTarget))
            {
                _showLids = !_showLids;
                Repaint();
            }
        }

        private void SetMaterialView(bool on)
        {
            if (_materialView == on)
            {
                return;
            }

            _materialView = on;
            ApplyVisibility();
            Repaint();
        }

        // Switches between object mode and edit mode. The selections of the meshes stay as they are; only the tools and what they draw come and go.
        private void SetEditMode(bool on)
        {
            if (_editMode == on)
            {
                return;
            }

            _editMode = on;

            // Entering the Edit mode lets go of the picked object (the selection in Edit mode is of vertices, edges and faces), but the tools keep
            // working on the body mesh that was picked; with a piece of clothing picked (it has nothing to edit) or nothing, they work on every body
            // mesh. Leaving the mode, they go back to every mesh.
            if (on)
            {
                _model.EditRow = _model.HasSelectedRow ? _model.SelectedRow : -1;
                SelectObject(-1, -1);
            }
            else
            {
                _model.EditRow = -1;
            }

            // The body meshes a look hides are shown while they are edited.
            ApplyVisibility();
            _painting = false;
            _selectionDown = false;
            _selectionBox = false;
            _objectDown = false;
            _hoverValid = false;
            _hoverRow = -1;
            _hoverCloth = -1;
            _selectionKey = -1;
            _faceKey = -1;
            Repaint();
        }

        // The coloured parts see-through (or opaque again): the colours carry the alpha, so they are painted again.
        private void SetXRay(bool on)
        {
            _xray = on;
            _model.Opacity = on ? XRayOpacity : (byte)255;
            _model.Recolor();
            Repaint();
        }

        // A square icon button: blue when it is the mode in use; dimmed (and dead) when it is not available now.
        private bool ToolButton(Rect area, bool active, string tooltip, int icon, bool enabled = true)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(area, active ? ButtonOn : enabled && area.Contains(Event.current.mousePosition) ? ButtonHover : ButtonOff);
                DrawIcon(area, icon, active);
                if (!enabled)
                {
                    EditorGUI.DrawRect(area, ButtonDimmed);
                }
            }

            var previous = GUI.enabled;
            GUI.enabled = previous && enabled;
            var pressed = GUI.Button(area, new GUIContent(string.Empty, tooltip), GUIStyle.none);
            GUI.enabled = previous;
            return pressed;
        }

        // 0 = click (arrow), 1 = paint (circle), 2 = vertex, 3 = edge, 4 = face; the last three like Blender's cube icons. 5 = coloured parts, 6 = materials,
        // 7 = object mode (solid cube), 8 = edit mode (wire cube with its vertices), 9 = X-ray (two squares seen through each other), 10 = lines (a ring),
        // 11 = lids (a filled ring).
        private static void DrawIcon(Rect area, int icon, bool active)
        {
            var colour = active ? Color.white : new Color(0.80f, 0.81f, 0.84f);
            var accent = active ? new Color(1f, 0.82f, 0.45f) : new Color(1f, 0.65f, 0.2f);
            var cx = area.center.x;
            var cy = area.center.y;
            Handles.BeginGUI();
            var previousColor = Handles.color;
            switch (icon)
            {
                case 0:
                    Handles.color = colour;
                    IconTriangle[0] = new Vector3(cx - 5f, cy - 8f);
                    IconTriangle[1] = new Vector3(cx - 5f, cy + 5f);
                    IconTriangle[2] = new Vector3(cx + 6f, cy);
                    Handles.DrawAAConvexPolygon(IconTriangle);
                    IconPolygon[0] = new Vector3(cx - 1f, cy + 2f);
                    IconPolygon[1] = new Vector3(cx + 2f, cy + 9f);
                    IconPolygon[2] = new Vector3(cx + 5f, cy + 8f);
                    IconPolygon[3] = new Vector3(cx + 2f, cy + 1f);
                    Handles.DrawAAConvexPolygon(IconPolygon);
                    break;
                case 1:
                    Handles.color = colour;
                    Handles.DrawWireDisc(new Vector3(cx, cy), Vector3.forward, 8f, 1.6f);
                    Handles.color = accent;
                    Handles.DrawSolidDisc(new Vector3(cx, cy), Vector3.forward, 2.2f);
                    break;
                case 5:
                    DrawSquare(cx - 7.5f, cy - 7.5f, new Color(0.95f, 0.45f, 0.45f));
                    DrawSquare(cx + 0.5f, cy - 7.5f, new Color(0.45f, 0.78f, 0.95f));
                    DrawSquare(cx - 7.5f, cy + 0.5f, new Color(0.55f, 0.88f, 0.5f));
                    DrawSquare(cx + 0.5f, cy + 0.5f, new Color(0.98f, 0.82f, 0.4f));
                    break;
                case 6:
                    Handles.color = colour;
                    Handles.DrawSolidDisc(new Vector3(cx, cy), Vector3.forward, 8f);
                    Handles.color = active ? new Color(0.55f, 0.74f, 1f) : new Color(0.46f, 0.52f, 0.60f);
                    Handles.DrawSolidDisc(new Vector3(cx + 1.8f, cy + 1.8f), Vector3.forward, 5.4f);
                    Handles.color = new Color(1f, 1f, 1f, 0.9f);
                    Handles.DrawSolidDisc(new Vector3(cx - 3f, cy - 3f), Vector3.forward, 2.2f);
                    break;
                case 7:
                case 8:
                    DrawCube(cx, cy, icon == 7, colour, accent);
                    break;
                case 9:
                    SetPolygonSquare(cx - 8f, cy - 8f, 11f);
                    Handles.color = new Color(accent.r, accent.g, accent.b, 0.65f);
                    Handles.DrawAAConvexPolygon(IconPolygon);
                    SetPolygonSquare(cx - 3f, cy - 3f, 11f);
                    Handles.color = new Color(colour.r, colour.g, colour.b, 0.28f);
                    Handles.DrawAAConvexPolygon(IconPolygon);
                    for (var corner = 0; corner < 4; corner++)
                    {
                        IconSquare[corner] = IconPolygon[corner];
                    }

                    IconSquare[4] = IconSquare[0];
                    Handles.color = colour;
                    Handles.DrawAAPolyLine(1.5f, IconSquare);
                    break;
                case 10:
                    SetEllipse(cx, cy, 8.5f, 4.5f);
                    Handles.color = accent;
                    Handles.DrawAAPolyLine(2.2f, IconEllipse);
                    break;
                case 11:
                    SetEllipse(cx, cy, 8.5f, 4.5f);
                    Handles.color = new Color(0.9f, 0.25f, 0.25f, 0.7f);
                    Handles.DrawAAConvexPolygon(IconEllipse);
                    Handles.color = colour;
                    Handles.DrawAAPolyLine(1.5f, IconEllipse);
                    break;
                default:
                    DrawCubeFace(cx, cy, icon, colour, accent);
                    break;
            }

            Handles.color = previousColor;
            Handles.EndGUI();
        }

        // The square of IconPolygon at (x, y), the given size.
        private static void SetPolygonSquare(float x, float y, float size)
        {
            IconPolygon[0] = new Vector3(x, y);
            IconPolygon[1] = new Vector3(x + size, y);
            IconPolygon[2] = new Vector3(x + size, y + size);
            IconPolygon[3] = new Vector3(x, y + size);
        }

        // A closed ring of points (the first is repeated at the end) in IconEllipse.
        private static void SetEllipse(float cx, float cy, float radiusX, float radiusY)
        {
            const int steps = 16;
            for (var i = 0; i <= steps; i++)
            {
                var angle = i * Mathf.PI * 2f / steps;
                IconEllipse[i] = new Vector3(cx + Mathf.Cos(angle) * radiusX, cy + Mathf.Sin(angle) * radiusY);
            }
        }

        private static void FillQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        {
            IconPolygon[0] = a;
            IconPolygon[1] = b;
            IconPolygon[2] = c;
            IconPolygon[3] = d;
            Handles.color = color;
            Handles.DrawAAConvexPolygon(IconPolygon);
        }

        // A cube seen from a corner: solid with three shades (object mode) or as a wire with its vertices (edit mode).
        private static void DrawCube(float cx, float cy, bool solid, Color colour, Color accent)
        {
            var top = new Vector3(cx, cy - 8f);
            var upperRight = new Vector3(cx + 7f, cy - 4f);
            var lowerRight = new Vector3(cx + 7f, cy + 4f);
            var bottom = new Vector3(cx, cy + 8f);
            var lowerLeft = new Vector3(cx - 7f, cy + 4f);
            var upperLeft = new Vector3(cx - 7f, cy - 4f);
            var centre = new Vector3(cx, cy);
            if (solid)
            {
                FillQuad(top, upperRight, centre, upperLeft, colour);
                FillQuad(upperLeft, centre, bottom, lowerLeft, new Color(colour.r * 0.58f, colour.g * 0.58f, colour.b * 0.58f, 1f));
                FillQuad(upperRight, lowerRight, bottom, centre, new Color(colour.r * 0.8f, colour.g * 0.8f, colour.b * 0.8f, 1f));
                return;
            }

            IconHexagon[0] = top;
            IconHexagon[1] = upperRight;
            IconHexagon[2] = lowerRight;
            IconHexagon[3] = bottom;
            IconHexagon[4] = lowerLeft;
            IconHexagon[5] = upperLeft;
            IconHexagon[6] = top;
            Handles.color = colour;
            Handles.DrawAAPolyLine(1.5f, IconHexagon);
            IconLine[0] = centre;
            IconLine[1] = upperLeft;
            Handles.DrawAAPolyLine(1.5f, IconLine);
            IconLine[1] = upperRight;
            Handles.DrawAAPolyLine(1.5f, IconLine);
            IconLine[1] = bottom;
            Handles.DrawAAPolyLine(1.5f, IconLine);
            for (var i = 0; i < 6; i++)
            {
                Handles.color = i == 0 ? accent : colour;
                Handles.DrawSolidDisc(IconHexagon[i], Vector3.forward, i == 0 ? 2.8f : 1.8f);
            }

            Handles.color = colour;
            Handles.DrawSolidDisc(centre, Vector3.forward, 1.8f);
        }

        // A small filled square of the coloured-parts icon.
        private static void DrawSquare(float x, float y, Color color)
        {
            IconPolygon[0] = new Vector3(x, y);
            IconPolygon[1] = new Vector3(x + 7f, y);
            IconPolygon[2] = new Vector3(x + 7f, y + 7f);
            IconPolygon[3] = new Vector3(x, y + 7f);
            Handles.color = color;
            Handles.DrawAAConvexPolygon(IconPolygon);
        }

        // A square seen from the front with its corners as dots; the vertex, the top edge or the whole face is the part that is lit.
        private static void DrawCubeFace(float cx, float cy, int icon, Color colour, Color accent)
        {
            const float half = 7.5f;
            if (icon == 4)
            {
                IconPolygon[0] = new Vector3(cx - half, cy - half);
                IconPolygon[1] = new Vector3(cx + half, cy - half);
                IconPolygon[2] = new Vector3(cx + half, cy + half);
                IconPolygon[3] = new Vector3(cx - half, cy + half);
                Handles.color = new Color(accent.r, accent.g, accent.b, 0.55f);
                Handles.DrawAAConvexPolygon(IconPolygon);
            }

            IconSquare[0] = new Vector3(cx - half, cy - half);
            IconSquare[1] = new Vector3(cx + half, cy - half);
            IconSquare[2] = new Vector3(cx + half, cy + half);
            IconSquare[3] = new Vector3(cx - half, cy + half);
            IconSquare[4] = IconSquare[0];
            Handles.color = colour;
            Handles.DrawAAPolyLine(1.5f, IconSquare);

            if (icon == 3)
            {
                IconLine[0] = IconSquare[0];
                IconLine[1] = IconSquare[1];
                Handles.color = accent;
                Handles.DrawAAPolyLine(3.5f, IconLine);
            }

            for (var corner = 0; corner < 4; corner++)
            {
                var lit = (icon == 2 && corner == 0) || (icon == 3 && corner < 2) || icon == 4;
                Handles.color = lit ? accent : colour;
                Handles.DrawSolidDisc(IconSquare[corner], Vector3.forward, lit ? 3.2f : 2f);
            }
        }

        // The bar at the bottom: how many vertices are selected, which cut they are for, and what to do with them.
        private void DrawHud(Rect rect)
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
                    var style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, alignment = TextAnchor.MiddleCenter };
                    style.normal.textColor = Color.white;
                    GUI.Label(new Rect(toast.x + 6f, toast.y, toast.width - 12f, toast.height), _toast, style);
                }
            }

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(box, HudBackground);
            }

            var hasSelection = _selectedVertices > 0;
            var cutIndex = _model.SelectedCut;
            var hasCut = cutIndex >= 0 && cutIndex < _model.Cuts.Count && _model.Cuts[cutIndex].Bone != null;

            var title = hasSelection ? _selectedVertices + " selected" : "None selected";
            GUI.Label(new Rect(box.x + 10f, box.y + 7f, 110f, 20f), new GUIContent(title, "How many vertices are selected."), EditorStyles.boldLabel);
            GUI.Label(new Rect(box.x + 124f, box.y + 7f, 32f, 20f), "Cut:");
            var cutLabel = hasCut ? (_cutManual ? _model.Cuts[cutIndex].Name : "Auto: " + _model.Cuts[cutIndex].Name) : "Auto";
            if (GUI.Button(new Rect(box.x + 156f, box.y + 7f, box.width - 166f, 20f), new GUIContent(cutLabel, "The cut the selection is for. Auto picks the cut nearest to the selection; you can choose another."), EditorStyles.popup))
            {
                ShowCutMenu();
            }

            var y = box.y + 33f;
            var half = (box.width - 26f) * 0.5f;
            using (new EditorGUI.DisabledScope(!hasSelection || !hasCut))
            {
                if (GUI.Button(new Rect(box.x + 10f, y, half, 22f), new GUIContent("Add to cut", "The selection becomes the cut line: the selected vertices and the side of them that is the cut's part go with the cut; what lies beyond them goes back to the body. Shown green. Select a closed ring or a whole strip round the body part.")))
                {
                    Defer(() => AssignSelection(GoreOverrideKind.Include));
                }

                if (GUI.Button(new Rect(box.x + 16f + half, y, half, 22f), new GUIContent("Remove from cut", "The selected vertices and what lies beyond them go back to the body; the cut's part on the other side stays. Shown red. Select a closed ring or a whole strip.")))
                {
                    Defer(() => AssignSelection(GoreOverrideKind.Exclude));
                }
            }
        }

        private void ShowCutMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Auto (the cut nearest to the selection)"), !_cutManual, () => Defer(() =>
            {
                _cutManual = false;
                _selectionKey = -1;
                RefreshSelectionStats();
            }));
            menu.AddSeparator(string.Empty);
            for (var i = 0; i < _model.Cuts.Count; i++)
            {
                var index = i;
                menu.AddItem(new GUIContent(_model.Cuts[i].Name), _cutManual && i == _model.SelectedCut, () => Defer(() =>
                {
                    _cutManual = true;
                    _model.SelectedCut = index;
                    _model.Recolor();
                    RefreshSelectionStats();
                }));
            }

            if (_model.Cuts.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("No cuts yet"));
            }

            menu.ShowAsContext();
        }

        // The transform of the cut chosen in the bar (the cut the selection is for), or null.
        private Transform ActiveCutBone()
        {
            var index = _model.SelectedCut;
            return index >= 0 && index < _model.Cuts.Count ? _model.Cuts[index].Bone : null;
        }

        // The selection becomes the cut line of the active cut (Add: it and the part's side go with the cut; Remove: it and what lies beyond go back to the body).
        private void AssignSelection(GoreOverrideKind kind)
        {
            var bone = ActiveCutBone();
            var parents = _model.SegmentParent;
            var cutIndex = _model.SelectedCut;
            if (bone == null || parents == null)
            {
                _toast = "Choose a cut first (the Cut list in the bar), and make sure the cuts are valid.";
                _toastKey = _selectionKey;
                return;
            }

            var before = CaptureAll();
            var versions = VersionKey();
            CollectEdits();
            var total = 0;
            var included = 0;
            var excluded = 0;
            var closedGroups = 0;
            var separated = false;
            foreach (var edit in _edits)
            {
                if (edit.SelectedCount() == 0)
                {
                    continue;
                }

                edit.EnsureOverrides(bone);
                var kindsByCut = new byte[_model.Cuts.Count][];
                for (var cut = 0; cut < kindsByCut.Length; cut++)
                {
                    kindsByCut[cut] = edit.OverridesOf(_model.Cuts[cut].Bone);
                }

                total += edit.Decide(kindsByCut, cutIndex, kind, parents, out var meshIncluded, out var meshExcluded, out var meshSeparated, out var meshClosed);
                included += meshIncluded;
                excluded += meshExcluded;
                closedGroups += meshClosed;
                separated |= meshSeparated;
            }

            if (VersionKey() != versions)
            {
                PushUndo(before);
            }

            _model.Dirty = true;
            RefreshSelectionStats();

            var name = _model.Cuts[cutIndex].Name;
            if (separated)
            {
                var closedNote = closedGroups > 0 ? " (your line did not go all the way round, so I closed it on the far side with " + closedGroups + " more vertices)" : string.Empty;
                _toast = kind == GoreOverrideKind.Include
                    ? name + " now ends at your selection: " + included + " vertices added, " + excluded + " taken out" + closedNote + ". Ctrl + Z undoes it."
                    : name + " now ends before your selection: " + excluded + " vertices taken out" + closedNote + ". Ctrl + Z undoes it.";
            }
            else
            {
                _toast = (kind == GoreOverrideKind.Include ? "Added only the " : "Removed only the ") + total + " selected vertices: they do not form a line round the body part (and could not be closed), so there is no line to cut along. Select a ring or a strip round it. Ctrl + Z undoes it.";
            }

            _toastKey = _selectionKey;
            Repaint();
        }

        // The brush while painting: a circle round the mouse.
        private void DrawPaintCursor(Rect rect)
        {
            if (!_editMode || !_paint || !rect.Contains(Event.current.mousePosition) || InUi(Event.current.mousePosition))
            {
                return;
            }

            GUI.BeginClip(rect);
            Handles.BeginGUI();
            // Inside the clip the mouse position is already relative to it.
            var centre = new Vector3(Event.current.mousePosition.x, Event.current.mousePosition.y);
            using (new Handles.DrawingScope(new Color(1f, 1f, 1f, 0.9f)))
            {
                Handles.DrawWireDisc(centre, Vector3.forward, _brushRadius, 1.5f);
            }

            using (new Handles.DrawingScope(new Color(1f, 0.6f, 0.1f, 0.10f)))
            {
                Handles.DrawSolidDisc(centre, Vector3.forward, _brushRadius);
            }

            Handles.EndGUI();
            GUI.EndClip();
        }

        private void DrawSelectionBox()
        {
            if (!_selectionBox)
            {
                return;
            }

            var box = Rect.MinMaxRect(
                Mathf.Min(_selectionStart.x, _selectionNow.x), Mathf.Min(_selectionStart.y, _selectionNow.y),
                Mathf.Max(_selectionStart.x, _selectionNow.x), Mathf.Max(_selectionStart.y, _selectionNow.y));
            EditorGUI.DrawRect(box, new Color(1f, 0.6f, 0.1f, 0.12f));
            var line = new Color(1f, 0.6f, 0.1f, 0.9f);
            EditorGUI.DrawRect(new Rect(box.x, box.y, box.width, 1f), line);
            EditorGUI.DrawRect(new Rect(box.x, box.yMax - 1f, box.width, 1f), line);
            EditorGUI.DrawRect(new Rect(box.x, box.y, 1f, box.height), line);
            EditorGUI.DrawRect(new Rect(box.xMax - 1f, box.y, 1f, box.height), line);
        }

        #endregion

        #region Undo

        private const int MaxUndoSteps = 50;

        // The selection and the decisions of every mesh at one moment.
        private sealed class UndoStepData
        {
            public readonly Dictionary<GoreStudioMeshEdit, GoreStudioMeshEdit.State> States = new Dictionary<GoreStudioMeshEdit, GoreStudioMeshEdit.State>();

            // The pieces drawn by hand on a static mesh; only set by the actions that change them (a selection step does not copy them).
            public GoreStudioStatic.OwnerSnapshot Owners;
        }

        private readonly List<UndoStepData> _undo = new List<UndoStepData>(MaxUndoSteps + 1);
        private readonly List<UndoStepData> _redo = new List<UndoStepData>(MaxUndoSteps + 1);
        private UndoStepData _strokeBefore;
        private int _strokeKey;

        private UndoStepData CaptureAll()
        {
            var step = new UndoStepData();
            foreach (var row in _model.Rows)
            {
                if (row.Edit != null)
                {
                    step.States.Add(row.Edit, row.Edit.Capture());
                }
            }

            if (IsStaticTarget && _static.Edit != null)
            {
                step.States.Add(_static.Edit, _static.Edit.Capture());
            }

            return step;
        }

        // A number that changes whenever any selection or decision of any mesh does.
        private int VersionKey()
        {
            var key = 17;
            foreach (var row in _model.Rows)
            {
                if (row.Edit != null)
                {
                    key = unchecked(key * 31 + row.Edit.Version);
                    key = unchecked(key * 31 + row.Edit.OverrideVersion);
                }
            }

            if (IsStaticTarget && _static.Edit != null)
            {
                key = unchecked(key * 31 + _static.Edit.Version);
            }

            return key;
        }

        private void PushUndo(UndoStepData before)
        {
            _undo.Add(before);
            if (_undo.Count > MaxUndoSteps)
            {
                _undo.RemoveAt(0);
            }

            _redo.Clear();
        }

        private void UndoStep()
        {
            if (_undo.Count == 0)
            {
                return;
            }

            var step = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            var current = CaptureAll();
            if (step.Owners != null)
            {
                current.Owners = _static.Snapshot();
            }

            _redo.Add(current);
            RestoreAll(step);
        }

        private void RedoStep()
        {
            if (_redo.Count == 0)
            {
                return;
            }

            var step = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            var current = CaptureAll();
            if (step.Owners != null)
            {
                current.Owners = _static.Snapshot();
            }

            _undo.Add(current);
            RestoreAll(step);
        }

        private void RestoreAll(UndoStepData step)
        {
            foreach (var pair in step.States)
            {
                pair.Key.Restore(pair.Value);
            }

            if (step.Owners != null && IsStaticTarget)
            {
                _static.Restore(step.Owners);
            }

            _toast = string.Empty;
            _model.Dirty = true;
            _selectionKey = -1;
            RefreshSelectionStats();
        }

        #endregion

        #region Info

        private const float InfoButtonSize = 28f;
        private const float InfoMargin = 12f;
        private const float InfoPanelWidth = 340f;
        private const float InfoLineHeight = 18f;

        // What each action is, then how to do it.
        private static readonly string[][] InfoLines =
        {
            new[] { "Orbit", "Middle drag" },
            new[] { "Pan", "Shift + middle drag" },
            new[] { "Zoom", "Scroll wheel" },
            new[] { "Front / right / top view", "Numpad 1 / 3 / 7" },
            new[] { "Perspective / orthographic", "Numpad 5, or the gizmo" },
            new[] { "Frame the character / the selection", "F / Numpad ." },
            new[] { "Object mode / edit mode", "Tab" },
            new[] { "Pick an object (object mode)", "Click" },
            new[] { "Vertex / edge / face (edit mode)", "1 / 2 / 3" },
            new[] { "Click select / paint select", "C" },
            new[] { "Coloured parts / materials", "M" },
            new[] { "Select (edit mode)", "Click" },
            new[] { "Add or remove one", "Ctrl + click" },
            new[] { "Select with a box", "Drag" },
            new[] { "Take out of the selection", "Shift + drag" },
            new[] { "Select an edge loop", "Alt + click" },
            new[] { "Paint: select / deselect", "Drag / Shift + drag" },
            new[] { "Paint: brush size", "Scroll wheel" },
            new[] { "Select all / none / invert", "A (again: none) / Alt + A / Ctrl + I" },
            new[] { "Undo / redo", "Ctrl + Z / Ctrl + Y" }
        };

        private bool _infoOpen;

        private static Rect InfoButtonRect(Rect view) => new Rect(view.xMax - InfoMargin - InfoButtonSize, view.yMax - InfoMargin - InfoButtonSize, InfoButtonSize, InfoButtonSize);

        private static Rect InfoPanelRect(Rect view)
        {
            var button = InfoButtonRect(view);
            var height = InfoLines.Length * InfoLineHeight + 16f;
            return new Rect(button.xMax - InfoPanelWidth, button.y - 8f - height, InfoPanelWidth, height);
        }

        // A press on the button opens or closes the panel; a press on the open panel does nothing; a press anywhere else closes it
        // (and goes on to do what it does there). Returns whether the press was the info's.
        private bool HandleInfoPress(Rect view, Vector2 point)
        {
            if (InfoButtonRect(view).Contains(point))
            {
                _infoOpen = !_infoOpen;
                return true;
            }

            if (_infoOpen && InfoPanelRect(view).Contains(point))
            {
                return true;
            }

            if (_infoOpen)
            {
                _infoOpen = false;
                Repaint();
            }

            return false;
        }

        // The round info button in the bottom-right corner, and, when it is open, the panel above it.
        private void DrawInfo(Rect view)
        {
            var button = InfoButtonRect(view);
            _uiRects.Add(button);
            if (_infoOpen)
            {
                _uiRects.Add(InfoPanelRect(view));
            }

            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            var hover = button.Contains(Event.current.mousePosition) || _infoOpen;
            Handles.BeginGUI();
            using (new Handles.DrawingScope(hover ? new Color(0.36f, 0.36f, 0.36f, 1f) : new Color(0.26f, 0.26f, 0.26f, 1f)))
            {
                Handles.DrawSolidDisc(button.center, Vector3.forward, InfoButtonSize * 0.5f);
            }

            Handles.EndGUI();
            var icon = EditorGUIUtility.IconContent("UnityEditor.InspectorWindow").image;
            if (icon != null)
            {
                GUI.DrawTexture(new Rect(button.center.x - 8f, button.center.y - 8f, 16f, 16f), icon, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.Label(button, "i", EditorStyles.centeredGreyMiniLabel);
            }

            if (!_infoOpen)
            {
                return;
            }

            var panel = InfoPanelRect(view);
            EditorGUI.DrawRect(panel, new Color(0.19f, 0.19f, 0.19f, 0.97f));
            var border = new Color(0.09f, 0.09f, 0.09f, 1f);
            EditorGUI.DrawRect(new Rect(panel.x, panel.y, panel.width, 1f), border);
            EditorGUI.DrawRect(new Rect(panel.x, panel.yMax - 1f, panel.width, 1f), border);
            EditorGUI.DrawRect(new Rect(panel.x, panel.y, 1f, panel.height), border);
            EditorGUI.DrawRect(new Rect(panel.xMax - 1f, panel.y, 1f, panel.height), border);
            var action = new GUIStyle(EditorStyles.miniLabel);
            action.normal.textColor = new Color(1f, 1f, 1f, 0.8f);
            var input = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleRight };
            input.normal.textColor = new Color(1f, 1f, 1f, 0.9f);
            for (var i = 0; i < InfoLines.Length; i++)
            {
                var line = new Rect(panel.x + 10f, panel.y + 8f + i * InfoLineHeight, panel.width - 20f, InfoLineHeight);
                GUI.Label(line, InfoLines[i][0], action);
                GUI.Label(line, InfoLines[i][1], input);
            }
        }

        #endregion

        #region Input

        private void HandleHotkeys(Rect rect, Event current)
        {
            if (current.type != EventType.KeyDown || !rect.Contains(current.mousePosition) || EditorGUIUtility.editingTextField)
            {
                return;
            }

            // Ctrl and Command are for the editor's own shortcuts (copy, select all...): only Ctrl + I is ours.
            if (current.control || current.command)
            {
                if (current.keyCode == KeyCode.Z && !current.shift)
                {
                    UndoStep();
                    current.Use();
                    Repaint();
                }
                else if (current.keyCode == KeyCode.Y || (current.keyCode == KeyCode.Z && current.shift))
                {
                    RedoStep();
                    current.Use();
                    Repaint();
                }
                else if (current.keyCode == KeyCode.I && _editMode)
                {
                    TrackedChange(InvertSelection);
                    current.Use();
                    Repaint();
                }

                return;
            }

            var handled = true;
            switch (current.keyCode)
            {
                case KeyCode.Tab:
                    Defer(() => SetEditMode(!_editMode));
                    break;
                case KeyCode.Alpha1 when _editMode:
                    _tool = GoreTool.Vertex;
                    break;
                case KeyCode.Alpha2 when _editMode:
                    _tool = GoreTool.Edge;
                    break;
                case KeyCode.Alpha3 when _editMode:
                    _tool = GoreTool.Face;
                    break;
                case KeyCode.M:
                    SetMaterialView(!_materialView);
                    break;
                case KeyCode.C when _editMode:
                    _paint = !_paint;
                    _painting = false;
                    _selectionDown = false;
                    _selectionBox = false;
                    break;
                case KeyCode.Escape when _editMode && _paint:
                    _paint = false;
                    _painting = false;
                    break;
                case KeyCode.A when _editMode:
                    if (current.alt)
                    {
                        TrackedChange(() =>
                        {
                            GoreStudioSelection.ClearAll(_edits);
                            RefreshSelectionStats();
                        });
                    }
                    else
                    {
                        // A again, when everything is selected already, lets go of everything.
                        TrackedChange(AllSelected() ? (System.Action)ClearSelection : SelectAll);
                    }

                    break;
                case KeyCode.KeypadPeriod when _editMode:
                    FrameSelection();
                    break;
                default:
                    handled = false;
                    break;
            }

            if (handled)
            {
                current.Use();
                Repaint();
            }
        }

        // Whether every vertex of every mesh being edited is selected.
        private bool AllSelected()
        {
            if (_edits.Count == 0)
            {
                return false;
            }

            foreach (var edit in _edits)
            {
                if (edit.SelectedCount() != edit.GroupCount)
                {
                    return false;
                }
            }

            return true;
        }

        private void ClearSelection()
        {
            GoreStudioSelection.ClearAll(_edits);
            RefreshSelectionStats();
        }

        private void SelectAll()
        {
            foreach (var edit in _edits)
            {
                edit.SelectAll();
            }

            RefreshSelectionStats();
        }

        // What the user sees changed by an action becomes one step of the undo.
        private void TrackedChange(System.Action action)
        {
            var before = CaptureAll();
            var key = VersionKey();
            action();
            if (VersionKey() != key)
            {
                PushUndo(before);
            }
        }

        private void InvertSelection()
        {
            foreach (var edit in _edits)
            {
                edit.Invert();
            }

            RefreshSelectionStats();
        }

        private void FrameSelection()
        {
            if (GoreStudioSelection.SelectionBounds(_edits, out var bounds))
            {
                if (bounds.size.sqrMagnitude < 1e-6f)
                {
                    bounds.size = Vector3.one * 0.1f;
                }

                _view.FocusOn(bounds);
                Repaint();
            }
        }

        // The numbers of the bar, recounted only when a selection or a decision changed (a change of any mesh's versions, or of the active cut).
        private void RefreshSelectionStats()
        {
            CollectEdits();
            var key = SelectionKey();
            if (key == _selectionKey)
            {
                return;
            }

            _selectedVertices = GoreStudioSelection.CountVertices(_edits);

            // Without a cut chosen by hand, the one nearest to the selection is the cut it is meant for (and is shown highlighted).
            if (!_cutManual && _selectedVertices > 0)
            {
                var guess = GuessCut();
                if (guess >= 0 && guess != _model.SelectedCut)
                {
                    _model.SelectedCut = guess;
                    _model.Recolor();
                }
            }

            key = SelectionKey();
            _selectionKey = key;
            if (_toast.Length > 0 && key != _toastKey)
            {
                _toast = string.Empty;
            }
        }

        private int SelectionKey()
        {
            var key = _edits.Count * 7 + _model.SelectedCut;
            foreach (var edit in _edits)
            {
                key = unchecked(key * 31 + edit.Version);
                key = unchecked(key * 31 + edit.OverrideVersion);
            }

            return key;
        }

        // The cut whose part (by the bone weights) is nearest to the middle of the selection; -1 when it cannot be told.
        private int GuessCut()
        {
            var centre = Vector3.zero;
            var count = 0;
            foreach (var edit in _edits)
            {
                for (var g = 0; g < edit.GroupCount; g++)
                {
                    if (edit.Selected[g])
                    {
                        centre += edit.GroupPosition[g];
                        count++;
                    }
                }
            }

            if (count == 0)
            {
                return -1;
            }

            centre /= count;
            var best = -1;
            var bestDistance = float.PositiveInfinity;
            for (var cut = 0; cut < _model.Cuts.Count; cut++)
            {
                foreach (var edit in _edits)
                {
                    var distance = edit.NearestInside(cut, centre);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = cut;
                    }
                }
            }

            return best;
        }

        // Clicking and boxing, or painting, depending on the mode.
        private void HandleSelection(Rect rect, Event current)
        {
            var mouse = current.mousePosition;
            var overUi = InUi(mouse);
            if (!_editMode)
            {
                if (IsStaticTarget)
                {
                    HandleStaticObjectPick(rect, current, overUi);
                }
                else
                {
                    HandleObjectPick(rect, current, overUi);
                }

                return;
            }

            // A piece of clothing is set up in its own card; the tools edit body meshes only.
            if (_model.EditsNothing)
            {
                return;
            }

            if (_paint)
            {
                HandlePaint(rect, current, overUi);
            }
            else
            {
                HandleClickAndBox(rect, current, overUi);
            }
        }

        private void HandlePaint(Rect rect, Event current, bool overUi)
        {
            switch (current.type)
            {
                case EventType.MouseDown:
                    if (current.button == 0 && rect.Contains(current.mousePosition) && !overUi)
                    {
                        GUIUtility.keyboardControl = 0;
                        _painting = true;
                        _strokeBefore = CaptureAll();
                        _strokeKey = VersionKey();
                        _paintLast = current.mousePosition;
                        PaintAt(rect, current.mousePosition, current.mousePosition, current.shift);
                        current.Use();
                    }

                    break;
                case EventType.MouseDrag:
                    if (_painting)
                    {
                        PaintAt(rect, _paintLast, current.mousePosition, current.shift);
                        _paintLast = current.mousePosition;
                        current.Use();
                    }

                    break;
                case EventType.MouseUp:
                    if (_painting && current.button == 0)
                    {
                        _painting = false;
                        if (VersionKey() != _strokeKey)
                        {
                            PushUndo(_strokeBefore);
                        }

                        _strokeBefore = null;
                        current.Use();
                    }

                    break;
            }
        }

        private void PaintAt(Rect rect, Vector2 from, Vector2 to, bool subtract)
        {
            CollectEdits();
            GoreStudioSelection.Paint(_edits, _tool, _view, rect, from, to, _brushRadius, subtract);
            RefreshSelectionStats();
            Repaint();
        }

        // A click picks, a drag makes a box, Alt + click an edge loop. Ctrl toggles or adds, Shift takes out.
        private void HandleClickAndBox(Rect rect, Event current, bool overUi)
        {
            switch (current.type)
            {
                case EventType.MouseDown:
                    if (current.button == 0 && rect.Contains(current.mousePosition) && !overUi)
                    {
                        GUIUtility.keyboardControl = 0;
                        _selectionDown = true;
                        _selectionBox = false;
                        _selectionStart = current.mousePosition;
                        _selectionNow = current.mousePosition;
                        current.Use();
                    }

                    break;
                case EventType.MouseDrag:
                    if (_selectionDown)
                    {
                        _selectionNow = current.mousePosition;
                        if (!_selectionBox && (_selectionNow - _selectionStart).sqrMagnitude > DragThreshold * DragThreshold)
                        {
                            _selectionBox = true;
                        }

                        current.Use();
                        Repaint();
                    }

                    break;
                case EventType.MouseUp:
                    if (_selectionDown && current.button == 0)
                    {
                        var wasBox = _selectionBox;
                        _selectionDown = false;
                        _selectionBox = false;
                        CollectEdits();
                        var mouse = current.mousePosition;
                        var shift = current.shift;
                        var control = current.control;
                        var alt = current.alt;
                        TrackedChange(() =>
                        {
                            if (wasBox)
                            {
                                var box = Rect.MinMaxRect(Mathf.Min(_selectionStart.x, mouse.x), Mathf.Min(_selectionStart.y, mouse.y), Mathf.Max(_selectionStart.x, mouse.x), Mathf.Max(_selectionStart.y, mouse.y));
                                GoreStudioSelection.Box(_edits, _tool, _view, rect, box, shift ? GoreSelectOp.Subtract : control ? GoreSelectOp.Add : GoreSelectOp.Replace);
                            }
                            else
                            {
                                var op = shift ? GoreSelectOp.Subtract : control ? (alt ? GoreSelectOp.Add : GoreSelectOp.Toggle) : GoreSelectOp.Replace;
                                GoreStudioSelection.Click(_edits, _tool, _view, rect, mouse, alt, op);
                            }

                            RefreshSelectionStats();
                        });
                        current.Use();
                        Repaint();
                    }

                    break;
            }
        }

        #endregion

        #region Objects

        // The meshes the tools work on: none in object mode (their selections stay as they are), else the picked body mesh or all the shown ones.
        private void CollectEdits()
        {
            if (_editMode && IsStaticTarget)
            {
                _edits.Clear();
                if (_static.Edit != null && !_static.Busy)
                {
                    _edits.Add(_static.Edit);
                }
            }
            else if (_editMode)
            {
                _model.ActiveEdits(_edits);
            }
            else
            {
                _edits.Clear();
            }
        }

        // Picks a body mesh (<paramref name="row"/>) or a piece of clothing (<paramref name="cloth"/>), or neither (-1, -1): the old selection is let go of.
        private void SelectObject(int row, int cloth)
        {
            _model.SelectedRow = row;
            _model.SelectedCloth = cloth;
            _model.Recolor();
            _selectionKey = -1;
            _faceKey = -1;
            Repaint();
        }

        // A click without a drag picks the object under the mouse; a click on nothing lets go. The mouse moving over the preview finds the object under it.
        private void HandleObjectPick(Rect rect, Event current, bool overUi)
        {
            var mouse = current.mousePosition;
            switch (current.type)
            {
                case EventType.MouseMove:
                    UpdateHover(rect, mouse, overUi);
                    break;
                case EventType.MouseLeaveWindow:
                    _hoverValid = false;
                    if (_hoverRow >= 0 || _hoverCloth >= 0)
                    {
                        _hoverRow = -1;
                        _hoverCloth = -1;
                        Repaint();
                    }

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
                        PickObject(rect, mouse, out var row, out var cloth);

                        // A click on nothing lets go of the chosen body part too, as if its colour was clicked again.
                        var letGo = row < 0 && cloth < 0;
                        Defer(() =>
                        {
                            if (letGo)
                            {
                                _cutManual = false;
                                _model.SelectedCut = -1;
                            }

                            SelectObject(row, cloth);
                        });
                        current.Use();
                    }

                    break;
            }
        }

        // Works out the object under the mouse, but only when the mouse moved a few pixels (the test goes over every triangle).
        private void UpdateHover(Rect rect, Vector2 mouse, bool overUi)
        {
            if (overUi || !rect.Contains(mouse))
            {
                _hoverValid = false;
                if (_hoverRow >= 0 || _hoverCloth >= 0)
                {
                    _hoverRow = -1;
                    _hoverCloth = -1;
                    Repaint();
                }

                return;
            }

            if (_hoverValid && (mouse - _hoverTested).sqrMagnitude <= HoverMoveThreshold * HoverMoveThreshold)
            {
                return;
            }

            _hoverTested = mouse;
            _hoverValid = true;
            if (PickTriangleCount() > PickTriangleLimit)
            {
                return;
            }

            PickObject(rect, mouse, out var row, out var cloth);
            if (row != _hoverRow || cloth != _hoverCloth)
            {
                _hoverRow = row;
                _hoverCloth = cloth;
                Repaint();
            }
        }

        // How many triangles a pick goes over: the cut and shown body meshes, and the shown clothes.
        private int PickTriangleCount()
        {
            var total = 0;
            foreach (var row in _model.Rows)
            {
                if (row.Cut && BodyShown(row))
                {
                    total += row.TriangleVertices.Length / 3;
                }
            }

            foreach (var cloth in _model.Cloth)
            {
                if (cloth.Show)
                {
                    total += cloth.SoupWorld.Length / 3;
                }
            }

            return total;
        }

        // Sends a ray through the mouse and finds the nearest triangle of a shown body mesh (as it is posed) or of a shown piece of clothing.
        // No allocation: it reads the arrays the analysis already holds.
        private void PickObject(Rect rect, Vector2 mouse, out int pickedRow, out int pickedCloth)
        {
            var ray = _view.ScreenRay(rect, mouse);
            var origin = ray.origin;
            var direction = ray.direction;
            var best = float.PositiveInfinity;
            pickedRow = -1;
            pickedCloth = -1;
            for (var r = 0; r < _model.Rows.Count; r++)
            {
                var row = _model.Rows[r];
                if (!row.Cut || !BodyShown(row))
                {
                    continue;
                }

                var world = row.World;
                var indices = row.TriangleVertices;
                for (var i = 0; i + 2 < indices.Length; i += 3)
                {
                    if (RayTriangle(origin, direction, world[indices[i]], world[indices[i + 1]], world[indices[i + 2]], out var distance) && distance < best)
                    {
                        best = distance;
                        pickedRow = r;
                        pickedCloth = -1;
                    }
                }
            }

            for (var c = 0; c < _model.Cloth.Count; c++)
            {
                var cloth = _model.Cloth[c];
                if (!cloth.Show)
                {
                    continue;
                }

                var corners = cloth.SoupWorld;
                for (var i = 0; i + 2 < corners.Length; i += 3)
                {
                    if (RayTriangle(origin, direction, corners[i], corners[i + 1], corners[i + 2], out var distance) && distance < best)
                    {
                        best = distance;
                        pickedRow = -1;
                        pickedCloth = c;
                    }
                }
            }
        }

        // Moeller-Trumbore, both faces of the triangle count; the distance is along the (unit) direction.
        private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0f;
            var edge1 = b - a;
            var edge2 = c - a;
            var p = Vector3.Cross(direction, edge2);
            var determinant = Vector3.Dot(edge1, p);
            if (determinant > -1e-12f && determinant < 1e-12f)
            {
                return false;
            }

            var inverse = 1f / determinant;
            var offset = origin - a;
            var u = Vector3.Dot(offset, p) * inverse;
            if (u < 0f || u > 1f)
            {
                return false;
            }

            var q = Vector3.Cross(offset, edge1);
            var v = Vector3.Dot(direction, q) * inverse;
            if (v < 0f || u + v > 1f)
            {
                return false;
            }

            distance = Vector3.Dot(edge2, q) * inverse;
            return distance > 0f;
        }

        // The name of a picked body mesh, or the name of a piece of clothing and what it does now; empty when there is none.
        private string DescribeObject(int row, int cloth)
        {
            if (row >= 0 && row < _model.Rows.Count)
            {
                return _model.Rows[row].Label.text;
            }

            if (cloth >= 0 && cloth < _model.Cloth.Count)
            {
                var piece = _model.Cloth[cloth];
                var result = piece.Result;
                if (string.IsNullOrEmpty(result))
                {
                    return piece.Label.text;
                }

                return piece.Label.text + " - " + (result.Length > 60 ? result.Substring(0, 57) + "..." : result);
            }

            return string.Empty;
        }

        // Object mode: the name of the object under the mouse and of the picked one, at the top of the preview right of the toolbar.
        private void DrawObjectLabel(Rect rect)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            if (IsStaticTarget)
            {
                DrawStaticLabel(rect);
                return;
            }

            var hover = _editMode ? string.Empty : DescribeObject(_hoverRow, _hoverCloth);
            var picked = DescribeObject(_model.HasSelectedRow ? _model.SelectedRow : -1, _model.HasSelectedCloth ? _model.SelectedCloth : -1);
            var lines = 0;
            if (_editMode)
            {
                // What the tools work on, so it is clear after the picking was let go of.
                var target = _model.EditsNothing ? string.Empty : _model.HasSelectedRow ? _model.Rows[_model.SelectedRow].Label.text : _model.EditRow >= 0 && _model.EditRow < _model.Rows.Count ? _model.Rows[_model.EditRow].Label.text : "all body meshes";
                if (target.Length > 0)
                {
                    LabelLines[lines++] = "Editing: " + target;
                }
            }
            else
            {
                if (hover.Length > 0 && hover != picked)
                {
                    LabelLines[lines++] = "Under mouse: " + hover;
                }

                if (picked.Length > 0)
                {
                    LabelLines[lines++] = "Picked: " + picked;
                }
            }

            DrawLabelBox(rect, lines);
        }

        // The box with the lines of LabelLines at the top of the preview, right of the toolbar.
        private void DrawLabelBox(Rect rect, int lines)
        {
            if (lines == 0)
            {
                return;
            }

            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
                _labelStyle.normal.textColor = Color.white;
            }

            var x = _toolbarRight + 10f;
            var y = rect.y + 8f;
            if (rect.xMax - x < 180f)
            {
                // Not enough room beside the toolbar: under it.
                x = rect.x + 8f;
                y += ButtonHeight + 6f;
            }

            var width = 0f;
            for (var i = 0; i < lines; i++)
            {
                LabelContent.text = LabelLines[i];
                width = Mathf.Max(width, _labelStyle.CalcSize(LabelContent).x);
            }

            var box = new Rect(x, y, Mathf.Min(width + 14f, rect.xMax - x - 8f), lines * 16f + 6f);
            EditorGUI.DrawRect(box, HudBackground);
            for (var i = 0; i < lines; i++)
            {
                GUI.Label(new Rect(box.x + 7f, box.y + 3f + i * 16f, box.width - 10f, 16f), LabelLines[i], _labelStyle);
            }
        }

        // Edit mode with a piece of clothing picked: the tools have nothing to edit, so say where to go instead.
        private void DrawClothNotice(Rect rect)
        {
            var box = new Rect(rect.center.x - 215f, rect.yMax - 16f - 38f, 430f, 38f);
            _uiRects.Add(box);
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            if (_noticeStyle == null)
            {
                _noticeStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, alignment = TextAnchor.MiddleCenter };
                _noticeStyle.normal.textColor = Color.white;
            }

            EditorGUI.DrawRect(box, HudBackground);
            GUI.Label(new Rect(box.x + 8f, box.y, box.width - 16f, box.height), ClothNotice, _noticeStyle);
        }

        #endregion
    }
}