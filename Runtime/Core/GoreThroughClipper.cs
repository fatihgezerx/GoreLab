using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoreLab
{
    /// <summary>What <see cref="GoreThroughClipper.Divide"/> made: the mesh cut along the borders of the columns, and the column every triangle of it is in.</summary>
    public sealed class GoreThroughResult
    {
        /// <summary>The mesh with every triangle that crossed a column border cut along it (same submeshes and attributes as the source).</summary>
        public GoreMeshData Mesh;

        /// <summary>Per triangle of <see cref="Mesh"/> (flat number), the column (piece) it is in.</summary>
        public int[] Owner;

        /// <summary>Per triangle of <see cref="Mesh"/>, the flat number of the source triangle it was cut from.</summary>
        public int[] Parent;

        /// <summary>The number of columns.</summary>
        public int PieceCount;
    }

    /// <summary>
    /// Divides a mesh into columns right through it along an axis, with straight borders. Seeds are spread over the mesh as seen along the axis; the
    /// cell of a seed is every place nearer to it than to any other seed (a Voronoi cell) and the column is everything of the surface above or below that
    /// cell, front, back and sides together. Triangles that cross a border are cut along it (with every attribute blended), so the borders are straight
    /// lines on the surface however big the triangles are, and the cut edges of two neighbouring columns meet exactly: the result is as closed as the source.
    /// Pure arrays in, pure arrays out; the same seed gives the same columns.
    /// </summary>
    public static class GoreThroughClipper
    {
        // How many places are tried for the next seed: the one farthest from the seeds so far wins (an even spread without clumps).
        private const int Candidates = 16;

        private struct Corner
        {
            internal Vector3 Position;
            internal Vector3 Bary;
        }

        /// <summary>
        /// Divides <paramref name="mesh"/> into <paramref name="count"/> columns along <paramref name="axis"/> (Auto = the thinnest way). The count is raised
        /// to the number of separate pieces the mesh already is (each needs a seed) and never goes above <see cref="GorePartitioner.MaxPiecesLimit"/>
        /// (unless the mesh has more separate pieces than that).
        /// </summary>
        public static GoreThroughResult Divide(GoreMeshData mesh, int count, int seed, GoreBreakAxis axis)
        {
            if (axis == GoreBreakAxis.Auto)
            {
                axis = GorePartitioner.ThinAxis(mesh, out _);
            }

            var corners = GoreMeshTopology.Flatten(mesh, out var submesh);
            var triangles = corners.Length / 3;
            var empty = new GoreThroughResult { Mesh = mesh, Owner = Array.Empty<int>(), Parent = Array.Empty<int>() };
            if (triangles == 0)
            {
                return empty;
            }

            GoreMeshTopology.Adjacency(corners, GoreMeshTopology.Weld(mesh.Vertices), out var start, out var list);
            var island = GoreMeshTopology.TriangleIslands(start, list, out var islandCount);
            var total = Math.Max(Math.Max(2, islandCount), Math.Min(count, GorePartitioner.MaxPiecesLimit));

            var flat = new Vector2[mesh.Vertices.Length];
            for (var v = 0; v < flat.Length; v++)
            {
                flat[v] = Project(mesh.Vertices[v], axis);
            }

            var random = new GorePartitioner.Xorshift((uint)seed);
            var seeds = PickSeeds(flat, corners, island, islandCount, total, random, out var seedIsland);
            var cells = new List<int>[islandCount];
            for (var i = 0; i < islandCount; i++)
            {
                cells[i] = new List<int>();
            }

            for (var s = 0; s < seeds.Length; s++)
            {
                cells[seedIsland[s]].Add(s);
            }

            var output = new Builder(mesh);
            var polygon = new List<Corner>(8);
            var scratch = new List<Corner>(8);
            for (var t = 0; t < triangles; t++)
            {
                var own = cells[island[t]];
                var c0 = corners[t * 3];
                var c1 = corners[t * 3 + 1];
                var c2 = corners[t * 3 + 2];
                var n0 = Nearest(flat[c0], seeds, own);
                var n1 = Nearest(flat[c1], seeds, own);
                var n2 = Nearest(flat[c2], seeds, own);
                if (n0 == n1 && n1 == n2)
                {
                    // Every corner is in one cell, and a cell is convex: the whole triangle is.
                    output.Add(t, submesh[t], c0, c1, c2, n0,
                        new Corner { Position = mesh.Vertices[c0], Bary = new Vector3(1, 0, 0) },
                        new Corner { Position = mesh.Vertices[c1], Bary = new Vector3(0, 1, 0) },
                        new Corner { Position = mesh.Vertices[c2], Bary = new Vector3(0, 0, 1) });
                    continue;
                }

                for (var i = 0; i < own.Count; i++)
                {
                    var cell = own[i];
                    polygon.Clear();
                    polygon.Add(new Corner { Position = mesh.Vertices[c0], Bary = new Vector3(1, 0, 0) });
                    polygon.Add(new Corner { Position = mesh.Vertices[c1], Bary = new Vector3(0, 1, 0) });
                    polygon.Add(new Corner { Position = mesh.Vertices[c2], Bary = new Vector3(0, 0, 1) });
                    for (var j = 0; j < own.Count && polygon.Count >= 3; j++)
                    {
                        if (own[j] != cell)
                        {
                            Clip(polygon, scratch, seeds, cell, own[j], axis);
                        }
                    }

                    if (polygon.Count >= 3)
                    {
                        for (var k = 1; k + 1 < polygon.Count; k++)
                        {
                            output.Add(t, submesh[t], c0, c1, c2, cell, polygon[0], polygon[k], polygon[k + 1]);
                        }
                    }
                }
            }

            var result = output.Result(seeds.Length);
            return result;
        }

        private static Vector2 Project(Vector3 p, GoreBreakAxis axis)
        {
            return axis == GoreBreakAxis.X ? new Vector2(p.y, p.z) : axis == GoreBreakAxis.Y ? new Vector2(p.x, p.z) : new Vector2(p.x, p.y);
        }

        private static int Nearest(Vector2 p, Vector2[] seeds, List<int> cells)
        {
            var best = cells[0];
            var bestDistance = float.MaxValue;
            for (var i = 0; i < cells.Count; i++)
            {
                var distance = (seeds[cells[i]] - p).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = cells[i];
                }
            }

            return best;
        }

        // Keeps the part of the polygon that is nearer to seed <cell> than to seed <other>. The plane between two seeds is worked out the same way
        // whichever of the two asks (lower number first), and an edge is cut from its lower end, so two triangles that share an edge, and the two
        // cells on either side of a border, agree on every cut point.
        private static void Clip(List<Corner> polygon, List<Corner> scratch, Vector2[] seeds, int cell, int other, GoreBreakAxis axis)
        {
            var a = Math.Min(cell, other);
            var b = Math.Max(cell, other);
            var sa = seeds[a];
            var sb = seeds[b];
            var direction = 2f * (sb - sa);
            var offset = sa.sqrMagnitude - sb.sqrMagnitude;
            var sign = cell == a ? 1f : -1f;

            scratch.Clear();
            var count = polygon.Count;
            var previous = polygon[count - 1];
            var previousValue = Value(previous.Position, direction, offset, axis);
            for (var i = 0; i < count; i++)
            {
                var current = polygon[i];
                var currentValue = Value(current.Position, direction, offset, axis);
                var previousIn = sign * previousValue <= 0f;
                var currentIn = sign * currentValue <= 0f;
                if (previousIn != currentIn)
                {
                    scratch.Add(Cut(previous, previousValue, current, currentValue));
                }

                if (currentIn)
                {
                    scratch.Add(current);
                }

                previous = current;
                previousValue = currentValue;
            }

            polygon.Clear();
            for (var i = 0; i < scratch.Count; i++)
            {
                if (polygon.Count == 0 || polygon[polygon.Count - 1].Position != scratch[i].Position)
                {
                    polygon.Add(scratch[i]);
                }
            }

            if (polygon.Count > 1 && polygon[0].Position == polygon[polygon.Count - 1].Position)
            {
                polygon.RemoveAt(polygon.Count - 1);
            }
        }

        // Below zero on the side of the lower seed.
        private static float Value(Vector3 p, Vector2 direction, float offset, GoreBreakAxis axis)
        {
            return Vector2.Dot(Project(p, axis), direction) + offset;
        }

        // The point where the edge between two corners crosses the plane, worked out from the end that sorts first, so it is the same from either side.
        private static Corner Cut(Corner p, float pv, Corner q, float qv)
        {
            if (Before(q.Position, p.Position))
            {
                var t = p;
                p = q;
                q = t;
                var tv = pv;
                pv = qv;
                qv = tv;
            }

            var f = pv / (pv - qv);
            return new Corner { Position = p.Position + (q.Position - p.Position) * f, Bary = p.Bary + (q.Bary - p.Bary) * f };
        }

        private static bool Before(Vector3 a, Vector3 b)
        {
            if (a.x != b.x)
            {
                return a.x < b.x;
            }

            return a.y != b.y ? a.y < b.y : a.z < b.z;
        }

        // Seeds: one in every separate piece of the mesh, the rest where the mesh seen along the axis has the most room (the best of a few
        // random places, by distance to the seeds of the same piece).
        private static Vector2[] PickSeeds(Vector2[] flat, int[] corners, int[] island, int islandCount, int total, GorePartitioner.Xorshift random, out int[] seedIsland)
        {
            var triangles = corners.Length / 3;
            var area = new double[triangles];
            var sum = 0.0;
            for (var t = 0; t < triangles; t++)
            {
                var a = flat[corners[t * 3]];
                var b = flat[corners[t * 3 + 1]];
                var c = flat[corners[t * 3 + 2]];
                area[t] = Math.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) * 0.5;
                sum += area[t];
            }

            var seeds = new List<Vector2>(total);
            var owners = new List<int>(total);
            var firstOfIsland = new int[islandCount];
            for (var i = 0; i < islandCount; i++)
            {
                firstOfIsland[i] = -1;
            }

            // A triangle chosen by area (a bigger one more often), then a place inside it.
            Vector2 Sample(out int chosen)
            {
                chosen = 0;
                if (sum <= 0.0)
                {
                    chosen = random.Next(triangles);
                    return flat[corners[chosen * 3]];
                }

                var target = random.NextDouble() * sum;
                var running = 0.0;
                for (var t = 0; t < triangles; t++)
                {
                    running += area[t];
                    chosen = t;
                    if (running >= target)
                    {
                        break;
                    }
                }

                var u = (float)random.NextDouble();
                var v = (float)random.NextDouble();
                if (u + v > 1f)
                {
                    u = 1f - u;
                    v = 1f - v;
                }

                return flat[corners[chosen * 3]] + (flat[corners[chosen * 3 + 1]] - flat[corners[chosen * 3]]) * u + (flat[corners[chosen * 3 + 2]] - flat[corners[chosen * 3]]) * v;
            }

            // First, one in each separate piece (a piece without any area as seen along the axis gets the place of one of its corners).
            for (var i = 0; i < islandCount; i++)
            {
                var place = Vector2.zero;
                var found = false;
                for (var attempt = 0; attempt < 64 && !found; attempt++)
                {
                    var p = Sample(out var chosen);
                    if (island[chosen] == i)
                    {
                        place = p;
                        found = true;
                    }
                }

                if (!found)
                {
                    for (var t = 0; t < triangles && !found; t++)
                    {
                        if (island[t] == i)
                        {
                            place = flat[corners[t * 3]];
                            found = true;
                        }
                    }
                }

                seeds.Add(place);
                owners.Add(i);
            }

            while (seeds.Count < total)
            {
                var best = Vector2.zero;
                var bestIsland = 0;
                var bestDistance = -1f;
                for (var c = 0; c < Candidates; c++)
                {
                    var p = Sample(out var chosen);
                    var inIsland = island[chosen];
                    var nearest = float.MaxValue;
                    for (var s = 0; s < seeds.Count; s++)
                    {
                        if (owners[s] == inIsland)
                        {
                            nearest = Math.Min(nearest, (seeds[s] - p).sqrMagnitude);
                        }
                    }

                    if (nearest > bestDistance)
                    {
                        bestDistance = nearest;
                        best = p;
                        bestIsland = inIsland;
                    }
                }

                seeds.Add(best);
                owners.Add(bestIsland);
            }

            seedIsland = owners.ToArray();
            return seeds.ToArray();
        }

        // Collects the cut triangles: new vertices (attributes blended from the corners of the source triangle) and triangles per submesh.
        private sealed class Builder
        {
            private readonly GoreMeshData _source;
            private readonly List<Vector3> _positions = new List<Vector3>();
            private readonly List<Vector3> _normals;
            private readonly List<Vector4> _tangents;
            private readonly List<Color32> _colors;
            private readonly List<Vector4>[] _uvs = new List<Vector4>[4];
            private readonly List<int>[] _triangles;
            private readonly List<int>[] _owners;
            private readonly List<int>[] _parents;
            private readonly Dictionary<Vector3, int> _local = new Dictionary<Vector3, int>();
            private int _localTriangle = -1;

            internal Builder(GoreMeshData source)
            {
                _source = source;
                var count = source.Vertices.Length;
                _normals = source.Normals.Length == count ? new List<Vector3>() : null;
                _tangents = source.Tangents.Length == count ? new List<Vector4>() : null;
                _colors = source.Colors.Length == count ? new List<Color32>() : null;
                for (var c = 0; c < 4; c++)
                {
                    if (source.Uvs[c] != null && source.Uvs[c].Length == count)
                    {
                        _uvs[c] = new List<Vector4>();
                    }
                }

                _triangles = new List<int>[source.Triangles.Length];
                _owners = new List<int>[source.Triangles.Length];
                _parents = new List<int>[source.Triangles.Length];
                for (var s = 0; s < _triangles.Length; s++)
                {
                    _triangles[s] = new List<int>();
                    _owners[s] = new List<int>();
                    _parents[s] = new List<int>();
                }
            }

            // One triangle of the result, cut from source triangle <parent> (corners c0, c1, c2) with the given blends of its corners.
            internal void Add(int parent, int submesh, int c0, int c1, int c2, int owner, Corner a, Corner b, Corner c)
            {
                if (parent != _localTriangle)
                {
                    _localTriangle = parent;
                    _local.Clear();
                }

                var bary0 = a.Bary;
                var bary1 = b.Bary;
                var bary2 = c.Bary;
                var ia = Vertex(c0, c1, c2, bary0, a.Position);
                var ib = Vertex(c0, c1, c2, bary1, b.Position);
                var ic = Vertex(c0, c1, c2, bary2, c.Position);
                _triangles[submesh].Add(ia);
                _triangles[submesh].Add(ib);
                _triangles[submesh].Add(ic);
                _owners[submesh].Add(owner);
                _parents[submesh].Add(parent);
            }

            // The vertex at a blend of the three corners of the source triangle: made once per place within one source triangle.
            private int Vertex(int c0, int c1, int c2, Vector3 bary, Vector3 position)
            {
                if (_local.TryGetValue(position, out var existing))
                {
                    return existing;
                }

                var index = _positions.Count;
                _positions.Add(position);
                if (_normals != null)
                {
                    var n = _source.Normals[c0] * bary.x + _source.Normals[c1] * bary.y + _source.Normals[c2] * bary.z;
                    _normals.Add(n.sqrMagnitude > 1e-12f ? n.normalized : _source.Normals[c0]);
                }

                if (_tangents != null)
                {
                    var t0 = _source.Tangents[c0];
                    var t1 = _source.Tangents[c1];
                    var t2 = _source.Tangents[c2];
                    var sum = new Vector3(t0.x, t0.y, t0.z) * bary.x + new Vector3(t1.x, t1.y, t1.z) * bary.y + new Vector3(t2.x, t2.y, t2.z) * bary.z;
                    sum = sum.sqrMagnitude > 1e-12f ? sum.normalized : new Vector3(t0.x, t0.y, t0.z);
                    _tangents.Add(new Vector4(sum.x, sum.y, sum.z, t0.w));
                }

                if (_colors != null)
                {
                    var color0 = _source.Colors[c0];
                    var color1 = _source.Colors[c1];
                    var color2 = _source.Colors[c2];
                    _colors.Add(new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt(color0.r * bary.x + color1.r * bary.y + color2.r * bary.z), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(color0.g * bary.x + color1.g * bary.y + color2.g * bary.z), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(color0.b * bary.x + color1.b * bary.y + color2.b * bary.z), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(color0.a * bary.x + color1.a * bary.y + color2.a * bary.z), 0, 255)));
                }

                for (var ch = 0; ch < 4; ch++)
                {
                    if (_uvs[ch] != null)
                    {
                        _uvs[ch].Add(_source.Uvs[ch][c0] * bary.x + _source.Uvs[ch][c1] * bary.y + _source.Uvs[ch][c2] * bary.z);
                    }
                }

                _local.Add(position, index);
                return index;
            }

            internal GoreThroughResult Result(int pieces)
            {
                var mesh = new GoreMeshData
                {
                    Vertices = _positions.ToArray(),
                    Normals = _normals != null ? _normals.ToArray() : Array.Empty<Vector3>(),
                    Tangents = _tangents != null ? _tangents.ToArray() : Array.Empty<Vector4>(),
                    Colors = _colors != null ? _colors.ToArray() : Array.Empty<Color32>(),
                    BindPoses = _source.BindPoses,
                    Bounds = _source.Bounds,
                    Triangles = new int[_triangles.Length][]
                };

                for (var ch = 0; ch < 4; ch++)
                {
                    if (_uvs[ch] != null)
                    {
                        mesh.Uvs[ch] = _uvs[ch].ToArray();
                        mesh.UvDimension[ch] = _source.UvDimension[ch];
                    }
                }

                var owner = new List<int>();
                var parent = new List<int>();
                for (var s = 0; s < _triangles.Length; s++)
                {
                    mesh.Triangles[s] = _triangles[s].ToArray();
                    owner.AddRange(_owners[s]);
                    parent.AddRange(_parents[s]);
                }

                return new GoreThroughResult { Mesh = mesh, Owner = owner.ToArray(), Parent = parent.ToArray(), PieceCount = pieces };
            }
        }
    }
}
