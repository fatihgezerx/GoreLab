using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoreLab
{
    /// <summary>Everything of a source mesh the parts copy from, as plain arrays (so the splitting can run on another thread).</summary>
    public sealed class GoreMeshData
    {
        /// <summary>Positions in the mesh's own space.</summary>
        public Vector3[] Vertices = Array.Empty<Vector3>();

        /// <summary>Normals, or empty when the mesh has none.</summary>
        public Vector3[] Normals = Array.Empty<Vector3>();

        /// <summary>Tangents, or empty when the mesh has none.</summary>
        public Vector4[] Tangents = Array.Empty<Vector4>();

        /// <summary>Vertex colours, or empty when the mesh has none.</summary>
        public Color32[] Colors = Array.Empty<Color32>();

        /// <summary>The four texture coordinate channels (null for a channel the mesh does not have).</summary>
        public readonly Vector4[][] Uvs = new Vector4[4][];

        /// <summary>How many components each channel of <see cref="Uvs"/> has (2, 3 or 4).</summary>
        public readonly int[] UvDimension = new int[4];

        /// <summary>Up to four bones per vertex.</summary>
        public BoneWeight[] Weights = Array.Empty<BoneWeight>();

        /// <summary>The bind poses of the mesh.</summary>
        public Matrix4x4[] BindPoses = Array.Empty<Matrix4x4>();

        /// <summary>The bounds of the mesh.</summary>
        public Bounds Bounds;

        /// <summary>The triangles of every submesh (empty for one that is not made of triangles).</summary>
        public int[][] Triangles = Array.Empty<int[]>();
    }

    /// <summary>The attributes of the vertices one part uses, already picked out of the refined mesh.</summary>
    public sealed class GorePartArrays
    {
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector4[] Tangents;
        public Color32[] Colors;
        public readonly Vector4[][] Uvs = new Vector4[4][];
        public readonly int[] UvDimension = new int[4];
        public BoneWeight[] Weights;
        public int[][] SubmeshTriangles;

        /// <summary>For each submesh of the part, the submesh of the source it was made from (so it takes that one's material).</summary>
        public int[] SubmeshSource;
    }

    /// <summary>What a worn mesh has to know about one cut: where the bone of the cut is in its bone list, and where and how wide the cut of the naked body is.</summary>
    public readonly struct GoreCutHint
    {
        /// <summary>The index of the cut's bone in the renderer's bones, or -1 when the renderer does not use it.</summary>
        public readonly int BoneIndex;

        /// <summary>The middle of the cut line of the naked meshes, in the space of the cut's bone.</summary>
        public readonly Vector3 Center;

        /// <summary>How far the cut line of the naked meshes is from its middle.</summary>
        public readonly float Radius;

        /// <summary>Whether <see cref="Center"/> and <see cref="Radius"/> were measured.</summary>
        public readonly bool Valid;

        /// <summary>A worn piece is never split by this cut (the head): it goes whole to a side.</summary>
        public readonly bool KeepWhole;

        /// <summary>The piece is split at this cut by its bones, whatever it looks like (the user asked for it): none of the rules that keep a piece whole applies.</summary>
        public readonly bool Force;

        public GoreCutHint(int boneIndex, Vector3 center, float radius, bool valid, bool keepWhole = false, bool force = false)
        {
            BoneIndex = boneIndex;
            Center = center;
            Radius = radius;
            Valid = valid;
            KeepWhole = keepWhole;
            Force = force;
        }
    }

    /// <summary>What the pipeline made from one mesh: a part for every segment (null where no skin follows it), and the points of every cut line.</summary>
    public sealed class GoreProcessed
    {
        /// <summary>Per segment (the body first, then one for each cut), its part or null.</summary>
        public GorePartArrays[] Parts = Array.Empty<GorePartArrays>();

        /// <summary>Per cut, the points where its part meets the rest of the body in this mesh (mesh space).</summary>
        public Vector3[][] SeamPoints = Array.Empty<Vector3[]>();

        /// <summary>Warnings the splitter raised.</summary>
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Splits a mesh into a part for every segment of a cut tree, at run time, from plain arrays: <see cref="GoreSplitter"/> and the blending of
    /// the vertices it makes on the cut lines. It touches no Unity object, so it can run on a worker thread (the caller reads the mesh into
    /// <see cref="GoreMeshData"/> first and builds <c>Mesh</c> objects from the result afterwards, both on the main thread). The editor
    /// setup does the same work with its own copy of this code for the character's prepared meshes.
    /// </summary>
    public static class GoreMeshPipeline
    {
        /// <summary>
        /// Splits <paramref name="data"/>. <paramref name="boneSegment"/> says for every bone of the renderer which segment it belongs to (the
        /// body is 0, cut n is n + 1) and <paramref name="segmentParent"/> which segment each segment is cut from (-1 for the body). Returns
        /// null when the mesh cannot be cut (no bone weights).
        /// </summary>
        public static GoreProcessed Process(GoreMeshData data, int[] boneSegment, int[] segmentParent) => Process(data, boneSegment, segmentParent, null);

        /// <summary>
        /// The same, with <paramref name="hints"/> (per cut): a worn mesh is cut by a cut only where it goes round the limb as one ring at about the place
        /// and size the naked meshes cut it. Anything else that only brushes the cut (strands of hair, a cape, a helmet's rim) is not cut: it goes whole to
        /// the side it mostly belongs to, and makes no lid. Null hints test the ring but not the place.
        /// </summary>
        public static GoreProcessed Process(GoreMeshData data, int[] boneSegment, int[] segmentParent, GoreCutHint[] hints) => Process(data, boneSegment, segmentParent, hints, null);

        /// <summary>
        /// The same, with <paramref name="pieceSegment"/>: for each separate piece of the mesh (see <see cref="Islands"/>) the segment the whole piece goes
        /// with (0 = the body, cut n = n + 1), -1 to leave that piece to its bone weights, or -3 to leave that piece out of the result altogether (it
        /// is made by another call). A piece that goes to a segment is kept out of every cut line, so it is never cut. Null = no piece is decided by hand.
        /// </summary>
        public static GoreProcessed Process(GoreMeshData data, int[] boneSegment, int[] segmentParent, GoreCutHint[] hints, int[] pieceSegment)
        {
            if (data == null || data.Vertices.Length == 0 || data.Weights.Length != data.Vertices.Length)
            {
                return null;
            }

            GoreVertexOverride[] overrides = null;
            if (pieceSegment != null)
            {
                var pieceOf = Pieces(data.Vertices, data.Triangles, out _);
                overrides = PieceOverrides(pieceOf, data.Vertices.Length, segmentParent, pieceSegment);
                data = WithoutDropped(data, pieceOf, pieceSegment);
            }

            var input = new GoreSplitInput
            {
                Positions = data.Vertices,
                Weights = data.Weights,
                Submeshes = data.Triangles,
                BoneSegment = boneSegment,
                SegmentParent = segmentParent,
                Lids = true,
                BuildCaps = true,
                Overrides = overrides
            };

            // The first split only tells where the mesh goes round the limb (its caps are not used: a worn piece makes no lid).
            var split = GoreSplitter.Split(input);
            var whole = WholePieces(split, boneSegment, segmentParent, hints, data.BindPoses);
            if (whole != null)
            {
                input.BoneSegment = whole;
                split = GoreSplitter.Split(input);
            }

            var refined = Refine(data, split);
            var result = new GoreProcessed
            {
                Parts = new GorePartArrays[split.Segments.Length],
                SeamPoints = split.SeamPoints ?? new Vector3[Math.Max(0, split.Segments.Length - 1)][]
            };

            result.Warnings.AddRange(split.Warnings);
            for (var segment = 0; segment < split.Segments.Length; segment++)
            {
                var part = split.Segments[segment];
                if (part.IsEmpty)
                {
                    continue;
                }

                result.Parts[segment] = Pick(refined, part);
            }

            return result;
        }

        /// <summary>
        /// Numbers the separate pieces of a mesh: triangles that share a point (points at the same place count as one, so a hard edge does not
        /// separate them) are one piece. Returns for every vertex the number of its piece (0, 1, 2... in the order of the first vertex of each), or -1
        /// for a vertex no triangle uses. The same numbers come out of the same mesh in the editor and at run time.
        /// </summary>
        public static int[] Islands(Vector3[] vertices, int[][] triangles, out int count) => Islands(vertices, triangles, false, out count);

        /// <summary>
        /// The pieces a mesh is divided into by hand: its separate pieces (<see cref="Islands(Vector3[], int[][], out int)"/>), or, when the mesh is one
        /// single piece (both trouser legs joined at the top), its left and right half, divided by the middle of the mesh along x. The same numbers in
        /// the editor and at run time.
        /// </summary>
        public static int[] Pieces(Vector3[] vertices, int[][] triangles, out int count)
        {
            var islands = Islands(vertices, triangles, false, out count);
            return count == 1 ? Islands(vertices, triangles, true, out count) : islands;
        }

        /// <summary>
        /// The same; with <paramref name="bySide"/> every piece is divided in two by the middle of the mesh along x (between the smallest and the
        /// biggest x of the vertices that are used): the piece number is the island's number times two, plus one for the x that is at or beyond the
        /// middle. The count is then twice the number of islands (a half may be empty).
        /// </summary>
        public static int[] Islands(Vector3[] vertices, int[][] triangles, bool bySide, out int count)
        {
            var weld = new int[vertices.Length];
            var ids = new Dictionary<(int, int, int), int>(vertices.Length);
            for (var v = 0; v < vertices.Length; v++)
            {
                var key = ((int)Math.Round(vertices[v].x * 10000.0), (int)Math.Round(vertices[v].y * 10000.0), (int)Math.Round(vertices[v].z * 10000.0));
                if (!ids.TryGetValue(key, out var id))
                {
                    id = ids.Count;
                    ids.Add(key, id);
                }

                weld[v] = id;
            }

            var parent = new int[ids.Count];
            for (var i = 0; i < parent.Length; i++)
            {
                parent[i] = i;
            }

            var used = new bool[vertices.Length];
            foreach (var list in triangles)
            {
                for (var k = 0; k + 2 < list.Length; k += 3)
                {
                    var a = Root(parent, weld[list[k]]);
                    var b = Root(parent, weld[list[k + 1]]);
                    var c = Root(parent, weld[list[k + 2]]);
                    parent[b] = a;
                    parent[c] = a;
                    used[list[k]] = true;
                    used[list[k + 1]] = true;
                    used[list[k + 2]] = true;
                }
            }

            var number = new Dictionary<int, int>();
            var result = new int[vertices.Length];
            for (var v = 0; v < vertices.Length; v++)
            {
                if (!used[v])
                {
                    result[v] = -1;
                    continue;
                }

                var root = Root(parent, weld[v]);
                if (!number.TryGetValue(root, out var piece))
                {
                    piece = number.Count;
                    number.Add(root, piece);
                }

                result[v] = piece;
            }

            count = number.Count;
            if (bySide)
            {
                var low = float.MaxValue;
                var high = float.MinValue;
                for (var v = 0; v < vertices.Length; v++)
                {
                    if (result[v] >= 0)
                    {
                        low = Math.Min(low, vertices[v].x);
                        high = Math.Max(high, vertices[v].x);
                    }
                }

                var middle = (low + high) * 0.5f;
                for (var v = 0; v < vertices.Length; v++)
                {
                    if (result[v] >= 0)
                    {
                        result[v] = result[v] * 2 + (vertices[v].x >= middle ? 1 : 0);
                    }
                }

                count *= 2;
            }

            return result;
        }

        private static int Root(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        // The decisions that put each decided piece wholly into its segment: the vertex is included in the segment's cut and excluded from the cuts
        // below it; for the body it is excluded from every cut that is cut from the body.
        private static GoreVertexOverride[] PieceOverrides(int[] island, int vertexCount, int[] segmentParent, int[] pieceSegment)
        {
            var list = new List<GoreVertexOverride>(vertexCount);
            for (var v = 0; v < island.Length; v++)
            {
                var piece = island[v];
                if (piece < 0 || piece >= pieceSegment.Length || pieceSegment[piece] < 0 || pieceSegment[piece] >= segmentParent.Length)
                {
                    continue;
                }

                var segment = pieceSegment[piece];
                if (segment > 0)
                {
                    list.Add(new GoreVertexOverride { Cut = segment - 1, Vertex = v, Kind = GoreOverrideKind.Include });
                }

                for (var s = 1; s < segmentParent.Length; s++)
                {
                    if (segmentParent[s] == segment)
                    {
                        list.Add(new GoreVertexOverride { Cut = s - 1, Vertex = v, Kind = GoreOverrideKind.Exclude });
                    }
                }
            }

            return list.ToArray();
        }

        // The same mesh without the triangles of the pieces that are left out (a triangle is left out when all three of its vertices are in such a piece).
        private static GoreMeshData WithoutDropped(GoreMeshData data, int[] pieceOf, int[] pieceSegment)
        {
            var any = false;
            foreach (var segment in pieceSegment)
            {
                any |= segment == -3;
            }

            if (!any)
            {
                return data;
            }

            var copy = new GoreMeshData
            {
                Vertices = data.Vertices,
                Normals = data.Normals,
                Tangents = data.Tangents,
                Colors = data.Colors,
                Weights = data.Weights,
                BindPoses = data.BindPoses,
                Bounds = data.Bounds,
                Triangles = new int[data.Triangles.Length][]
            };

            for (var channel = 0; channel < 4; channel++)
            {
                copy.Uvs[channel] = data.Uvs[channel];
                copy.UvDimension[channel] = data.UvDimension[channel];
            }

            var kept = new List<int>(1024);
            for (var s = 0; s < data.Triangles.Length; s++)
            {
                kept.Clear();
                var list = data.Triangles[s];
                for (var k = 0; k + 2 < list.Length; k += 3)
                {
                    if (!Dropped(pieceOf[list[k]], pieceSegment) || !Dropped(pieceOf[list[k + 1]], pieceSegment) || !Dropped(pieceOf[list[k + 2]], pieceSegment))
                    {
                        kept.Add(list[k]);
                        kept.Add(list[k + 1]);
                        kept.Add(list[k + 2]);
                    }
                }

                copy.Triangles[s] = kept.ToArray();
            }

            return copy;
        }

        private static bool Dropped(int piece, int[] pieceSegment) => piece >= 0 && piece < pieceSegment.Length && pieceSegment[piece] == -3;

        // A cut needs, in this mesh, one closed ring of at least this many points holding at least this share of all the seam's points (skin, gloves
        // and sleeves make one ring; a handful of strands or a cape do not), and the ring must lie within this many radii of where the naked body's
        // cut is (a helmet's rim is a ring too, but not at the neck).
        private const int MinRingPoints = 10;
        private const float RingShare = 0.6f;
        private const float NearRing = 2f;

        // At a cut that never splits worn pieces (the head) a piece goes with the part when at least this share of it follows the part's bones: hair
        // is mostly weighted to the head even when its long end hangs down the back.
        private const float PartShare = 0.3f;

        // The bone-to-segment mapping with the cuts the mesh does not really go round taken out, or null when every cut it touches is a real ring.
        private static int[] WholePieces(GoreSplitResult split, int[] boneSegment, int[] segmentParent, GoreCutHint[] hints, Matrix4x4[] bindPoses)
        {
            var segments = segmentParent.Length;
            var triangles = new int[segments];
            var total = 0;
            for (var s = 0; s < segments; s++)
            {
                var part = split.Segments[s];
                if (part.IsEmpty)
                {
                    continue;
                }

                for (var sub = 0; sub < part.SubmeshTriangles.Length; sub++)
                {
                    triangles[s] += part.SubmeshTriangles[sub].Length / 3;
                }

                total += triangles[s];
            }

            int[] map = null;
            for (var cut = 0; cut < segments - 1; cut++)
            {
                var segment = cut + 1;
                var inside = 0;
                for (var s = 0; s < segments; s++)
                {
                    if (InSubtree(s, segment, segmentParent))
                    {
                        inside += triangles[s];
                    }
                }

                var hint = hints != null && cut < hints.Length ? hints[cut] : default;

                // A piece made of separate islands (an armband and a chest plate in one mesh) that never touches the cut line has nothing to be
                // decided: each island follows its own bones, so the band goes with the arm. Only a piece that crosses the cut line without going
                // round the limb (hair strands, a cape) is taken whole.
                var crossings = split.SeamPoints != null && cut < split.SeamPoints.Length && split.SeamPoints[cut] != null ? split.SeamPoints[cut].Length : 0;
                if (hint.Force || inside == 0 || inside == total || crossings == 0 || (!hint.KeepWhole && IsRing(split, cut, hint, bindPoses)))
                {
                    continue;
                }

                // Whole to one side: a small part joins the segment it is cut from; a big one takes that segment over.
                map = map ?? (int[])boneSegment.Clone();
                var parent = segmentParent[segment];
                var partWins = hint.KeepWhole ? inside > total * PartShare : inside > total - inside;
                for (var b = 0; b < map.Length; b++)
                {
                    if (!partWins)
                    {
                        if (InSubtree(map[b], segment, segmentParent))
                        {
                            map[b] = parent;
                        }
                    }
                    else if (map[b] == parent)
                    {
                        map[b] = segment;
                    }
                }
            }

            return map;
        }

        private static bool InSubtree(int segment, int root, int[] segmentParent)
        {
            for (var s = segment; s >= 0; s = segmentParent[s])
            {
                if (s == root)
                {
                    return true;
                }
            }

            return false;
        }

        // Whether the mesh makes one big closed ring where it meets this cut, at about the place and size of the naked body's cut.
        private static bool IsRing(GoreSplitResult split, int cut, GoreCutHint hint, Matrix4x4[] bindPoses)
        {
            var cap = split.Caps[cut];
            var points = split.SeamPoints != null && cut < split.SeamPoints.Length ? split.SeamPoints[cut] : null;
            if (cap == null || cap.LoopLength == null || points == null || points.Length == 0)
            {
                return false;
            }

            var biggest = 0;
            for (var i = 0; i < cap.LoopLength.Length; i++)
            {
                biggest = Math.Max(biggest, cap.LoopLength[i]);
            }

            if (biggest < MinRingPoints || biggest < points.Length * RingShare)
            {
                return false;
            }

            if (!hint.Valid || hint.BoneIndex < 0 || hint.BoneIndex >= bindPoses.Length)
            {
                return true;
            }

            var middle = Vector3.zero;
            for (var i = 0; i < points.Length; i++)
            {
                middle += points[i];
            }

            var local = bindPoses[hint.BoneIndex].MultiplyPoint3x4(middle / points.Length);
            return (local - hint.Center).magnitude <= hint.Radius * NearRing;
        }

        // The vertex data of the refined vertex list: the source vertices as they were, then every vertex the splitter made on a cut line,
        // blended from the two vertices it lies between (position and weights come from the splitter).
        private static GoreMeshData Refine(GoreMeshData source, GoreSplitResult split)
        {
            var refs = split.Vertices;
            var count = refs.Length;
            var original = source.Vertices.Length;
            var result = new GoreMeshData { Vertices = split.Positions, Weights = split.Weights, BindPoses = source.BindPoses, Bounds = source.Bounds, Triangles = source.Triangles };

            if (source.Normals.Length == original)
            {
                var normals = new Vector3[count];
                Array.Copy(source.Normals, normals, original);
                for (var i = original; i < count; i++)
                {
                    var blended = Vector3.Lerp(normals[refs[i].A], normals[refs[i].B], refs[i].T);
                    normals[i] = blended.sqrMagnitude > 1e-12f ? blended.normalized : normals[refs[i].A];
                }

                result.Normals = normals;
            }

            if (source.Tangents.Length == original)
            {
                var tangents = new Vector4[count];
                Array.Copy(source.Tangents, tangents, original);
                for (var i = original; i < count; i++)
                {
                    var a = tangents[refs[i].A];
                    var b = tangents[refs[i].B];
                    var blended = Vector3.Lerp(new Vector3(a.x, a.y, a.z), new Vector3(b.x, b.y, b.z), refs[i].T);
                    blended = blended.sqrMagnitude > 1e-12f ? blended.normalized : new Vector3(a.x, a.y, a.z);
                    tangents[i] = new Vector4(blended.x, blended.y, blended.z, a.w);
                }

                result.Tangents = tangents;
            }

            if (source.Colors.Length == original)
            {
                var colors = new Color32[count];
                Array.Copy(source.Colors, colors, original);
                for (var i = original; i < count; i++)
                {
                    colors[i] = Color32.Lerp(colors[refs[i].A], colors[refs[i].B], refs[i].T);
                }

                result.Colors = colors;
            }

            for (var channel = 0; channel < 4; channel++)
            {
                result.UvDimension[channel] = source.UvDimension[channel];
                var uvs = source.Uvs[channel];
                if (uvs == null || uvs.Length != original)
                {
                    continue;
                }

                var refinedUvs = new Vector4[count];
                Array.Copy(uvs, refinedUvs, original);
                for (var i = original; i < count; i++)
                {
                    refinedUvs[i] = Vector4.Lerp(refinedUvs[refs[i].A], refinedUvs[refs[i].B], refs[i].T);
                }

                result.Uvs[channel] = refinedUvs;
            }

            return result;
        }

        // The vertices a part uses, with all their attributes, and its triangles per submesh.
        private static GorePartArrays Pick(GoreMeshData source, GoreSegmentPart part)
        {
            var map = part.VertexMap;
            var result = new GorePartArrays
            {
                Vertices = Pick(source.Vertices, map),
                Normals = source.Normals.Length == source.Vertices.Length ? Pick(source.Normals, map) : Array.Empty<Vector3>(),
                Tangents = source.Tangents.Length == source.Vertices.Length ? Pick(source.Tangents, map) : Array.Empty<Vector4>(),
                Colors = source.Colors.Length == source.Vertices.Length ? Pick(source.Colors, map) : Array.Empty<Color32>(),
                Weights = Pick(source.Weights, map),
                SubmeshTriangles = part.SubmeshTriangles,
                SubmeshSource = part.SubmeshSource
            };

            for (var channel = 0; channel < 4; channel++)
            {
                result.UvDimension[channel] = source.UvDimension[channel];
                if (source.Uvs[channel] != null)
                {
                    result.Uvs[channel] = Pick(source.Uvs[channel], map);
                }
            }

            return result;
        }

        private static T[] Pick<T>(T[] source, int[] map)
        {
            var result = new T[map.Length];
            for (var i = 0; i < map.Length; i++)
            {
                result[i] = source[map[i]];
            }

            return result;
        }
    }
}
