using System;
using System.Collections.Generic;

namespace GoreLab
{
    /// <summary>What the user asked of one static mesh in the Gore Studio.</summary>
    public sealed class GoreBreakSettings
    {
        /// <summary>How the pieces are decided.</summary>
        public GoreBreakMode Mode = GoreBreakMode.Random;

        /// <summary>How many triangles to add before dividing (0 = none).</summary>
        public int ExtraTriangles;

        /// <summary>Which triangles of the source (flat numbers) may be split to add them; null = every triangle.</summary>
        public bool[] ExtraSelection;

        /// <summary>Surface: patches of the surface. Through: columns right through the object along <see cref="Axis"/>.</summary>
        public GoreBreakShape Shape = GoreBreakShape.Surface;

        /// <summary>Through: the way through the object (Auto = the thinnest way).</summary>
        public GoreBreakAxis Axis = GoreBreakAxis.Auto;

        /// <summary>Random: how many pieces (at least 2).</summary>
        public int RandomPieces = 8;

        /// <summary>Random: the seed; the same seed gives the same pieces.</summary>
        public int RandomSeed = 1;

        /// <summary>Manual: the piece of every triangle of the finer mesh (flat numbers), or -1 for the rest piece.</summary>
        public int[] ManualOwners;

        /// <summary>Manual: how many pieces the numbers in <see cref="ManualOwners"/> go up to.</summary>
        public int ManualPieces;
    }

    /// <summary>The outcome of <see cref="GoreBreakPlanner.Plan"/>: the finer mesh and which piece every triangle of it belongs to.</summary>
    public sealed class GoreBreakPlan
    {
        /// <summary>The mesh the pieces are cut from: the source with the extra triangles in it (the source itself when none were added).</summary>
        public GoreMeshData Mesh;

        /// <summary>Per triangle of <see cref="Mesh"/> (flat number), whether it was made by adding triangles.</summary>
        public bool[] IsNew;

        /// <summary>Per triangle of <see cref="Mesh"/>, the piece it belongs to (-1 = the rest piece).</summary>
        public int[] Owner;

        /// <summary>The number of pieces the numbers in <see cref="Owner"/> go up to (not counting the rest piece).</summary>
        public int PieceCount;

        /// <summary>How many triangles were added to the source.</summary>
        public int Added;

        /// <summary>Whether the pieces are columns through the object (<see cref="GoreBreakShape.Through"/>); the lids then end at the middle of the piece.</summary>
        public bool Through;

        /// <summary>Whether the pieces were cut along straight borders through the object (Random + Through): the broken faces get flat lids, one for every border between two pieces.</summary>
        public bool PlanarLids;

        /// <summary>For a Through division, the axis that was used (X, Y or Z), also when Auto was asked.</summary>
        public GoreBreakAxis AxisUsed = GoreBreakAxis.Auto;

        /// <summary>The number of triangles of <see cref="Mesh"/>.</summary>
        public int TriangleCount;

        /// <summary>What was noticed on the way (a Random count that was raised, a Manual list that no longer fits).</summary>
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Works out how a static mesh is divided without saving anything: adds the extra triangles and gives every triangle to a piece. The studio's
    /// preview and the setup both go through this, so what the preview shows is what is saved.
    /// </summary>
    public static class GoreBreakPlanner
    {
        /// <summary>Divides <paramref name="source"/> as <paramref name="settings"/> says. The source is never changed.</summary>
        public static GoreBreakPlan Plan(GoreMeshData source, GoreBreakSettings settings)
        {
            var plan = new GoreBreakPlan();
            if (settings.ExtraTriangles > 0)
            {
                var tessellation = GoreTessellator.Tessellate(source, settings.ExtraTriangles, settings.ExtraSelection);
                plan.Mesh = tessellation.Mesh;
                plan.IsNew = tessellation.IsNew;
                plan.Added = tessellation.Added;
                if (tessellation.Added < settings.ExtraTriangles)
                {
                    plan.Notes.Add("Only " + tessellation.Added + " triangles could be added: the rest of the surface has no area left to split.");
                }
            }
            else
            {
                plan.Mesh = source;
                plan.IsNew = new bool[GoreMeshTopology.TriangleCount(source)];
            }

            plan.TriangleCount = GoreMeshTopology.TriangleCount(plan.Mesh);
            if (settings.Mode == GoreBreakMode.Random)
            {
                int actual;
                plan.Through = settings.Shape == GoreBreakShape.Through;
                if (plan.Through)
                {
                    // The triangles that cross a border are cut along it, so the mesh the pieces are cut from is a finer one than the one given.
                    plan.AxisUsed = settings.Axis == GoreBreakAxis.Auto ? GorePartitioner.ThinAxis(plan.Mesh, out _) : settings.Axis;
                    var clipped = GoreThroughClipper.Divide(plan.Mesh, settings.RandomPieces, settings.RandomSeed, plan.AxisUsed);
                    var isNew = new bool[clipped.Parent.Length];
                    for (var t = 0; t < isNew.Length; t++)
                    {
                        isNew[t] = plan.IsNew[clipped.Parent[t]];
                    }

                    plan.Mesh = clipped.Mesh;
                    plan.IsNew = isNew;
                    plan.TriangleCount = GoreMeshTopology.TriangleCount(plan.Mesh);
                    plan.Owner = clipped.Owner;
                    plan.PlanarLids = true;
                    actual = clipped.PieceCount;
                }
                else
                {
                    plan.Owner = GorePartitioner.Divide(plan.Mesh, settings.RandomPieces, settings.RandomSeed, out actual);
                }

                plan.PieceCount = actual;
                if (actual != settings.RandomPieces)
                {
                    plan.Notes.Add(actual > settings.RandomPieces
                        ? "The mesh is already " + actual + " separate pieces, so it cannot be divided into fewer."
                        : "The mesh has only " + plan.TriangleCount + " triangles, so it is divided into " + actual + " pieces. Add triangles to get more.");
                }
            }
            else
            {
                plan.Through = settings.Shape == GoreBreakShape.Through;
                plan.AxisUsed = settings.Axis == GoreBreakAxis.Auto ? GorePartitioner.ThinAxis(plan.Mesh, out _) : settings.Axis;
                plan.Owner = new int[plan.TriangleCount];
                plan.PieceCount = Math.Max(0, settings.ManualPieces);
                if (settings.ManualOwners != null && settings.ManualOwners.Length == plan.TriangleCount)
                {
                    Array.Copy(settings.ManualOwners, plan.Owner, plan.TriangleCount);
                }
                else
                {
                    for (var t = 0; t < plan.Owner.Length; t++)
                    {
                        plan.Owner[t] = -1;
                    }

                    if (settings.ManualOwners != null && settings.ManualOwners.Length > 0)
                    {
                        plan.Notes.Add("The pieces drawn by hand no longer fit the mesh (the number of triangles changed), so they were dropped.");
                    }
                }
            }

            return plan;
        }
    }
}
