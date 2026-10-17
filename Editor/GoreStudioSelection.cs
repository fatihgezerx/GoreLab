using System.Collections.Generic;
using UnityEngine;

namespace GoreLab
{
    /// <summary>What the selection tools pick: single vertices, edges (both their vertices) or faces (the triangle's three).</summary>
    internal enum GoreTool
    {
        /// <summary>Select single vertices.</summary>
        Vertex,

        /// <summary>Select edges.</summary>
        Edge,

        /// <summary>Select faces (triangles).</summary>
        Face
    }

    /// <summary>What a click or a box does to the selection that was there.</summary>
    internal enum GoreSelectOp
    {
        /// <summary>The new selection replaces the old one.</summary>
        Replace,

        /// <summary>The new selection is added to the old one.</summary>
        Add,

        /// <summary>The new selection is taken out of the old one.</summary>
        Subtract,

        /// <summary>A click on a selected element deselects it, on an unselected one selects it (the rest stays).</summary>
        Toggle
    }

    /// <summary>
    /// Picking, box selection and paint selection over the meshes the studio shows, in screen space: the groups (welded vertices), edges and
    /// triangles of every <see cref="GoreStudioMeshEdit"/> are projected with the preview's own camera math and compared with the mouse.
    /// Only what faces the camera can be picked.
    /// </summary>
    internal static class GoreStudioSelection
    {
        // How close (GUI points) the mouse has to be to a vertex or an edge to pick it with a click.
        private const float PickRadius = 12f;

        // The things a stroke can touch (found once per stroke piece, so the samples along it only look at these).
        private static readonly List<int> Candidates = new List<int>(2048);

        /// <summary>One click: picks the vertex, edge or face under the mouse and applies it to the selection (or, with <paramref name="loop"/>, the loop of the nearest edge).</summary>
        internal static void Click(IList<GoreStudioMeshEdit> edits, GoreTool tool, GoreOrbitView view, Rect rect, Vector2 mouse, bool loop, GoreSelectOp op)
        {
            var cameraPosition = view.Pivot - view.Forward * view.Distance;
            if (op == GoreSelectOp.Replace)
            {
                ClearAll(edits);
            }

            foreach (var edit in edits)
            {
                Project(edit, view, rect, cameraPosition);
            }

            if (loop)
            {
                if (PickEdge(edits, mouse, out var loopEdit, out var loopEdge))
                {
                    loopEdit.SelectLoop(loopEdge);
                }

                return;
            }

            switch (tool)
            {
                case GoreTool.Vertex:
                    if (PickVertex(edits, mouse, out var vertexEdit, out var group))
                    {
                        Apply(vertexEdit, group, op);
                    }

                    break;
                case GoreTool.Edge:
                    if (PickEdge(edits, mouse, out var edgeEdit, out var edge))
                    {
                        ApplyAll(edgeEdit, op, edgeEdit.EdgeA[edge], edgeEdit.EdgeB[edge]);
                    }

                    break;
                case GoreTool.Face:
                    if (PickFace(edits, view.ScreenRay(rect, mouse), out var faceEdit, out var triangle))
                    {
                        ApplyAll(faceEdit, op, faceEdit.TriangleGroups[triangle * 3], faceEdit.TriangleGroups[triangle * 3 + 1], faceEdit.TriangleGroups[triangle * 3 + 2]);
                    }

                    break;
            }
        }

        /// <summary>Box selection: every vertex (edge: both ends, face: its centre) inside <paramref name="box"/> on screen.</summary>
        internal static void Box(IList<GoreStudioMeshEdit> edits, GoreTool tool, GoreOrbitView view, Rect rect, Rect box, GoreSelectOp op)
        {
            if (op == GoreSelectOp.Replace)
            {
                ClearAll(edits);
            }

            var cameraPosition = view.Pivot - view.Forward * view.Distance;
            var value = op != GoreSelectOp.Subtract;
            foreach (var edit in edits)
            {
                Project(edit, view, rect, cameraPosition);
                var screen = edit.Screen;
                var seen = edit.Seen;
                switch (tool)
                {
                    case GoreTool.Vertex:
                        for (var g = 0; g < edit.GroupCount; g++)
                        {
                            if (seen[g] && box.Contains(screen[g]))
                            {
                                edit.Set(g, value);
                            }
                        }

                        break;
                    case GoreTool.Edge:
                        for (var e = 0; e < edit.EdgeCount; e++)
                        {
                            var a = edit.EdgeA[e];
                            var b = edit.EdgeB[e];
                            if (seen[a] && seen[b] && box.Contains(screen[a]) && box.Contains(screen[b]))
                            {
                                edit.Set(a, value);
                                edit.Set(b, value);
                            }
                        }

                        break;
                    case GoreTool.Face:
                        for (var t = 0; t < edit.TriangleCount; t++)
                        {
                            var a = edit.TriangleGroups[t * 3];
                            var b = edit.TriangleGroups[t * 3 + 1];
                            var c = edit.TriangleGroups[t * 3 + 2];
                            if (seen[a] && seen[b] && seen[c] && box.Contains((screen[a] + screen[b] + screen[c]) / 3f))
                            {
                                edit.Set(a, value);
                                edit.Set(b, value);
                                edit.Set(c, value);
                            }
                        }

                        break;
                }
            }
        }

        /// <summary>
        /// Paint selection (Blender's circle select): everything the round brush touches while the mouse moves from <paramref name="from"/> to
        /// <paramref name="to"/> (a vertex inside the circle, an edge it crosses, a face whose centre is inside) is selected, or deselected with
        /// <paramref name="subtract"/>. The path is sampled so a fast stroke leaves no gaps.
        /// </summary>
        internal static void Paint(IList<GoreStudioMeshEdit> edits, GoreTool tool, GoreOrbitView view, Rect rect, Vector2 from, Vector2 to, float radius, bool subtract)
        {
            var cameraPosition = view.Pivot - view.Forward * view.Distance;
            var value = !subtract;
            var steps = Mathf.Max(1, Mathf.CeilToInt((to - from).magnitude / Mathf.Max(1f, radius * 0.5f)));
            var squared = radius * radius;
            var stroke = Rect.MinMaxRect(Mathf.Min(from.x, to.x) - radius, Mathf.Min(from.y, to.y) - radius, Mathf.Max(from.x, to.x) + radius, Mathf.Max(from.y, to.y) + radius);
            foreach (var edit in edits)
            {
                Project(edit, view, rect, cameraPosition);
                var screen = edit.Screen;
                var seen = edit.Seen;

                // Only what is near the stroke is looked at for every sample of it.
                Candidates.Clear();
                switch (tool)
                {
                    case GoreTool.Vertex:
                        for (var g = 0; g < edit.GroupCount; g++)
                        {
                            if (seen[g] && stroke.Contains(screen[g]))
                            {
                                Candidates.Add(g);
                            }
                        }

                        break;
                    case GoreTool.Edge:
                        for (var e = 0; e < edit.EdgeCount; e++)
                        {
                            var a = edit.EdgeA[e];
                            var b = edit.EdgeB[e];
                            if (seen[a] && seen[b] && stroke.Overlaps(Rect.MinMaxRect(Mathf.Min(screen[a].x, screen[b].x), Mathf.Min(screen[a].y, screen[b].y), Mathf.Max(screen[a].x, screen[b].x), Mathf.Max(screen[a].y, screen[b].y))))
                            {
                                Candidates.Add(e);
                            }
                        }

                        break;
                    case GoreTool.Face:
                        for (var t = 0; t < edit.TriangleCount; t++)
                        {
                            var a = edit.TriangleGroups[t * 3];
                            var b = edit.TriangleGroups[t * 3 + 1];
                            var c = edit.TriangleGroups[t * 3 + 2];
                            if (seen[a] && seen[b] && seen[c] && stroke.Contains((screen[a] + screen[b] + screen[c]) / 3f))
                            {
                                Candidates.Add(t);
                            }
                        }

                        break;
                }

                for (var step = 0; step <= steps; step++)
                {
                    var centre = Vector2.Lerp(from, to, step / (float)steps);
                    foreach (var index in Candidates)
                    {
                        switch (tool)
                        {
                            case GoreTool.Vertex:
                                if ((screen[index] - centre).sqrMagnitude <= squared)
                                {
                                    edit.Set(index, value);
                                }

                                break;
                            case GoreTool.Edge:
                                var first = edit.EdgeA[index];
                                var second = edit.EdgeB[index];
                                if (DistanceToSegment(centre, screen[first], screen[second]) <= squared)
                                {
                                    edit.Set(first, value);
                                    edit.Set(second, value);
                                }

                                break;
                            default:
                                var a = edit.TriangleGroups[index * 3];
                                var b = edit.TriangleGroups[index * 3 + 1];
                                var c = edit.TriangleGroups[index * 3 + 2];
                                if (((screen[a] + screen[b] + screen[c]) / 3f - centre).sqrMagnitude <= squared)
                                {
                                    edit.Set(a, value);
                                    edit.Set(b, value);
                                    edit.Set(c, value);
                                }

                                break;
                        }
                    }
                }
            }
        }

        /// <summary>Deselects everything of every mesh.</summary>
        internal static void ClearAll(IList<GoreStudioMeshEdit> edits)
        {
            foreach (var edit in edits)
            {
                edit.Clear();
            }
        }

        /// <summary>The bounds of the selected groups; false when nothing is selected.</summary>
        internal static bool SelectionBounds(IList<GoreStudioMeshEdit> edits, out Bounds bounds)
        {
            var any = false;
            bounds = new Bounds();
            foreach (var edit in edits)
            {
                for (var g = 0; g < edit.GroupCount; g++)
                {
                    if (!edit.Selected[g])
                    {
                        continue;
                    }

                    if (!any)
                    {
                        bounds = new Bounds(edit.GroupPosition[g], Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(edit.GroupPosition[g]);
                    }
                }
            }

            return any;
        }

        /// <summary>How many vertices are selected.</summary>
        internal static int CountVertices(IList<GoreStudioMeshEdit> edits)
        {
            var count = 0;
            foreach (var edit in edits)
            {
                count += edit.SelectedCount();
            }

            return count;
        }

        private static void Apply(GoreStudioMeshEdit edit, int group, GoreSelectOp op)
        {
            switch (op)
            {
                case GoreSelectOp.Subtract:
                    edit.Set(group, false);
                    break;
                case GoreSelectOp.Toggle:
                    edit.Set(group, !edit.Selected[group]);
                    break;
                default:
                    edit.Set(group, true);
                    break;
            }
        }

        // A click on an edge or a face acts on all its vertices together (a toggle looks at the first one).
        private static void ApplyAll(GoreStudioMeshEdit edit, GoreSelectOp op, params int[] groups)
        {
            var value = op != GoreSelectOp.Subtract;
            if (op == GoreSelectOp.Toggle)
            {
                value = !edit.Selected[groups[0]];
            }

            foreach (var group in groups)
            {
                edit.Set(group, value);
            }
        }

        // Where every group of a mesh is on screen, and whether it can be seen (into the mesh's own buffers).
        private static void Project(GoreStudioMeshEdit edit, GoreOrbitView view, Rect rect, Vector3 cameraPosition)
        {
            view.BeginProjection(rect);
            var forward = view.Forward;
            var orthographic = view.Orthographic;
            for (var g = 0; g < edit.GroupCount; g++)
            {
                edit.Screen[g] = view.ProjectCached(edit.GroupPosition[g], out var front);
                edit.Seen[g] = front && edit.Visible(g, cameraPosition, forward, orthographic);
            }
        }

        private static bool PickVertex(IList<GoreStudioMeshEdit> edits, Vector2 mouse, out GoreStudioMeshEdit hit, out int group)
        {
            hit = null;
            group = -1;
            var best = PickRadius * PickRadius;
            foreach (var edit in edits)
            {
                for (var g = 0; g < edit.GroupCount; g++)
                {
                    if (!edit.Seen[g])
                    {
                        continue;
                    }

                    var distance = (edit.Screen[g] - mouse).sqrMagnitude;
                    if (distance < best)
                    {
                        best = distance;
                        hit = edit;
                        group = g;
                    }
                }
            }

            return hit != null;
        }

        private static bool PickEdge(IList<GoreStudioMeshEdit> edits, Vector2 mouse, out GoreStudioMeshEdit hit, out int edge)
        {
            hit = null;
            edge = -1;
            var best = PickRadius * PickRadius;
            foreach (var edit in edits)
            {
                for (var e = 0; e < edit.EdgeCount; e++)
                {
                    var a = edit.EdgeA[e];
                    var b = edit.EdgeB[e];
                    if (!edit.Seen[a] || !edit.Seen[b])
                    {
                        continue;
                    }

                    var distance = DistanceToSegment(mouse, edit.Screen[a], edit.Screen[b]);
                    if (distance < best)
                    {
                        best = distance;
                        hit = edit;
                        edge = e;
                    }
                }
            }

            return hit != null;
        }

        // The squared distance from a point to a segment, in the plane.
        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            var edge = b - a;
            var length = edge.sqrMagnitude;
            var t = length > 1e-6f ? Mathf.Clamp01(Vector2.Dot(point - a, edge) / length) : 0f;
            return (point - (a + edge * t)).sqrMagnitude;
        }

        // The nearest triangle along a ray (the visible one), over every mesh.
        private static bool PickFace(IList<GoreStudioMeshEdit> edits, Ray ray, out GoreStudioMeshEdit hit, out int triangle)
        {
            hit = null;
            triangle = -1;
            var best = float.MaxValue;
            foreach (var edit in edits)
            {
                for (var t = 0; t < edit.TriangleCount; t++)
                {
                    var distance = RayTriangle(ray, edit.GroupPosition[edit.TriangleGroups[t * 3]], edit.GroupPosition[edit.TriangleGroups[t * 3 + 1]], edit.GroupPosition[edit.TriangleGroups[t * 3 + 2]]);
                    if (distance >= 0f && distance < best)
                    {
                        best = distance;
                        hit = edit;
                        triangle = t;
                    }
                }
            }

            return hit != null;
        }

        // Moller-Trumbore, both faces; the distance along the ray, or -1.
        private static float RayTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c)
        {
            var edge1 = b - a;
            var edge2 = c - a;
            var p = Vector3.Cross(ray.direction, edge2);
            var determinant = Vector3.Dot(edge1, p);
            if (determinant > -1e-12f && determinant < 1e-12f)
            {
                return -1f;
            }

            var inverse = 1f / determinant;
            var toOrigin = ray.origin - a;
            var u = Vector3.Dot(toOrigin, p) * inverse;
            if (u < 0f || u > 1f)
            {
                return -1f;
            }

            var q = Vector3.Cross(toOrigin, edge1);
            var v = Vector3.Dot(ray.direction, q) * inverse;
            if (v < 0f || u + v > 1f)
            {
                return -1f;
            }

            var t = Vector3.Dot(edge2, q) * inverse;
            return t > 0f ? t : -1f;
        }
    }
}
