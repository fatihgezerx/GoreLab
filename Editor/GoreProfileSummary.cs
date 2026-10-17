using System;
using System.Text;
using UnityEngine;

namespace GoreLab
{
    /// <summary>The texts that describe one skinned mesh of a <see cref="GoreProfile"/> (a <see cref="GoreMeshSet"/>).</summary>
    internal sealed class GoreSetInfo
    {
        /// <summary>The name of the renderer the set was made from.</summary>
        public string Name;

        /// <summary>The name of the source mesh.</summary>
        public string Source;

        /// <summary>"yes" when the cuts of this mesh get lids.</summary>
        public string Lids;

        /// <summary>How many segments have a part of the mesh, for example "9 of 14".</summary>
        public string Parts;

        /// <summary>The triangle count of all parts together.</summary>
        public string Triangles;

        /// <summary>How many cuts have a lid, for example "13 of 13".</summary>
        public string CutLids;
    }

    /// <summary>The texts that describe one cut of a <see cref="GoreProfile"/>.</summary>
    internal sealed class GoreCutInfo
    {
        /// <summary>The cut's name and limb in one line, for example "LeftUpperArm (LeftUpperArm)".</summary>
        public string Title;

        /// <summary>The parent and the cuts below, in one sentence.</summary>
        public string Detail;

        /// <summary>The cut's own name.</summary>
        public string Name;

        /// <summary>The humanoid part the cut belongs to, as text.</summary>
        public string Limb;

        /// <summary>The name of the cut this one lies below, or "body".</summary>
        public string Parent;

        /// <summary>The names of the cuts that come off together with this one, or "none".</summary>
        public string Below;
    }

    /// <summary>
    /// Works out the read-only summary of a <see cref="GoreProfile"/> (what the setup made) as plain texts. It has no UI, so the studio
    /// Inspector and the plain fallback Inspector show the same numbers. Call it when the Inspector opens and when the project changes,
    /// not on every repaint: it walks the mesh assets.
    /// </summary>
    internal static class GoreProfileSummary
    {
        /// <summary>True when the profile has no cuts or no skinned meshes yet (the setup was not done).</summary>
        internal static bool IsEmpty(GoreProfile profile) => profile.CutCount == 0 || profile.SetCount == 0;

        /// <summary>"13 cuts, 3 skinned meshes".</summary>
        internal static string Counts(GoreProfile profile) =>
            GoreGUI.Plural(profile.CutCount, "cut", "cuts") + ", " + GoreGUI.Plural(profile.SetCount, "skinned mesh", "skinned meshes");

        /// <summary>One <see cref="GoreSetInfo"/> for every skinned mesh of the profile.</summary>
        internal static GoreSetInfo[] BuildSets(GoreProfile profile)
        {
            var cutCount = profile.CutCount;
            var sets = new GoreSetInfo[profile.SetCount];
            for (var s = 0; s < sets.Length; s++)
            {
                sets[s] = BuildSet(profile.MeshSets[s], cutCount);
            }

            return sets;
        }

        /// <summary>One <see cref="GoreCutInfo"/> for every cut of the profile.</summary>
        internal static GoreCutInfo[] BuildCuts(GoreProfile profile)
        {
            var cuts = new GoreCutInfo[profile.CutCount];
            var builder = new StringBuilder();
            for (var c = 0; c < cuts.Length; c++)
            {
                cuts[c] = BuildCut(profile, c, builder);
            }

            return cuts;
        }

        private static GoreSetInfo BuildSet(GoreMeshSet set, int cutCount)
        {
            if (set == null)
            {
                return new GoreSetInfo { Name = "(empty)", Source = "-", Lids = "-", Parts = "-", Triangles = "-", CutLids = "-" };
            }

            var segments = set.Segments ?? Array.Empty<Mesh>();
            var filled = 0;
            var indices = 0L;
            for (var i = 0; i < segments.Length; i++)
            {
                var mesh = segments[i];
                if (mesh == null)
                {
                    continue;
                }

                filled++;
                for (var sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    indices += mesh.GetIndexCount(sub);
                }
            }

            var withLids = 0;
            if (set.LimbCaps != null)
            {
                for (var i = 0; i < set.LimbCaps.Length && i < cutCount; i++)
                {
                    if (set.LimbCaps[i] != null)
                    {
                        withLids++;
                    }
                }
            }

            return new GoreSetInfo
            {
                Name = string.IsNullOrEmpty(set.Name) ? "(unnamed)" : set.Name,
                Source = set.SourceMesh != null ? set.SourceMesh.name : "(missing)",
                Lids = set.Lids ? "yes" : "no",
                Parts = filled + " of " + segments.Length,
                Triangles = (indices / 3).ToString("N0"),
                CutLids = withLids + " of " + cutCount
            };
        }

        private static GoreCutInfo BuildCut(GoreProfile profile, int index, StringBuilder builder)
        {
            var cut = profile.Cuts[index];
            if (cut == null)
            {
                return new GoreCutInfo { Title = "(empty)", Detail = string.Empty, Name = "(empty)", Limb = string.Empty, Parent = string.Empty, Below = string.Empty };
            }

            var parent = cut.Parent >= 0 && cut.Parent < profile.CutCount && profile.Cuts[cut.Parent] != null ? profile.Cuts[cut.Parent].Name : "body";
            builder.Clear();
            var below = cut.Below ?? Array.Empty<int>();
            for (var i = 0; i < below.Length; i++)
            {
                var other = below[i] >= 0 && below[i] < profile.CutCount ? profile.Cuts[below[i]] : null;
                if (other == null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(other.Name);
            }

            var belowText = builder.Length > 0 ? builder.ToString() : "none";
            return new GoreCutInfo
            {
                Title = cut.Name + " (" + cut.Limb + ")",
                Detail = "Parent: " + parent + ". Below: " + belowText + ".",
                Name = cut.Name,
                Limb = cut.Limb.ToString(),
                Parent = parent,
                Below = belowText
            };
        }
    }
}
