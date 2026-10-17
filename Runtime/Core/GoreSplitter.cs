using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoreLab
{
    /// <summary>What the splitter reads: the arrays of one skinned mesh and how its bones are grouped into segments.</summary>
    public sealed class GoreSplitInput
    {
        /// <summary>Vertex positions in the mesh's own (bind pose) space.</summary>
        public Vector3[] Positions;

        /// <summary>The bone influences of each vertex (up to four), as in <see cref="Mesh.boneWeights"/>.</summary>
        public BoneWeight[] Weights;

        /// <summary>The triangle index list of each submesh.</summary>
        public int[][] Submeshes;

        /// <summary>For each bone of the mesh (the index in the renderer's bone list): the segment it belongs to. 0 is the body.</summary>
        public int[] BoneSegment;

        /// <summary>For each segment, the segment it is joined to (the one it is cut away from); -1 for the body.</summary>
        public int[] SegmentParent;

        /// <summary>
        /// Vertices that were assigned to a cut, or kept out of it, by hand (null = none). An included vertex counts as fully inside that cut (and
        /// so inside every cut above it, and outside every cut that is neither above nor below it); an excluded one as fully outside it and every
        /// cut below it. The cut line is clipped through the edges next to such a vertex like anywhere else.
        /// </summary>
        public GoreVertexOverride[] Overrides;

        /// <summary>Whether the cuts get lids. False for a mesh whose cuts are left open (clothes): no seams are searched and nothing is warned about.</summary>
        public bool Lids = true;

        /// <summary>Whether the per-mesh lid fans are built. The seam points (<see cref="GoreSplitResult.SeamPoints"/>) are made either way; the run-time pipeline needs only those.</summary>
        public bool BuildCaps = true;
    }

    /// <summary>The part of the mesh that one segment owns: the vertices it uses and its triangles per submesh.</summary>
    public sealed class GoreSegmentPart
    {
        /// <summary>The source vertex of each vertex of the part (new index to old index).</summary>
        public int[] VertexMap = Array.Empty<int>();

        /// <summary>The triangles of each submesh of the part, in the part's own vertex numbers; empty submeshes are left out.</summary>
        public int[][] SubmeshTriangles = Array.Empty<int[]>();

        /// <summary>For each submesh of the part, the submesh of the source it was taken from.</summary>
        public int[] SubmeshSource = Array.Empty<int>();

        /// <summary>Whether the part has any triangle.</summary>
        public bool IsEmpty => VertexMap.Length == 0;
    }

    /// <summary>One vertex of a cap: copied from a source vertex (its position, bone weights and the rest), or a centroid made for the cap.</summary>
    public readonly struct GoreCapVertex
    {
        /// <summary>The source vertex it is a copy of, or -1 for a centroid.</summary>
        public readonly int Source;

        /// <summary>The position of a centroid (not used when <see cref="Source"/> is 0 or more).</summary>
        public readonly Vector3 Position;

        /// <summary>The bone weights of a centroid.</summary>
        public readonly BoneWeight Weights;

        /// <summary>The normal of the part's cap: pointing out of the part through the cut. The stump's is the opposite.</summary>
        public readonly Vector3 Normal;

        /// <summary>A flat mapping of the cap into 0 to 1, for a wound texture.</summary>
        public readonly Vector2 Uv;

        public GoreCapVertex(int source, Vector3 position, BoneWeight weights, Vector3 normal, Vector2 uv)
        {
            Source = source;
            Position = position;
            Weights = weights;
            Normal = normal;
            Uv = uv;
        }
    }

    /// <summary>The lids of one cut: the vertices both lids share and the triangles of each.</summary>
    public sealed class GoreCapPart
    {
        public GoreCapVertex[] Vertices = Array.Empty<GoreCapVertex>();

        /// <summary>Triangles of the lid on the cut-off part, facing out of it (towards the body).</summary>
        public int[] LimbTriangles = Array.Empty<int>();

        /// <summary>Triangles of the lid on the body, facing the other way (towards where the part was).</summary>
        public int[] StumpTriangles = Array.Empty<int>();

        /// <summary>How many closed edges (loops) were found.</summary>
        public int Loops;

        /// <summary>For each loop, the index in <see cref="Vertices"/> of its first point (the points of a loop follow each other round the ring; the loop's centroid comes right after them).</summary>
        public int[] LoopFirst = Array.Empty<int>();

        /// <summary>For each loop, how many ring points it has (not counting its centroid).</summary>
        public int[] LoopLength = Array.Empty<int>();
    }

    /// <summary>A vertex the splitter made on a cut line: a blend of two earlier vertices (of the refined list), <see cref="T"/> of the way from A to B.</summary>
    public readonly struct GoreVertexRef
    {
        /// <summary>The vertex it starts from (an index of the refined list; the source vertex itself for the first ones).</summary>
        public readonly int A;

        /// <summary>The vertex it ends at; the same as <see cref="A"/> for a vertex of the source mesh.</summary>
        public readonly int B;

        /// <summary>How far from A to B (0 to 1).</summary>
        public readonly float T;

        public GoreVertexRef(int a, int b, float t)
        {
            A = a;
            B = b;
            T = t;
        }
    }

    /// <summary>What the splitter made: one part per segment, one cap per cut (null when its seam has no closed edge) and what it noticed.</summary>
    public sealed class GoreSplitResult
    {
        /// <summary>
        /// The vertices every index of the result points at: the source vertices first (unchanged), then the vertices made on the cut lines.
        /// The part maps (<see cref="GoreSegmentPart.VertexMap"/>) and the lids (<see cref="GoreCapVertex.Source"/>) index this list.
        /// </summary>
        public GoreVertexRef[] Vertices;

        /// <summary>
        /// The share of every source vertex in every cut as the bone weights give it, before any decision by hand: <c>SourceShares[vertex * cuts + cut]</c>,
        /// 0 to 1 (how much of the vertex's weight sits on the cut's bone and below). The studio uses it to tell which side of a cut a vertex is on.
        /// </summary>
        public float[] SourceShares;

        /// <summary>The position of each refined vertex.</summary>
        public Vector3[] Positions;

        /// <summary>The bone weights of each refined vertex.</summary>
        public BoneWeight[] Weights;

        public GoreSegmentPart[] Segments;

        /// <summary>Per cut (segment n + 1 is cut n): its lids, or null.</summary>
        public GoreCapPart[] Caps;

        /// <summary>Per cut, the points of the line where its part meets the rest of the body in this mesh (null when lids are off). The cut's lid is made from these points of every mesh together.</summary>
        public Vector3[][] SeamPoints;

        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Splits a skinned mesh into the parts that can come off, and closes the cuts. Pure arrays in, pure arrays out: no Unity objects
    /// are touched, so the rules can be checked by hand; the editor setup copies the vertex data through the maps this makes.
    /// </summary>
    /// <remarks>
    /// For every cut, each vertex has a share: how much of its bone weight sits on the cut's bone and everything below it (0 to 1). The cut
    /// is the line where that share is one half. Triangles that cross the line are clipped along it (new vertices on the edges, with their
    /// position, weights and every other attribute blended), so the line is a clean smooth ring, not the staircase of whole triangles a
    /// vertex-by-vertex decision would leave. A triangle then belongs to the deepest cut it lies inside. Where a segment meets the one it
    /// is cut from, the triangles of the two end at the line; its edges are chained into closed loops and each loop gets a lid.
    /// </remarks>
    public static class GoreSplitter
    {
        // Positions closer than this (meters) are one point for the seam: a mesh with hard edges or texture seams has several vertices there.
        private const float WeldSize = 1e-4f;

        // A vertex whose share is this close to one half is on the cut line itself (no vertex is made next to it).
        private const float OnLine = 1e-4f;

        /// <summary>Splits the mesh. Never throws for a mesh that is merely odd: it reports what it noticed in <see cref="GoreSplitResult.Warnings"/>.</summary>
        public static GoreSplitResult Split(GoreSplitInput source)
        {
            var segmentCount = source.SegmentParent.Length;
            var input = Refine(source, out var refs, out var triangleSegments, out var naturalShares);
            var result = new GoreSplitResult
            {
                Vertices = refs,
                SourceShares = naturalShares,
                Positions = input.Positions,
                Weights = input.Weights,
                Segments = new GoreSegmentPart[segmentCount],
                Caps = new GoreCapPart[Math.Max(0, segmentCount - 1)]
            };

            var positions = input.Positions;
            var vertexCount = positions.Length;

            for (var segment = 0; segment < segmentCount; segment++)
            {
                result.Segments[segment] = BuildPart(input, triangleSegments, segment, vertexCount);
                if (result.Segments[segment].IsEmpty && segment > 0)
                {
                    result.Warnings.Add($"Segment {segment} (cut {segment - 1}) has no triangle: no skin follows its bones for more than half. Check the bone, or use another cut.");
                }
            }

            if (!input.Lids)
            {
                return result;
            }

            var weld = Weld(positions);
            var seams = FindSeams(input, triangleSegments, weld, segmentCount);
            var place = WeldPositions(weld, positions);
            result.SeamPoints = SeamPoints(seams, place);
            for (var cut = 0; cut < segmentCount - 1; cut++)
            {
                var segment = cut + 1;
                var loops = Loops(seams[cut], place, out var openChains, out var pinched);
                if (openChains > 0)
                {
                    result.Warnings.Add($"Cut {cut}: {openChains} edge chain(s) of its seam are not closed (the mesh has an open border there), so its lid is closed with a straight line and may look rough.");
                }

                if (pinched > 0)
                {
                    result.Warnings.Add($"Cut {cut}: its seam touches itself at {pinched} point(s); the lid may have a twisted edge there.");
                }

                if (loops.Count == 0)
                {
                    if (!result.Segments[segment].IsEmpty)
                    {
                        result.Warnings.Add($"Cut {cut} has no closed edge where it meets the rest of the body, so it gets no lid.");
                    }

                    continue;
                }

                if (input.BuildCaps)
                {
                    result.Caps[cut] = BuildCap(input, triangleSegments, weld, segment, loops, result.Segments[segment]);
                }
            }

            return result;
        }

        // The positions of the points of every cut line, whether or not the line closes into a loop (a piece of a modular character often does not).
        // The position of every weld number (the first vertex that has it).
        private static Dictionary<int, Vector3> WeldPositions(int[] weld, Vector3[] positions)
        {
            var place = new Dictionary<int, Vector3>();
            for (var v = 0; v < positions.Length; v++)
            {
                if (!place.ContainsKey(weld[v]))
                {
                    place.Add(weld[v], positions[v]);
                }
            }

            return place;
        }

        private static Vector3[][] SeamPoints(List<(int, int)>[] seams, Dictionary<int, Vector3> place)
        {
            var result = new Vector3[seams.Length][];
            for (var cut = 0; cut < seams.Length; cut++)
            {
                var ids = new HashSet<int>();
                foreach (var (a, b) in seams[cut])
                {
                    ids.Add(a);
                    ids.Add(b);
                }

                var points = new Vector3[ids.Count];
                var n = 0;
                foreach (var id in ids)
                {
                    points[n++] = place[id];
                }

                result[cut] = points;
            }

            return result;
        }

        // Clips the mesh along every cut line and says which segment each resulting triangle belongs to. Returns the refined mesh: the source
        // vertices first, unchanged, then the ones made on the lines (refs says how each was made, so the editor can blend the rest of its data).
        private static GoreSplitInput Refine(GoreSplitInput input, out GoreVertexRef[] refs, out int[][] triangleSegments, out float[] naturalShares)
        {
            var segmentCount = input.SegmentParent.Length;
            var cutCount = Math.Max(0, segmentCount - 1);
            var inSubtree = SubtreeTable(input.SegmentParent);

            // The share of every vertex in every cut (a flat table, cutCount values per vertex): from the bone weights, or decided by hand.
            // A vertex made on a cut line gets its shares blended from the two vertices it lies between, so a decision by hand is kept.
            var table = ShareTable(input, inSubtree, cutCount, input.Positions.Length, out naturalShares);

            var positions = new List<Vector3>(input.Positions);
            var weights = new List<BoneWeight>(input.Weights);
            var refList = new List<GoreVertexRef>(positions.Count);
            for (var v = 0; v < positions.Count; v++)
            {
                refList.Add(new GoreVertexRef(v, v, 0f));
            }

            var triangles = new List<int>[input.Submeshes.Length];
            for (var s = 0; s < triangles.Length; s++)
            {
                triangles[s] = new List<int>(input.Submeshes[s]);
            }

            var inside = new int[5];
            var outside = new int[5];
            var corner = new int[3];
            var sign = new int[3];
            for (var cut = 0; cut < cutCount; cut++)
            {
                var segment = cut + 1;
                var share = new float[positions.Count];
                for (var v = 0; v < share.Length; v++)
                {
                    share[v] = table[v * cutCount + cut] - 0.5f;
                }

                // One new vertex per edge that the line crosses, however many triangles use the edge.
                var made = new Dictionary<long, int>();
                for (var s = 0; s < triangles.Length; s++)
                {
                    var source = triangles[s];
                    var next = new List<int>(source.Count + 24);
                    for (var t = 0; t + 2 < source.Count; t += 3)
                    {
                        var anyAbove = false;
                        var anyBelow = false;
                        for (var k = 0; k < 3; k++)
                        {
                            corner[k] = source[t + k];
                            var value = share[corner[k]];
                            sign[k] = value > OnLine ? 1 : value < -OnLine ? -1 : 0;
                            anyAbove |= sign[k] > 0;
                            anyBelow |= sign[k] < 0;
                        }

                        if (!anyAbove || !anyBelow)
                        {
                            next.Add(corner[0]);
                            next.Add(corner[1]);
                            next.Add(corner[2]);
                            continue;
                        }

                        // The line crosses the triangle: walk its edges once, building the polygon on each side.
                        var insideCount = 0;
                        var outsideCount = 0;
                        for (var k = 0; k < 3; k++)
                        {
                            var a = corner[k];
                            var b = corner[(k + 1) % 3];
                            if (sign[k] >= 0)
                            {
                                inside[insideCount++] = a;
                            }

                            if (sign[k] <= 0)
                            {
                                outside[outsideCount++] = a;
                            }

                            if (sign[k] * sign[(k + 1) % 3] < 0)
                            {
                                var crossing = Crossing(made, share, positions, weights, refList, table, cutCount, cut, a, b);
                                inside[insideCount++] = crossing;
                                outside[outsideCount++] = crossing;
                            }
                        }

                        Emit(next, inside, insideCount, positions);
                        Emit(next, outside, outsideCount, positions);
                    }

                    triangles[s] = next;
                }
            }

            var refined = new GoreSplitInput
            {
                Positions = positions.ToArray(),
                Weights = weights.ToArray(),
                Submeshes = new int[triangles.Length][],
                BoneSegment = input.BoneSegment,
                SegmentParent = input.SegmentParent,
                Lids = input.Lids,
                BuildCaps = input.BuildCaps
            };
            for (var s = 0; s < triangles.Length; s++)
            {
                refined.Submeshes[s] = triangles[s].ToArray();
            }

            refs = refList.ToArray();
            triangleSegments = TriangleSegments(refined, table, cutCount);
            return refined;
        }

        // The share of every source vertex in every cut: how much of its bone weight sits on the cut's bone and below, or what the user decided
        // by hand (an include is 1 for the cut and every cut above it and 0 for every cut that is neither above nor below; an exclude is 0 for the
        // cut and every cut below it). Excludes are applied first, so an explicit include wins over an exclude of another cut.
        private static List<float> ShareTable(GoreSplitInput input, bool[,] inSubtree, int cutCount, int vertexCount, out float[] natural)
        {
            var table = new List<float>(Math.Max(1, vertexCount * Math.Max(1, cutCount)) + 1024);
            for (var v = 0; v < vertexCount; v++)
            {
                for (var cut = 0; cut < cutCount; cut++)
                {
                    table.Add(Share(inSubtree, input.BoneSegment, cut + 1, input.Weights[v]));
                }
            }

            natural = table.ToArray();
            var overrides = input.Overrides;
            if (overrides == null || overrides.Length == 0 || cutCount == 0)
            {
                return table;
            }

            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var entry in overrides)
                {
                    if (entry.Vertex < 0 || entry.Vertex >= vertexCount || entry.Cut < 0 || entry.Cut >= cutCount || entry.Kind == GoreOverrideKind.Auto)
                    {
                        continue;
                    }

                    if ((pass == 0) != (entry.Kind == GoreOverrideKind.Exclude))
                    {
                        continue;
                    }

                    var segment = entry.Cut + 1;
                    for (var s = 1; s <= cutCount; s++)
                    {
                        var index = entry.Vertex * cutCount + (s - 1);
                        if (entry.Kind == GoreOverrideKind.Exclude)
                        {
                            // Out of the cut and of everything below it.
                            if (inSubtree[segment, s])
                            {
                                table[index] = 0f;
                            }
                        }
                        else if (inSubtree[s, segment])
                        {
                            // In the cut and in every cut above it.
                            table[index] = 1f;
                        }
                        else if (!inSubtree[segment, s])
                        {
                            // A cut that is neither above nor below it cannot have the vertex too.
                            table[index] = 0f;
                        }
                    }
                }
            }

            return table;
        }

        // The vertex where the cut line crosses the edge between a and b (made once per edge; the same whichever way round it is asked for).
        private static int Crossing(Dictionary<long, int> made, float[] share, List<Vector3> positions, List<BoneWeight> weights, List<GoreVertexRef> refs, List<float> table, int cutCount, int cut, int a, int b)
        {
            var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (made.TryGetValue(key, out var existing))
            {
                return existing;
            }

            // The two ends in a fixed order that depends on where they are (not on their numbers), so the copies of an edge that a texture seam
            // or a hard edge duplicated give the very same new point, to the last bit, and the seam still welds into one ring.
            var pa = positions[a];
            var pb = positions[b];
            var swap = pa.x != pb.x ? pa.x > pb.x : pa.y != pb.y ? pa.y > pb.y : pa.z != pb.z ? pa.z > pb.z : a > b;
            var from = swap ? b : a;
            var to = swap ? a : b;

            // The shares of the two ends have opposite signs, so the difference is never zero.
            var t = Mathf.Clamp01(share[from] / (share[from] - share[to]));
            var index = positions.Count;
            positions.Add(Vector3.Lerp(positions[from], positions[to], t));
            weights.Add(Blend(weights[from], weights[to], t));
            refs.Add(new GoreVertexRef(from, to, t));

            // Exactly half in the cut it was made for (it is on its line); blended for the others.
            for (var c = 0; c < cutCount; c++)
            {
                table.Add(c == cut ? 0.5f : Mathf.Lerp(table[from * cutCount + c], table[to * cutCount + c], t));
            }

            made.Add(key, index);
            return index;
        }

        // A convex polygon of three or four points as triangles; a quad is cut along its shorter diagonal.
        private static void Emit(List<int> triangles, int[] polygon, int count, List<Vector3> positions)
        {
            if (count < 3)
            {
                return;
            }

            if (count == 3)
            {
                triangles.Add(polygon[0]);
                triangles.Add(polygon[1]);
                triangles.Add(polygon[2]);
                return;
            }

            var first = (positions[polygon[0]] - positions[polygon[2]]).sqrMagnitude;
            var second = (positions[polygon[1]] - positions[polygon[3]]).sqrMagnitude;
            if (first <= second)
            {
                AddTriangle(triangles, polygon[0], polygon[1], polygon[2]);
                AddTriangle(triangles, polygon[0], polygon[2], polygon[3]);
            }
            else
            {
                AddTriangle(triangles, polygon[0], polygon[1], polygon[3]);
                AddTriangle(triangles, polygon[1], polygon[2], polygon[3]);
            }
        }

        private static void AddTriangle(List<int> triangles, int a, int b, int c)
        {
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }

        // How much of a vertex's bone weight sits on the bones of a cut's segment and the segments below it (0 to 1).
        private static float Share(bool[,] inSubtree, int[] boneSegment, int segment, BoneWeight w)
        {
            var total = w.weight0 + w.weight1 + w.weight2 + w.weight3;
            if (total <= 0f)
            {
                return 0f;
            }

            var sum = Part(inSubtree, boneSegment, segment, w.boneIndex0, w.weight0)
                      + Part(inSubtree, boneSegment, segment, w.boneIndex1, w.weight1)
                      + Part(inSubtree, boneSegment, segment, w.boneIndex2, w.weight2)
                      + Part(inSubtree, boneSegment, segment, w.boneIndex3, w.weight3);
            return sum / total;
        }

        private static float Part(bool[,] inSubtree, int[] boneSegment, int segment, int bone, float weight)
        {
            return weight > 0f && bone >= 0 && bone < boneSegment.Length && inSubtree[segment, boneSegment[bone]] ? weight : 0f;
        }

        // inSubtree[a, b]: segment b is segment a or lies below it.
        private static bool[,] SubtreeTable(int[] segmentParent)
        {
            var count = segmentParent.Length;
            var table = new bool[count, count];
            for (var b = 0; b < count; b++)
            {
                var guard = 0;
                for (var p = b; p >= 0 && p < count && guard++ <= count; p = segmentParent[p])
                {
                    table[p, b] = true;
                    if (segmentParent[p] == p)
                    {
                        break;
                    }
                }
            }

            return table;
        }

        // Two weight sets blended (t of the way from a to b): up to eight bones, the four strongest kept, the sum made 1 again.
        private static BoneWeight Blend(BoneWeight a, BoneWeight b, float t)
        {
            var bones = new int[8];
            var amounts = new float[8];
            var count = 0;
            for (var k = 0; k < 8; k++)
            {
                var from = k < 4 ? a : b;
                var slot = k % 4;
                var bone = slot == 0 ? from.boneIndex0 : slot == 1 ? from.boneIndex1 : slot == 2 ? from.boneIndex2 : from.boneIndex3;
                var amount = slot == 0 ? from.weight0 : slot == 1 ? from.weight1 : slot == 2 ? from.weight2 : from.weight3;
                amount *= k < 4 ? 1f - t : t;
                if (amount <= 0f)
                {
                    continue;
                }

                var found = -1;
                for (var i = 0; i < count; i++)
                {
                    if (bones[i] == bone)
                    {
                        found = i;
                        break;
                    }
                }

                if (found >= 0)
                {
                    amounts[found] += amount;
                }
                else
                {
                    bones[count] = bone;
                    amounts[count] = amount;
                    count++;
                }
            }

            var result = new BoneWeight();
            var sum = 0f;
            for (var k = 0; k < 4; k++)
            {
                var best = -1;
                for (var i = 0; i < count; i++)
                {
                    if (amounts[i] > 0f && (best < 0 || amounts[i] > amounts[best]))
                    {
                        best = i;
                    }
                }

                if (best < 0)
                {
                    break;
                }

                if (k == 0) { result.boneIndex0 = bones[best]; result.weight0 = amounts[best]; }
                else if (k == 1) { result.boneIndex1 = bones[best]; result.weight1 = amounts[best]; }
                else if (k == 2) { result.boneIndex2 = bones[best]; result.weight2 = amounts[best]; }
                else { result.boneIndex3 = bones[best]; result.weight3 = amounts[best]; }

                sum += amounts[best];
                amounts[best] = 0f;
            }

            if (sum <= 0f)
            {
                return a;
            }

            result.weight0 /= sum;
            result.weight1 /= sum;
            result.weight2 /= sum;
            result.weight3 /= sum;
            return result;
        }

        // The segment of every triangle: the deepest cut it lies inside (its three shares, added up, are above one half), or the body.
        private static int[][] TriangleSegments(GoreSplitInput input, List<float> table, int cutCount)
        {
            var segmentCount = input.SegmentParent.Length;
            var depth = new int[segmentCount];
            for (var s = 0; s < segmentCount; s++)
            {
                var guard = 0;
                for (var p = input.SegmentParent[s]; p >= 0 && p < segmentCount && guard++ <= segmentCount; p = input.SegmentParent[p])
                {
                    depth[s]++;
                }
            }

            var vertexCount = input.Positions.Length;
            var shares = new float[Math.Max(0, segmentCount - 1)][];
            for (var cut = 0; cut < shares.Length; cut++)
            {
                var values = new float[vertexCount];
                for (var v = 0; v < vertexCount; v++)
                {
                    // The same rule the clipping used: a vertex on the line (and every vertex that was made on it, which is exactly half)
                    // counts as exactly 0, so a sliver along the line is decided by its real vertices.
                    var value = table[v * cutCount + cut] - 0.5f;
                    values[v] = value > OnLine || value < -OnLine ? value : 0f;
                }

                shares[cut] = values;
            }

            var result = new int[input.Submeshes.Length][];
            for (var s = 0; s < input.Submeshes.Length; s++)
            {
                var triangles = input.Submeshes[s];
                var segments = new int[triangles.Length / 3];
                for (var t = 0; t < segments.Length; t++)
                {
                    var a = triangles[t * 3];
                    var b = triangles[t * 3 + 1];
                    var c = triangles[t * 3 + 2];
                    var best = 0;
                    for (var cut = 0; cut < shares.Length; cut++)
                    {
                        var values = shares[cut];
                        if (values[a] + values[b] + values[c] > 0f && depth[cut + 1] > depth[best])
                        {
                            best = cut + 1;
                        }
                    }

                    segments[t] = best;
                }

                result[s] = segments;
            }

            return result;
        }

        // The triangles of one segment, with the vertices they use renumbered; submeshes without a triangle of it are left out.
        private static GoreSegmentPart BuildPart(GoreSplitInput input, int[][] triangleSegments, int segment, int vertexCount)
        {
            var remap = new int[vertexCount];
            for (var i = 0; i < remap.Length; i++)
            {
                remap[i] = -1;
            }

            var map = new List<int>();
            var submeshes = new List<int[]>();
            var sources = new List<int>();
            for (var s = 0; s < input.Submeshes.Length; s++)
            {
                var triangles = input.Submeshes[s];
                var segments = triangleSegments[s];
                var list = new List<int>();
                for (var t = 0; t < segments.Length; t++)
                {
                    if (segments[t] != segment)
                    {
                        continue;
                    }

                    for (var k = 0; k < 3; k++)
                    {
                        var v = triangles[t * 3 + k];
                        if (remap[v] < 0)
                        {
                            remap[v] = map.Count;
                            map.Add(v);
                        }

                        list.Add(remap[v]);
                    }
                }

                if (list.Count > 0)
                {
                    submeshes.Add(list.ToArray());
                    sources.Add(s);
                }
            }

            return new GoreSegmentPart { VertexMap = map.ToArray(), SubmeshTriangles = submeshes.ToArray(), SubmeshSource = sources.ToArray() };
        }

        // A number for every vertex that is the same for vertices at the same place.
        private static int[] Weld(Vector3[] positions)
        {
            var result = new int[positions.Length];
            var ids = new Dictionary<(long, long, long), int>(positions.Length);
            for (var v = 0; v < positions.Length; v++)
            {
                var p = positions[v];
                var key = ((long)Mathf.Round(p.x / WeldSize), (long)Mathf.Round(p.y / WeldSize), (long)Mathf.Round(p.z / WeldSize));
                if (!ids.TryGetValue(key, out var id))
                {
                    id = ids.Count;
                    ids.Add(key, id);
                }

                result[v] = id;
            }

            return result;
        }

        // For each cut, the edges (pairs of weld numbers) where a triangle of its segment meets a triangle of a segment that does NOT come off
        // with it: the segment it is cut from, but also a neighbour that is not below it (the other leg at the crotch). The edges against the
        // cuts below it are inside the part that comes off and need no lid.
        private static List<(int, int)>[] FindSeams(GoreSplitInput input, int[][] triangleSegments, int[] weld, int segmentCount)
        {
            var inSubtree = SubtreeTable(input.SegmentParent);

            // key: the two weld numbers; value: the segments of the triangles that use the edge (two at most are kept).
            var edges = new Dictionary<long, (int first, int second)>();
            for (var s = 0; s < input.Submeshes.Length; s++)
            {
                var triangles = input.Submeshes[s];
                var segments = triangleSegments[s];
                for (var t = 0; t < segments.Length; t++)
                {
                    var segment = segments[t];
                    for (var k = 0; k < 3; k++)
                    {
                        var a = weld[triangles[t * 3 + k]];
                        var b = weld[triangles[t * 3 + (k + 1) % 3]];
                        if (a == b)
                        {
                            continue;
                        }

                        var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                        if (!edges.TryGetValue(key, out var seen))
                        {
                            edges.Add(key, (segment, -1));
                        }
                        else if (seen.second < 0 && seen.first != segment)
                        {
                            edges[key] = (seen.first, segment);
                        }
                    }
                }
            }

            var seams = new List<(int, int)>[Math.Max(0, segmentCount - 1)];
            for (var i = 0; i < seams.Length; i++)
            {
                seams[i] = new List<(int, int)>();
            }

            foreach (var pair in edges)
            {
                var (first, second) = pair.Value;
                if (second < 0)
                {
                    continue;
                }

                var edge = ((int)(pair.Key >> 32), (int)(pair.Key & 0xffffffffL));
                if (first > 0 && !inSubtree[first, second])
                {
                    seams[first - 1].Add(edge);
                }

                if (second > 0 && !inSubtree[second, first])
                {
                    seams[second - 1].Add(edge);
                }
            }

            return seams;
        }

        // Chains the edges of a seam into loops of weld numbers, in order. A chain that does not close (the mesh has an open border there) is walked
        // from its end, never from its middle, so one gap makes one open chain and not two or three pieces; the open chains are then joined end to
        // end, nearest first, into one ring, and what is left open is closed by a straight line when the lid is made. A loop with fewer than three
        // points is dropped.
        private static List<List<int>> Loops(List<(int a, int b)> edges, Dictionary<int, Vector3> place, out int openChains, out int pinched)
        {
            var result = new List<List<int>>();
            openChains = 0;
            pinched = 0;
            if (edges.Count < 3)
            {
                return result;
            }

            var neighbours = new Dictionary<int, List<int>>();
            foreach (var (a, b) in edges)
            {
                Link(neighbours, a, b);
                Link(neighbours, b, a);
            }

            foreach (var pair in neighbours)
            {
                if (pair.Value.Count > 2)
                {
                    pinched++;
                }
            }

            var used = new HashSet<long>();
            var open = new List<List<int>>();

            // The open chains first: they start at a point with only one edge.
            foreach (var pair in neighbours)
            {
                if (pair.Value.Count == 1 && !used.Contains(EdgeKey(pair.Key, pair.Value[0])))
                {
                    var chain = Walk(pair.Key, neighbours, used);
                    if (chain.Count >= 2)
                    {
                        open.Add(chain);
                    }
                }
            }

            // What is left are closed rings.
            foreach (var (startA, startB) in edges)
            {
                if (used.Contains(EdgeKey(startA, startB)))
                {
                    continue;
                }

                var ring = Walk(startA, neighbours, used);
                if (ring.Count > 1 && ring[ring.Count - 1] == ring[0])
                {
                    ring.RemoveAt(ring.Count - 1);
                    if (ring.Count >= 3)
                    {
                        result.Add(ring);
                    }
                }
                else if (ring.Count >= 2)
                {
                    open.Add(ring);
                }
            }

            JoinOpenChains(open, place);
            foreach (var chain in open)
            {
                if (chain.Count >= 3)
                {
                    result.Add(chain);
                    openChains++;
                }
            }

            return result;
        }

        // Follows the unused edges from <start> until there is none (a dead end) or the walk is back at the start.
        private static List<int> Walk(int start, Dictionary<int, List<int>> neighbours, HashSet<long> used)
        {
            var path = new List<int> { start };
            var previous = -1;
            var current = start;
            while (true)
            {
                var next = -1;
                foreach (var candidate in neighbours[current])
                {
                    if (candidate != previous && !used.Contains(EdgeKey(current, candidate)))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next < 0)
                {
                    break;
                }

                used.Add(EdgeKey(current, next));
                previous = current;
                current = next;
                path.Add(current);
                if (current == start)
                {
                    break;
                }
            }

            return path;
        }

        // Joins open chains end to end: the two ends of different chains that are nearest each other, again and again, until one chain is left.
        private static void JoinOpenChains(List<List<int>> open, Dictionary<int, Vector3> place)
        {
            while (open.Count > 1)
            {
                var bestI = -1;
                var bestJ = -1;
                var bestEndI = 0;
                var bestEndJ = 0;
                var best = float.MaxValue;
                for (var i = 0; i < open.Count; i++)
                {
                    for (var j = i + 1; j < open.Count; j++)
                    {
                        for (var endI = 0; endI < 2; endI++)
                        {
                            for (var endJ = 0; endJ < 2; endJ++)
                            {
                                var pointI = place[endI == 0 ? open[i][0] : open[i][open[i].Count - 1]];
                                var pointJ = place[endJ == 0 ? open[j][0] : open[j][open[j].Count - 1]];
                                var distance = (pointI - pointJ).sqrMagnitude;
                                if (distance < best)
                                {
                                    best = distance;
                                    bestI = i;
                                    bestJ = j;
                                    bestEndI = endI;
                                    bestEndJ = endJ;
                                }
                            }
                        }
                    }
                }

                // The joining end of the first chain must be its last, the joining end of the second its first.
                if (bestEndI == 0)
                {
                    open[bestI].Reverse();
                }

                if (bestEndJ == 1)
                {
                    open[bestJ].Reverse();
                }

                open[bestI].AddRange(open[bestJ]);
                open.RemoveAt(bestJ);
            }
        }

        private static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        private static void Link(Dictionary<int, List<int>> map, int from, int to)
        {
            if (!map.TryGetValue(from, out var list))
            {
                list = new List<int>(2);
                map.Add(from, list);
            }

            list.Add(to);
        }

        // The lids of a cut: for each loop a fan of triangles round a centroid, once facing out of the part and once the other way.
        private static GoreCapPart BuildCap(GoreSplitInput input, int[][] triangleSegments, int[] weld, int segment, List<List<int>> loops, GoreSegmentPart part)
        {
            // A source vertex of the cut-off part for every weld number it uses (its weights are the ones the lid follows).
            var representative = new Dictionary<int, int>();
            for (var s = 0; s < input.Submeshes.Length; s++)
            {
                var triangles = input.Submeshes[s];
                var segments = triangleSegments[s];
                for (var t = 0; t < segments.Length; t++)
                {
                    if (segments[t] != segment)
                    {
                        continue;
                    }

                    for (var k = 0; k < 3; k++)
                    {
                        var v = triangles[t * 3 + k];
                        if (!representative.ContainsKey(weld[v]))
                        {
                            representative.Add(weld[v], v);
                        }
                    }
                }
            }

            var inside = Vector3.zero;
            for (var i = 0; i < part.VertexMap.Length; i++)
            {
                inside += input.Positions[part.VertexMap[i]];
            }

            inside = part.VertexMap.Length > 0 ? inside / part.VertexMap.Length : inside;

            var vertices = new List<GoreCapVertex>();
            var limb = new List<int>();
            var stump = new List<int>();
            var loopFirst = new List<int>();
            var loopLength = new List<int>();
            var used = 0;
            foreach (var loop in loops)
            {
                var points = new List<int>(loop.Count);
                foreach (var id in loop)
                {
                    if (representative.TryGetValue(id, out var source))
                    {
                        points.Add(source);
                    }
                }

                if (points.Count < 3)
                {
                    continue;
                }

                var centroid = Vector3.zero;
                foreach (var source in points)
                {
                    centroid += input.Positions[source];
                }

                centroid /= points.Count;

                // The lid's normal is the normal of the loop's own plane (not the way from the middle of the part, which tilts for a foot or a
                // hand); the way from the middle of the part to the middle of the loop only picks which side is out of the part.
                var side = centroid - inside;
                var outward = Newell(input.Positions, points);
                outward = outward.sqrMagnitude > 1e-12f ? outward.normalized : (side.sqrMagnitude > 1e-10f ? side.normalized : Vector3.up);
                if (Vector3.Dot(outward, side) < 0f)
                {
                    outward = -outward;
                }
                Basis(outward, out var u, out var v);
                var radius = 1e-4f;
                foreach (var source in points)
                {
                    var offset = input.Positions[source] - centroid;
                    radius = Mathf.Max(radius, Mathf.Abs(Vector3.Dot(offset, u)), Mathf.Abs(Vector3.Dot(offset, v)));
                }

                var first = vertices.Count;
                loopFirst.Add(first);
                loopLength.Add(points.Count);
                foreach (var source in points)
                {
                    var offset = input.Positions[source] - centroid;
                    var uv = new Vector2(0.5f + 0.5f * Vector3.Dot(offset, u) / radius, 0.5f + 0.5f * Vector3.Dot(offset, v) / radius);
                    vertices.Add(new GoreCapVertex(source, default, default, outward, uv));
                }

                var centre = vertices.Count;
                vertices.Add(new GoreCapVertex(-1, centroid, CentroidWeights(input.Weights, points), outward, new Vector2(0.5f, 0.5f)));

                // One winding for the whole fan, from the way the loop runs round (a zigzag loop has slivers that face sideways, so a
                // winding chosen triangle by triangle would be inconsistent there).
                var flip = Vector3.Dot(Newell(input.Positions, points), outward) < 0f;
                for (var i = 0; i < points.Count; i++)
                {
                    var a = first + i;
                    var b = first + (i + 1) % points.Count;
                    if (!flip)
                    {
                        limb.Add(centre);
                        limb.Add(a);
                        limb.Add(b);
                        stump.Add(centre);
                        stump.Add(b);
                        stump.Add(a);
                    }
                    else
                    {
                        limb.Add(centre);
                        limb.Add(b);
                        limb.Add(a);
                        stump.Add(centre);
                        stump.Add(a);
                        stump.Add(b);
                    }
                }

                used++;
            }

            return used == 0
                ? null
                : new GoreCapPart
                {
                    Vertices = vertices.ToArray(),
                    LimbTriangles = limb.ToArray(),
                    StumpTriangles = stump.ToArray(),
                    Loops = used,
                    LoopFirst = loopFirst.ToArray(),
                    LoopLength = loopLength.ToArray()
                };
        }

        // The bone weights of the middle of a lid: the average of the weights round it, the four strongest kept.
        private static BoneWeight CentroidWeights(BoneWeight[] weights, List<int> points)
        {
            var sums = new Dictionary<int, float>();
            foreach (var point in points)
            {
                var w = weights[point];
                Accumulate(sums, w.boneIndex0, w.weight0);
                Accumulate(sums, w.boneIndex1, w.weight1);
                Accumulate(sums, w.boneIndex2, w.weight2);
                Accumulate(sums, w.boneIndex3, w.weight3);
            }

            var bones = new int[4];
            var amounts = new float[4];
            foreach (var pair in sums)
            {
                // Insert into the four strongest.
                for (var i = 0; i < 4; i++)
                {
                    if (pair.Value > amounts[i])
                    {
                        for (var j = 3; j > i; j--)
                        {
                            bones[j] = bones[j - 1];
                            amounts[j] = amounts[j - 1];
                        }

                        bones[i] = pair.Key;
                        amounts[i] = pair.Value;
                        break;
                    }
                }
            }

            var total = amounts[0] + amounts[1] + amounts[2] + amounts[3];
            if (total <= 0f)
            {
                return new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            }

            return new BoneWeight
            {
                boneIndex0 = bones[0], weight0 = amounts[0] / total,
                boneIndex1 = bones[1], weight1 = amounts[1] / total,
                boneIndex2 = bones[2], weight2 = amounts[2] / total,
                boneIndex3 = bones[3], weight3 = amounts[3] / total
            };
        }

        private static void Accumulate(Dictionary<int, float> sums, int bone, float weight)
        {
            if (weight <= 0f)
            {
                return;
            }

            sums[bone] = sums.TryGetValue(bone, out var value) ? value + weight : weight;
        }

        // The normal of a closed loop of points (Newell's method); the size of the result follows the loop's area.
        private static Vector3 Newell(Vector3[] positions, List<int> points)
        {
            var normal = Vector3.zero;
            for (var i = 0; i < points.Count; i++)
            {
                var current = positions[points[i]];
                var next = positions[points[(i + 1) % points.Count]];
                normal.x += (current.y - next.y) * (current.z + next.z);
                normal.y += (current.z - next.z) * (current.x + next.x);
                normal.z += (current.x - next.x) * (current.y + next.y);
            }

            return normal;
        }

        // Two directions at right angles to the normal and to each other: the axes of the flat mapping of a lid.
        private static void Basis(Vector3 normal, out Vector3 u, out Vector3 v)
        {
            var helper = Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right;
            u = Vector3.Cross(normal, helper).normalized;
            v = Vector3.Cross(normal, u);
        }
    }
}
