using System;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// One place a character can be cut: a bone whose part of the body (its segment: the skin that follows that bone and nothing
    /// below another cut) can be taken off. Written by the setup window; read at run time.
    /// </summary>
    [Serializable]
    public sealed class GoreCut
    {
        [Tooltip("What the cut is called, e.g. LeftUpperArm. A humanoid's cuts carry the names of the Gore Limb values.")]
        public string Name = "Cut";

        [Tooltip("Which part of a humanoid this is; None for a cut of any other rig.")]
        public GoreLimb Limb = GoreLimb.None;

        [Tooltip("The path of the bone below the character's root object, so the cut finds its bone again in a copy of the character.")]
        public string BonePath = string.Empty;

        [Tooltip("The cut this one lies below (the upper arm of a forearm), or -1 when its part is joined to the body itself.")]
        public int Parent = -1;

        [Tooltip("The cuts below this one: they come off together with it. Not including itself.")]
        public int[] Below = Array.Empty<int>();

        [Tooltip("The material that fills this cut: the lid on the part that comes off and the stump on the body (flesh for a limb, wood for a table). Empty = the default red material.")]
        public Material FillMaterial;

        [Tooltip("A worn piece (hair, a helmet, a hood) is never split by this cut: it goes whole to the side it mostly belongs to. On for the head, off for a limb, where a glove or a sleeve has to be split.")]
        public bool WornWhole;
    }

    /// <summary>Which of a renderer's materials each submesh of one of its parts uses.</summary>
    [Serializable]
    public sealed class SegmentMaterials
    {
        [Tooltip("For each submesh of the part, the index of the source renderer's material.")]
        public int[] Source = Array.Empty<int>();
    }

    /// <summary>What the user decided by hand about a vertex and a cut.</summary>
    public enum GoreOverrideKind : byte
    {
        /// <summary>Left to the bone weights.</summary>
        Auto = 0,

        /// <summary>The vertex goes with the part that comes off with the cut, whatever its weights say.</summary>
        Include = 1,

        /// <summary>The vertex stays on the body side of the cut (and of every cut below it).</summary>
        Exclude = 2
    }

    /// <summary>A vertex of a source mesh that was assigned to a cut, or kept out of it, by hand (in the Gore Studio).</summary>
    [Serializable]
    public struct GoreVertexOverride
    {
        /// <summary>The cut (a number of <see cref="GoreProfile.Cuts"/>).</summary>
        public int Cut;

        /// <summary>The vertex of the source mesh.</summary>
        public int Vertex;

        /// <summary>What was decided.</summary>
        public GoreOverrideKind Kind;
    }

    /// <summary>Where the cut of the character's own (naked) meshes crosses the limb, in the space of the cut's bone, and how wide the limb is there.</summary>
    [Serializable]
    public sealed class GoreCutSection
    {
        [Tooltip("The middle of the cut line, in the space of the cut's bone.")]
        public Vector3 Center;

        [Tooltip("The typical distance of the cut line from its middle.")]
        public float Radius;

        [Tooltip("Whether the cut line had enough points to measure.")]
        public bool Valid;
    }

    /// <summary>What a worn piece (armour, a sleeve, hair) does when a cut happens, set by hand in the Cloth parts card of the Gore Studio.</summary>
    public enum GoreClothMode : byte
    {
        /// <summary>No rule was set (a piece the studio never saw): the automatic rules apply, a readable piece that goes round a limb is split there, any other piece goes whole with the part it mostly belongs to.</summary>
        Auto = 0,

        /// <summary>The piece is cut exactly along the cuts, by its bone weights (needs Read/Write on its mesh); the piece on the body side stays or flies off, the piece on the part side follows the part or flies off.</summary>
        Split = 1,

        /// <summary>The piece is not cut (no Read/Write needed): it stays on the body, or goes whole with the cut chosen as its owner, following the part or flying off.</summary>
        Whole = 2,

        /// <summary>The mesh is divided into its separate pieces (needs Read/Write); each piece goes whole with the part chosen for it (left and right trouser leg of one mesh), following it or flying off.</summary>
        Pieces = 3
    }

    /// <summary>What to do with one worn mesh: found by the mesh the renderer shows.</summary>
    [Serializable]
    public sealed class GoreClothRule
    {
        [Tooltip("The worn mesh this rule is for. Every character that wears this mesh follows it.")]
        public Mesh Mesh;

        [Tooltip("What the piece is called (for the studio).")]
        public string Name = string.Empty;

        [Tooltip("What the piece does when a cut happens.")]
        public GoreClothMode Mode = GoreClothMode.Auto;

        [Tooltip("Whole: the cut the piece goes with (a number of the profile's cuts), or -1 for the body (the piece stays).")]
        public int Owner = -1;

        [Tooltip("The piece flies off on its own instead of following the part it belongs to (Split: the piece on the part side; Whole: the piece).")]
        public bool Flies;

        [Tooltip("Pieces: for each piece of the mesh (its separate pieces, or its left and right half when it is one piece), in the order the studio lists them, the cut it goes with (a number of the profile's cuts), -1 for the body, -2 to leave it to its bone weights.")]
        public int[] PieceOwners = Array.Empty<int>();

        [Tooltip("Whole: the owner was chosen by Auto (the studio worked it out and shows Auto again). Not used at run time.")]
        public bool OwnerAuto;

        [Tooltip("Pieces: for each piece, in the same order, whether its owner was chosen by Auto. Not used at run time.")]
        public bool[] PieceAuto = Array.Empty<bool>();

        [Tooltip("Pieces: for each piece, in the same order, whether it is cut along the body parts (PieceOwners is then not used for it) instead of going whole with a part.")]
        public bool[] PieceCut = Array.Empty<bool>();

        [Tooltip("Pieces: for each piece that is cut, in the same order, whether the piece on its body side flies off too (PieceFlies is then the part side).")]
        public bool[] PieceBodyFlies = Array.Empty<bool>();

        [Tooltip("Pieces: for each piece, in the same order, whether it flies off on its own when the part it goes with is cut off (false = it follows the part).")]
        public bool[] PieceFlies = Array.Empty<bool>();

        [Tooltip("Split: the piece on the body side (what is left of the cut piece) flies off too, instead of staying on the body.")]
        public bool BodyFlies;
    }

    /// <summary>
    /// What was made from one skinned mesh of the character: a part of it for every segment (the body first, then one for each cut) and the
    /// two lids of every cut. A character with several skinned meshes (body, clothes, hair) has a set for each; they are cut along the same bones.
    /// </summary>
    [Serializable]
    public sealed class GoreMeshSet
    {
        [Tooltip("The name of the renderer this set was made from.")]
        public string Name = string.Empty;

        [Tooltip("The mesh this set was made from. A character whose renderer has another mesh needs its own profile.")]
        public Mesh SourceMesh;

        [Tooltip("Vertices the user assigned to a cut, or kept out of it, by hand in the Gore Studio. Only the studio reads this: it fills them in again when the character is opened.")]
        public GoreVertexOverride[] Overrides = Array.Empty<GoreVertexOverride>();

        [Tooltip("The bind poses of the source mesh (what a cut needs to keep the skin round the stump on the body).")]
        public Matrix4x4[] BindPoses = Array.Empty<Matrix4x4>();

        [Tooltip("A worn piece that is not split: it goes whole with one part (made at run time for a mesh that cannot be read).")]
        public bool Whole;

        [Tooltip("A worn piece whose part flies off on its own when its cut is made, instead of following the part it belongs to (made at run time from the cloth rules).")]
        public bool Fly;

        [Tooltip("A worn piece whose body side flies off too when a cut is made across it, instead of staying on the body (made at run time from the cloth rules).")]
        public bool BodyFly;

        [Tooltip("Whether the cuts of this mesh get lids (a stump and a lid on the part): for the skin yes, for clothes usually no.")]
        public bool Lids = true;

        [Tooltip("Per segment (the body first, then one for each cut), the part of the mesh that follows that segment's bones. Null when the segment has no triangle.")]
        public Mesh[] Segments = Array.Empty<Mesh>();

        [Tooltip("Per segment, which of the source renderer's materials each submesh of its part uses.")]
        public SegmentMaterials[] Materials = Array.Empty<SegmentMaterials>();

        [Tooltip("Per cut, the stump: the lid that closes the body where the part was taken off. Null when the cut has no closed edge or this set has no lids.")]
        public Mesh[] Stumps = Array.Empty<Mesh>();

        [Tooltip("Per cut, the lid that closes the part that was taken off, at its cut end. Null when the cut has no closed edge or this set has no lids.")]
        public Mesh[] LimbCaps = Array.Empty<Mesh>();
    }

    /// <summary>
    /// Everything that was worked out once, in the editor, for one character's meshes and bones: the cuts and how they are nested, the parts
    /// of each skinned mesh, and the settings of what a cut-off part does. The meshes are assets in the folder next to it. Share one profile
    /// between every character that has the same meshes and bones. Made by <c>Tools &gt; Gore Studio</c>.
    /// </summary>
    [CreateAssetMenu(fileName = "New Gore Profile", menuName = "GoreLab/Gore Profile")]
    public sealed class GoreProfile : ScriptableObject
    {
        [Tooltip("The places this character can be cut, in the order the setup made them.")]
        public GoreCut[] Cuts = Array.Empty<GoreCut>();

        [Tooltip("What was made from each skinned mesh of the character.")]
        public GoreMeshSet[] MeshSets = Array.Empty<GoreMeshSet>();

        [Tooltip("Per cut, where the cut of the character's own meshes crosses the limb and how wide it is there. A worn piece is cut only by a cut it goes round at about this place and size.")]
        public GoreCutSection[] Sections = Array.Empty<GoreCutSection>();

        [Min(0f)] [Tooltip("Seconds a cut-off part lies there before it is removed. 0 = it stays.")]
        public float DespawnTime = 25f;

        [Min(0.1f)] [Tooltip("Kilograms per cubic meter of a cut-off part, to find its weight from its size.")]
        public float Density = 600f;

        [Min(0f)] [Tooltip("How hard a cut-off part spins when it flies off, in turns per second at most.")]
        public float Spin = 2f;

#if UNITY_EDITOR
        // What the user dropped in the Worn equipment card of the Gore Studio for this character (prefabs, folders, looks), kept so the card is the
        // same next time. Editor only: it is not in a build, so it pulls nothing into it.
        [HideInInspector] public UnityEngine.Object[] WornSources = Array.Empty<UnityEngine.Object>();
#endif

        [Tooltip("What each worn mesh does when a cut happens, set in the Cloth parts card of the Gore Studio. A mesh without a rule follows the automatic rules.")]
        public GoreClothRule[] ClothRules = Array.Empty<GoreClothRule>();

        [Tooltip("Cut the equipment a character wears (skinned meshes put on at run time, which the setup did not prepare) along the same cuts. Worn meshes need Read/Write. Off: only the prepared meshes are cut.")]
        public bool CutEquipment = true;

        // Frames to wait before looking at the equipment: a worn piece ties its bones to the skeleton in its own first frame. Rarely changed.
        [HideInInspector] [Min(1)] public int EquipmentFrames = 2;

        [Tooltip("The blood that spurts from a cut and then runs: a Particle System prefab, played at the stump on the body and at the end of the part that comes off. Empty = no blood.")]
        public GameObject BloodPrefab;

        [Min(0f)] [Tooltip("Seconds the blood keeps running from a cut, then it stops and the effect is given back. 0 = the prefab's own duration.")]
        public float BleedTime = 8f;

        [Min(0.01f)] [Tooltip("Scales the blood effect: a thin wrist and a thick thigh need different sizes.")]
        public float BloodScale = 1f;

        /// <summary>The number of cuts.</summary>
        public int CutCount => Cuts != null ? Cuts.Length : 0;

        /// <summary>The number of skinned meshes that were set up.</summary>
        public int SetCount => MeshSets != null ? MeshSets.Length : 0;

        /// <summary>The number of the segment the cut number <paramref name="cut"/> takes off: the body is segment 0, the cut n is segment n + 1.</summary>
        public static int SegmentOf(int cut) => cut + 1;

        /// <summary>The rule of a worn mesh, or null when it follows the automatic rules.</summary>
        public GoreClothRule RuleFor(Mesh mesh)
        {
            if (mesh == null || ClothRules == null)
            {
                return null;
            }

            for (var i = 0; i < ClothRules.Length; i++)
            {
                var rule = ClothRules[i];
                if (rule != null && rule.Mesh == mesh && rule.Mode != GoreClothMode.Auto)
                {
                    return rule;
                }
            }

            return null;
        }

        /// <summary>The number of the cut with <paramref name="name"/>, or -1.</summary>
        public int IndexOf(string name)
        {
            for (var i = 0; i < CutCount; i++)
            {
                if (Cuts[i] != null && string.Equals(Cuts[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The number of the cut of a humanoid's <paramref name="limb"/>, or -1 when this profile has none for it.</summary>
        public int IndexOf(GoreLimb limb)
        {
            if (limb == GoreLimb.None)
            {
                return -1;
            }

            for (var i = 0; i < CutCount; i++)
            {
                if (Cuts[i] != null && Cuts[i].Limb == limb)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
