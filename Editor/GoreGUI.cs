using UnityEditor;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// The look of the GoreLab windows and inspectors: a banner (title plus a one-line state in a colored box), titled cards,
    /// a few text helpers and the small pieces the rows of the Gore Studio panel are built from (fixed columns, swatches, check boxes,
    /// headings, an inset for the settings of one row). Everything here must be called from inside an OnGUI / OnInspectorGUI.
    /// </summary>
    internal static class GoreGUI
    {
        /// <summary>The color of the accent bars and of neutral states (the GoreLab brand magenta-red, a little lighter than #C2185B so it reads on the dark skin).</summary>
        internal static readonly Color Accent = new Color(0.88f, 0.25f, 0.48f);

        /// <summary>The color of a state that is fine.</summary>
        internal static readonly Color Good = new Color(0.36f, 0.80f, 0.52f);

        /// <summary>The color of a state that needs attention.</summary>
        internal static readonly Color Caution = new Color(1f, 0.74f, 0.28f);

        /// <summary>The color of a state that is broken.</summary>
        internal static readonly Color Bad = new Color(0.95f, 0.42f, 0.42f);

        /// <summary>The height of one row of a list.</summary>
        internal const float RowHeight = 18f;

        /// <summary>The width of a column that holds only a check box (its header sits above it).</summary>
        internal const float CheckColumn = 34f;

        /// <summary>The space between two columns of a row.</summary>
        internal const float ColumnGap = 4f;

        private static GUIStyle _title;
        private static GUIStyle _state;
        private static GUIStyle _section;
        private static GUIStyle _subsection;
        private static GUIStyle _note;
        private static GUIStyle _caption;
        private static GUIStyle _headerLeft;
        private static GUIStyle _headerCenter;
        private static GUIStyle _headerRight;
        private static GUIStyle _cardPadding;
        private static GUIStyle _pagePadding;
        private static GUIStyle _insetPadding;

        /// <summary>
        /// The banner at the top: a <paramref name="title"/> and, under it, one line of <paramref name="state"/> tinted with
        /// <paramref name="stateColor"/> (the accent bar at the left has the same color).
        /// </summary>
        internal static void Banner(string title, string state, Color stateColor)
        {
            EnsureStyles();
            var rect = GUILayoutUtility.GetRect(0f, 54f, GUILayout.ExpandWidth(true));
            Rounded(rect, EditorGUIUtility.isProSkin ? new Color(0.25f, 0.13f, 0.19f) : new Color(0.96f, 0.80f, 0.87f), 8f);
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(rect.x + 8f, rect.y + 11f, 3f, 32f), stateColor);
            }

            GUI.Label(new Rect(rect.x + 18f, rect.y + 7f, rect.width - 28f, 22f), title, _title);
            var previous = GUI.contentColor;
            GUI.contentColor = EditorGUIUtility.isProSkin ? Color.Lerp(stateColor, Color.white, 0.35f) : Color.Lerp(stateColor, Color.black, 0.45f);
            GUI.Label(new Rect(rect.x + 18f, rect.y + 29f, rect.width - 28f, 16f), state, _state);
            GUI.contentColor = previous;
        }

        /// <summary>Starts a card: a box with a bold <paramref name="title"/>. Returns the title row, so a button can sit at its right end. Close it with <see cref="EndCard"/>.</summary>
        internal static Rect BeginCard(string title)
        {
            EnsureStyles();
            var rect = EditorGUILayout.BeginVertical(_cardPadding);
            var border = EditorGUIUtility.isProSkin ? new Color(0.12f, 0.12f, 0.13f) : new Color(0.60f, 0.60f, 0.62f);
            var fill = EditorGUIUtility.isProSkin ? new Color(0.265f, 0.265f, 0.275f) : new Color(0.88f, 0.88f, 0.89f);
            Rounded(rect, border, 7f);
            Rounded(new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f), fill, 6f);

            var row = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(row.x, row.y + 4f, 3f, 14f), Accent);
            }

            GUI.Label(new Rect(row.x + 9f, row.y, row.width - 9f, row.height), title, _section);
            return row;
        }

        /// <summary>Ends a card started with <see cref="BeginCard"/>.</summary>
        internal static void EndCard() => EditorGUILayout.EndVertical();

        /// <summary>Starts the padded page of a window (the inspectors do not need it). Close it with <see cref="EndPage"/>.</summary>
        internal static void BeginPage()
        {
            EnsureStyles();
            EditorGUILayout.BeginVertical(_pagePadding);
        }

        /// <summary>Ends the page started with <see cref="BeginPage"/>.</summary>
        internal static void EndPage() => EditorGUILayout.EndVertical();

        /// <summary>A Unity HelpBox with the message and its icon (info, warning or error).</summary>
        internal static void Help(string message, MessageType type) => EditorGUILayout.HelpBox(message, type);

        /// <summary>A small muted line of explanation that wraps.</summary>
        internal static void Note(string text)
        {
            EnsureStyles();
            GUILayout.Label(text, _note);
        }

        /// <summary>"1 cut", "3 cuts": the count with its noun, in the plural unless it is one.</summary>
        internal static string Plural(int count, string singular, string plural) => count + " " + (count == 1 ? singular : plural);

        #region Rows

        /// <summary>
        /// One row of a list: a rectangle as wide as the space allows. Draw the controls of the row into it with <see cref="TakeLeft"/> and
        /// <see cref="TakeRight"/>, so every row has the same columns whatever the width of the panel.
        /// </summary>
        internal static Rect Row(float height = RowHeight) => GUILayoutUtility.GetRect(0f, height, GUILayout.ExpandWidth(true));

        /// <summary>Cuts a column of <paramref name="width"/> off the left end of <paramref name="rest"/> and returns it; <paramref name="rest"/> keeps what is left.</summary>
        internal static Rect TakeLeft(ref Rect rest, float width)
        {
            var taken = new Rect(rest.x, rest.y, width, rest.height);
            rest.xMin = Mathf.Min(rest.xMax, rest.xMin + width + ColumnGap);
            return taken;
        }

        /// <summary>Cuts a column of <paramref name="width"/> off the right end of <paramref name="rest"/> and returns it; <paramref name="rest"/> keeps what is left.</summary>
        internal static Rect TakeRight(ref Rect rest, float width)
        {
            var taken = new Rect(rest.xMax - width, rest.y, width, rest.height);
            rest.xMax = Mathf.Max(rest.xMin, rest.xMax - width - ColumnGap);
            return taken;
        }

        /// <summary>Tints the row of the selected item (a little wider than the row, so the text keeps its margin).</summary>
        internal static void Highlight(Rect row)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(row.x - 3f, row.y, row.width + 6f, row.height), new Color(Accent.r, Accent.g, Accent.b, 0.20f));
            }
        }

        /// <summary>A small box of <paramref name="color"/> (the colour of a part in the preview); outlined in white when <paramref name="selected"/>.</summary>
        internal static void SwatchBox(Rect column, Color color, bool selected)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            var outline = selected ? Color.white : (EditorGUIUtility.isProSkin ? new Color(0.10f, 0.10f, 0.11f) : new Color(0.45f, 0.45f, 0.47f));
            EditorGUI.DrawRect(new Rect(column.x + 1f, column.y + 2f, 14f, 14f), outline);
            EditorGUI.DrawRect(new Rect(column.x + 2f, column.y + 3f, 12f, 12f), color);
        }

        /// <summary>A <see cref="SwatchBox"/> that can be pressed. Returns true when it was.</summary>
        internal static bool Swatch(Rect column, Color color, bool selected, GUIContent tip)
        {
            SwatchBox(column, color, selected);
            EditorGUIUtility.AddCursorRect(column, MouseCursor.Link);
            return GUI.Button(column, tip, GUIStyle.none);
        }

        /// <summary>A check box centred in a column, so the boxes of every row stand under the header of their column.</summary>
        internal static bool CheckBox(Rect column, bool value, GUIContent tip)
        {
            var box = new Rect(column.x + (column.width - 16f) * 0.5f, column.y + 1f, 16f, 16f);
            return GUI.Toggle(box, value, tip);
        }

        /// <summary>The title of a column of check boxes or text, above the first row (muted and small).</summary>
        internal static void ColumnHeader(Rect column, string text, string tooltip, TextAnchor anchor)
        {
            EnsureStyles();
            var style = anchor == TextAnchor.MiddleLeft ? _headerLeft : anchor == TextAnchor.MiddleRight ? _headerRight : _headerCenter;
            GUI.Label(column, new GUIContent(text, tooltip), style);
        }

        /// <summary>A small muted value at the right end of a row (a count, a status). Its tooltip is the one of <paramref name="content"/>.</summary>
        internal static void Detail(Rect column, GUIContent content)
        {
            EnsureStyles();
            GUI.Label(column, content, _headerRight);
        }

        /// <summary>A small muted label for a field inside a row (Body side, Part side).</summary>
        internal static void Caption(Rect rect, string text)
        {
            EnsureStyles();
            GUI.Label(rect, text, _caption);
        }

        // Only the tooltip of a content is used by Tip: its text must never be drawn.
        private static readonly GUIContent TipContent = new GUIContent();

        /// <summary>Puts the tooltip of <paramref name="tip"/> on an area that has none of its own (a popup, an object field). It draws nothing (the text of <paramref name="tip"/> is not drawn) and takes no clicks.</summary>
        internal static void Tip(Rect rect, GUIContent tip)
        {
            TipContent.text = string.Empty;
            TipContent.tooltip = tip.tooltip;
            GUI.Label(rect, TipContent, GUIStyle.none);
        }

        /// <summary>
        /// A sub-heading inside a card: a thin line, then a bold <paramref name="title"/> and, at the right, a muted <paramref name="detail"/>
        /// (empty for none). It starts a second topic in the same card, so two controls that look alike but do different jobs stay apart.
        /// </summary>
        internal static void Heading(string title, string detail)
        {
            EnsureStyles();
            GUILayout.Space(8f);
            var rect = GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(rect.x, rect.y - 3f, rect.width, 1f), EditorGUIUtility.isProSkin ? new Color(0.16f, 0.16f, 0.17f) : new Color(0.70f, 0.70f, 0.72f));
            }

            GUI.Label(rect, title, _subsection);
            if (!string.IsNullOrEmpty(detail))
            {
                GUI.Label(rect, detail, _headerRight);
            }
        }

        /// <summary>Starts a darker, indented box under a row: the settings of that row. Close it with <see cref="EndInset"/>.</summary>
        internal static void BeginInset()
        {
            EnsureStyles();
            var rect = EditorGUILayout.BeginVertical(_insetPadding);
            Rounded(rect, EditorGUIUtility.isProSkin ? new Color(0.20f, 0.20f, 0.21f) : new Color(0.80f, 0.80f, 0.81f), 4f);
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(rect.x, rect.y + 3f, 2f, Mathf.Max(0f, rect.height - 6f)), Accent);
            }
        }

        /// <summary>Ends the box started with <see cref="BeginInset"/>.</summary>
        internal static void EndInset() => EditorGUILayout.EndVertical();

        #endregion

        private static void EnsureStyles()
        {
            if (_title != null)
            {
                return;
            }

            var muted = EditorGUIUtility.isProSkin ? new Color(0.66f, 0.70f, 0.76f) : new Color(0.28f, 0.32f, 0.38f);
            _title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 };
            _state = new GUIStyle(EditorStyles.label) { fontSize = 11, clipping = TextClipping.Clip };
            _state.normal.textColor = Color.white;
            _section = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            _subsection = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
            _note = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            _note.normal.textColor = muted;
            _caption = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            _caption.normal.textColor = muted;
            _headerLeft = new GUIStyle(_caption);
            _headerCenter = new GUIStyle(_caption) { alignment = TextAnchor.MiddleCenter };
            _headerRight = new GUIStyle(_caption) { alignment = TextAnchor.MiddleRight };
            _cardPadding = new GUIStyle { padding = new RectOffset(12, 12, 8, 10), margin = new RectOffset(0, 0, 4, 4) };
            _pagePadding = new GUIStyle { padding = new RectOffset(8, 8, 8, 6) };
            _insetPadding = new GUIStyle { padding = new RectOffset(10, 6, 5, 5), margin = new RectOffset(0, 0, 2, 4) };
        }

        // A filled rectangle with rounded corners (drawn on repaint only).
        private static void Rounded(Rect rect, Color color, float radius)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color, Vector4.zero, new Vector4(radius, radius, radius, radius));
        }
    }
}
