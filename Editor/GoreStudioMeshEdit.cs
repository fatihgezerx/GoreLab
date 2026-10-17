using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GoreLab
{
    /// <summary>
    /// What the selection tools of the studio work on for one skinned mesh: its vertices in the pose the preview shows, welded by position into
    /// groups (a vertex that a texture seam or a hard edge duplicated is one group, like in Blender), the edges and triangles between the groups,
    /// and which groups are selected. Built once per character, read-only apart from the selection.
    /// </summary>
    internal sealed class GoreStudioMeshEdit : IDisposable
    {
        // Positions closer than this (meters) are the same vertex.
        private const float WeldSize = 1e-4f;

        // How straight an edge has to continue to belong to the same loop (cosine of the angle: about 60 degrees).
        private const float LoopStraightness = 0.5f;

        /// <summary>The world position of every source vertex of the mesh in the preview's pose.</summary>
        public Vector3[] SourcePosition = Array.Empty<Vector3>();

        /// <summary>For every source vertex, the group it was welded into.</summary>
        public int[] Group = Array.Empty<int>();

        // Per cut (by its bone, which stays the same when the list of cuts changes), what was decided for every group.
        private readonly Dictionary<Transform, byte[]> _overrides = new Dictionary<Transform, byte[]>();

        /// <summary>Changes whenever a decision does.</summary>
        public int OverrideVersion;

        /// <summary>Per group: the position and the (averaged, normalised) normal.</summary>
        public Vector3[] GroupPosition = Array.Empty<Vector3>();

        public Vector3[] GroupNormal = Array.Empty<Vector3>();

        /// <summary>Three groups per triangle of the source mesh (degenerate triangles are left out).</summary>
        public int[] TriangleGroups = Array.Empty<int>();

        /// <summary>Only for a static mesh (<see cref="FromStatic"/>): the flat number of the mesh's triangle that each triangle of <see cref="TriangleGroups"/> is (empty otherwise).</summary>
        public int[] TriangleFlat = Array.Empty<int>();

        /// <summary>Per group, the first source vertex that was welded into it (its bone weights stand for the group).</summary>
        public int[] GroupFirstVertex = Array.Empty<int>();

        /// <summary>
        /// Per group and cut, how much of the group's bone weight sits on the cut's bone and below (<c>GroupShares[group * cuts + cut]</c>) and the
        /// segment the weights put the group in (0 = body, n + 1 = cut n). Filled by <see cref="SetNatural"/> once the cuts are analysed.
        /// </summary>
        public float[] GroupShares = Array.Empty<float>();

        public int[] GroupSegment = Array.Empty<int>();

        /// <summary>Where each group is on screen and whether it can be seen, for the last projection (reused, so a drag does not allocate).</summary>
        public Vector2[] Screen = Array.Empty<Vector2>();

        public bool[] Seen = Array.Empty<bool>();

        private int _naturalCuts;

        /// <summary>Per edge, its two groups.</summary>
        public int[] EdgeA = Array.Empty<int>();

        public int[] EdgeB = Array.Empty<int>();

        /// <summary>The neighbours of group g are <c>Neighbors[NeighborStart[g] .. NeighborStart[g + 1])</c>.</summary>
        public int[] NeighborStart = Array.Empty<int>();

        public int[] Neighbors = Array.Empty<int>();

        /// <summary>Per group, whether it is selected.</summary>
        public bool[] Selected = Array.Empty<bool>();

        /// <summary>Every edge as a thin line (drawn over the model in the edit tools).</summary>
        public Mesh Wire;

        /// <summary>Changes whenever the selection does (what is drawn from it is rebuilt then).</summary>
        public int Version;

        /// <summary>The number of vertex groups.</summary>
        public int GroupCount => GroupPosition.Length;

        /// <summary>The number of edges.</summary>
        public int EdgeCount => EdgeA.Length;

        /// <summary>The number of triangles.</summary>
        public int TriangleCount => TriangleGroups.Length / 3;

        /// <summary>
        /// Reads the mesh of <paramref name="renderer"/> in its current pose. Null when the mesh cannot be posed (the renderer's object is
        /// inactive, or the mesh gives back fewer vertices than it has).
        /// </summary>
        public static GoreStudioMeshEdit Build(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;
            var count = mesh.vertexCount;
            var baked = new Mesh();
            Vector3[] local;
            Vector3[] localNormals;
            var wasEnabled = renderer.enabled;
            try
            {
                renderer.enabled = true;
                renderer.BakeMesh(baked, true);
                local = baked.vertices;
                localNormals = baked.normals;
            }
            finally
            {
                renderer.enabled = wasEnabled;
                Object.DestroyImmediate(baked);
            }

            if (local.Length < count)
            {
                return null;
            }

            var matrix = Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
            var rotation = renderer.transform.rotation;
            var hasNormals = localNormals.Length >= count;
            var positions = new Vector3[count];
            var worldNormals = hasNormals ? new Vector3[count] : null;
            for (var v = 0; v < count; v++)
            {
                positions[v] = matrix.MultiplyPoint3x4(local[v]);
                if (hasNormals)
                {
                    worldNormals[v] = rotation * localNormals[v];
                }
            }

            var lists = new List<int[]>(mesh.subMeshCount);
            for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                if (mesh.GetSubMesh(submesh).topology == MeshTopology.Triangles)
                {
                    lists.Add(mesh.GetTriangles(submesh));
                }
            }

            return Create(positions, worldNormals, lists, false);
        }

        /// <summary>
        /// The edit data of a static mesh (no bones): its vertices moved to the world by <paramref name="localToWorld"/>, welded and linked like the
        /// skinned ones. <see cref="TriangleFlat"/> tells which triangle of the mesh (flat number, see <see cref="GoreMeshTopology.Flatten"/>) each
        /// triangle of <see cref="TriangleGroups"/> is.
        /// </summary>
        public static GoreStudioMeshEdit FromStatic(GoreMeshData data, Matrix4x4 localToWorld)
        {
            var count = data.Vertices.Length;
            var positions = new Vector3[count];
            var hasNormals = data.Normals.Length == count;
            var worldNormals = hasNormals ? new Vector3[count] : null;

            // A normal moves with the inverse transpose, so a scaled object keeps its normals true.
            var normalMatrix = localToWorld.inverse.transpose;
            for (var v = 0; v < count; v++)
            {
                positions[v] = localToWorld.MultiplyPoint3x4(data.Vertices[v]);
                if (hasNormals)
                {
                    worldNormals[v] = normalMatrix.MultiplyVector(data.Normals[v]);
                }
            }

            return Create(positions, worldNormals, data.Triangles, true);
        }

        // Welds the vertices into groups, and finds the triangles and edges between the groups. <vertexNormals> may be null.
        private static GoreStudioMeshEdit Create(Vector3[] positions, Vector3[] vertexNormals, IList<int[]> triangleLists, bool recordFlat)
        {
            var count = positions.Length;
            var hasNormals = vertexNormals != null;
            var edit = new GoreStudioMeshEdit { SourcePosition = positions };

            // Welding: one group per place.
            var group = new int[count];
            var ids = new Dictionary<(long, long, long), int>(count);
            var sums = new List<Vector3>(count);
            var normals = new List<Vector3>(count);
            var members = new List<int>(count);
            for (var v = 0; v < count; v++)
            {
                var position = positions[v];
                var key = ((long)Mathf.Round(position.x / WeldSize), (long)Mathf.Round(position.y / WeldSize), (long)Mathf.Round(position.z / WeldSize));
                if (!ids.TryGetValue(key, out var id))
                {
                    id = ids.Count;
                    ids.Add(key, id);
                    sums.Add(Vector3.zero);
                    normals.Add(Vector3.zero);
                    members.Add(0);
                }

                group[v] = id;
                sums[id] += position;
                members[id]++;
                if (hasNormals)
                {
                    normals[id] += vertexNormals[v];
                }
            }

            edit.Group = group;
            var groups = ids.Count;
            edit.GroupPosition = new Vector3[groups];
            edit.GroupNormal = new Vector3[groups];
            edit.Selected = new bool[groups];
            edit.Screen = new Vector2[groups];
            edit.Seen = new bool[groups];
            edit.GroupFirstVertex = new int[groups];
            for (var g = 0; g < groups; g++)
            {
                edit.GroupFirstVertex[g] = -1;
            }

            for (var v = 0; v < count; v++)
            {
                if (edit.GroupFirstVertex[group[v]] < 0)
                {
                    edit.GroupFirstVertex[group[v]] = v;
                }
            }

            for (var g = 0; g < groups; g++)
            {
                edit.GroupPosition[g] = sums[g] / members[g];
                var normal = normals[g];
                edit.GroupNormal[g] = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.zero;
            }

            // Triangles and edges.
            var triangles = new List<int>(count * 6);
            var edgeSet = new HashSet<long>();
            var edgeA = new List<int>(count * 3);
            var edgeB = new List<int>(count * 3);
            var flat = recordFlat ? new List<int>(count * 2) : null;
            var number = 0;
            for (var list = 0; list < triangleLists.Count; list++)
            {
                var indices = triangleLists[list];
                for (var k = 0; k + 2 < indices.Length; k += 3, number++)
                {
                    var a = group[indices[k]];
                    var b = group[indices[k + 1]];
                    var c = group[indices[k + 2]];
                    if (a == b || b == c || a == c)
                    {
                        continue;
                    }

                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                    flat?.Add(number);
                    AddEdge(edgeSet, edgeA, edgeB, a, b);
                    AddEdge(edgeSet, edgeA, edgeB, b, c);
                    AddEdge(edgeSet, edgeA, edgeB, c, a);
                }
            }

            edit.TriangleGroups = triangles.ToArray();
            if (flat != null)
            {
                edit.TriangleFlat = flat.ToArray();
            }

            edit.EdgeA = edgeA.ToArray();
            edit.EdgeB = edgeB.ToArray();
            edit.BuildNeighbors();
            edit.BuildWire();
            return edit;
        }

        private static void AddEdge(HashSet<long> set, List<int> a, List<int> b, int first, int second)
        {
            var low = first < second ? first : second;
            var high = first < second ? second : first;
            if (set.Add(((long)low << 32) | (uint)high))
            {
                a.Add(low);
                b.Add(high);
            }
        }

        private void BuildNeighbors()
        {
            var groups = GroupCount;
            NeighborStart = new int[groups + 1];
            for (var e = 0; e < EdgeA.Length; e++)
            {
                NeighborStart[EdgeA[e] + 1]++;
                NeighborStart[EdgeB[e] + 1]++;
            }

            for (var g = 0; g < groups; g++)
            {
                NeighborStart[g + 1] += NeighborStart[g];
            }

            Neighbors = new int[EdgeA.Length * 2];
            var fill = new int[groups];
            for (var e = 0; e < EdgeA.Length; e++)
            {
                Neighbors[NeighborStart[EdgeA[e]] + fill[EdgeA[e]]++] = EdgeB[e];
                Neighbors[NeighborStart[EdgeB[e]] + fill[EdgeB[e]]++] = EdgeA[e];
            }
        }

        private void BuildWire()
        {
            var vertices = new Vector3[EdgeA.Length * 2];
            var colors = new Color32[vertices.Length];
            var indices = new int[vertices.Length];
            var color = new Color32(10, 10, 12, 110);
            for (var e = 0; e < EdgeA.Length; e++)
            {
                vertices[e * 2] = GroupPosition[EdgeA[e]];
                vertices[e * 2 + 1] = GroupPosition[EdgeB[e]];
                colors[e * 2] = color;
                colors[e * 2 + 1] = color;
                indices[e * 2] = e * 2;
                indices[e * 2 + 1] = e * 2 + 1;
            }

            Wire = new Mesh { name = "Gore Studio wire", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            Wire.vertices = vertices;
            Wire.colors32 = colors;
            Wire.SetIndices(indices, MeshTopology.Lines, 0, false);
            Wire.RecalculateBounds();
        }

        /// <summary>Whether a group can be seen from the camera: one whose normal turns away from the camera is hidden (a cheap stand-in for occlusion).</summary>
        public bool Visible(int group, Vector3 cameraPosition, Vector3 forward, bool orthographic)
        {
            var normal = GroupNormal[group];
            if (normal == Vector3.zero)
            {
                return true;
            }

            var toCamera = orthographic ? -forward : cameraPosition - GroupPosition[group];
            return Vector3.Dot(normal, toCamera) > 0f;
        }

        #region Selection

        /// <summary>Selects or deselects one group.</summary>
        public void Set(int group, bool value)
        {
            if (Selected[group] != value)
            {
                Selected[group] = value;
                Version++;
            }
        }

        /// <summary>Deselects everything.</summary>
        public void Clear()
        {
            var changed = false;
            for (var g = 0; g < Selected.Length; g++)
            {
                changed |= Selected[g];
                Selected[g] = false;
            }

            if (changed)
            {
                Version++;
            }
        }

        /// <summary>Selects everything.</summary>
        public void SelectAll()
        {
            for (var g = 0; g < Selected.Length; g++)
            {
                Selected[g] = true;
            }

            Version++;
        }

        /// <summary>Selects what is not selected and the other way round.</summary>
        public void Invert()
        {
            for (var g = 0; g < Selected.Length; g++)
            {
                Selected[g] = !Selected[g];
            }

            Version++;
        }

        /// <summary>How many groups are selected.</summary>
        public int SelectedCount()
        {
            var count = 0;
            for (var g = 0; g < Selected.Length; g++)
            {
                if (Selected[g])
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Selects the loop an edge belongs to: from both ends of the edge it keeps going along the edge that continues most straight (within
        /// about 60 degrees), through the mesh's own flow lines, until the loop closes or turns. On a triangulated mesh this follows the
        /// quads' straight edges.
        /// </summary>
        public void SelectLoop(int edge)
        {
            var first = EdgeA[edge];
            var second = EdgeB[edge];
            var visited = new HashSet<int> { first, second };
            Set(first, true);
            Set(second, true);
            var endForward = Walk(first, second, visited, out var closedForward);
            var endBackward = Walk(second, first, visited, out var closedBackward);

            // A loop that stopped short of itself (an irregular vertex on the way) is joined over a gap of a few edges, so a ring round a body
            // part really closes: it is only a ring if nothing can get between its ends.
            if (!closedForward && !closedBackward && endForward != endBackward)
            {
                JoinGap(endForward, endBackward);
            }
        }

        // Keeps going from the edge (previous, current) along the straightest edge; returns the last vertex reached and whether it ran into the loop itself.
        private int Walk(int previous, int current, HashSet<int> visited, out bool closed)
        {
            closed = false;
            var limit = GroupCount;
            for (var step = 0; step < limit; step++)
            {
                var direction = (GroupPosition[current] - GroupPosition[previous]).normalized;
                var best = -1;
                var bestDot = LoopStraightness;
                for (var k = NeighborStart[current]; k < NeighborStart[current + 1]; k++)
                {
                    var candidate = Neighbors[k];
                    if (candidate == previous)
                    {
                        continue;
                    }

                    var dot = Vector3.Dot((GroupPosition[candidate] - GroupPosition[current]).normalized, direction);
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        best = candidate;
                    }
                }

                if (best < 0)
                {
                    return current;
                }

                if (!visited.Add(best))
                {
                    closed = true;
                    return current;
                }

                Set(best, true);
                previous = current;
                current = best;
            }

            return current;
        }

        // Selects the shortest way between the two ends of a loop when it is a few edges long at most.
        private void JoinGap(int from, int to)
        {
            const int MaxSteps = 10;
            var parent = new Dictionary<int, int> { { from, -1 } };
            var queue = new Queue<(int group, int depth)>();
            queue.Enqueue((from, 0));
            while (queue.Count > 0)
            {
                var (group, depth) = queue.Dequeue();
                if (group == to)
                {
                    for (var step = group; step >= 0; step = parent[step])
                    {
                        Set(step, true);
                    }

                    return;
                }

                if (depth >= MaxSteps)
                {
                    continue;
                }

                for (var k = NeighborStart[group]; k < NeighborStart[group + 1]; k++)
                {
                    var next = Neighbors[k];
                    if (!parent.ContainsKey(next))
                    {
                        parent.Add(next, group);
                        queue.Enqueue((next, depth + 1));
                    }
                }
            }
        }

        /// <summary>
        /// How far (in the preview's world) the nearest group that is inside a cut by its bone weights is from <paramref name="point"/>; infinity
        /// when the weights are not known or nothing is inside. Used to guess which cut a selection is meant for.
        /// </summary>
        public float NearestInside(int cut, Vector3 point)
        {
            var best = float.PositiveInfinity;
            if (!HasNatural || cut < 0 || cut >= _naturalCuts)
            {
                return best;
            }

            for (var g = 0; g < GroupCount; g++)
            {
                if (GroupShares[g * _naturalCuts + cut] >= 0.5f)
                {
                    var distance = (GroupPosition[g] - point).sqrMagnitude;
                    if (distance < best)
                    {
                        best = distance;
                    }
                }
            }

            return float.IsPositiveInfinity(best) ? best : Mathf.Sqrt(best);
        }

        #endregion

        #region Undo

        /// <summary>The selection and the decisions of one mesh, copied, so they can be put back.</summary>
        public sealed class State
        {
            public bool[] Selected;
            public Dictionary<Transform, byte[]> Overrides;
        }

        /// <summary>Copies the selection and the decisions.</summary>
        public State Capture()
        {
            var state = new State { Selected = (bool[])Selected.Clone(), Overrides = new Dictionary<Transform, byte[]>(_overrides.Count) };
            foreach (var pair in _overrides)
            {
                state.Overrides.Add(pair.Key, (byte[])pair.Value.Clone());
            }

            return state;
        }

        /// <summary>Puts back what <see cref="Capture"/> copied.</summary>
        public void Restore(State state)
        {
            Array.Copy(state.Selected, Selected, Math.Min(state.Selected.Length, Selected.Length));
            _overrides.Clear();
            foreach (var pair in state.Overrides)
            {
                _overrides.Add(pair.Key, (byte[])pair.Value.Clone());
            }

            Version++;
            OverrideVersion++;
        }

        #endregion

        #region Decisions by hand

        /// <summary>What was decided for a group and a cut (by its bone).</summary>
        public GoreOverrideKind GetOverride(Transform cutBone, int group)
        {
            return cutBone != null && _overrides.TryGetValue(cutBone, out var kinds) && group < kinds.Length ? (GoreOverrideKind)kinds[group] : GoreOverrideKind.Auto;
        }

        /// <summary>The decisions for a cut as an array per group, made when there is none yet (to be written to with <see cref="Decide"/>).</summary>
        public byte[] EnsureOverrides(Transform cutBone)
        {
            if (!_overrides.TryGetValue(cutBone, out var kinds))
            {
                kinds = new byte[GroupCount];
                _overrides.Add(cutBone, kinds);
            }

            return kinds;
        }

        /// <summary>The decisions for a cut as an array per group (null when there are none): for drawing, without a lookup per vertex.</summary>
        public byte[] OverridesOf(Transform cutBone) => cutBone != null && _overrides.TryGetValue(cutBone, out var kinds) ? kinds : null;

        /// <summary>Decides a group for a cut: put it in the part, keep it out, or leave it to the weights.</summary>
        public void SetOverride(Transform cutBone, int group, GoreOverrideKind kind)
        {
            if (cutBone == null || group < 0 || group >= GroupCount)
            {
                return;
            }

            if (!_overrides.TryGetValue(cutBone, out var kinds))
            {
                if (kind == GoreOverrideKind.Auto)
                {
                    return;
                }

                kinds = new byte[GroupCount];
                _overrides.Add(cutBone, kinds);
            }

            if (kinds[group] != (byte)kind)
            {
                kinds[group] = (byte)kind;
                OverrideVersion++;
            }
        }

        /// <summary>How many groups have this decision for a cut.</summary>
        public int CountOverrides(Transform cutBone, GoreOverrideKind kind)
        {
            var kinds = OverridesOf(cutBone);
            var count = 0;
            if (kinds != null)
            {
                for (var g = 0; g < kinds.Length; g++)
                {
                    if (kinds[g] == (byte)kind)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>Takes back every decision for a cut.</summary>
        public void ClearOverrides(Transform cutBone)
        {
            if (cutBone != null && _overrides.Remove(cutBone))
            {
                OverrideVersion++;
            }
        }

        /// <summary>
        /// The decisions as the splitter and the profile take them: one entry per source vertex (a welded group is every vertex in it), the cut
        /// as its number in <paramref name="cuts"/>. Null when there are none.
        /// </summary>
        public GoreVertexOverride[] ToOverrides(IList<GoreBaker.CutSpec> cuts)
        {
            List<GoreVertexOverride> list = null;
            for (var cut = 0; cut < cuts.Count; cut++)
            {
                var kinds = OverridesOf(cuts[cut].Bone);
                if (kinds == null)
                {
                    continue;
                }

                for (var v = 0; v < Group.Length; v++)
                {
                    var kind = kinds[Group[v]];
                    if (kind != 0)
                    {
                        list = list ?? new List<GoreVertexOverride>();
                        list.Add(new GoreVertexOverride { Cut = cut, Vertex = v, Kind = (GoreOverrideKind)kind });
                    }
                }
            }

            return list?.ToArray();
        }

        /// <summary>Gives the groups the bone-weight view of the cuts: which side of each cut they are on, as the analysis found it.</summary>
        public void SetNatural(float[] sourceShares, int cutCount, int[] segmentParent)
        {
            _naturalCuts = cutCount;
            GroupShares = new float[GroupCount * cutCount];
            GroupSegment = new int[GroupCount];
            var depth = new int[segmentParent.Length];
            for (var segment = 0; segment < depth.Length; segment++)
            {
                for (var p = segmentParent[segment]; p >= 0 && depth[segment] <= depth.Length; p = segmentParent[p])
                {
                    depth[segment]++;
                }
            }

            for (var g = 0; g < GroupCount; g++)
            {
                var vertex = GroupFirstVertex[g];
                var best = 0;
                for (var cut = 0; cut < cutCount; cut++)
                {
                    var share = vertex >= 0 && (vertex + 1) * cutCount <= sourceShares.Length ? sourceShares[vertex * cutCount + cut] : 0f;
                    GroupShares[g * cutCount + cut] = share;
                    if (share >= 0.5f && depth[cut + 1] > depth[best])
                    {
                        best = cut + 1;
                    }
                }

                GroupSegment[g] = best;
            }
        }

        /// <summary>Whether the weights are known (the cuts were analysed), so a decision can fill a region.</summary>
        public bool HasNatural => _naturalCuts > 0 && GroupShares.Length == GroupCount * _naturalCuts;

        // Whether every group is in a cut now: its bone weights, changed by the decisions by hand of every cut with the same rules the splitter
        // uses (an exclude is out of the cut and every cut below it; an include is in the cut and every cut above it and out of every cut that is
        // neither; excludes first). One array of answers for all groups, so the fill does not redo it for every step.
        private bool[] InsideOf(int cut, byte[][] kindsByCut, int[] segmentParent)
        {
            var cutCount = _naturalCuts;
            var inside = new bool[GroupCount];
            var shares = new float[cutCount];
            for (var g = 0; g < GroupCount; g++)
            {
                for (var c = 0; c < cutCount; c++)
                {
                    shares[c] = GroupShares[g * cutCount + c];
                }

                for (var pass = 0; pass < 2; pass++)
                {
                    for (var k = 0; k < cutCount && k < kindsByCut.Length; k++)
                    {
                        var kinds = kindsByCut[k];
                        var kind = kinds != null ? kinds[g] : (byte)0;
                        if (kind == 0 || (pass == 0) != (kind == (byte)GoreOverrideKind.Exclude))
                        {
                            continue;
                        }

                        var segment = k + 1;
                        for (var s = 1; s <= cutCount; s++)
                        {
                            if (kind == (byte)GoreOverrideKind.Exclude)
                            {
                                if (IsBelow(segmentParent, segment, s))
                                {
                                    shares[s - 1] = 0f;
                                }
                            }
                            else if (IsBelow(segmentParent, s, segment))
                            {
                                shares[s - 1] = 1f;
                            }
                            else if (!IsBelow(segmentParent, segment, s))
                            {
                                shares[s - 1] = 0f;
                            }
                        }
                    }
                }

                inside[g] = shares[cut] >= 0.5f;
            }

            return inside;
        }

        // Whether segment <b> is segment <a> or below it.
        private static bool IsBelow(int[] segmentParent, int a, int b)
        {
            for (var guard = 0; b >= 0 && guard <= segmentParent.Length; guard++)
            {
                if (b == a)
                {
                    return true;
                }

                b = segmentParent[b];
            }

            return false;
        }

        /// <summary>
        /// Decides the selected groups for a cut, with the selection as the cut line. The selected groups cut the mesh into pieces; the piece
        /// that is the part's side (most of the cut's own groups, fewest of the groups of the segment it is cut from) keeps or gets the cut, the
        /// pieces on the other side give up what they had of it. Add (Include): the part's side and the selection go with the cut, whatever the
        /// other side had of it goes back to the body. Remove (Exclude): the selection and the other side go back to the body, the part's side
        /// stays as it is. When the selection does not cut the mesh into pieces (a patch, half a ring) there is no line, and just the selected
        /// groups are decided. Auto takes the decision back for the selected groups only.
        /// Returns how many groups were decided; <paramref name="included"/> and <paramref name="excluded"/> count the groups the line moved
        /// (not the selected ones), <paramref name="separated"/> says there was a line.
        /// </summary>
        public int Decide(byte[][] kindsByCut, int cut, GoreOverrideKind kind, int[] segmentParent, out int included, out int excluded, out bool separated, out int closed)
        {
            included = 0;
            excluded = 0;
            separated = false;
            closed = 0;
            if (kindsByCut == null || cut < 0 || cut >= kindsByCut.Length || kindsByCut[cut] == null)
            {
                return 0;
            }

            var kinds = kindsByCut[cut];
            var changed = false;
            var decided = 0;

            if (kind != GoreOverrideKind.Auto && HasNatural && cut < _naturalCuts)
            {
                var inside = InsideOf(cut, kindsByCut, segmentParent);
                var parent = segmentParent[cut + 1];
                var piece = new int[GroupCount];
                var score = new List<int>();
                var pieceCount = Pieces(inside, parent, piece, score);

                // An arc that does not go all the way round is closed on the far side of the body part first (the shortest way between its two
                // ends that does not touch it): the owner draws the line where it should be, the rest of the ring follows from it.
                if (pieceCount < 2)
                {
                    var closure = CloseArc();
                    if (closure != null)
                    {
                        foreach (var group in closure)
                        {
                            Selected[group] = true;
                        }

                        pieceCount = Pieces(inside, parent, piece, score);
                        if (pieceCount >= 2)
                        {
                            closed = closure.Count;
                            Version++;
                        }
                        else
                        {
                            foreach (var group in closure)
                            {
                                Selected[group] = false;
                            }
                        }
                    }
                }

                if (pieceCount >= 2)
                {
                    separated = true;
                    var core = 0;
                    for (var id = 1; id < pieceCount; id++)
                    {
                        if (score[id] > score[core])
                        {
                            core = id;
                        }
                    }

                    for (var g = 0; g < GroupCount; g++)
                    {
                        if (Selected[g])
                        {
                            continue;
                        }

                        if (piece[g] == core)
                        {
                            if (kind == GoreOverrideKind.Include && !inside[g])
                            {
                                changed |= kinds[g] != (byte)GoreOverrideKind.Include;
                                kinds[g] = (byte)GoreOverrideKind.Include;
                                included++;
                                decided++;
                            }
                        }
                        else if (inside[g])
                        {
                            changed |= kinds[g] != (byte)GoreOverrideKind.Exclude;
                            kinds[g] = (byte)GoreOverrideKind.Exclude;
                            excluded++;
                            decided++;
                        }
                    }
                }
            }

            for (var g = 0; g < Selected.Length; g++)
            {
                if (Selected[g])
                {
                    changed |= kinds[g] != (byte)kind;
                    kinds[g] = (byte)kind;
                    decided++;
                }
            }

            if (changed)
            {
                OverrideVersion++;
            }

            return decided;
        }

        // The pieces of the mesh without the selected groups (breadth first over the welded vertices): fills <piece> (the piece of every
        // group, -1 for a selected one) and <score> (per piece: groups inside the cut minus groups of the segment it is cut from). Returns the count.
        private int Pieces(bool[] inside, int parent, int[] piece, List<int> score)
        {
            for (var g = 0; g < GroupCount; g++)
            {
                piece[g] = -1;
            }

            score.Clear();
            var pieceCount = 0;
            var queue = new Queue<int>();
            for (var start = 0; start < GroupCount; start++)
            {
                if (Selected[start] || piece[start] >= 0)
                {
                    continue;
                }

                var id = pieceCount++;
                var value = 0;
                piece[start] = id;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var group = queue.Dequeue();
                    value += (inside[group] ? 1 : 0) - (GroupSegment[group] == parent ? 1 : 0);
                    for (var k = NeighborStart[group]; k < NeighborStart[group + 1]; k++)
                    {
                        var next = Neighbors[k];
                        if (!Selected[next] && piece[next] < 0)
                        {
                            piece[next] = id;
                            queue.Enqueue(next);
                        }
                    }
                }

                score.Add(value);
            }

            return pieceCount;
        }

        // The groups that would close the selection into a ring, or null. The selection has to be one connected arc; its two ends (the ends of
        // its longest path) are joined by the shortest way over the mesh that stays off the arc, as long as that way is not much longer than the arc.
        private List<int> CloseArc()
        {
            var members = new List<int>();
            for (var g = 0; g < GroupCount; g++)
            {
                if (Selected[g])
                {
                    members.Add(g);
                }
            }

            if (members.Count < 3)
            {
                return null;
            }

            // The farthest group from any member inside the selection, twice: the two ends of its longest path.
            var first = Farthest(members[0], out var reached);
            if (reached != members.Count)
            {
                return null;
            }

            var end = Farthest(first, out _);
            var start = first;
            var arcLength = PathLengthInSelection(start, end);

            // Dijkstra over the whole mesh, from one end to the other, never through the arc itself.
            var distance = new float[GroupCount];
            var from = new int[GroupCount];
            for (var g = 0; g < GroupCount; g++)
            {
                distance[g] = float.PositiveInfinity;
                from[g] = -1;
            }

            var open = new SortedSet<(float, int)> { (0f, start) };
            distance[start] = 0f;
            var limit = arcLength * 2.5f + 1e-3f;
            while (open.Count > 0)
            {
                var (current, group) = open.Min;
                open.Remove(open.Min);
                if (group == end)
                {
                    break;
                }

                if (current > limit)
                {
                    return null;
                }

                for (var k = NeighborStart[group]; k < NeighborStart[group + 1]; k++)
                {
                    var next = Neighbors[k];
                    if (Selected[next] && next != end)
                    {
                        continue;
                    }

                    var candidate = current + (GroupPosition[next] - GroupPosition[group]).magnitude;
                    if (candidate < distance[next])
                    {
                        if (!float.IsPositiveInfinity(distance[next]))
                        {
                            open.Remove((distance[next], next));
                        }

                        distance[next] = candidate;
                        from[next] = group;
                        open.Add((candidate, next));
                    }
                }
            }

            if (float.IsPositiveInfinity(distance[end]) || distance[end] > limit)
            {
                return null;
            }

            var path = new List<int>();
            for (var step = from[end]; step >= 0 && step != start; step = from[step])
            {
                path.Add(step);
            }

            return path.Count > 0 ? path : null;
        }

        // The selected group farthest (in steps over selected groups) from <origin>; <reached> is how many selected groups can be reached at all.
        private int Farthest(int origin, out int reached)
        {
            var depth = new Dictionary<int, int> { { origin, 0 } };
            var queue = new Queue<int>();
            queue.Enqueue(origin);
            var best = origin;
            while (queue.Count > 0)
            {
                var group = queue.Dequeue();
                if (depth[group] > depth[best])
                {
                    best = group;
                }

                for (var k = NeighborStart[group]; k < NeighborStart[group + 1]; k++)
                {
                    var next = Neighbors[k];
                    if (Selected[next] && !depth.ContainsKey(next))
                    {
                        depth.Add(next, depth[group] + 1);
                        queue.Enqueue(next);
                    }
                }
            }

            reached = depth.Count;
            return best;
        }

        // The length of the shortest way between two selected groups that stays on selected groups.
        private float PathLengthInSelection(int start, int end)
        {
            var distance = new Dictionary<int, float> { { start, 0f } };
            var open = new SortedSet<(float, int)> { (0f, start) };
            while (open.Count > 0)
            {
                var (current, group) = open.Min;
                open.Remove(open.Min);
                if (group == end)
                {
                    return current;
                }

                for (var k = NeighborStart[group]; k < NeighborStart[group + 1]; k++)
                {
                    var next = Neighbors[k];
                    if (!Selected[next])
                    {
                        continue;
                    }

                    var candidate = current + (GroupPosition[next] - GroupPosition[group]).magnitude;
                    if (!distance.TryGetValue(next, out var known) || candidate < known)
                    {
                        if (distance.ContainsKey(next))
                        {
                            open.Remove((known, next));
                        }

                        distance[next] = candidate;
                        open.Add((candidate, next));
                    }
                }
            }

            return float.PositiveInfinity;
        }

        #endregion

        /// <summary>Frees the wire mesh.</summary>
        public void Dispose()
        {
            if (Wire != null)
            {
                Object.DestroyImmediate(Wire);
                Wire = null;
            }
        }
    }
}
