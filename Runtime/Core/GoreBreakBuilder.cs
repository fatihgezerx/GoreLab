using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoreLab
{
    /// <summary>One piece of a broken static mesh as plain arrays, in the space of the mesh it came from (assembled, the pieces look like the original).</summary>
    public sealed class GoreBreakPieceArrays
    {
        /// <summary>The vertices and triangles of the piece. <see cref="GorePartArrays.SubmeshSource"/> is the submesh of the source each submesh was taken from, or -1 for the lid (it takes the fill material).</summary>
        public GorePartArrays Part;

        /// <summary>The middle of the piece (where its weight is centred).</summary>
        public Vector3 Center;

        /// <summary>The bounds of the piece.</summary>
        public Bounds Bounds;

        /// <summary>The volume of the piece in cubic units of the mesh space: the volume of the whole mesh times the share of its surface the piece has; for a mesh that is not closed, the surface of the piece times the thickness asked for.</summary>
        public float Volume;

        /// <summary>The number of triangles that are the surface of the original (the lids are not counted).</summary>
        public int TriangleCount;

        /// <summary>Whether the piece has a lid on at least one of its broken edges.</summary>
        public bool HasLid;
    }

    /// <summary>What <see cref="GoreBreakBuilder.Build"/> made.</summary>
    public sealed class GoreBreakResult
    {
        /// <summary>Per piece number, its arrays, or null when no triangle was given to it. With a rest piece it is the last one.</summary>
        public GoreBreakPieceArrays[] Pieces = Array.Empty<GoreBreakPieceArrays>();

        /// <summary>The number of the piece that took the triangles given to nobody (Manual mode), or -1 when there was none.</summary>
        public int RestPiece = -1;

        /// <summary>Whether the surface is closed, so the broken edges got lids.</summary>
        public bool Closed;

        /// <summary>What was noticed while building.</summary>
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Cuts a static mesh into pieces: every piece gets the triangles that were given to it, with the attributes of their vertices, and, when the
    /// surface is closed (a crate, a table top, a statue), a lid over every broken edge so a piece is never hollow. Pure arrays in, pure arrays out.
    /// </summary>
    /// <remarks>
    /// A piece is a patch of the surface, so a lid that stayed on the surface (a fan from the middle of the broken edge, as a limb's lid is) would
    /// leave a flat patch as thin as paper. The lid is a cone from the broken edge to a point inside the solid, half way through it under the
    /// patch, so the piece is a chunk with a rough break face. The weight of a piece is the share of the surface it has times the volume of the whole mesh.
    /// </remarks>
    public static class GoreBreakBuilder
    {
        // A lid vertex is copied from nothing: it takes the white of an untinted vertex.
        private static readonly Color32 LidColor = new Color32(255, 255, 255, 255);

        // What every piece needs to know about the mesh as a whole.
        private sealed class Context
        {
            internal GoreMeshData Mesh;
            internal int[] Corners;
            internal int[] Submesh;
            internal int[] Weld;
            internal int[] OwnerOf;
            internal Dictionary<long, int> Along;
            internal float TotalVolume;
            internal float TotalArea;
            internal Vector3 Middle;
            internal float Extent;
            internal float LidUvScale;
            internal float OpenThickness;
            internal bool ApexAtCentroid;
            internal GoreBreakAxis PlanarAxis;
            internal readonly List<float> Hits = new List<float>(8);
        }

        /// <summary>
        /// Builds the pieces. <paramref name="owner"/> gives every triangle (flat number, see <see cref="GoreMeshTopology.Flatten"/>) its piece, 0 up to
        /// <paramref name="pieceCount"/> - 1; a negative number leaves the triangle to the rest piece (made only when some triangle needs it).
        /// <paramref name="lidUvScale"/> is how many texture tiles one unit of the mesh space is across a lid; <paramref name="openThickness"/> is the
        /// thickness that gives a piece of an open surface (a wall) its volume. With <paramref name="throughPieces"/> (columns right through the object)
        /// the lid ends at the middle of the piece, which lies inside the solid, instead of being searched for from the way the patch faces. With
        /// <paramref name="planarAxis"/> X, Y or Z (the borders between pieces are flat planes along that axis: <see cref="GoreThroughClipper"/>) there is one flat
        /// lid for every border between two pieces, in the plane of the border; Auto = not flat.
        /// </summary>
        public static GoreBreakResult Build(GoreMeshData mesh, int[] owner, int pieceCount, float lidUvScale, float openThickness, bool throughPieces = false, GoreBreakAxis planarAxis = GoreBreakAxis.Auto)
        {
            var corners = GoreMeshTopology.Flatten(mesh, out var submesh);
            var triangles = corners.Length / 3;
            var result = new GoreBreakResult();
            if (owner == null || owner.Length != triangles)
            {
                result.Warnings.Add("The pieces do not match the mesh: every triangle needs a piece.");
                return result;
            }

            var weld = GoreMeshTopology.Weld(mesh.Vertices);
            result.Closed = GoreMeshTopology.IsClosed(corners, weld);

            var needsRest = false;
            for (var t = 0; t < triangles; t++)
            {
                if (owner[t] < 0 || owner[t] >= pieceCount)
                {
                    needsRest = true;
                    break;
                }
            }

            var total = pieceCount + (needsRest ? 1 : 0);
            result.RestPiece = needsRest ? pieceCount : -1;
            result.Pieces = new GoreBreakPieceArrays[total];
            var ownerOf = new int[triangles];
            var members = new List<int>[total];
            for (var t = 0; t < triangles; t++)
            {
                var piece = owner[t] >= 0 && owner[t] < pieceCount ? owner[t] : pieceCount;
                ownerOf[t] = piece;
                if (members[piece] == null)
                {
                    members[piece] = new List<int>();
                }

                members[piece].Add(t);
            }

            var context = new Context
            {
                Mesh = mesh,
                Corners = corners,
                Submesh = submesh,
                Weld = weld,
                OwnerOf = ownerOf,
                LidUvScale = lidUvScale,
                OpenThickness = Math.Max(0f, openThickness),
                ApexAtCentroid = throughPieces,
                PlanarAxis = planarAxis
            };

            Measure(context, triangles);

            // Which triangle runs along each directed edge, to tell where a piece ends and another begins.
            if (result.Closed)
            {
                context.Along = new Dictionary<long, int>(triangles * 3);
                for (var t = 0; t < triangles; t++)
                {
                    for (var k = 0; k < 3; k++)
                    {
                        var from = weld[corners[t * 3 + k]];
                        var to = weld[corners[t * 3 + (k + 1) % 3]];
                        if (from != to)
                        {
                            context.Along[GoreMeshTopology.DirectedKey(from, to)] = t;
                        }
                    }
                }
            }
            else if (total > 1)
            {
                result.Warnings.Add("The mesh is not closed (a wall, a plane or a mesh with holes): the broken edges get no lids.");
            }

            var map = new int[mesh.Vertices.Length];
            for (var v = 0; v < map.Length; v++)
            {
                map[v] = -1;
            }

            for (var piece = 0; piece < total; piece++)
            {
                if (members[piece] == null)
                {
                    continue;
                }

                result.Pieces[piece] = BuildPiece(context, members[piece], piece, map);
            }

            return result;
        }

        // The size of the whole mesh: its surface, its volume (when it is closed), its middle and its largest extent.
        private static void Measure(Context context, int triangles)
        {
            var mesh = context.Mesh;
            double area = 0;
            double volume = 0;
            var bounds = new Bounds(mesh.Vertices.Length > 0 ? mesh.Vertices[0] : Vector3.zero, Vector3.zero);
            for (var v = 1; v < mesh.Vertices.Length; v++)
            {
                bounds.Encapsulate(mesh.Vertices[v]);
            }

            for (var t = 0; t < triangles; t++)
            {
                var a = mesh.Vertices[context.Corners[t * 3]];
                var b = mesh.Vertices[context.Corners[t * 3 + 1]];
                var c = mesh.Vertices[context.Corners[t * 3 + 2]];
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5;
                volume += Vector3.Dot(a, Vector3.Cross(b, c));
            }

            context.TotalArea = (float)area;
            context.TotalVolume = (float)Math.Abs(volume / 6.0);
            context.Middle = bounds.center;
            context.Extent = Math.Max(bounds.size.x, Math.Max(bounds.size.y, bounds.size.z));
        }

        private static GoreBreakPieceArrays BuildPiece(Context context, List<int> triangles, int piece, int[] map)
        {
            var mesh = context.Mesh;
            var corners = context.Corners;
            var vertexMap = new List<int>();
            for (var i = 0; i < triangles.Count; i++)
            {
                for (var k = 0; k < 3; k++)
                {
                    var v = corners[triangles[i] * 3 + k];
                    if (map[v] < 0)
                    {
                        map[v] = vertexMap.Count;
                        vertexMap.Add(v);
                    }
                }
            }

            // One list of triangles per submesh of the source that the piece uses.
            var used = new List<int>();
            var perSubmesh = new Dictionary<int, List<int>>();
            for (var i = 0; i < triangles.Count; i++)
            {
                var s = context.Submesh[triangles[i]];
                if (!perSubmesh.TryGetValue(s, out var list))
                {
                    list = new List<int>();
                    perSubmesh.Add(s, list);
                    used.Add(s);
                }

                list.Add(map[corners[triangles[i] * 3]]);
                list.Add(map[corners[triangles[i] * 3 + 1]]);
                list.Add(map[corners[triangles[i] * 3 + 2]]);
            }

            used.Sort();

            var positions = new List<Vector3>(vertexMap.Count);
            for (var i = 0; i < vertexMap.Count; i++)
            {
                positions.Add(mesh.Vertices[vertexMap[i]]);
            }

            var normals = mesh.Normals.Length == mesh.Vertices.Length ? PickList(mesh.Normals, vertexMap) : null;
            var tangents = mesh.Tangents.Length == mesh.Vertices.Length ? PickList(mesh.Tangents, vertexMap) : null;
            var colors = mesh.Colors.Length == mesh.Vertices.Length ? PickList(mesh.Colors, vertexMap) : null;
            var uvs = new List<Vector4>[4];
            var dimension = new int[4];
            for (var c = 0; c < 4; c++)
            {
                if (mesh.Uvs[c] != null && mesh.Uvs[c].Length == mesh.Vertices.Length)
                {
                    uvs[c] = PickList(mesh.Uvs[c], vertexMap);
                    dimension[c] = mesh.UvDimension[c];
                }
            }

            var area = SurfaceArea(context, triangles);

            // The broken edges: the edges of this piece whose other side belongs to another piece.
            var lidTriangles = new List<int>();
            var hasLid = false;
            if (context.Along != null)
            {
                var edges = new List<(int from, int to, int other)>();
                for (var i = 0; i < triangles.Count; i++)
                {
                    var t = triangles[i];
                    for (var k = 0; k < 3; k++)
                    {
                        var from = context.Weld[corners[t * 3 + k]];
                        var to = context.Weld[corners[t * 3 + (k + 1) % 3]];
                        if (from != to && context.Along.TryGetValue(GoreMeshTopology.DirectedKey(to, from), out var other) && context.OwnerOf[other] != piece)
                        {
                            edges.Add((from, to, context.OwnerOf[other]));
                        }
                    }
                }

                if (edges.Count >= 3)
                {
                    hasLid = AddLids(context, triangles, edges, positions, normals, tangents, colors, uvs, dimension, lidTriangles);
                }
            }

            var part = new GorePartArrays
            {
                Vertices = positions.ToArray(),
                Normals = normals != null ? normals.ToArray() : Array.Empty<Vector3>(),
                Tangents = tangents != null ? tangents.ToArray() : Array.Empty<Vector4>(),
                Colors = colors != null ? colors.ToArray() : Array.Empty<Color32>(),
                Weights = Array.Empty<BoneWeight>()
            };

            for (var c = 0; c < 4; c++)
            {
                if (uvs[c] != null)
                {
                    part.Uvs[c] = uvs[c].ToArray();
                    part.UvDimension[c] = dimension[c];
                }
            }

            var submeshCount = used.Count + (lidTriangles.Count > 0 ? 1 : 0);
            part.SubmeshTriangles = new int[submeshCount][];
            part.SubmeshSource = new int[submeshCount];
            for (var i = 0; i < used.Count; i++)
            {
                part.SubmeshTriangles[i] = perSubmesh[used[i]].ToArray();
                part.SubmeshSource[i] = used[i];
            }

            if (lidTriangles.Count > 0)
            {
                part.SubmeshTriangles[used.Count] = lidTriangles.ToArray();
                part.SubmeshSource[used.Count] = -1;
            }

            // Leave the shared map clean for the next piece.
            for (var i = 0; i < vertexMap.Count; i++)
            {
                map[vertexMap[i]] = -1;
            }

            var bounds = new Bounds(part.Vertices.Length > 0 ? part.Vertices[0] : Vector3.zero, Vector3.zero);
            for (var i = 1; i < part.Vertices.Length; i++)
            {
                bounds.Encapsulate(part.Vertices[i]);
            }

            float volume;
            if (context.Along != null)
            {
                volume = context.TotalArea > 0f ? context.TotalVolume * (area / context.TotalArea) : 0f;
            }
            else
            {
                volume = area * context.OpenThickness;
            }

            return new GoreBreakPieceArrays
            {
                Part = part,
                Bounds = bounds,
                Center = bounds.center,
                TriangleCount = triangles.Count,
                HasLid = hasLid,
                Volume = volume
            };
        }

        private static float SurfaceArea(Context context, List<int> triangles)
        {
            double area = 0;
            var vertices = context.Mesh.Vertices;
            for (var i = 0; i < triangles.Count; i++)
            {
                var t = triangles[i];
                var a = vertices[context.Corners[t * 3]];
                area += Vector3.Cross(vertices[context.Corners[t * 3 + 1]] - a, vertices[context.Corners[t * 3 + 2]] - a).magnitude * 0.5;
            }

            return (float)area;
        }

        // Closes every loop of broken edges with a cone to a point inside the solid. The edge of the piece runs from -> to; the lid runs the other way
        // along it (to -> from), which is what makes it face out of the piece.
        private static bool AddLids(Context context, List<int> triangles, List<(int from, int to, int other)> edges, List<Vector3> positions, List<Vector3> normals,
            List<Vector4> tangents, List<Color32> colors, List<Vector4>[] uvs, int[] dimension, List<int> lidTriangles)
        {
            // The place of every point, found once for the points the edges use.
            var place = new Dictionary<int, Vector3>();
            for (var i = 0; i < triangles.Count; i++)
            {
                for (var k = 0; k < 3; k++)
                {
                    var v = context.Corners[triangles[i] * 3 + k];
                    if (!place.ContainsKey(context.Weld[v]))
                    {
                        place.Add(context.Weld[v], context.Mesh.Vertices[v]);
                    }
                }
            }

            if (uvs[0] == null)
            {
                uvs[0] = new List<Vector4>(positions.Count);
                for (var i = 0; i < positions.Count; i++)
                {
                    uvs[0].Add(Vector4.zero);
                }

                dimension[0] = 2;
            }

            if (context.PlanarAxis == GoreBreakAxis.Auto)
            {
                var all = new List<(int from, int to)>(edges.Count);
                foreach (var (from, to, _) in edges)
                {
                    all.Add((from, to));
                }

                return CloseLoops(context, place, all, FindApex(context, triangles), false, positions, normals, tangents, colors, uvs, lidTriangles);
            }

            // Flat borders: the edges that face one neighbour all lie in one plane and make one loop (or a few), closed by a fan from its own middle.
            var byNeighbour = new Dictionary<int, List<(int from, int to)>>();
            foreach (var (from, to, other) in edges)
            {
                if (!byNeighbour.TryGetValue(other, out var group))
                {
                    group = new List<(int from, int to)>();
                    byNeighbour.Add(other, group);
                }

                group.Add((from, to));
            }

            var any = false;
            foreach (var pair in byNeighbour)
            {
                if (pair.Value.Count >= 2)
                {
                    any |= CloseLoops(context, place, pair.Value, Vector3.zero, true, positions, normals, tangents, colors, uvs, lidTriangles);
                }
            }

            return any;
        }

        // Chains the edges into loops and closes each with a fan to <apex>, or to the middle of the loop itself (flat lids).
        private static bool CloseLoops(Context context, Dictionary<int, Vector3> place, List<(int from, int to)> edges, Vector3 apex, bool middleOfLoop, List<Vector3> positions,
            List<Vector3> normals, List<Vector4> tangents, List<Color32> colors, List<Vector4>[] uvs, List<int> lidTriangles)
        {
            var rings = middleOfLoop ? FlatRings(edges, place, context.PlanarAxis) : Rings(edges);
            var any = false;
            foreach (var ring in rings)
            {
                if (ring.Count < 3)
                {
                    continue;
                }

                var top = apex;
                if (middleOfLoop)
                {
                    top = Vector3.zero;
                    for (var i = 0; i < ring.Count; i++)
                    {
                        top += place[ring[i]];
                    }

                    top /= ring.Count;
                }

                for (var i = 0; i < ring.Count; i++)
                {
                    // The piece's edge runs ring[i] -> ring[i + 1]; the lid runs back along it.
                    AddLidTriangle(place[ring[(i + 1) % ring.Count]], place[ring[i]], top, context.LidUvScale, positions, normals, tangents, colors, uvs, lidTriangles);
                }

                any = true;
            }

            return any;
        }

        // The loops the edges make, walked edge by edge (a chain that does not close is left as it is and closed by a straight line).
        private static List<List<int>> Rings(List<(int from, int to)> edges)
        {
            var outgoing = Outgoing(edges);
            var result = new List<List<int>>();
            foreach (var (startFrom, startTo) in edges)
            {
                if (!outgoing[startFrom].Remove(startTo))
                {
                    continue;
                }

                var ring = new List<int> { startFrom };
                var current = startTo;
                while (current != startFrom)
                {
                    ring.Add(current);
                    if (!outgoing.TryGetValue(current, out var next) || next.Count == 0)
                    {
                        break;
                    }

                    var to = next[0];
                    next.RemoveAt(0);
                    current = to;
                }

                result.Add(ring);
            }

            return result;
        }

        private static Dictionary<int, List<int>> Outgoing(List<(int from, int to)> edges)
        {
            var outgoing = new Dictionary<int, List<int>>();
            foreach (var (from, to) in edges)
            {
                if (!outgoing.TryGetValue(from, out var list))
                {
                    list = new List<int>(1);
                    outgoing.Add(from, list);
                }

                list.Add(to);
            }

            return outgoing;
        }

        // The edges between two pieces whose border is a flat plane along the axis: on the surface they are chains (on the front, on the back, along the
        // sides) that the inside of the object joins: the end of a chain meets the start of the one that begins right above or below it (the same place seen
        // along the axis). The chains are joined in that way into closed loops, so the lid fills the whole flat border.
        private static List<List<int>> FlatRings(List<(int from, int to)> edges, Dictionary<int, Vector3> place, GoreBreakAxis axis)
        {
            var outgoing = Outgoing(edges);
            var incoming = new HashSet<int>();
            foreach (var (_, to) in edges)
            {
                incoming.Add(to);
            }

            // Chains start where nothing comes in; what is left after them are closed loops.
            var chains = new List<List<int>>();
            var loops = new List<List<int>>();
            foreach (var (startFrom, _) in edges)
            {
                if (incoming.Contains(startFrom) || !outgoing.TryGetValue(startFrom, out var first) || first.Count == 0)
                {
                    continue;
                }

                var chain = new List<int> { startFrom };
                var current = startFrom;
                while (outgoing.TryGetValue(current, out var next) && next.Count > 0)
                {
                    current = next[0];
                    next.RemoveAt(0);
                    chain.Add(current);
                }

                chains.Add(chain);
            }

            foreach (var (startFrom, _) in edges)
            {
                if (!outgoing.TryGetValue(startFrom, out var list) || list.Count == 0)
                {
                    continue;
                }

                var ring = new List<int>();
                var current = startFrom;
                while (outgoing.TryGetValue(current, out var next) && next.Count > 0)
                {
                    ring.Add(current);
                    current = next[0];
                    next.RemoveAt(0);
                    if (current == startFrom)
                    {
                        break;
                    }
                }

                loops.Add(ring);
            }

            var unused = new List<int>();
            for (var i = 0; i < chains.Count; i++)
            {
                unused.Add(i);
            }

            while (unused.Count > 0)
            {
                var firstChain = unused[0];
                unused.RemoveAt(0);
                var ring = new List<int>(chains[firstChain]);
                while (true)
                {
                    var end = place[ring[ring.Count - 1]];
                    var best = -1;
                    var bestDistance = Across(end, place[ring[0]], axis);
                    var bestFull = (end - place[ring[0]]).sqrMagnitude;
                    for (var u = 0; u < unused.Count; u++)
                    {
                        var start = place[chains[unused[u]][0]];
                        var distance = Across(end, start, axis);
                        var full = (end - start).sqrMagnitude;
                        if (distance < bestDistance || (distance == bestDistance && full < bestFull))
                        {
                            bestDistance = distance;
                            bestFull = full;
                            best = u;
                        }
                    }

                    if (best < 0)
                    {
                        break;
                    }

                    ring.AddRange(chains[unused[best]]);
                    unused.RemoveAt(best);
                }

                loops.Add(ring);
            }

            return loops;
        }

        // How far apart two places are seen along the axis (the distance across the other two directions).
        private static float Across(Vector3 a, Vector3 b, GoreBreakAxis axis)
        {
            var d = a - b;
            if (axis == GoreBreakAxis.X)
            {
                d.x = 0f;
            }
            else if (axis == GoreBreakAxis.Y)
            {
                d.y = 0f;
            }
            else
            {
                d.z = 0f;
            }

            return d.sqrMagnitude;
        }

        // A point inside the solid under the patch: from the middle of the patch, in the direction opposite to the way the patch faces (or towards the
        // middle of the mesh when the patch wraps round and faces every way), half way through the solid.
        private static Vector3 FindApex(Context context, List<int> triangles)
        {
            var vertices = context.Mesh.Vertices;
            var normalSum = Vector3.zero;
            var centroid = Vector3.zero;
            double area = 0;
            for (var i = 0; i < triangles.Count; i++)
            {
                var t = triangles[i];
                var a = vertices[context.Corners[t * 3]];
                var b = vertices[context.Corners[t * 3 + 1]];
                var c = vertices[context.Corners[t * 3 + 2]];
                var cross = Vector3.Cross(b - a, c - a);
                var share = cross.magnitude * 0.5f;
                normalSum += cross * 0.5f;
                centroid += (a + b + c) / 3f * share;
                area += share;
            }

            if (area <= 1e-12)
            {
                return vertices[context.Corners[triangles[0] * 3]];
            }

            centroid /= (float)area;
            if (context.ApexAtCentroid)
            {
                return centroid;
            }

            var inward = -normalSum;
            if (inward.magnitude < 0.15f * (float)area)
            {
                inward = context.Middle - centroid;
            }

            if (inward.sqrMagnitude < 1e-12f)
            {
                inward = Vector3.down;
            }

            inward.Normalize();

            var hits = context.Hits;
            hits.Clear();
            var count = context.Corners.Length / 3;
            for (var t = 0; t < count; t++)
            {
                var hit = RayHit(centroid, inward, vertices[context.Corners[t * 3]], vertices[context.Corners[t * 3 + 1]], vertices[context.Corners[t * 3 + 2]]);
                if (hit > 1e-5f)
                {
                    hits.Add(hit);
                }
            }

            hits.Sort();
            float depth;
            if (hits.Count == 0)
            {
                depth = context.Extent * 0.1f;
            }
            else if (hits.Count % 2 == 1 || hits.Count == 1)
            {
                // The middle of the patch is inside the solid: half way to the far side.
                depth = hits[0] * 0.5f;
            }
            else
            {
                // It is outside (the patch bulges): half way through the first stretch of solid.
                depth = (hits[0] + hits[1]) * 0.5f;
            }

            return centroid + inward * depth;
        }

        // Moller-Trumbore: how far along the ray the triangle is hit, or -1.
        private static float RayHit(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
        {
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(direction, e2);
            var det = Vector3.Dot(e1, p);
            if (det > -1e-12f && det < 1e-12f)
            {
                return -1f;
            }

            var inverse = 1f / det;
            var to = origin - a;
            var u = Vector3.Dot(to, p) * inverse;
            if (u < 0f || u > 1f)
            {
                return -1f;
            }

            var q = Vector3.Cross(to, e1);
            var v = Vector3.Dot(direction, q) * inverse;
            if (v < 0f || u + v > 1f)
            {
                return -1f;
            }

            return Vector3.Dot(e2, q) * inverse;
        }

        // One flat triangle of a lid, with its own vertices: its own normal (a rough break face) and a texture mapped from the side it faces most.
        private static void AddLidTriangle(Vector3 a, Vector3 b, Vector3 c, float scale, List<Vector3> positions, List<Vector3> normals, List<Vector4> tangents,
            List<Color32> colors, List<Vector4>[] uvs, List<int> lidTriangles)
        {
            var normal = Vector3.Cross(b - a, c - a);
            normal = normal.sqrMagnitude > 1e-18f ? normal.normalized : Vector3.up;
            var ax = Mathf.Abs(normal.x);
            var ay = Mathf.Abs(normal.y);
            var az = Mathf.Abs(normal.z);
            var axis = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
            var along = axis == 1 ? Vector3.right : axis == 0 ? Vector3.forward : Vector3.right;

            AddLidVertex(a, normal, axis, along, scale, positions, normals, tangents, colors, uvs);
            AddLidVertex(b, normal, axis, along, scale, positions, normals, tangents, colors, uvs);
            AddLidVertex(c, normal, axis, along, scale, positions, normals, tangents, colors, uvs);
            var first = positions.Count - 3;
            lidTriangles.Add(first);
            lidTriangles.Add(first + 1);
            lidTriangles.Add(first + 2);
        }

        private static void AddLidVertex(Vector3 point, Vector3 normal, int axis, Vector3 along, float scale, List<Vector3> positions, List<Vector3> normals,
            List<Vector4> tangents, List<Color32> colors, List<Vector4>[] uvs)
        {
            positions.Add(point);
            if (normals != null)
            {
                normals.Add(normal);
            }

            if (tangents != null)
            {
                tangents.Add(new Vector4(along.x, along.y, along.z, 1f));
            }

            if (colors != null)
            {
                colors.Add(LidColor);
            }

            // Mapped in units of the mesh space, so the grain of a lid matches whatever size the object is.
            var uv = axis == 0 ? new Vector4(point.z, point.y, 0f, 0f) : axis == 1 ? new Vector4(point.x, point.z, 0f, 0f) : new Vector4(point.x, point.y, 0f, 0f);
            for (var c = 0; c < 4; c++)
            {
                if (uvs[c] != null)
                {
                    uvs[c].Add(c == 0 ? uv * scale : Vector4.zero);
                }
            }
        }

        private static List<Vector3> PickList(Vector3[] source, List<int> map)
        {
            var list = new List<Vector3>(map.Count);
            for (var i = 0; i < map.Count; i++)
            {
                list.Add(source[map[i]]);
            }

            return list;
        }

        private static List<Vector4> PickList(Vector4[] source, List<int> map)
        {
            var list = new List<Vector4>(map.Count);
            for (var i = 0; i < map.Count; i++)
            {
                list.Add(source[map[i]]);
            }

            return list;
        }

        private static List<Color32> PickList(Color32[] source, List<int> map)
        {
            var list = new List<Color32>(map.Count);
            for (var i = 0; i < map.Count; i++)
            {
                list.Add(source[map[i]]);
            }

            return list;
        }
    }
}
