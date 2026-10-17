using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// Plain-array helpers for a mesh that is broken into pieces (a static mesh: no bones): the triangles of every submesh as one flat list,
    /// which triangle touches which, and whether the surface is closed. Points at the same place count as one, so a hard edge or a texture seam
    /// (the same place with two vertices) does not open the surface.
    /// </summary>
    public static class GoreMeshTopology
    {
        // Positions closer than this (in the mesh's own units) are one point.
        private const float WeldSize = 1e-4f;

        /// <summary>The number of triangles of all submeshes together.</summary>
        public static int TriangleCount(GoreMeshData mesh)
        {
            var count = 0;
            for (var s = 0; s < mesh.Triangles.Length; s++)
            {
                count += mesh.Triangles[s].Length / 3;
            }

            return count;
        }

        /// <summary>
        /// The corners of every triangle as one list (three vertex numbers per triangle). The triangles of submesh 0 come first, then those of submesh 1 and
        /// so on; this "flat" number of a triangle is the one every other call here and in the pieces uses. <paramref name="submesh"/> says which
        /// submesh each triangle is in.
        /// </summary>
        public static int[] Flatten(GoreMeshData mesh, out int[] submesh)
        {
            var count = TriangleCount(mesh);
            var corners = new int[count * 3];
            submesh = new int[count];
            var next = 0;
            for (var s = 0; s < mesh.Triangles.Length; s++)
            {
                var list = mesh.Triangles[s];
                for (var k = 0; k + 2 < list.Length; k += 3)
                {
                    corners[next * 3] = list[k];
                    corners[next * 3 + 1] = list[k + 1];
                    corners[next * 3 + 2] = list[k + 2];
                    submesh[next] = s;
                    next++;
                }
            }

            return corners;
        }

        /// <summary>A number for every vertex that is the same for vertices at the same place.</summary>
        public static int[] Weld(Vector3[] positions)
        {
            var result = new int[positions.Length];
            var map = new WeldMap();
            for (var v = 0; v < positions.Length; v++)
            {
                result[v] = map.Number(positions[v]);
            }

            return result;
        }

        /// <summary>
        /// Hands out one number per place: points closer than the weld size get the same number, also when a rounding border lies between them (two
        /// points that differ by a rounding error in the last digit must not be told apart).
        /// </summary>
        internal sealed class WeldMap
        {
            private readonly Dictionary<(long, long, long), List<int>> _cells = new Dictionary<(long, long, long), List<int>>();
            private readonly List<Vector3> _points = new List<Vector3>();

            internal int Number(Vector3 p)
            {
                var cx = (long)Mathf.Floor(p.x / WeldSize);
                var cy = (long)Mathf.Floor(p.y / WeldSize);
                var cz = (long)Mathf.Floor(p.z / WeldSize);
                var limit = WeldSize * WeldSize;
                for (var x = cx - 1; x <= cx + 1; x++)
                {
                    for (var y = cy - 1; y <= cy + 1; y++)
                    {
                        for (var z = cz - 1; z <= cz + 1; z++)
                        {
                            if (!_cells.TryGetValue((x, y, z), out var ids))
                            {
                                continue;
                            }

                            for (var i = 0; i < ids.Count; i++)
                            {
                                if ((_points[ids[i]] - p).sqrMagnitude <= limit)
                                {
                                    return ids[i];
                                }
                            }
                        }
                    }
                }

                var id = _points.Count;
                _points.Add(p);
                if (!_cells.TryGetValue((cx, cy, cz), out var own))
                {
                    own = new List<int>(2);
                    _cells.Add((cx, cy, cz), own);
                }

                own.Add(id);
                return id;
            }
        }

        internal static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        internal static long DirectedKey(int from, int to) => ((long)from << 32) | (uint)to;

        /// <summary>
        /// For every triangle, the triangles that share an edge with it (CSR: the neighbours of triangle t are <c>list[start[t]]</c> up to but not including
        /// <c>list[start[t + 1]]</c>). A triangle with a corner that has no area, and the edge of a corner pair at one place, are ignored.
        /// </summary>
        public static void Adjacency(int[] corners, int[] weld, out int[] start, out int[] list)
        {
            var count = corners.Length / 3;
            var edges = new Dictionary<long, List<int>>(count * 2);
            for (var t = 0; t < count; t++)
            {
                for (var k = 0; k < 3; k++)
                {
                    var a = weld[corners[t * 3 + k]];
                    var b = weld[corners[t * 3 + (k + 1) % 3]];
                    if (a == b)
                    {
                        continue;
                    }

                    var key = EdgeKey(a, b);
                    if (!edges.TryGetValue(key, out var users))
                    {
                        users = new List<int>(2);
                        edges.Add(key, users);
                    }

                    users.Add(t);
                }
            }

            var degree = new int[count + 1];
            foreach (var users in edges.Values)
            {
                for (var i = 0; i < users.Count; i++)
                {
                    for (var j = 0; j < users.Count; j++)
                    {
                        if (i != j && users[i] != users[j])
                        {
                            degree[users[i] + 1]++;
                        }
                    }
                }
            }

            start = new int[count + 1];
            for (var t = 0; t < count; t++)
            {
                start[t + 1] = start[t] + degree[t + 1];
            }

            list = new int[start[count]];
            var fill = new int[count];
            foreach (var users in edges.Values)
            {
                for (var i = 0; i < users.Count; i++)
                {
                    for (var j = 0; j < users.Count; j++)
                    {
                        if (i != j && users[i] != users[j])
                        {
                            var t = users[i];
                            list[start[t] + fill[t]++] = users[j];
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Whether the surface is closed: every edge between two points is used by exactly two triangles that run along it in opposite directions (a
        /// crate, a statue, a table top; not a plane or a wall). Only a closed surface gets lids where it is broken.
        /// </summary>
        public static bool IsClosed(int[] corners, int[] weld)
        {
            var count = corners.Length / 3;
            var directed = new Dictionary<long, int>(count * 3);
            var any = false;
            for (var t = 0; t < count; t++)
            {
                var a = weld[corners[t * 3]];
                var b = weld[corners[t * 3 + 1]];
                var c = weld[corners[t * 3 + 2]];
                if (a == b || b == c || c == a)
                {
                    continue;
                }

                any = true;
                for (var k = 0; k < 3; k++)
                {
                    var from = weld[corners[t * 3 + k]];
                    var to = weld[corners[t * 3 + (k + 1) % 3]];
                    var key = DirectedKey(from, to);
                    directed.TryGetValue(key, out var seen);
                    if (seen > 0)
                    {
                        // Two triangles run the same way along one edge: the surface is not oriented, or the edge is used three times.
                        return false;
                    }

                    directed[key] = 1;
                }
            }

            if (!any)
            {
                return false;
            }

            foreach (var key in directed.Keys)
            {
                var from = (int)(key >> 32);
                var to = (int)(key & 0xffffffffL);
                if (!directed.ContainsKey(DirectedKey(to, from)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Numbers the separate pieces of the surface: triangles that share an edge are one piece. Returns for every triangle (flat number) its piece,
        /// and the number of pieces in <paramref name="count"/>.
        /// </summary>
        public static int[] TriangleIslands(int[] start, int[] list, out int count)
        {
            var triangles = start.Length - 1;
            var result = new int[triangles];
            for (var t = 0; t < triangles; t++)
            {
                result[t] = -1;
            }

            count = 0;
            var stack = new Stack<int>();
            for (var t = 0; t < triangles; t++)
            {
                if (result[t] >= 0)
                {
                    continue;
                }

                result[t] = count;
                stack.Push(t);
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    for (var i = start[current]; i < start[current + 1]; i++)
                    {
                        var next = list[i];
                        if (result[next] < 0)
                        {
                            result[next] = count;
                            stack.Push(next);
                        }
                    }
                }

                count++;
            }

            return result;
        }
    }

    /// <summary>A small binary min-heap of (key, id, extra) entries; the key decides the order, ties go to the smaller id so a result is the same every time.</summary>
    internal sealed class GoreHeap
    {
        internal struct Entry
        {
            internal double Key;
            internal int Id;
            internal int Extra;
        }

        private Entry[] _items = new Entry[64];
        private int _count;

        internal int Count => _count;

        internal void Push(double key, int id, int extra)
        {
            if (_count == _items.Length)
            {
                Array.Resize(ref _items, _count * 2);
            }

            var i = _count++;
            _items[i] = new Entry { Key = key, Id = id, Extra = extra };
            while (i > 0)
            {
                var parent = (i - 1) / 2;
                if (!Less(_items[i], _items[parent]))
                {
                    break;
                }

                (_items[i], _items[parent]) = (_items[parent], _items[i]);
                i = parent;
            }
        }

        internal Entry Pop()
        {
            var top = _items[0];
            _items[0] = _items[--_count];
            var i = 0;
            while (true)
            {
                var left = i * 2 + 1;
                var right = left + 1;
                var smallest = i;
                if (left < _count && Less(_items[left], _items[smallest]))
                {
                    smallest = left;
                }

                if (right < _count && Less(_items[right], _items[smallest]))
                {
                    smallest = right;
                }

                if (smallest == i)
                {
                    break;
                }

                (_items[i], _items[smallest]) = (_items[smallest], _items[i]);
                i = smallest;
            }

            return top;
        }

        private static bool Less(Entry a, Entry b) => a.Key < b.Key || (a.Key == b.Key && a.Id < b.Id);
    }
}
