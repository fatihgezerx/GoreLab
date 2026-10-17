using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GoreLab
{
    // Dressing the naked character in the preview and the Cloth parts card: every piece of clothing that was put on is listed, can be shown on its
    // own, and is set up there (cut along the body, whole with a part, or left on the body; following its part or flying off).
    // The list and the settings are drawn with fixed columns (see GoreGUI.Row, TakeLeft, TakeRight) so every row lines up at any panel width.
    internal sealed partial class GoreStudioWindow
    {
        private static readonly GUIContent[] BodySideChoices =
        {
            new GUIContent("Follow", "The piece on the body side stays with the body."),
            new GUIContent("Fly", "The piece on the body side flies off too, on its own.")
        };

        private static readonly GUIContent[] PartSideChoices =
        {
            new GUIContent("Follow", "The piece on the part side goes with the part that is cut off."),
            new GUIContent("Fly", "The piece on the part side flies off on its own.")
        };

        private static readonly GUIContent[] FollowOrFly =
        {
            new GUIContent("Follow", "The piece goes with its part when the part is cut off (on the body it just stays)."),
            new GUIContent("Fly", "The piece flies off on its own when its part is cut off.")
        };

        private static readonly GUIContent ClothSwatchTip = new GUIContent(string.Empty, "Select this piece to set it up; the rest of the character is dimmed.");
        private static readonly GUIContent ClothShowTip = new GUIContent(string.Empty, "Show or hide this piece in the preview only.");
        private static readonly GUIContent ReadableLabel = new GUIContent("R/W", "Read/Write is on for its mesh. Cutting the piece needs it.");
        private static readonly GUIContent UnreadableLabel = new GUIContent("no R/W", "Read/Write is off for its mesh, so the piece cannot be cut.");
        private static readonly GUIContent CutLabel = new GUIContent("Cut", "Cut the piece along the body parts (a sleeve at the elbow): its mesh is divided there, so there are gaps. Needs Read/Write on its mesh. Not ticked: the piece is not cut and goes whole with the part it hangs on.");
        private static readonly GUIContent DivideLabel = new GUIContent("Divide", "This mesh holds separate pieces that hang on different parts (a left and a right leg). Divide it so that each piece gets its own settings.");
        private static readonly GUIContent PieceCutLabel = new GUIContent("Cut", "Cut this piece along the body parts (needs Read/Write). Not ticked: it goes whole with a part.");
        private static readonly GUIContent BodySideLabel = new GUIContent("Body side", "What the piece on the body side of a cut does: Follow = it stays with the body, Fly = it flies off too.");
        private static readonly GUIContent PartSideLabel = new GUIContent("Part side", "What the piece on the side of the part that is cut off does: Follow = it goes with the part, Fly = it flies off on its own.");
        private static readonly GUIContent GoesWithLabel = new GUIContent("Goes with", "The part the piece goes with. Auto is the part it hangs on; choose another part when Auto is wrong.");
        private static readonly GUIContent FollowFlyTip = new GUIContent(string.Empty, "Follow: the piece goes with its part. Fly: it flies off on its own. (On the body a piece just stays.)");
        private static readonly GUIContent[] PieceLabels = MakePieceLabels();
        private static readonly GUIContent BodyChoice = new GUIContent("Body", "The piece stays on the body.");
        private static readonly GUIContent UndressLabel = new GUIContent("Undress", "Takes all the clothes off the character in the preview. Their settings are kept.");
        private static readonly GUIContent MakeReadableLabel = new GUIContent("Make readable", "Ticks Read/Write in the import settings of its model and reimports it.");

        private const string PiecesNote = "Each piece is cut, or goes whole with its own part.";
        private const int MaxListedPieces = 24;
        private const float SettingsLabelWidth = 62f;
        private const float SideWidth = 84f;
        private const float FlyWidth = 62f;
        private const float PieceNameWidth = 56f;
        private const float PieceIndent = 12f;
        private const float StatusWidth = 46f;
        private const float SideCaptionWidth = 50f;
        private const float SidePopupWidth = 60f;

        // True after Dress: the same character loaded again (after Apply) is dressed again.
        private bool _dressed;

        // The entries of the "Goes with" popup, kept until the cuts change (rebuilding them for every row of every repaint would allocate for nothing).
        private GUIContent[] _ownerChoices = Array.Empty<GUIContent>();
        private string[] _ownerSource = Array.Empty<string>();
        private readonly Dictionary<string, GUIContent> _autoChoices = new Dictionary<string, GUIContent>(16);

        private static GUIContent[] MakePieceLabels()
        {
            var labels = new GUIContent[MaxListedPieces];
            for (var i = 0; i < labels.Length; i++)
            {
                labels[i] = new GUIContent("Piece " + (i + 1), "Press to show only this piece in the preview.");
            }

            return labels;
        }

        // Puts everything that was dropped in the Worn equipment card on the naked character.
        private void DressWorn()
        {
            if (!_model.HasTarget || _model.Kind != GoreMeshKind.Skinned)
            {
                return;
            }

            _model.Undress();
            var pieces = 0;
            foreach (var prefab in _wornPrefabs)
            {
                if (prefab == null)
                {
                    continue;
                }

                var root = Instantiate(prefab);
                root.name = prefab.name;
                root.hideFlags = HideFlags.HideAndDontSave;
                SilenceInstance(root);
                _preview.AddSingleGO(root);
                var added = _model.Dress(root);
                if (added == 0)
                {
                    DestroyImmediate(root);
                }

                pieces += added;
            }

            if (pieces > 0)
            {
                PrepareLooks();
            }

            _dressed = pieces > 0;
            _message = pieces > 0 ? "Dressed: " + GoreGUI.Plural(pieces, "piece", "pieces") + ". Set each one up in Cloth parts." : "Nothing to put on: the dropped assets have no active skinned mesh.";
            _messageType = MessageType.Info;
            Repaint();
        }

        // The dropped assets that know how the character looks in the game (see IGoreLookPreview) finish the dressing: skins on the naked body, materials
        // on the clothes, naked parts the clothes hide. A failure in one of them is logged and does not stop the rest.
        private void PrepareLooks()
        {
            var items = new List<GameObject>(_model.Cloth.Count);
            foreach (var cloth in _model.Cloth)
            {
                if (cloth.Root != null && !items.Contains(cloth.Root))
                {
                    items.Add(cloth.Root);
                }
            }

            _model.RememberBody();
            var hidden = new HashSet<Renderer>();
            foreach (var asset in _worn)
            {
                if (asset is GoreLab.IGoreLookPreview look)
                {
                    try
                    {
                        look.PreparePreview(_model.Instance, items, hidden);
                    }
                    catch (System.Exception exception)
                    {
                        Debug.LogException(exception, asset);
                    }
                }
            }

            _model.HideBodyMeshes(hidden);
        }

        private void DrawCloth()
        {
            GoreGUI.BeginCard("Cloth parts");
            if (_model.Cloth.Count == 0)
            {
                GoreGUI.Note("Nothing is dressed yet. Drop the clothes into Worn equipment below, then press Dress.");
                GoreGUI.EndCard();
                return;
            }

            GoreGUI.Note("Click a piece to show it alone and set it up.");

            var header = GoreGUI.Row(16f);
            GoreGUI.TakeLeft(ref header, 16f);
            GoreGUI.ColumnHeader(GoreGUI.TakeLeft(ref header, GoreGUI.CheckColumn), "Show", "Show or hide the piece in the preview (the preview only).", TextAnchor.MiddleCenter);
            GoreGUI.ColumnHeader(GoreGUI.TakeRight(ref header, StatusWidth), "R/W", "Read/Write of the piece's mesh. Cutting the piece needs it.", TextAnchor.MiddleRight);
            GoreGUI.ColumnHeader(header, "Piece", "One piece of clothing that was put on.", TextAnchor.MiddleLeft);

            var showChanged = false;
            for (var i = 0; i < _model.Cloth.Count; i++)
            {
                var cloth = _model.Cloth[i];
                var selected = i == _model.SelectedCloth;
                var line = GoreGUI.Row();
                if (selected)
                {
                    GoreGUI.Highlight(line);
                }

                var rest = line;
                var swatch = GoreGUI.TakeLeft(ref rest, 16f);
                var show = GoreGUI.TakeLeft(ref rest, GoreGUI.CheckColumn);
                var status = GoreGUI.TakeRight(ref rest, StatusWidth);

                // The colour selects the piece too, like the colour of a body part does.
                if (GoreGUI.Swatch(swatch, _model.SegmentColor(cloth.Majority), selected, ClothSwatchTip))
                {
                    SelectCloth(i);
                }

                EditorGUI.BeginChangeCheck();
                var shown = GoreGUI.CheckBox(show, cloth.Show, ClothShowTip);
                if (EditorGUI.EndChangeCheck())
                {
                    cloth.Show = shown;
                    showChanged = true;
                }

                EditorGUIUtility.AddCursorRect(rest, MouseCursor.Link);
                if (GUI.Button(rest, cloth.Label, selected ? EditorStyles.boldLabel : EditorStyles.label))
                {
                    SelectCloth(i);
                }

                var previous = GUI.contentColor;
                if (!cloth.Readable)
                {
                    GUI.contentColor = GoreGUI.Caution;
                }

                GoreGUI.Detail(status, cloth.Readable ? ReadableLabel : UnreadableLabel);
                GUI.contentColor = previous;

                if (selected)
                {
                    DrawClothSettings(cloth);
                }
            }

            if (showChanged)
            {
                Defer(ApplyVisibility);
            }

            GUILayout.Space(2f);
            if (GUILayout.Button(UndressLabel))
            {
                Defer(() =>
                {
                    _model.Undress();
                    _dressed = false;
                    _model.Recolor();
                });
            }

            GoreGUI.EndCard();
        }

        // Selects a piece (or clears the selection when it already is the one): the rest of the character is dimmed.
        private void SelectCloth(int index)
        {
            Defer(() => SelectObject(-1, _model.SelectedCloth == index ? -1 : index));
        }

        private void DrawClothSettings(GoreStudioCloth cloth)
        {
            var rule = cloth.Rule;
            GoreGUI.BeginInset();
            EditorGUI.BeginChangeCheck();

            var cut = GUI.Toggle(ToggleRect(GoreGUI.Row(), 70f), rule.Mode == GoreClothMode.Split, CutLabel);
            var divide = rule.Mode == GoreClothMode.Pieces;
            var owner = rule.Owner;
            var flies = rule.Flies;
            var bodyFlies = rule.BodyFlies;

            if (rule.Mode == GoreClothMode.Split)
            {
                bodyFlies = SideRow(BodySideLabel, BodySideChoices, bodyFlies);
                flies = SideRow(PartSideLabel, PartSideChoices, flies);
            }
            else
            {
                if (rule.Mode == GoreClothMode.Pieces || cloth.CanDivide)
                {
                    divide = GUI.Toggle(ToggleRect(GoreGUI.Row(), 70f), divide, DivideLabel);
                }

                if (rule.Mode == GoreClothMode.Pieces && cloth.PieceCount > 0)
                {
                    DrawPieceRows(cloth, rule);
                }
                else
                {
                    var line = GoreGUI.Row();
                    GUI.Label(GoreGUI.TakeLeft(ref line, SettingsLabelWidth), GoesWithLabel);
                    owner = OwnerPopups(line, owner, cloth.AutoOwner, ref flies);
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                var mode = cut ? GoreClothMode.Split : divide && (cloth.CanDivide || rule.Mode == GoreClothMode.Pieces) ? GoreClothMode.Pieces : GoreClothMode.Whole;
                if (mode != rule.Mode)
                {
                    // A new kind of setting starts clean.
                    cloth.SelectedPiece = -1;
                    if (mode == GoreClothMode.Split)
                    {
                        flies = false;
                        bodyFlies = false;
                    }
                    else if (mode == GoreClothMode.Whole)
                    {
                        owner = null;
                        flies = false;
                    }
                    else
                    {
                        rule.PieceOwners = Array.Empty<string>();
                        rule.PieceFlies = Array.Empty<bool>();
                        rule.PieceCut = Array.Empty<bool>();
                        rule.PieceBodyFlies = Array.Empty<bool>();
                    }
                }

                rule.Mode = mode;
                rule.Owner = owner;
                rule.Flies = flies;
                rule.BodyFlies = bodyFlies;
                Defer(() => _model.RefreshCloth());
            }

            var pieces = rule.Mode == GoreClothMode.Pieces && cloth.PieceCount > 0;
            var note = pieces ? PiecesNote : cloth.Result;
            if (!string.IsNullOrEmpty(note))
            {
                GoreGUI.Note(note);
            }

            if (rule.Mode == GoreClothMode.Split && !cloth.Readable)
            {
                GoreGUI.Help("Its mesh is not readable, so it cannot be cut. Switch Read/Write on in the import settings of its model, or untick Cut (that needs none).", MessageType.Warning);
                var model = FindWornMesh(cloth.Renderer.sharedMesh);
                if (model != null && model.Importer != null && GUILayout.Button(MakeReadableLabel))
                {
                    Defer(() => MakeOneReadable(model));
                }
            }

            if (cloth.Missing > 0)
            {
                GoreGUI.Note(GoreGUI.Plural(cloth.Missing, "bone is", "bones are") + " missing on the character, so the piece may not follow the animation.");
            }

            GoreGUI.EndInset();
        }

        // The pieces of a divided mesh: two lines for each. The first has its name (press it to show only that piece) and its Cut tick box; the second
        // is either the part it goes with and Follow / Fly, or, when it is cut, Body side and Part side. Edits go straight into the rule's arrays.
        private void DrawPieceRows(GoreStudioCloth cloth, GoreStudioClothRule rule)
        {
            var total = cloth.PieceCount;
            var listed = 0;
            for (var piece = 0; piece < total; piece++)
            {
                if (cloth.PieceVertices[piece] == 0)
                {
                    continue;
                }

                if (++listed > MaxListedPieces)
                {
                    continue;
                }

                var owner = piece < rule.PieceOwners.Length ? rule.PieceOwners[piece] : null;
                var flies = piece < rule.PieceFlies.Length && rule.PieceFlies[piece];
                var pieceCut = piece < rule.PieceCut.Length && rule.PieceCut[piece];
                var bodyFlies = piece < rule.PieceBodyFlies.Length && rule.PieceBodyFlies[piece];
                var selected = cloth.SelectedPiece == piece;

                GUILayout.Space(2f);
                EditorGUI.BeginChangeCheck();
                var first = GoreGUI.Row();
                if (selected)
                {
                    GoreGUI.Highlight(first);
                }

                var rest = first;
                var nameRect = GoreGUI.TakeLeft(ref rest, PieceNameWidth);
                EditorGUIUtility.AddCursorRect(nameRect, MouseCursor.Link);
                var pressed = GUI.Button(nameRect, PieceLabels[listed - 1], selected ? EditorStyles.boldLabel : EditorStyles.label);
                pieceCut = GUI.Toggle(ToggleRect(rest, 60f), pieceCut, PieceCutLabel);

                var second = GoreGUI.Row();
                second.xMin += PieceIndent;
                if (pieceCut)
                {
                    GoreGUI.Caption(GoreGUI.TakeLeft(ref second, SideCaptionWidth), "Body side");
                    bodyFlies = FlyPopup(GoreGUI.TakeLeft(ref second, SidePopupWidth), bodyFlies, BodySideChoices, BodySideLabel);
                    GoreGUI.Caption(GoreGUI.TakeLeft(ref second, SideCaptionWidth), "Part side");
                    flies = FlyPopup(GoreGUI.TakeLeft(ref second, SidePopupWidth), flies, PartSideChoices, PartSideLabel);
                }
                else
                {
                    owner = OwnerPopups(second, owner, cloth.PieceAuto[piece], ref flies);
                }

                if (EditorGUI.EndChangeCheck())
                {
                    rule.PieceOwners = Resized(rule.PieceOwners, total);
                    rule.PieceFlies = Resized(rule.PieceFlies, total);
                    rule.PieceCut = Resized(rule.PieceCut, total);
                    rule.PieceBodyFlies = Resized(rule.PieceBodyFlies, total);
                    rule.PieceOwners[piece] = owner;
                    rule.PieceFlies[piece] = flies;
                    rule.PieceCut[piece] = pieceCut;
                    rule.PieceBodyFlies[piece] = bodyFlies;
                }

                if (pressed)
                {
                    var number = piece;
                    Defer(() =>
                    {
                        cloth.SelectedPiece = cloth.SelectedPiece == number ? -1 : number;
                        _model.Recolor();
                    });
                }
            }

            if (listed > MaxListedPieces)
            {
                GoreGUI.Note("and " + (listed - MaxListedPieces) + " more pieces, left to their bone weights.");
            }
        }

        // The array as long as the mesh has pieces (a new one when it was shorter, so what the user did not touch keeps its default).
        private static T[] Resized<T>(T[] source, int length)
        {
            if (source.Length == length)
            {
                return source;
            }

            var copy = new T[length];
            Array.Copy(source, copy, Math.Min(source.Length, length));
            return copy;
        }

        // A check box with its label, in a rectangle no wider than it needs (so a click beside it does not tick it).
        private static Rect ToggleRect(Rect row, float width) => new Rect(row.x, row.y, Mathf.Min(width, row.width), row.height);

        // A label and one Follow / Fly popup (Body side, Part side). Returns true for Fly.
        private static bool SideRow(GUIContent label, GUIContent[] choices, bool flies)
        {
            var line = GoreGUI.Row();
            GUI.Label(GoreGUI.TakeLeft(ref line, SettingsLabelWidth), label);
            var popup = new Rect(line.x, line.y, Mathf.Min(SideWidth, line.width), line.height);
            var result = EditorGUI.Popup(popup, flies ? 1 : 0, choices) == 1;
            GoreGUI.Tip(popup, label);
            return result;
        }

        private static bool FlyPopup(Rect rect, bool flies, GUIContent[] choices, GUIContent tip)
        {
            var result = EditorGUI.Popup(rect, flies ? 1 : 0, choices) == 1;
            GoreGUI.Tip(rect, tip);
            return result;
        }

        // The "Goes with" popup and, at its right, Follow / Fly (greyed on the body: a piece that stays does not fly). Auto shows the part it chose, any
        // other part can be picked. Returns the owner: null = Auto, empty = the body, or a cut's name.
        private string OwnerPopups(Rect rest, string owner, string auto, ref bool flies)
        {
            var flyRect = GoreGUI.TakeRight(ref rest, FlyWidth);
            var choices = OwnerChoices(auto);
            var current = owner == null ? 0 : owner.Length == 0 ? 1 : 0;
            for (var c = 0; c < _model.Cuts.Count; c++)
            {
                if (owner != null && owner.Length > 0 && string.Equals(_model.Cuts[c].Name, owner, StringComparison.Ordinal))
                {
                    current = c + 2;
                }
            }

            var picked = EditorGUI.Popup(rest, current, choices);
            GoreGUI.Tip(rest, choices[current]);
            var result = picked == 0 ? null : picked == 1 ? string.Empty : _model.Cuts[picked - 2].Name;
            var resolved = result ?? auto;
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(resolved)))
            {
                flies = EditorGUI.Popup(flyRect, flies ? 1 : 0, FollowOrFly) == 1;
            }

            GoreGUI.Tip(flyRect, FollowFlyTip);
            return result;
        }

        // Auto, Body, then every cut. The array is shared; its first entry is set for the row being drawn.
        private GUIContent[] OwnerChoices(string auto)
        {
            var count = _model.Cuts.Count;
            var stale = _ownerChoices.Length != count + 2;
            for (var c = 0; !stale && c < count; c++)
            {
                stale = !string.Equals(_ownerSource[c], _model.Cuts[c].Name, StringComparison.Ordinal);
            }

            if (stale)
            {
                _ownerChoices = new GUIContent[count + 2];
                _ownerSource = new string[count];
                _ownerChoices[1] = BodyChoice;
                for (var c = 0; c < count; c++)
                {
                    _ownerSource[c] = _model.Cuts[c].Name;

                    // A slash would make a submenu of the popup.
                    var text = _model.Cuts[c].Name.Replace('/', '-');
                    _ownerChoices[c + 2] = new GUIContent(text, text);
                }

                _autoChoices.Clear();
            }

            var key = auto ?? string.Empty;
            if (!_autoChoices.TryGetValue(key, out var content))
            {
                var text = "Auto: " + (key.Length == 0 ? "Body" : key.Replace('/', '-'));
                content = new GUIContent(text, text + ". Pick another part below when this is wrong.");
                _autoChoices.Add(key, content);
            }

            _ownerChoices[0] = content;
            return _ownerChoices;
        }

        private GoreWornMesh FindWornMesh(Mesh mesh)
        {
            foreach (var worn in _wornMeshes)
            {
                if (worn.Mesh == mesh)
                {
                    return worn;
                }
            }

            return null;
        }

        // Switches Read/Write on for one model and puts the clothes on again, so the pieces are the reimported ones.
        private void MakeOneReadable(GoreWornMesh worn)
        {
            var changed = GoreWornScan.MakeReadable(new List<GoreWornMesh> { worn });
            ScanWorn();
            if (changed > 0)
            {
                DressWorn();
            }
        }
    }
}
