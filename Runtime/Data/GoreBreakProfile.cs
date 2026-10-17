using System;
using UnityEngine;

namespace GoreLab
{
    /// <summary>How the pieces of a static mesh are decided in the Gore Studio.</summary>
    public enum GoreBreakMode : byte
    {
        /// <summary>The user gives the triangles to the pieces by hand.</summary>
        Manual = 0,

        /// <summary>The studio divides the mesh into a number of pieces at random.</summary>
        Random = 1
    }

    /// <summary>How a static mesh is divided: by patches of its surface, or by columns right through it.</summary>
    public enum GoreBreakShape : byte
    {
        /// <summary>The pieces are patches of the surface: the front and the back of a slab break apart independently (a statue, a rock, a table).</summary>
        Surface = 0,

        /// <summary>The pieces are columns right through the object along an axis: the front, the back and the sides of a cell go together (a wall, a door, a sign).</summary>
        Through = 1
    }

    /// <summary>The way a <see cref="GoreBreakShape.Through"/> division goes through the object, in the space of its mesh.</summary>
    public enum GoreBreakAxis : byte
    {
        /// <summary>The thinnest way of the object (the thickness of a wall).</summary>
        Auto = 0,
        X = 1,
        Y = 2,
        Z = 3
    }

    /// <summary>One piece of a static mesh that can break off: its mesh, in the space of the object it came from, and what it is made of.</summary>
    [Serializable]
    public sealed class GoreBreakPiece
    {
        [Tooltip("What the piece is called (for the studio).")]
        public string Name = string.Empty;

        [Tooltip("The mesh of the piece, in the space of the mesh it was cut from, so the pieces put together look like the original.")]
        public Mesh Mesh;

        [Tooltip("For each submesh of the piece, the number of the source renderer's material it uses, or -1 for the lid (the profile's fill material).")]
        public int[] Materials = Array.Empty<int>();

        [Tooltip("The middle of the piece in the space of its mesh: where its weight is centred.")]
        public Vector3 Center;

        [Tooltip("The size of the piece in the space of its mesh (for its collider).")]
        public Vector3 Size;

        [Tooltip("The volume of the piece in the space of its mesh, in cubic units (its weight is this times the density and the scale of the object).")]
        public float Volume;
    }

    /// <summary>
    /// Everything worked out once, in the editor, for one static mesh (a table, a crate, a wall): the pieces it breaks into, and what a piece does
    /// when it breaks off. The meshes are assets in the folder next to it. Share one profile between every object that has the same mesh. Made by
    /// <c>Tools &gt; Gore Studio</c>.
    /// </summary>
    [CreateAssetMenu(fileName = "New Gore Break Profile", menuName = "GoreLab/Gore Break Profile")]
    public sealed class GoreBreakProfile : ScriptableObject
    {
        [Tooltip("The mesh the pieces were made from. An object whose mesh is another one needs its own profile.")]
        public Mesh SourceMesh;

        [Tooltip("The pieces the mesh breaks into.")]
        public GoreBreakPiece[] Pieces = Array.Empty<GoreBreakPiece>();

        [Tooltip("The material of the broken faces (wood for a table, stone for a statue). The studio fills it with the default dark material when none is chosen.")]
        public Material FillMaterial;

        [Tooltip("Whether the mesh is closed, so the broken faces got lids. A wall or a plane is not and breaks into thin shards.")]
        public bool Closed;

        [Min(0f)] [Tooltip("Seconds a broken piece lies there before it is removed. 0 = it stays.")]
        public float DespawnTime = 25f;

        [Min(0.1f)] [Tooltip("Kilograms per cubic meter of a piece, to find its weight from its size.")]
        public float Density = 600f;

        [Min(0f)] [Tooltip("How hard a piece spins when it breaks off, in turns per second at most.")]
        public float Spin = 1.5f;

        [Min(0.001f)] [Tooltip("The thickness, in meters, that gives the pieces of a wall or a plane (not closed) a weight.")]
        public float OpenThickness = 0.05f;

        // What the studio decided, kept so it opens as it was left. Not used at run time.
        [HideInInspector] public GoreBreakMode Mode = GoreBreakMode.Random;
        [HideInInspector] public GoreBreakShape Shape = GoreBreakShape.Surface;
        [HideInInspector] public GoreBreakAxis Axis = GoreBreakAxis.Auto;
        [HideInInspector] [Min(2)] public int RandomPieces = 8;
        [HideInInspector] public int RandomSeed = 1;
        [HideInInspector] [Min(0)] public int ExtraTriangles;
        [HideInInspector] public int[] ExtraSelection = Array.Empty<int>();
        [HideInInspector] public int[] ManualOwners = Array.Empty<int>();
        [HideInInspector] [Min(0.01f)] public float LidUvScale = 1f;

        /// <summary>The number of pieces.</summary>
        public int PieceCount => Pieces != null ? Pieces.Length : 0;
    }
}
