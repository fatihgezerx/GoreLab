using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoreLab
{
    /// <summary>What <see cref="GoreTessellator.Tessellate"/> made: the finer mesh and which of its triangles are new.</summary>
    public sealed class GoreTessellation
    {
        /// <summary>The finer mesh. Same submeshes and attributes as the source; the source itself is never changed.</summary>
        public GoreMeshData Mesh;

        /// <summary>For every triangle of <see cref="Mesh"/> (flat number, see <see cref="GoreMeshTopology.Flatten"/>), whether it was made by a split (both halves of a split triangle are new).</summary>
        public bool[] IsNew;

        /// <summary>How many triangles were added to the source.</summary>
        public int Added;
    }

    /// <summary>
    /// Adds triangles to a mesh so that it has edges to be broken along (a flat wall of two triangles can only break into two pieces). It splits the
    /// biggest triangle again and again along its longest edge, and the triangle on the other side of that edge along the same edge, so no crack
    /// opens. Pure arrays in, pure arrays out.
    /// </summary>
    public static class GoreTessellator
    {
        /// <summary>
        /// Adds at least <paramref name="extra"/> triangles (one more when the last split also had to split a neighbour). With <paramref name="selected"/>
        /// (one flag per triangle of the source, flat numbers) only those triangles, and the triangles their splits made, are chosen to be split; a neighbour
        /// outside the selection is still split along the shared edge when it must be, to keep the surface closed. Null = every triangle may be split.
        /// New vertices get position, normal, tangent, colour and every texture coordinate blended from the two ends of the edge; two vertices at one place
        /// (a hard edge, a texture seam) each get their own new vertex, at the same place, so the seam stays.
        /// </summary>
        public static GoreTessellation Tessellate(GoreMeshData source, int extra, bool[] selected)
        {
            var flatCorners = GoreMeshTopology.Flatten(source, out var flatSubmesh);
            var triangleCount = flatCorners.Length / 3;
            var work = new Work(source, flatCorners, flatSubmesh, selected != null && selected.Length == triangleCount ? selected : null);
            work.Run(extra);
            return work.Result(source);
        }

        private sealed class Work
        {
            private readonly List<Vector3> _positions;
            private readonly List<Vector3> _normals;
            private readonly List<Vector4> _tangents;
            private readonly List<Color32> _colors;
            private readonly List<Vector4>[] _uvs = new List<Vector4>[4];
            private readonly List<BoneWeight> _weights;
            private readonly List<int> _weld = new List<int>();
            private readonly GoreMeshTopology.WeldMap _weldIds = new GoreMeshTopology.WeldMap();
            private readonly Dictionary<long, int> _mids = new Dictionary<long, int>();
            private readonly Dictionary<long, List<int>> _edges = new Dictionary<long, List<int>>();

            private readonly List<int> _corners = new List<int>();
            private readonly List<int> _submesh = new List<int>();
            private readonly List<bool> _isNew = new List<bool>();
            private readonly List<bool> _eligible = new List<bool>();
            private readonly List<int> _version = new List<int>();
            private readonly GoreHeap _heap = new GoreHeap();
            private int _added;

            internal Work(GoreMeshData source, int[] corners, int[] submesh, bool[] selected)
            {
                _positions = new List<Vector3>(source.Vertices);
                var count = source.Vertices.Length;
                _normals = source.Normals.Length == count ? new List<Vector3>(source.Normals) : null;
                _tangents = source.Tangents.Length == count ? new List<Vector4>(source.Tangents) : null;
                _colors = source.Colors.Length == count ? new List<Color32>(source.Colors) : null;
                _weights = source.Weights.Length == count ? new List<BoneWeight>(source.Weights) : null;
                for (var c = 0; c < 4; c++)
                {
                    if (source.Uvs[c] != null && source.Uvs[c].Length == count)
                    {
                        _uvs[c] = new List<Vector4>(source.Uvs[c]);
                    }
                }

                for (var v = 0; v < count; v++)
                {
                    _weld.Add(_weldIds.Number(_positions[v]));
                }

                var triangles = corners.Length / 3;
                for (var t = 0; t < triangles; t++)
                {
                    _corners.Add(corners[t * 3]);
                    _corners.Add(corners[t * 3 + 1]);
                    _corners.Add(corners[t * 3 + 2]);
                    _submesh.Add(submesh[t]);
                    _isNew.Add(false);
                    _eligible.Add(selected == null || selected[t]);
                    _version.Add(0);
                    Register(t);
                    if (_eligible[t])
                    {
                        _heap.Push(-Area(t), t, 0);
                    }
                }
            }

            internal void Run(int extra)
            {
                while (_added < extra && _heap.Count > 0)
                {
                    var entry = _heap.Pop();
                    var t = entry.Id;
                    if (entry.Extra != _version[t] || !_eligible[t])
                    {
                        continue;
                    }

                    if (-entry.Key <= 1e-12)
                    {
                        // The biggest triangle left has no area: nothing worth splitting remains.
                        break;
                    }

                    var k = LongestEdge(t);
                    var a = _corners[t * 3 + k];
                    var b = _corners[t * 3 + (k + 1) % 3];
                    var key = GoreMeshTopology.EdgeKey(_weld[a], _weld[b]);

                    // The triangles on the other side of this edge, found before the split changes who uses it.
                    var others = new List<int>(2);
                    if (_edges.TryGetValue(key, out var users))
                    {
                        for (var i = 0; i < users.Count; i++)
                        {
                            if (users[i] != t)
                            {
                                others.Add(users[i]);
                            }
                        }
                    }

                    Split(t, k);
                    for (var i = 0; i < others.Count; i++)
                    {
                        var n = others[i];
                        var edge = EdgeOf(n, _weld[a], _weld[b]);
                        if (edge >= 0)
                        {
                            Split(n, edge);
                        }
                    }
                }
            }

            private void Register(int t)
            {
                for (var k = 0; k < 3; k++)
                {
                    var a = _weld[_corners[t * 3 + k]];
                    var b = _weld[_corners[t * 3 + (k + 1) % 3]];
                    if (a == b)
                    {
                        continue;
                    }

                    var key = GoreMeshTopology.EdgeKey(a, b);
                    if (!_edges.TryGetValue(key, out var users))
                    {
                        users = new List<int>(2);
                        _edges.Add(key, users);
                    }

                    users.Add(t);
                }
            }

            private void Unregister(int t)
            {
                for (var k = 0; k < 3; k++)
                {
                    var a = _weld[_corners[t * 3 + k]];
                    var b = _weld[_corners[t * 3 + (k + 1) % 3]];
                    if (a != b && _edges.TryGetValue(GoreMeshTopology.EdgeKey(a, b), out var users))
                    {
                        users.Remove(t);
                    }
                }
            }

            // The corner a triangle's edge starts at when it runs between the two points (either direction), or -1.
            private int EdgeOf(int t, int weldA, int weldB)
            {
                for (var k = 0; k < 3; k++)
                {
                    var a = _weld[_corners[t * 3 + k]];
                    var b = _weld[_corners[t * 3 + (k + 1) % 3]];
                    if ((a == weldA && b == weldB) || (a == weldB && b == weldA))
                    {
                        return k;
                    }
                }

                return -1;
            }

            private int LongestEdge(int t)
            {
                var best = 0;
                var bestLength = -1f;
                for (var k = 0; k < 3; k++)
                {
                    var length = (_positions[_corners[t * 3 + (k + 1) % 3]] - _positions[_corners[t * 3 + k]]).sqrMagnitude;
                    if (length > bestLength)
                    {
                        bestLength = length;
                        best = k;
                    }
                }

                return best;
            }

            private float Area(int t)
            {
                var a = _positions[_corners[t * 3]];
                var b = _positions[_corners[t * 3 + 1]];
                var c = _positions[_corners[t * 3 + 2]];
                return Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }

            // Splits triangle t at the middle of its edge that starts at corner k. The first half keeps the number t, the second is appended.
            private void Split(int t, int k)
            {
                var a = _corners[t * 3 + k];
                var b = _corners[t * 3 + (k + 1) % 3];
                var c = _corners[t * 3 + (k + 2) % 3];
                var m = Middle(a, b);
                Unregister(t);

                _corners[t * 3] = a;
                _corners[t * 3 + 1] = m;
                _corners[t * 3 + 2] = c;
                _isNew[t] = true;
                _version[t]++;

                var second = _version.Count;
                _corners.Add(m);
                _corners.Add(b);
                _corners.Add(c);
                _submesh.Add(_submesh[t]);
                _isNew.Add(true);
                _eligible.Add(_eligible[t]);
                _version.Add(0);

                Register(t);
                Register(second);
                _added++;
                if (_eligible[t])
                {
                    _heap.Push(-Area(t), t, _version[t]);
                    _heap.Push(-Area(second), second, 0);
                }
            }

            // The vertex in the middle of the edge between vertices a and b: made once for the pair, whoever asks.
            private int Middle(int a, int b)
            {
                var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (_mids.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                var index = _positions.Count;
                var position = (_positions[a] + _positions[b]) * 0.5f;
                _positions.Add(position);
                _weld.Add(_weldIds.Number(position));
                if (_normals != null)
                {
                    var n = _normals[a] + _normals[b];
                    _normals.Add(n.sqrMagnitude > 1e-12f ? n.normalized : _normals[a]);
                }

                if (_tangents != null)
                {
                    var ta = _tangents[a];
                    var tb = _tangents[b];
                    var sum = new Vector3(ta.x + tb.x, ta.y + tb.y, ta.z + tb.z);
                    sum = sum.sqrMagnitude > 1e-12f ? sum.normalized : new Vector3(ta.x, ta.y, ta.z);
                    _tangents.Add(new Vector4(sum.x, sum.y, sum.z, ta.w));
                }

                if (_colors != null)
                {
                    _colors.Add(Color32.Lerp(_colors[a], _colors[b], 0.5f));
                }

                if (_weights != null)
                {
                    _weights.Add(_weights[a]);
                }

                for (var c = 0; c < 4; c++)
                {
                    if (_uvs[c] != null)
                    {
                        _uvs[c].Add((_uvs[c][a] + _uvs[c][b]) * 0.5f);
                    }
                }

                _mids.Add(key, index);
                return index;
            }

            internal GoreTessellation Result(GoreMeshData source)
            {
                var mesh = new GoreMeshData
                {
                    Vertices = _positions.ToArray(),
                    Normals = _normals != null ? _normals.ToArray() : Array.Empty<Vector3>(),
                    Tangents = _tangents != null ? _tangents.ToArray() : Array.Empty<Vector4>(),
                    Colors = _colors != null ? _colors.ToArray() : Array.Empty<Color32>(),
                    Weights = _weights != null ? _weights.ToArray() : Array.Empty<BoneWeight>(),
                    BindPoses = source.BindPoses,
                    Bounds = source.Bounds,
                    Triangles = new int[source.Triangles.Length][]
                };

                for (var c = 0; c < 4; c++)
                {
                    if (_uvs[c] != null)
                    {
                        mesh.Uvs[c] = _uvs[c].ToArray();
                        mesh.UvDimension[c] = source.UvDimension[c];
                    }
                }

                // The triangles go back into their submeshes in list order; the flat number of the result follows that order.
                var counts = new int[mesh.Triangles.Length];
                for (var t = 0; t < _submesh.Count; t++)
                {
                    counts[_submesh[t]]++;
                }

                var next = new int[mesh.Triangles.Length];
                for (var s = 0; s < mesh.Triangles.Length; s++)
                {
                    mesh.Triangles[s] = new int[counts[s] * 3];
                }

                var isNew = new bool[_submesh.Count];
                var offset = new int[mesh.Triangles.Length];
                for (var s = 1; s < offset.Length; s++)
                {
                    offset[s] = offset[s - 1] + counts[s - 1];
                }

                for (var t = 0; t < _submesh.Count; t++)
                {
                    var s = _submesh[t];
                    var slot = next[s]++;
                    mesh.Triangles[s][slot * 3] = _corners[t * 3];
                    mesh.Triangles[s][slot * 3 + 1] = _corners[t * 3 + 1];
                    mesh.Triangles[s][slot * 3 + 2] = _corners[t * 3 + 2];
                    isNew[offset[s] + slot] = _isNew[t];
                }

                return new GoreTessellation { Mesh = mesh, IsNew = isNew, Added = _added };
            }
        }
    }
}
