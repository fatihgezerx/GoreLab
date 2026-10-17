using UnityEditor;
using UnityEngine;

namespace GoreLab
{
    // Why: the preview is rendered by a PreviewRenderUtility, so this class owns no camera; it only keeps the orbit state (pivot,
    // distance, yaw, pitch, projection) and writes it to the window's camera in Apply. The ray and the world-to-view math are done
    // by hand from the same state, so picking never depends on a camera that may be a frame behind. Perspective and orthographic
    // share one apparent size (ortho size = distance * tan(fov / 2)), which makes the toggle jump-free. Distance limits scale with
    // the framed bounds, so a tiny hand and a whole body both zoom comfortably. All input goes through one control id per call, so a
    // drag keeps running when the mouse leaves the rect. Nothing here allocates per repaint (arrays are made once in the constructor,
    // description strings are literals, the circle textures are static and created once).

    /// <summary>
    /// Blender-style camera control for a character preview: orbit, pan and zoom around a pivot with the mouse and the numpad,
    /// view snapping, a perspective / orthographic toggle, and an IMGUI orientation gizmo. Call <see cref="HandleInput"/> and
    /// <see cref="DrawNavigationGizmo"/> from OnGUI (the window needs <c>wantsMouseMove = true</c> for the gizmo hover highlight),
    /// and <see cref="Apply"/> before rendering the preview.
    /// </summary>
    internal sealed class GoreOrbitView
    {
        /// <summary>Which mouse drag is running.</summary>
        private enum DragMode
        {
            None,
            Orbit,
            Pan,
            Zoom
        }

        private const float FieldOfView = 30f;
        private const float MaxPitch = 89.9f;
        private const float OrbitDegreesPerPixel = 0.25f;
        private const float GizmoOrbitDegreesPerPixel = 0.4f;
        private const float ZoomPerPixel = 0.01f;
        private const float WheelNotchUnits = 3f;
        private const float KeyOrbitStep = 15f;
        private const float FrameMargin = 1.05f;
        private const float DefaultSize = 2f;
        private const float AxisSnapDot = 0.99996193f; // cos(0.5 degrees)
        private const int DragControlHint = 0x474F5231;
        private const int GizmoControlHint = 0x474F5232;

        private const float GizmoMargin = 12f;
        private const float GizmoSize = 90f;
        private const float GizmoAxisLength = 33f;
        private const float PositiveBubbleRadius = 10f;
        private const float NegativeBubbleRadius = 7f;
        private const float GizmoClickSlop = 3f;
        private const float GizmoCentreRadius = 8f;
        private const float LabelWidth = 44f;
        private const float LabelHeight = 16f;
        private const int CircleTextureSize = 64;

        // Half of the vertical field of view, as a tangent.
        private static readonly float TanHalfFov = Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad);

        // ln(1.1): one wheel notch changes the distance by about 10 percent.
        private static readonly float ZoomPerNotch = Mathf.Log(1.1f);

        // The direction the camera looks along for Front, Back, Right, Left, Top, Bottom (same order as the description table).
        private static readonly Vector3[] ViewForwards =
        {
            Vector3.back, Vector3.forward, Vector3.left, Vector3.right, Vector3.down, Vector3.up
        };

        // Perspective names first, then orthographic; each block: Front, Back, Right, Left, Top, Bottom, User.
        private static readonly string[] Descriptions =
        {
            "Perspective Front", "Perspective Back", "Perspective Right", "Perspective Left", "Perspective Top", "Perspective Bottom", "Perspective User",
            "Orthographic Front", "Orthographic Back", "Orthographic Right", "Orthographic Left", "Orthographic Top", "Orthographic Bottom", "Orthographic User"
        };

        // Gizmo axes: +X, -X, +Y, -Y, +Z, -Z (even index = positive).
        private static readonly Vector3[] GizmoAxes =
        {
            Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back
        };

        private static readonly Color[] AxisColors =
        {
            new Color(0.93f, 0.26f, 0.32f, 1f),
            new Color(0.55f, 0.84f, 0.12f, 1f),
            new Color(0.22f, 0.56f, 0.93f, 1f)
        };

        private static readonly string[] AxisLetters = { "X", "Y", "Z" };

        private static Texture2D _discTexture;
        private static Texture2D _ringTexture;
        private static GUIStyle _letterStyle;
        private static GUIStyle _modeStyle;

        /// <summary>The world point the camera orbits around.</summary>
        public Vector3 Pivot = new Vector3(0f, 1f, 0f);

        /// <summary>The distance from the camera to <see cref="Pivot"/>, in world units.</summary>
        public float Distance = 4f;

        /// <summary>The heading in degrees; 0 looks along +Z.</summary>
        public float Yaw = -150f;

        /// <summary>The tilt in degrees, clamped to +-89.9; positive looks down.</summary>
        public float Pitch = 15f;

        /// <summary>True for an orthographic camera, false for perspective.</summary>
        public bool Orthographic;

        private readonly Vector2[] _bubbleCenters = new Vector2[6];
        private readonly float[] _bubbleDepths = new float[6];
        private readonly int[] _bubbleOrder = new int[6];

        private Bounds _lastBounds;
        private bool _hasBounds;
        private float _size = DefaultSize;

        /// <summary>
        /// Whether Alt + left drag orbits (for mice without a middle button). The studio turns it off while a selection tool is active,
        /// because Alt + click selects an edge loop there.
        /// </summary>
        public bool AltLeftNavigates = true;
        private float _aspect = 1f;
        private DragMode _dragMode;

        private int _hoverBubble = -1;
        private bool _hoverLabel;
        private bool _hoverDisc;
        private int _pressedBubble = -1;
        private Vector2 _pressedPosition;
        private bool _pressDragged;

        /// <summary>The camera rotation: <c>Quaternion.Euler(Pitch, Yaw, 0)</c>.</summary>
        public Quaternion Rotation
        {
            get { return Quaternion.Euler(Pitch, Yaw, 0f); }
        }

        /// <summary>The direction the camera looks along.</summary>
        public Vector3 Forward
        {
            get { return Rotation * Vector3.forward; }
        }

        /// <summary>
        /// A label for the current view, for example "Perspective User" or "Orthographic Front". An axis view is named when the camera
        /// is within half a degree of it.
        /// </summary>
        public string Description
        {
            get
            {
                Vector3 forward = Forward;
                int kind = ViewForwards.Length; // User
                for (int index = 0; index < ViewForwards.Length; index++)
                {
                    if (Vector3.Dot(forward, ViewForwards[index]) >= AxisSnapDot)
                    {
                        kind = index;
                        break;
                    }
                }

                return Descriptions[(Orthographic ? ViewForwards.Length + 1 : 0) + kind];
            }
        }

        // The ortho half height; equal to the perspective view height at the pivot, so toggling keeps the apparent size.
        private float OrthoSize
        {
            get { return SafeDistance * TanHalfFov; }
        }

        private float SafeDistance
        {
            get { return Mathf.Max(Distance, 0.001f); }
        }

        private float NearClip
        {
            get { return Orthographic ? -_size * 10f : Mathf.Max(0.01f, SafeDistance * 0.02f); }
        }

        private float FarClip
        {
            get { return SafeDistance + _size * 50f; }
        }

        /// <summary>
        /// Centers the view on <paramref name="bounds"/> and backs off until the whole box fits (with a small margin), keeping the
        /// current angles. The size of the bounds also sets the zoom limits (min = size * 0.02, max = size * 30).
        /// </summary>
        public void Frame(Bounds bounds)
        {
            _lastBounds = bounds;
            _hasBounds = true;
            _size = Mathf.Max(bounds.size.magnitude, 0.01f);
            Pivot = bounds.center;

            // Fit the bounding sphere. A narrow window is limited by its width, so the half angle shrinks with the aspect.
            float radius = Mathf.Max(bounds.extents.magnitude, 0.005f);
            float halfAngle = Mathf.Atan(TanHalfFov * Mathf.Min(1f, _aspect));
            Distance = ClampDistance(radius / Mathf.Max(Mathf.Sin(halfAngle), 0.01f) * FrameMargin);
        }

        /// <summary>
        /// Centres the view on <paramref name="bounds"/> and backs off until it fits, like <see cref="Frame"/>, but leaves the framed size
        /// (the zoom limits and the box the F key frames) alone: for looking at a part of the character.
        /// </summary>
        public void FocusOn(Bounds bounds)
        {
            Pivot = bounds.center;
            float radius = Mathf.Max(bounds.extents.magnitude, 0.005f);
            float halfAngle = Mathf.Atan(TanHalfFov * Mathf.Min(1f, _aspect));
            Distance = ClampDistance(radius / Mathf.Max(Mathf.Sin(halfAngle), 0.01f) * FrameMargin);
        }

        /// <summary>
        /// Looks along <paramref name="viewAxis"/> (the camera looks in that direction). For a straight up or down axis the pitch is
        /// set to -+89.9 and the current yaw is kept.
        /// </summary>
        public void SnapTo(Vector3 viewAxis)
        {
            if (viewAxis.sqrMagnitude < 1e-8f)
            {
                return;
            }

            Vector3 direction = viewAxis.normalized;
            if (Mathf.Abs(direction.x) + Mathf.Abs(direction.z) < 1e-4f)
            {
                // Looking straight along Y: yaw is undefined, so keep it.
                Pitch = direction.y > 0f ? -MaxPitch : MaxPitch;
            }
            else
            {
                Yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
                Pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg, -MaxPitch, MaxPitch);
            }

            WrapYaw();
        }

        /// <summary>Switches between perspective and orthographic; the apparent size stays the same.</summary>
        public void ToggleOrthographic()
        {
            Orthographic = !Orthographic;
        }

        /// <summary>
        /// Handles the mouse and keyboard navigation. Call it from OnGUI for every event. Returns true when the view changed
        /// and the window should repaint.
        /// </summary>
        /// <param name="viewRect">The preview area in GUI space.</param>
        /// <param name="current">The event being processed.</param>
        public bool HandleInput(Rect viewRect, Event current)
        {
            // The id is taken for every event in the same order, so the hot control survives between events.
            int controlId = GUIUtility.GetControlID(DragControlHint, FocusType.Passive);
            RememberAspect(viewRect);
            bool changed = false;

            switch (current.type)
            {
                case EventType.MouseDown:
                    if (GUIUtility.hotControl == 0 && viewRect.Contains(current.mousePosition))
                    {
                        DragMode mode = DragModeFor(current);
                        if (mode != DragMode.None)
                        {
                            _dragMode = mode;
                            GUIUtility.hotControl = controlId;
                            current.Use();
                        }
                    }

                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlId && _dragMode != DragMode.None)
                    {
                        ApplyDrag(current.delta, viewRect.height);
                        current.Use();
                        changed = true;
                    }

                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        _dragMode = DragMode.None;
                        current.Use();
                        changed = true;
                    }

                    break;

                case EventType.ScrollWheel:
                    if (viewRect.Contains(current.mousePosition))
                    {
                        float notches = Mathf.Clamp(current.delta.y / WheelNotchUnits, -3f, 3f);
                        ZoomByExponent(notches * ZoomPerNotch);
                        current.Use();
                        changed = true;
                    }

                    break;

                case EventType.KeyDown:
                    changed = HandleKey(viewRect, current);
                    break;

                case EventType.Repaint:
                    if (GUIUtility.hotControl == controlId && _dragMode != DragMode.None)
                    {
                        // Keeps the navigation cursor while the mouse is outside the rect.
                        EditorGUIUtility.AddCursorRect(viewRect, CursorFor(_dragMode), controlId);
                    }

                    break;
            }

            return changed;
        }

        /// <summary>
        /// Writes the view to <paramref name="camera"/>: position, rotation, projection, size, aspect and clip planes.
        /// </summary>
        /// <param name="camera">The preview camera.</param>
        /// <param name="aspect">Width divided by height of the preview.</param>
        public void Apply(Camera camera, float aspect)
        {
            _aspect = Mathf.Max(aspect, 0.01f);
            Quaternion rotation = Rotation;
            Vector3 forward = rotation * Vector3.forward;
            camera.transform.SetPositionAndRotation(Pivot - forward * SafeDistance, rotation);
            camera.orthographic = Orthographic;
            camera.fieldOfView = FieldOfView;
            camera.orthographicSize = OrthoSize;
            camera.aspect = _aspect;

            // Ortho gets a near plane behind the camera so nothing near the pivot is clipped when zoomed in close.
            camera.nearClipPlane = NearClip;
            camera.farClipPlane = FarClip;
        }

        /// <summary>
        /// The world ray under <paramref name="mousePosition"/>. For an orthographic view the origin lies on the camera plane.
        /// </summary>
        /// <param name="viewRect">The preview area in GUI space.</param>
        /// <param name="mousePosition">A mouse position in the same GUI space.</param>
        public Ray ScreenRay(Rect viewRect, Vector2 mousePosition)
        {
            Quaternion rotation = Rotation;
            Vector3 right = rotation * Vector3.right;
            Vector3 up = rotation * Vector3.up;
            Vector3 forward = rotation * Vector3.forward;
            Vector3 cameraPosition = Pivot - forward * SafeDistance;
            float aspect = viewRect.width / Mathf.Max(viewRect.height, 1f);

            float ndcX = (mousePosition.x - viewRect.x) / Mathf.Max(viewRect.width, 1f) * 2f - 1f;
            float ndcY = 1f - (mousePosition.y - viewRect.y) / Mathf.Max(viewRect.height, 1f) * 2f;

            if (Orthographic)
            {
                float halfHeight = OrthoSize;
                // From the start of the view volume (the near plane is behind the camera plane), so what is drawn there can be hit too.
                Vector3 origin = cameraPosition - forward * (_size * 10f) + right * (ndcX * aspect * halfHeight) + up * (ndcY * halfHeight);
                return new Ray(origin, forward);
            }

            Vector3 direction = forward + right * (ndcX * aspect * TanHalfFov) + up * (ndcY * TanHalfFov);
            return new Ray(cameraPosition, direction.normalized);
        }

        private Vector3 _projectionRight;
        private Vector3 _projectionUp;
        private Vector3 _projectionForward;
        private Vector3 _projectionCamera;
        private Rect _projectionRect;
        private float _projectionAspect;
        private float _projectionHalfHeight;
        private float _projectionNear;
        private bool _projectionOrthographic;

        /// <summary>
        /// Prepares <see cref="ProjectCached"/> for many points: the camera's axes are worked out once instead of for every point. Call it
        /// again whenever the view or the rect changes.
        /// </summary>
        public void BeginProjection(Rect viewRect)
        {
            Quaternion rotation = Rotation;
            _projectionRight = rotation * Vector3.right;
            _projectionUp = rotation * Vector3.up;
            _projectionForward = rotation * Vector3.forward;
            _projectionCamera = Pivot - _projectionForward * SafeDistance;
            _projectionRect = viewRect;
            _projectionAspect = viewRect.width / Mathf.Max(viewRect.height, 1f);
            _projectionHalfHeight = OrthoSize;
            _projectionNear = NearClip;
            _projectionOrthographic = Orthographic;
        }

        /// <summary>The same as <see cref="WorldToView"/> for the view and rect of the last <see cref="BeginProjection"/>, without the per-point setup.</summary>
        public Vector2 ProjectCached(Vector3 world, out bool inFront)
        {
            Vector3 relative = world - _projectionCamera;
            float depth = Vector3.Dot(relative, _projectionForward);
            float ndcX;
            float ndcY;
            if (_projectionOrthographic)
            {
                ndcX = Vector3.Dot(relative, _projectionRight) / (_projectionHalfHeight * _projectionAspect);
                ndcY = Vector3.Dot(relative, _projectionUp) / _projectionHalfHeight;
            }
            else
            {
                float safeDepth = Mathf.Max(depth, 1e-4f);
                ndcX = Vector3.Dot(relative, _projectionRight) / (safeDepth * TanHalfFov * _projectionAspect);
                ndcY = Vector3.Dot(relative, _projectionUp) / (safeDepth * TanHalfFov);
            }

            inFront = depth > _projectionNear;
            return new Vector2(_projectionRect.x + (ndcX * 0.5f + 0.5f) * _projectionRect.width, _projectionRect.y + (0.5f - ndcY * 0.5f) * _projectionRect.height);
        }

        /// <summary>
        /// The GUI-space point (same space as the mouse position, inside <paramref name="viewRect"/> when visible) of a world point.
        /// </summary>
        /// <param name="viewRect">The preview area in GUI space.</param>
        /// <param name="world">The world point.</param>
        /// <param name="inFront">False when the point is behind the camera (or its near plane), so the result must not be used.</param>
        public Vector2 WorldToView(Rect viewRect, Vector3 world, out bool inFront)
        {
            Quaternion rotation = Rotation;
            Vector3 right = rotation * Vector3.right;
            Vector3 up = rotation * Vector3.up;
            Vector3 forward = rotation * Vector3.forward;
            Vector3 relative = world - (Pivot - forward * SafeDistance);
            float aspect = viewRect.width / Mathf.Max(viewRect.height, 1f);
            float depth = Vector3.Dot(relative, forward);
            float ndcX;
            float ndcY;

            if (Orthographic)
            {
                float halfHeight = OrthoSize;
                ndcX = Vector3.Dot(relative, right) / (halfHeight * aspect);
                ndcY = Vector3.Dot(relative, up) / halfHeight;
            }
            else
            {
                // Guard the divide; a point at or behind the camera is flagged by inFront anyway.
                float safeDepth = Mathf.Max(depth, 1e-4f);
                ndcX = Vector3.Dot(relative, right) / (safeDepth * TanHalfFov * aspect);
                ndcY = Vector3.Dot(relative, up) / (safeDepth * TanHalfFov);
            }

            inFront = depth > NearClip;
            return new Vector2(viewRect.x + (ndcX * 0.5f + 0.5f) * viewRect.width, viewRect.y + (0.5f - ndcY * 0.5f) * viewRect.height);
        }

        /// <summary>
        /// Draws the orientation gizmo in the top-right corner of <paramref name="viewRect"/> and handles its clicks and drags.
        /// Call it from OnGUI for every event. Clicking a bubble snaps to that view, clicking the centre or the Persp / Ortho label
        /// toggles the projection, dragging inside the disc orbits. Returns true when the view changed.
        /// </summary>
        /// <param name="viewRect">The preview area in GUI space.</param>
        /// <param name="current">The event being processed.</param>
        public bool DrawNavigationGizmo(Rect viewRect, Event current)
        {
            int controlId = GUIUtility.GetControlID(GizmoControlHint, FocusType.Passive);
            Rect gizmoRect = new Rect(viewRect.xMax - GizmoMargin - GizmoSize, viewRect.y + GizmoMargin, GizmoSize, GizmoSize);
            Rect labelRect = new Rect(gizmoRect.center.x - LabelWidth * 0.5f, gizmoRect.yMax + 2f, LabelWidth, LabelHeight);
            UpdateGizmoLayout(gizmoRect);

            Vector2 mouse = current.mousePosition;
            Vector2 centre = gizmoRect.center;
            int hitBubble = HitBubble(mouse);
            bool hitLabel = labelRect.Contains(mouse);
            bool hitDisc = (mouse - centre).sqrMagnitude <= GizmoSize * GizmoSize * 0.25f;
            bool changed = false;

            switch (current.type)
            {
                case EventType.MouseDown:
                    if (current.button == 0 && !current.alt && GUIUtility.hotControl == 0)
                    {
                        if (hitLabel)
                        {
                            ToggleOrthographic();
                            current.Use();
                            changed = true;
                        }
                        else if (hitDisc)
                        {
                            // A press may become a click or a drag, which is decided on mouse up / after some movement.
                            _pressedBubble = hitBubble;
                            _pressedPosition = mouse;
                            _pressDragged = false;
                            GUIUtility.hotControl = controlId;
                            current.Use();
                        }
                    }

                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlId)
                    {
                        if (!_pressDragged && (mouse - _pressedPosition).sqrMagnitude > GizmoClickSlop * GizmoClickSlop)
                        {
                            _pressDragged = true;
                        }

                        if (_pressDragged)
                        {
                            Orbit(current.delta, GizmoOrbitDegreesPerPixel);
                            changed = true;
                        }

                        current.Use();
                    }

                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        if (!_pressDragged)
                        {
                            if (_pressedBubble >= 0)
                            {
                                // Clicking +X shows the view from the +X side, so the camera looks along -X.
                                SnapTo(-GizmoAxes[_pressedBubble]);
                                changed = true;
                            }
                            else if ((mouse - centre).sqrMagnitude <= GizmoCentreRadius * GizmoCentreRadius)
                            {
                                ToggleOrthographic();
                                changed = true;
                            }
                        }

                        _pressedBubble = -1;
                        _pressDragged = false;
                        current.Use();
                    }

                    break;

                case EventType.MouseMove:
                case EventType.MouseLeaveWindow:
                    // A repaint is only needed when the highlight would look different.
                    if (current.type == EventType.MouseLeaveWindow)
                    {
                        hitBubble = -1;
                        hitLabel = false;
                        hitDisc = false;
                    }

                    changed = hitBubble != _hoverBubble || hitLabel != _hoverLabel || hitDisc != _hoverDisc;
                    break;

                case EventType.Repaint:
                    DrawGizmo(gizmoRect, labelRect, hitBubble, hitLabel, hitDisc);
                    break;
            }

            if (current.type != EventType.Repaint)
            {
                _hoverBubble = hitBubble;
                _hoverLabel = hitLabel;
                _hoverDisc = hitDisc;
            }

            return changed;
        }

        /// <summary>Destroys the generated circle textures. Call it from the window's OnDisable.</summary>
        internal static void ReleaseResources()
        {
            if (_discTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(_discTexture);
            }

            if (_ringTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(_ringTexture);
            }

            _discTexture = null;
            _ringTexture = null;
            _letterStyle = null;
            _modeStyle = null;
        }

        // ---- Navigation helpers ----

        private void RememberAspect(Rect viewRect)
        {
            if (viewRect.width > 1f && viewRect.height > 1f)
            {
                _aspect = viewRect.width / viewRect.height;
            }
        }

        private DragMode DragModeFor(Event current)
        {
            if (current.button == 2)
            {
                return current.shift ? DragMode.Pan : current.control ? DragMode.Zoom : DragMode.Orbit;
            }

            if (current.button == 1 && current.alt)
            {
                return DragMode.Zoom;
            }

            if (current.button == 0 && current.alt && AltLeftNavigates)
            {
                // For mice without a middle button.
                return current.shift ? DragMode.Pan : current.control ? DragMode.Zoom : DragMode.Orbit;
            }

            return DragMode.None;
        }

        private static MouseCursor CursorFor(DragMode mode)
        {
            switch (mode)
            {
                case DragMode.Pan:
                    return MouseCursor.Pan;
                case DragMode.Zoom:
                    return MouseCursor.Zoom;
                default:
                    return MouseCursor.Orbit;
            }
        }

        private void ApplyDrag(Vector2 delta, float viewHeight)
        {
            switch (_dragMode)
            {
                case DragMode.Orbit:
                    Orbit(delta, OrbitDegreesPerPixel);
                    break;

                case DragMode.Pan:
                {
                    // The view height at the pivot in world units is the same for perspective and ortho, so one scale fits both.
                    float worldPerPixel = 2f * OrthoSize / Mathf.Max(viewHeight, 1f);
                    Quaternion rotation = Rotation;
                    Vector3 right = rotation * Vector3.right;
                    Vector3 up = rotation * Vector3.up;

                    // Dragging right moves the scene right, so the pivot goes left.
                    Pivot += (up * delta.y - right * delta.x) * worldPerPixel;
                    break;
                }

                case DragMode.Zoom:
                    ZoomByExponent(delta.y * ZoomPerPixel);
                    break;
            }
        }

        private void Orbit(Vector2 delta, float degreesPerPixel)
        {
            Yaw += delta.x * degreesPerPixel;
            Pitch = Mathf.Clamp(Pitch + delta.y * degreesPerPixel, -MaxPitch, MaxPitch);
            WrapYaw();
        }

        private void WrapYaw()
        {
            Yaw = Mathf.Repeat(Yaw + 180f, 360f) - 180f;
        }

        private void ZoomByExponent(float exponent)
        {
            Distance = ClampDistance(SafeDistance * Mathf.Exp(exponent));
        }

        private float ClampDistance(float distance)
        {
            return Mathf.Clamp(distance, _size * 0.02f, _size * 30f);
        }

        private bool HandleKey(Rect viewRect, Event current)
        {
            // Typing in a text field must never move the camera, and the mouse has to be over the preview.
            if (EditorGUIUtility.editingTextField || !viewRect.Contains(current.mousePosition))
            {
                return false;
            }

            bool reverse = current.control;
            switch (current.keyCode)
            {
                case KeyCode.Keypad1:
                    SnapTo(reverse ? Vector3.forward : Vector3.back);
                    break;
                case KeyCode.Keypad3:
                    SnapTo(reverse ? Vector3.right : Vector3.left);
                    break;
                case KeyCode.Keypad7:
                    SnapTo(reverse ? Vector3.up : Vector3.down);
                    break;
                case KeyCode.Keypad5:
                    ToggleOrthographic();
                    break;
                case KeyCode.Keypad4:
                    Orbit(new Vector2(-KeyOrbitStep, 0f), 1f);
                    break;
                case KeyCode.Keypad6:
                    Orbit(new Vector2(KeyOrbitStep, 0f), 1f);
                    break;
                case KeyCode.Keypad8:
                    Orbit(new Vector2(0f, -KeyOrbitStep), 1f);
                    break;
                case KeyCode.Keypad2:
                    Orbit(new Vector2(0f, KeyOrbitStep), 1f);
                    break;
                case KeyCode.F:
                case KeyCode.Home:
                    if (!_hasBounds)
                    {
                        return false;
                    }

                    Frame(_lastBounds);
                    break;
                default:
                    return false;
            }

            current.Use();
            return true;
        }

        // ---- Gizmo helpers ----

        // Projects the six axes through the camera rotation and sorts them far to near.
        private void UpdateGizmoLayout(Rect gizmoRect)
        {
            Quaternion rotation = Rotation;
            Vector3 right = rotation * Vector3.right;
            Vector3 up = rotation * Vector3.up;
            Vector3 forward = rotation * Vector3.forward;
            Vector2 centre = gizmoRect.center;

            for (int index = 0; index < 6; index++)
            {
                Vector3 axis = GizmoAxes[index];
                float x = Vector3.Dot(axis, right);
                float y = Vector3.Dot(axis, up);

                // GUI y points down, the camera up points up.
                _bubbleCenters[index] = new Vector2(centre.x + x * GizmoAxisLength, centre.y - y * GizmoAxisLength);
                _bubbleDepths[index] = Vector3.Dot(axis, forward);
                _bubbleOrder[index] = index;
            }

            // Insertion sort, descending depth: the farthest axis comes first and is drawn first.
            for (int i = 1; i < 6; i++)
            {
                int key = _bubbleOrder[i];
                float keyDepth = _bubbleDepths[key];
                int j = i - 1;
                while (j >= 0 && _bubbleDepths[_bubbleOrder[j]] < keyDepth)
                {
                    _bubbleOrder[j + 1] = _bubbleOrder[j];
                    j--;
                }

                _bubbleOrder[j + 1] = key;
            }
        }

        // The nearest bubble under the mouse, or -1.
        private int HitBubble(Vector2 mouse)
        {
            for (int i = 5; i >= 0; i--)
            {
                int index = _bubbleOrder[i];
                float radius = BubbleRadius(index) + 2f;
                if ((mouse - _bubbleCenters[index]).sqrMagnitude <= radius * radius)
                {
                    return index;
                }
            }

            return -1;
        }

        private static float BubbleRadius(int index)
        {
            return (index & 1) == 0 ? PositiveBubbleRadius : NegativeBubbleRadius;
        }

        private void DrawGizmo(Rect gizmoRect, Rect labelRect, int hoverBubble, bool hoverLabel, bool hoverDisc)
        {
            EnsureResources();
            Color previousColor = GUI.color;
            Vector2 centre = gizmoRect.center;

            if (hoverDisc)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.12f);
                GUI.DrawTexture(gizmoRect, _discTexture);
            }

            // Lines first so every bubble covers the line ends.
            for (int index = 0; index < 6; index += 2)
            {
                DrawLine(centre, _bubbleCenters[index], 2f, ShadeAxisColor(index, false));
            }

            for (int i = 0; i < 6; i++)
            {
                int index = _bubbleOrder[i];
                bool positive = (index & 1) == 0;
                float radius = BubbleRadius(index);
                Vector2 position = _bubbleCenters[index];
                bool hovered = index == hoverBubble;
                Rect bubbleRect = new Rect(position.x - radius, position.y - radius, radius * 2f, radius * 2f);

                GUI.color = ShadeAxisColor(index, hovered);
                GUI.DrawTexture(bubbleRect, _discTexture);

                if (hovered)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.9f);
                    GUI.DrawTexture(bubbleRect, _ringTexture);
                }

                if (positive)
                {
                    GUI.color = Color.white;
                    GUI.Label(bubbleRect, AxisLetters[index >> 1], _letterStyle);
                }
            }

            // The mode label under the gizmo shows the CURRENT mode.
            GUI.color = Color.white;
            EditorGUI.DrawRect(labelRect, hoverLabel ? new Color(0.32f, 0.32f, 0.34f, 0.9f) : new Color(0.12f, 0.12f, 0.13f, 0.7f));
            GUI.Label(labelRect, Orthographic ? "Ortho" : "Persp", _modeStyle);

            GUI.color = previousColor;
        }

        // Positive axes get the full color, negative ones a dimmer shade; farther bubbles are slightly darker.
        private Color ShadeAxisColor(int index, bool hovered)
        {
            Color color = AxisColors[index >> 1];
            float shade = (index & 1) == 0 ? 1f : 0.5f;
            shade *= Mathf.Lerp(1f, 0.75f, _bubbleDepths[index] * 0.5f + 0.5f);
            color = new Color(color.r * shade, color.g * shade, color.b * shade, 1f);
            return hovered ? Color.Lerp(color, Color.white, 0.4f) : color;
        }

        // A line made of a stretched white texture under a rotated GUI matrix; works in any OnGUI and allocates nothing.
        private static void DrawLine(Vector2 from, Vector2 to, float width, Color color)
        {
            Vector2 delta = to - from;
            float length = delta.magnitude;
            if (length < 0.5f)
            {
                return;
            }

            Matrix4x4 previousMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, from);
            GUI.color = color;
            GUI.DrawTexture(new Rect(from.x, from.y - width * 0.5f, length, width), Texture2D.whiteTexture);
            GUI.matrix = previousMatrix;
        }

        // ---- Resources ----

        private static void EnsureResources()
        {
            if (_discTexture == null)
            {
                _discTexture = CreateCircleTexture(false);
            }

            if (_ringTexture == null)
            {
                _ringTexture = CreateCircleTexture(true);
            }

            if (_letterStyle == null)
            {
                _letterStyle = CreateCenteredStyle(11, new Color(0.06f, 0.06f, 0.06f, 1f));
            }

            if (_modeStyle == null)
            {
                _modeStyle = CreateCenteredStyle(10, new Color(0.88f, 0.88f, 0.88f, 1f));
            }
        }

        private static GUIStyle CreateCenteredStyle(int fontSize, Color textColor)
        {
            GUIStyle style = new GUIStyle(EditorStyles.boldLabel);
            style.alignment = TextAnchor.MiddleCenter;
            style.fontSize = fontSize;
            style.padding = new RectOffset(0, 0, 0, 0);
            style.margin = new RectOffset(0, 0, 0, 0);
            style.clipping = TextClipping.Overflow;
            style.normal.textColor = textColor;
            style.hover.textColor = textColor;
            style.active.textColor = textColor;
            style.focused.textColor = textColor;
            return style;
        }

        // A white anti-aliased disc, or a ring when asked; tinted at draw time with GUI.color.
        private static Texture2D CreateCircleTexture(bool ring)
        {
            Texture2D texture = new Texture2D(CircleTextureSize, CircleTextureSize, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            Color32[] pixels = new Color32[CircleTextureSize * CircleTextureSize];
            float centre = (CircleTextureSize - 1) * 0.5f;
            float outer = CircleTextureSize * 0.5f - 1f;
            float ringWidth = 4f;

            for (int y = 0; y < CircleTextureSize; y++)
            {
                for (int x = 0; x < CircleTextureSize; x++)
                {
                    float distance = Mathf.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre));

                    // About one pixel of soft edge for the anti-aliasing.
                    float coverage = Mathf.Clamp01(outer - distance + 0.5f);
                    if (ring)
                    {
                        coverage = Mathf.Min(coverage, Mathf.Clamp01(distance - (outer - ringWidth) + 0.5f));
                    }

                    pixels[y * CircleTextureSize + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(coverage * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }
    }
}
