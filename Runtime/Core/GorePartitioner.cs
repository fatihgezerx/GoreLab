using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// Divides a static mesh into pieces at random: spreads seeds over the surface and gives every triangle to the seed it is nearest to along the
    /// surface, so every piece is one connected patch. The same mesh, count and seed give the same pieces every time. Pure arrays in, pure arrays out.
    /// </summary>
    public static class GorePartitioner
    {
        /// <summary>The most pieces the studio offers. Each piece is a physical object of its own at run time, which costs.</summary>
        public const int MaxPiecesLimit = 100;

        /// <summary>The most pieces a mesh with <paramref name="triangleCount"/> triangles can be divided into: a piece needs at least one triangle.</summary>
        public static int MaxPieces(int triangleCount) => Math.Max(0, Math.Min(triangleCount, MaxPiecesLimit));

        /// <summary>The same; columns right through the object are cut out of the triangles, so with <paramref name="through"/> the number of triangles does not limit them.</summary>
        public static int MaxPieces(int triangleCount, bool through) => through ? MaxPiecesLimit : MaxPieces(triangleCount);

        /// <summary>The number of separate pieces the surface already is (triangles that share an edge are one piece).</summary>
        public static int IslandCount(GoreMeshData mesh)
        {
            var corners = GoreMeshTopology.Flatten(mesh, out _);
            if (corners.Length == 0)
            {
                return 0;
            }

            GoreMeshTopology.Adjacency(corners, GoreMeshTopology.Weld(mesh.Vertices), out var start, out var list);
            GoreMeshTopology.TriangleIslands(start, list, out var count);
            return count;
        }

        /// <summary>
        /// Divides the mesh into <paramref name="count"/> pieces. Returns the piece (0 up to <paramref name="actualCount"/> - 1) of every triangle, by flat
        /// number. <paramref name="actualCount"/> is <paramref name="count"/> raised to the number of separate pieces the mesh already is (each needs
        /// one seed) and lowered to the number of triangles. Deterministic for a <paramref name="seed"/>.
        /// </summary>
        public static int[] Divide(GoreMeshData mesh, int count, int seed, out int actualCount)
        {
            var corners = GoreMeshTopology.Flatten(mesh, out _);
            var triangles = corners.Length / 3;
            actualCount = 0;
            if (triangles == 0)
            {
                return Array.Empty<int>();
            }

            var weld = GoreMeshTopology.Weld(mesh.Vertices);
            GoreMeshTopology.Adjacency(corners, weld, out var start, out var list);
            var island = GoreMeshTopology.TriangleIslands(start, list, out var islandCount);

            actualCount = Math.Min(Math.Max(count, Math.Max(2, islandCount)), triangles);
            if (actualCount < islandCount)
            {
                // More separate pieces than triangles cannot happen, but keep the invariant: every island has a seed.
                actualCount = islandCount;
            }

            var centres = new Vector3[triangles];
            for (var t = 0; t < triangles; t++)
            {
                centres[t] = (mesh.Vertices[corners[t * 3]] + mesh.Vertices[corners[t * 3 + 1]] + mesh.Vertices[corners[t * 3 + 2]]) / 3f;
            }

            var random = new Xorshift((uint)seed);
            var seeds = PickSeeds(centres, island, islandCount, actualCount, random);
            return Flood(centres, start, list, seeds);
        }

        /// <summary>The thinnest axis of the mesh's bounds (X, Y or Z), and whether it is clearly thin: at most half of the next thinnest (a wall, a door, a sign; not a crate or a table).</summary>
        public static GoreBreakAxis ThinAxis(GoreMeshData mesh, out bool thin)
        {
            thin = false;
            if (mesh.Vertices.Length == 0)
            {
                return GoreBreakAxis.Z;
            }

            var bounds = new Bounds(mesh.Vertices[0], Vector3.zero);
            for (var v = 1; v < mesh.Vertices.Length; v++)
            {
                bounds.Encapsulate(mesh.Vertices[v]);
            }

            var size = bounds.size;
            var axis = GoreBreakAxis.X;
            var smallest = size.x;
            if (size.y < smallest)
            {
                axis = GoreBreakAxis.Y;
                smallest = size.y;
            }

            if (size.z < smallest)
            {
                axis = GoreBreakAxis.Z;
                smallest = size.z;
            }

            var next = float.MaxValue;
            if (axis != GoreBreakAxis.X)
            {
                next = Math.Min(next, size.x);
            }

            if (axis != GoreBreakAxis.Y)
            {
                next = Math.Min(next, size.y);
            }

            if (axis != GoreBreakAxis.Z)
            {
                next = Math.Min(next, size.z);
            }

            thin = smallest <= next * 0.5f;
            return axis;
        }

        // One seed in every separate piece of the mesh (so none is left without), the rest where the surface is farthest from the seeds so far.
        private static int[] PickSeeds(Vector3[] centres, int[] island, int islandCount, int count, Xorshift random)
        {
            var triangles = centres.Length;
            var seeds = new List<int>(count);
            var isSeed = new bool[triangles];
            var nearest = new float[triangles];
            for (var t = 0; t < triangles; t++)
            {
                nearest[t] = float.MaxValue;
            }

            var members = new List<int>[islandCount];
            for (var i = 0; i < islandCount; i++)
            {
                members[i] = new List<int>();
            }

            for (var t = 0; t < triangles; t++)
            {
                members[island[t]].Add(t);
            }

            for (var i = 0; i < islandCount; i++)
            {
                AddSeed(members[i][random.Next(members[i].Count)], centres, seeds, isSeed, nearest);
            }

            while (seeds.Count < count)
            {
                double total = 0;
                for (var t = 0; t < triangles; t++)
                {
                    if (!isSeed[t])
                    {
                        total += nearest[t];
                    }
                }

                var pick = -1;
                if (total > 0)
                {
                    var target = random.NextDouble() * total;
                    double running = 0;
                    for (var t = 0; t < triangles; t++)
                    {
                        if (isSeed[t])
                        {
                            continue;
                        }

                        running += nearest[t];
                        pick = t;
                        if (running >= target)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    // Every triangle sits on a seed already (a degenerate mesh): take the next one that is free.
                    for (var t = 0; t < triangles; t++)
                    {
                        if (!isSeed[t])
                        {
                            pick = t;
                            break;
                        }
                    }
                }

                if (pick < 0)
                {
                    break;
                }

                AddSeed(pick, centres, seeds, isSeed, nearest);
            }

            return seeds.ToArray();
        }

        private static void AddSeed(int triangle, Vector3[] centres, List<int> seeds, bool[] isSeed, float[] nearest)
        {
            seeds.Add(triangle);
            isSeed[triangle] = true;
            var at = centres[triangle];
            for (var t = 0; t < centres.Length; t++)
            {
                var distance = (centres[t] - at).sqrMagnitude;
                if (distance < nearest[t])
                {
                    nearest[t] = distance;
                }
            }
        }

        // Every triangle goes to the seed it can be reached from at the least cost along the surface (the cost of a step is the distance between the
        // middles of the two triangles), so a piece never has a gap.
        private static int[] Flood(Vector3[] centres, int[] start, int[] list, int[] seeds)
        {
            var triangles = centres.Length;
            var owner = new int[triangles];
            var distance = new double[triangles];
            for (var t = 0; t < triangles; t++)
            {
                owner[t] = -1;
                distance[t] = double.MaxValue;
            }

            var heap = new GoreHeap();
            for (var s = 0; s < seeds.Length; s++)
            {
                owner[seeds[s]] = s;
                distance[seeds[s]] = 0;
                heap.Push(0, seeds[s], s);
            }

            while (heap.Count > 0)
            {
                var entry = heap.Pop();
                var t = entry.Id;
                if (entry.Key > distance[t] || entry.Extra != owner[t])
                {
                    continue;
                }

                for (var i = start[t]; i < start[t + 1]; i++)
                {
                    var next = list[i];
                    var candidate = entry.Key + (centres[next] - centres[t]).magnitude;
                    if (candidate < distance[next])
                    {
                        distance[next] = candidate;
                        owner[next] = entry.Extra;
                        heap.Push(candidate, next, entry.Extra);
                    }
                }
            }

            return owner;
        }

        // A tiny deterministic generator: the same seed gives the same numbers on every machine and Unity version.
        internal struct Xorshift
        {
            private uint _state;

            internal Xorshift(uint seed)
            {
                _state = seed == 0 ? 0x9E3779B9u : seed * 2654435761u + 1u;
                if (_state == 0)
                {
                    _state = 1;
                }

                // Mix once so near seeds (1, 2, 3) do not start alike.
                for (var i = 0; i < 4; i++)
                {
                    NextUint();
                }
            }

            internal uint NextUint()
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return _state;
            }

            internal int Next(int limit) => (int)(NextUint() % (uint)limit);

            internal double NextDouble() => NextUint() / 4294967296.0;
        }
    }
}
