using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoreLab
{
    /// <summary>What the user decided for one worn mesh in the Cloth parts card (kept by mesh; written into the profile on Apply).</summary>
    internal sealed class GoreStudioClothRule
    {
        public Mesh Mesh;
        public string Name = string.Empty;
        public GoreClothMode Mode;

        /// <summary>The name of the cut a piece that is not cut goes with: null = Auto (what it hangs on), empty = the body. By name, so renaming or removing a cut cannot point it at another.</summary>
        public string Owner;

        public bool Flies;

        /// <summary>Cut: the piece on the body side flies off too.</summary>
        public bool BodyFlies;

        /// <summary>Pieces: for each separate piece of the mesh, the name of the cut it goes with, "" for the body, null for its bone weights.</summary>
        public string[] PieceOwners = Array.Empty<string>();

        /// <summary>Pieces: for each piece, whether it flies off on its own when its part is cut off (false = it follows the part).</summary>
        public bool[] PieceFlies = Array.Empty<bool>();

        /// <summary>Pieces: for each piece, whether it is cut along the body parts instead of going whole with a part.</summary>
        public bool[] PieceCut = Array.Empty<bool>();

        /// <summary>Pieces: for each piece that is cut, whether its body side flies off too (PieceFlies is its part side).</summary>
        public bool[] PieceBodyFlies = Array.Empty<bool>();
    }

    /// <summary>One piece of clothing put on the naked character in the preview, and how it is painted.</summary>
    internal sealed class GoreStudioCloth
    {
        public GameObject Root;
        public SkinnedMeshRenderer Renderer;
        public GoreStudioClothRule Rule;
        public GUIContent Label;
        public bool Show = true;
        public bool Readable;
        public int Vertices;

        /// <summary>How many of its bones the naked character's skeleton has no bone of that name for.</summary>
        public int Missing;

        /// <summary>The segment (the body is 0, cut n is n + 1) most of its bones belong to: where it goes when it is not split.</summary>
        public int Majority;

        /// <summary>What the piece does now, in words, for the card. Empty until the cuts were analysed.</summary>
        public string Result = string.Empty;

        /// <summary>Whether the coloured copy is painted by the cuts (false: the piece is drawn as it is).</summary>
        public bool Classified;

        // The pose the preview shows, read once when the piece is put on: loose triangles (so every triangle can have its own colour).
        public Mesh SegmentMesh;
        public int[] CornerVertex = Array.Empty<int>();
        public float[] SoupShade = Array.Empty<float>();
        public int[] SoupSegment = Array.Empty<int>();
        public Color32[] SoupColors = Array.Empty<Color32>();

        /// <summary>For each vertex, the bone with the strongest weight (null when the mesh cannot be read, so it can only go whole).</summary>
        public int[] StrongestBone;

        /// <summary>For each vertex, the piece of the mesh it is in (-1 when no triangle uses it); null when the mesh cannot be read.</summary>
        public int[] VertexPiece;

        /// <summary>How many pieces the mesh has (its separate pieces, or two halves when it is one piece).</summary>
        public int IslandCount;


        /// <summary>The position of every corner of the coloured copy.</summary>
        public Vector3[] SoupWorld = Array.Empty<Vector3>();

        /// <summary>The piece of every corner as the rule counts them (<see cref="GoreMeshPipeline.Pieces"/>), and how many vertices each has.</summary>
        public int[] SoupPiece = Array.Empty<int>();
        public int PieceCount;
        public int[] PieceVertices = Array.Empty<int>();

        /// <summary>For each piece, what Auto makes of it: the name of the cut it goes with, or empty for the body.</summary>
        public string[] PieceAuto = Array.Empty<string>();

        /// <summary>What Auto makes of the whole mesh: the name of the cut it goes with, or empty for the body.</summary>
        public string AutoOwner = string.Empty;

        /// <summary>Whether the mesh holds separate pieces that Auto sends to different parts (a left and a right leg): worth offering to divide it.</summary>
        public bool CanDivide;

        /// <summary>The piece shown on its own in the preview (Pieces mode), or -1.</summary>
        public int SelectedPiece = -1;
    }

    internal sealed partial class GoreStudioModel
    {
        /// <summary>The clothes that are on the character in the preview.</summary>
        public readonly List<GoreStudioCloth> Cloth = new List<GoreStudioCloth>();

        /// <summary>What was decided for each worn mesh, including meshes that are not on the character right now.</summary>
        public readonly Dictionary<Mesh, GoreStudioClothRule> ClothRules = new Dictionary<Mesh, GoreStudioClothRule>();

        /// <summary>The piece shown on its own in the preview (a number of <see cref="Cloth"/>), or -1.</summary>
        public int SelectedCloth = -1;

        #region Dressing

        /// <summary>
        /// Puts the item <paramref name="root"/> (an instance of a clothing prefab, already in the preview scene) on the naked character: every skinned
        /// renderer of it that is switched on has its bones tied to the bones of the character with the same name, the way the dressing system does
        /// it at run time. Returns how many pieces it brought.
        /// </summary>
        public int Dress(GameObject root)
        {
            if (root == null || Instance == null)
            {
                return 0;
            }

            var map = new Dictionary<string, Transform>(256);
            foreach (var bone in Instance.GetComponentsInChildren<Transform>(true))
            {
                if (!map.ContainsKey(bone.name))
                {
                    map.Add(bone.name, bone);
                }
            }

            var added = 0;
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null || renderer.bones.Length == 0 || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var bones = renderer.bones;
                var missing = 0;
                for (var i = 0; i < bones.Length; i++)
                {
                    if (bones[i] != null && map.TryGetValue(bones[i].name, out var found))
                    {
                        bones[i] = found;
                    }
                    else
                    {
                        missing++;
                    }
                }

                renderer.bones = bones;
                if (renderer.rootBone != null && map.TryGetValue(renderer.rootBone.name, out var rootBone))
                {
                    renderer.rootBone = rootBone;
                }

                var mesh = renderer.sharedMesh;
                var cloth = new GoreStudioCloth
                {
                    Root = root,
                    Renderer = renderer,
                    Rule = RuleOf(mesh, renderer.name),
                    Label = new GUIContent(renderer.name, mesh.name),
                    Readable = mesh.isReadable,
                    Vertices = mesh.vertexCount,
                    Missing = missing
                };

                Pose(cloth);
                Cloth.Add(cloth);
                added++;
            }

            Dirty = true;
            return added;
        }

        // What a look (see IGoreLookPreview) changed on the naked body: its materials before, and the meshes it hid.
        private readonly Dictionary<SkinnedMeshRenderer, Material[]> _bodyMaterials = new Dictionary<SkinnedMeshRenderer, Material[]>();
        private readonly List<GoreStudioRow> _hiddenByLook = new List<GoreStudioRow>();

        /// <summary>Remembers the materials of the naked meshes, before a look changes them.</summary>
        public void RememberBody()
        {
            _bodyMaterials.Clear();
            foreach (var row in Rows)
            {
                if (row.Renderer != null && !_bodyMaterials.ContainsKey(row.Renderer))
                {
                    _bodyMaterials.Add(row.Renderer, (Material[])row.Renderer.sharedMaterials.Clone());
                }
            }
        }

        /// <summary>Hides the skin of the naked meshes a look replaces (in the preview only: their lids and cut lines stay, and <see cref="RestoreBody"/> shows them again).</summary>
        public void HideBodyMeshes(ICollection<Renderer> hidden)
        {
            foreach (var row in Rows)
            {
                if (!row.HiddenByLook && row.Renderer != null && hidden.Contains(row.Renderer))
                {
                    row.HiddenByLook = true;
                    _hiddenByLook.Add(row);
                }
            }

            Dirty = true;
        }

        /// <summary>Gives the naked meshes their materials back and shows the ones a look hid.</summary>
        public void RestoreBody()
        {
            foreach (var pair in _bodyMaterials)
            {
                if (pair.Key != null)
                {
                    pair.Key.sharedMaterials = pair.Value;
                }
            }

            _bodyMaterials.Clear();
            foreach (var row in _hiddenByLook)
            {
                row.HiddenByLook = false;
            }

            _hiddenByLook.Clear();
        }

        /// <summary>Takes every piece of clothing off again (the items are destroyed).</summary>
        public void Undress()
        {
            RestoreBody();
            foreach (var cloth in Cloth)
            {
                FreeCloth(cloth);
            }

            Cloth.Clear();
            SelectedCloth = -1;
            Dirty = true;
        }

        private static void FreeCloth(GoreStudioCloth cloth)
        {
            if (cloth.SegmentMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(cloth.SegmentMesh);
                cloth.SegmentMesh = null;
            }

            if (cloth.Root != null)
            {
                UnityEngine.Object.DestroyImmediate(cloth.Root);
                cloth.Root = null;
            }
        }

        /// <summary>The rule of a worn mesh: the one kept for it, or a new one (automatic).</summary>
        public GoreStudioClothRule RuleOf(Mesh mesh, string name)
        {
            if (!ClothRules.TryGetValue(mesh, out var rule))
            {
                rule = new GoreStudioClothRule { Mesh = mesh, Name = name, Mode = GoreClothMode.Whole };
                ClothRules.Add(mesh, rule);
            }

            return rule;
        }

        // The rules an earlier setup saved in the profile (the owner by the name of its cut).
        private void LoadClothRules()
        {
            ClothRules.Clear();
            var profile = Existing != null ? Existing.Profile : null;
            if (profile == null || profile.ClothRules == null)
            {
                return;
            }

            foreach (var saved in profile.ClothRules)
            {
                if (saved == null || saved.Mesh == null || ClothRules.ContainsKey(saved.Mesh))
                {
                    continue;
                }

                var mode = saved.Mode == GoreClothMode.Split || saved.Mode == GoreClothMode.Pieces ? saved.Mode : GoreClothMode.Whole;
                var owner = saved.OwnerAuto ? null : CutNameOf(profile, saved.Owner);
                ClothRules.Add(saved.Mesh, new GoreStudioClothRule
                {
                    Mesh = saved.Mesh,
                    Name = saved.Name,
                    Mode = mode,
                    Owner = owner,
                    Flies = saved.Flies,
                    BodyFlies = saved.BodyFlies,
                    PieceOwners = PieceNames(saved.PieceOwners, saved.PieceAuto, profile),
                    PieceFlies = saved.PieceFlies != null ? (bool[])saved.PieceFlies.Clone() : Array.Empty<bool>(),
                    PieceCut = saved.PieceCut != null ? (bool[])saved.PieceCut.Clone() : Array.Empty<bool>(),
                    PieceBodyFlies = saved.PieceBodyFlies != null ? (bool[])saved.PieceBodyFlies.Clone() : Array.Empty<bool>()
                });
            }
        }

        // The name of a saved owner: the cut's name, or empty for the body.
        private static string CutNameOf(GoreProfile profile, int owner) => owner >= 0 && owner < profile.CutCount && profile.Cuts[owner] != null ? profile.Cuts[owner].Name : string.Empty;

        // The owners of the pieces of a mesh as names ("" = the body, null = Auto).
        private static string[] PieceNames(int[] owners, bool[] auto, GoreProfile profile)
        {
            var result = new string[owners != null ? owners.Length : 0];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = auto != null && i < auto.Length && auto[i] ? null : CutNameOf(profile, owners[i]);
            }

            return result;
        }

        // The cloth that is on the character for a rule, or null.
        private GoreStudioCloth ClothOf(GoreStudioClothRule rule)
        {
            foreach (var candidate in Cloth)
            {
                if (candidate.Rule == rule)
                {
                    return candidate;
                }
            }

            return null;
        }

        // The owner of a piece that is not cut, with Auto worked out: the name of a cut, or empty for the body.
        public static string OwnerOf(GoreStudioClothRule rule, GoreStudioCloth cloth) => rule.Owner ?? (cloth != null ? cloth.AutoOwner : string.Empty);

        // The number of a cut by its name, -1 for the body (and for a cut that is gone).
        private int OwnerNumber(string name) => string.IsNullOrEmpty(name) ? -1 : CutNumber(name);

        /// <summary>The rules for the profile: every rule (a piece set up in the studio is always saved, so what the preview shows is what the game does), with its owners as numbers of <see cref="Cuts"/> (-1 = the body). Auto is worked out and written as the number it gave, with a mark so the studio shows Auto again.</summary>
        public GoreClothRule[] ClothRulesToSave()
        {
            var result = new List<GoreClothRule>(ClothRules.Count);
            foreach (var pair in ClothRules)
            {
                var rule = pair.Value;
                if (rule.Mesh == null)
                {
                    continue;
                }

                var cloth = ClothOf(rule);
                var saved = new GoreClothRule { Mesh = rule.Mesh, Name = rule.Name, Mode = rule.Mode, Owner = -1 };
                if (rule.Mode == GoreClothMode.Split)
                {
                    saved.Flies = rule.Flies;
                    saved.BodyFlies = rule.BodyFlies;
                }
                else if (rule.Mode == GoreClothMode.Pieces)
                {
                    var count = rule.PieceOwners.Length;
                    saved.PieceOwners = new int[count];
                    saved.PieceAuto = new bool[count];
                    saved.PieceFlies = (bool[])rule.PieceFlies.Clone();
                    saved.PieceCut = (bool[])rule.PieceCut.Clone();
                    saved.PieceBodyFlies = (bool[])rule.PieceBodyFlies.Clone();
                    for (var i = 0; i < count; i++)
                    {
                        saved.PieceAuto[i] = rule.PieceOwners[i] == null;
                        var name = rule.PieceOwners[i] ?? (cloth != null && i < cloth.PieceAuto.Length ? cloth.PieceAuto[i] : null);

                        // A piece Auto could not place (the mesh was never looked at) is left to its bone weights.
                        saved.PieceOwners[i] = name == null ? -2 : OwnerNumber(name);
                    }
                }
                else
                {
                    saved.OwnerAuto = rule.Owner == null;
                    saved.Owner = OwnerNumber(OwnerOf(rule, cloth));
                    saved.Flies = rule.Flies && saved.Owner >= 0;
                }

                result.Add(saved);
            }

            return result.ToArray();
        }

        /// <summary>The number of the cut called <paramref name="name"/>, or -1.</summary>
        public int CutNumber(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }

            for (var i = 0; i < Cuts.Count; i++)
            {
                if (string.Equals(Cuts[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The colour of a segment in the preview (the body is 0, cut n is n + 1).</summary>
        public Color32 SegmentColor(int segment) => segment >= 0 && segment < Palette.Length ? Palette[segment] : Palette[0];

        /// <summary>The name of a segment for the card: "the body" or the cut.</summary>
        public string SegmentName(int segment) => segment <= 0 || segment > Cuts.Count ? "the body" : Cuts[segment - 1].Name;

        #endregion

        #region Pose and classification

        // Reads the pose the preview shows (once) and makes the coloured copy: loose triangles with a fixed-light shade.
        private static void Pose(GoreStudioCloth cloth)
        {
            var renderer = cloth.Renderer;
            var baked = new Mesh();
            var wasEnabled = renderer.enabled;
            Vector3[] local;
            int[] triangles;
            try
            {
                renderer.enabled = true;
                renderer.BakeMesh(baked, true);
                local = baked.vertices;
                triangles = baked.triangles;
            }
            catch (Exception)
            {
                // A mesh the editor cannot bake: the piece is simply drawn as it is.
                UnityEngine.Object.DestroyImmediate(baked);
                return;
            }
            finally
            {
                renderer.enabled = wasEnabled;
            }

            UnityEngine.Object.DestroyImmediate(baked);
            if (local.Length < cloth.Vertices || triangles.Length < 3)
            {
                return;
            }

            var matrix = Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
            var world = new Vector3[local.Length];
            for (var v = 0; v < local.Length; v++)
            {
                world[v] = matrix.MultiplyPoint3x4(local[v]);
            }

            var normals = new Vector3[world.Length];
            for (var k = 0; k + 2 < triangles.Length; k += 3)
            {
                var a = triangles[k];
                var b = triangles[k + 1];
                var c = triangles[k + 2];
                var face = Vector3.Cross(world[b] - world[a], world[c] - world[a]);
                normals[a] += face;
                normals[b] += face;
                normals[c] += face;
            }

            var corners = triangles.Length / 3 * 3;
            var positions = new Vector3[corners];
            var indices = new int[corners];
            cloth.CornerVertex = new int[corners];
            cloth.SoupShade = new float[corners];
            cloth.SoupSegment = new int[corners];
            cloth.SoupColors = new Color32[corners];
            for (var i = 0; i < corners; i++)
            {
                var vertex = triangles[i];
                positions[i] = world[vertex];
                indices[i] = i;
                cloth.CornerVertex[i] = vertex;
                var normal = normals[vertex];
                var light = normal.sqrMagnitude > 1e-18f ? Mathf.Max(0f, Vector3.Dot(normal.normalized, ShadeLight)) : 0.5f;
                cloth.SoupShade[i] = 0.5f + 0.5f * light;
            }

            cloth.SoupWorld = positions;
            cloth.SoupPiece = new int[corners];
            var mesh = new Mesh { name = "Gore Studio cloth", hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = positions;
            mesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
            mesh.RecalculateBounds();
            cloth.SegmentMesh = mesh;

            // The strongest bone of every vertex, when the mesh may be read (the coloured copy is then painted by the cuts).
            cloth.StrongestBone = null;
            if (cloth.Readable)
            {
                try
                {
                    var weights = renderer.sharedMesh.boneWeights;
                    if (weights.Length >= cloth.Vertices)
                    {
                        cloth.StrongestBone = new int[weights.Length];
                        for (var v = 0; v < weights.Length; v++)
                        {
                            var w = weights[v];
                            var index = w.boneIndex0;
                            var best = w.weight0;
                            if (w.weight1 > best) { best = w.weight1; index = w.boneIndex1; }
                            if (w.weight2 > best) { best = w.weight2; index = w.boneIndex2; }
                            if (w.weight3 > best) { index = w.boneIndex3; }
                            cloth.StrongestBone[v] = index;
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    cloth.StrongestBone = null;
                }

                if (cloth.StrongestBone != null)
                {
                    var mesh2 = renderer.sharedMesh;
                    var triangleLists = new int[mesh2.subMeshCount][];
                    for (var s = 0; s < triangleLists.Length; s++)
                    {
                        triangleLists[s] = mesh2.GetSubMesh(s).topology == MeshTopology.Triangles ? mesh2.GetTriangles(s) : Array.Empty<int>();
                    }

                    cloth.VertexPiece = GoreMeshPipeline.Pieces(mesh2.vertices, triangleLists, out var pieceCount);
                    cloth.IslandCount = pieceCount;
                }
            }
        }

        // Works out what every piece does with the cuts as they are now and gives its triangles the colour of where they go.
        private void ClassifyCloth()
        {
            var lines = _tree != null ? LimbLines() : null;
            foreach (var cloth in Cloth)
            {
                cloth.Classified = false;
                cloth.Result = string.Empty;
                cloth.CanDivide = false;
                if (_tree == null || cloth.Renderer == null)
                {
                    continue;
                }

                var bones = cloth.Renderer.bones;
                var segmentOfBone = new int[bones.Length];
                var counts = new int[Cuts.Count + 1];
                for (var b = 0; b < bones.Length; b++)
                {
                    for (var t = bones[b]; t != null; t = t.parent)
                    {
                        if (_tree.CutOfBone.TryGetValue(t, out var cut))
                        {
                            segmentOfBone[b] = cut + 1;
                            break;
                        }
                    }

                    if (bones[b] != null)
                    {
                        counts[segmentOfBone[b]]++;
                    }
                }

                var majority = 0;
                for (var s = 1; s < counts.Length; s++)
                {
                    if (counts[s] > counts[majority])
                    {
                        majority = s;
                    }
                }

                cloth.Majority = majority;
                var rule = cloth.Rule;
                var canSplit = cloth.StrongestBone != null;
                PiecesOf(cloth, segmentOfBone, majority, lines);

                var fixedSegment = -1;
                var pieces = rule.Mode == GoreClothMode.Pieces && canSplit && cloth.PieceCount > 0;
                if (rule.Mode == GoreClothMode.Split && canSplit)
                {
                    cloth.Result = "Cut along the body parts. Body side: " + (rule.BodyFlies ? "flies off." : "follows the body.") + " Part side: " + (rule.Flies ? "flies off on its own." : "follows its part.");
                }
                else if (pieces)
                {
                    cloth.Result = "Divided: each piece is cut or goes whole with its own part, following it or flying off as chosen.";
                }
                else
                {
                    // Not cut (or it cannot be cut): the whole piece goes with one part.
                    var owner = OwnerOf(rule, cloth);
                    fixedSegment = CutNumber(owner) >= 0 ? CutNumber(owner) + 1 : 0;
                    var name = SegmentName(fixedSegment);
                    var prefix = rule.Mode == GoreClothMode.Split ? "Not readable, so it cannot be cut. " : "Not cut. ";
                    cloth.Result = prefix + (fixedSegment == 0
                        ? "Follows the body (stays)."
                        : rule.Flies ? "Flies off on its own when " + name + " is cut off." : "Follows " + name + ".");
                }

                for (var c = 0; c + 2 < cloth.SoupSegment.Length; c += 3)
                {
                    int segment;
                    string chosen = null;
                    if (pieces)
                    {
                        var piece = cloth.SoupPiece.Length > c ? cloth.SoupPiece[c] : -1;
                        var cutPiece = piece >= 0 && piece < rule.PieceCut.Length && rule.PieceCut[piece];
                        if (piece >= 0 && piece < cloth.PieceAuto.Length && !cutPiece)
                        {
                            chosen = (piece < rule.PieceOwners.Length ? rule.PieceOwners[piece] : null) ?? cloth.PieceAuto[piece];
                        }
                    }

                    if (fixedSegment >= 0)
                    {
                        segment = fixedSegment;
                    }
                    else if (chosen != null && (chosen.Length == 0 || CutNumber(chosen) >= 0))
                    {
                        segment = chosen.Length == 0 ? 0 : CutNumber(chosen) + 1;
                    }
                    else
                    {
                        // The part most of the triangle's corners follow.
                        var a = SegmentOfVertex(cloth, segmentOfBone, c);
                        var b = SegmentOfVertex(cloth, segmentOfBone, c + 1);
                        var d = SegmentOfVertex(cloth, segmentOfBone, c + 2);
                        segment = b == d ? b : a;
                    }

                    cloth.SoupSegment[c] = segment;
                    cloth.SoupSegment[c + 1] = segment;
                    cloth.SoupSegment[c + 2] = segment;
                }

                cloth.Classified = cloth.SegmentMesh != null;
            }
        }

        // What Auto makes of the whole mesh and of each of its pieces (always, so the card can offer to divide a mesh): the part its vertices hang on
        // (the bone with the strongest weight of every vertex); a piece that hangs on the body only goes to the limb it lies on, when it lies on one.
        private void PiecesOf(GoreStudioCloth cloth, int[] segmentOfBone, int bonesMajority, (Vector3 from, Vector3 to)[] lines)
        {
            cloth.PieceCount = 0;
            cloth.PieceVertices = Array.Empty<int>();
            cloth.PieceAuto = Array.Empty<string>();
            cloth.AutoOwner = string.Empty;
            if (cloth.SoupWorld.Length == 0)
            {
                return;
            }

            var segments = Cuts.Count + 1;
            var all = new int[segments];
            var allSum = Vector3.zero;
            var corners = cloth.CornerVertex.Length;
            var weighted = cloth.StrongestBone != null && cloth.VertexPiece != null;
            var count = weighted ? cloth.IslandCount : 0;
            var perPiece = new int[count][];
            var sums = new Vector3[count];
            cloth.PieceVertices = new int[count];
            for (var p = 0; p < count; p++)
            {
                perPiece[p] = new int[segments];
            }

            for (var c = 0; c < corners; c++)
            {
                allSum += cloth.SoupWorld[c];
                var segment = weighted ? SegmentOfVertex(cloth, segmentOfBone, c) : bonesMajority;
                all[segment]++;
                var piece = weighted ? cloth.VertexPiece[cloth.CornerVertex[c]] : -1;
                cloth.SoupPiece[c] = piece;
                if (piece >= 0)
                {
                    cloth.PieceVertices[piece]++;
                    perPiece[piece][segment]++;
                    sums[piece] += cloth.SoupWorld[c];
                }
            }

            cloth.AutoOwner = AutoName(all, allSum / Mathf.Max(1, corners), lines);
            cloth.PieceCount = count;
            cloth.PieceAuto = new string[count];
            var seen = 0;
            string first = null;
            var differ = false;
            for (var p = 0; p < count; p++)
            {
                if (cloth.PieceVertices[p] == 0)
                {
                    continue;
                }

                cloth.PieceAuto[p] = AutoName(perPiece[p], sums[p] / cloth.PieceVertices[p], lines);
                seen++;
                first = first ?? cloth.PieceAuto[p];
                differ |= cloth.PieceAuto[p] != first;
            }

            cloth.CanDivide = seen >= 2 && differ;
        }

        // Auto: the part most vertices hang on (a limb or the head); when they hang on the body, the limb the middle of the piece lies along if it is
        // close to it (within half the limb's length), else the body. Returns the name of the cut, or empty for the body.
        private string AutoName(int[] onSegment, Vector3 middle, (Vector3 from, Vector3 to)[] lines)
        {
            var best = 0;
            for (var s = 1; s < onSegment.Length; s++)
            {
                if (onSegment[s] > onSegment[best])
                {
                    best = s;
                }
            }

            if (best > 0)
            {
                return Cuts[best - 1].Name;
            }

            var nearest = -1;
            var distance = float.MaxValue;
            for (var cut = 0; cut < Cuts.Count && lines != null; cut++)
            {
                if (Cuts[cut].Bone == null)
                {
                    continue;
                }

                var d = LineDistance(lines[cut].from, lines[cut].to, middle);
                if (d < distance)
                {
                    distance = d;
                    nearest = cut;
                }
            }

            if (nearest < 0)
            {
                return string.Empty;
            }

            var length = (lines[nearest].to - lines[nearest].from).magnitude;
            return length > 0.05f && distance <= length * length * 0.25f ? Cuts[nearest].Name : string.Empty;
        }

        // For every cut the line along its limb: from its bone to the bone of the next cut down (or, at the end of a limb, to the farthest bone below it).
        private (Vector3 from, Vector3 to)[] LimbLines()
        {
            var lines = new (Vector3, Vector3)[Cuts.Count];
            var parent = SegmentParent;
            for (var cut = 0; cut < Cuts.Count; cut++)
            {
                var bone = Cuts[cut].Bone;
                if (bone == null)
                {
                    continue;
                }

                var start = bone.position;
                var end = start;
                var found = false;
                for (var next = 0; next < Cuts.Count && parent != null; next++)
                {
                    if (parent[next + 1] == cut + 1 && Cuts[next].Bone != null)
                    {
                        end = Cuts[next].Bone.position;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    var far = 0f;
                    foreach (var below in bone.GetComponentsInChildren<Transform>())
                    {
                        var distance = (below.position - start).sqrMagnitude;
                        if (distance > far)
                        {
                            far = distance;
                            end = below.position;
                        }
                    }
                }

                lines[cut] = (start, end);
            }

            return lines;
        }

        // The squared distance from a point to the line between two points (the line ends where its points do).
        private static float LineDistance(Vector3 from, Vector3 to, Vector3 point)
        {
            var along = to - from;
            var length = along.sqrMagnitude;
            var t = length > 1e-10f ? Mathf.Clamp01(Vector3.Dot(point - from, along) / length) : 0f;
            return (from + along * t - point).sqrMagnitude;
        }

        private static int SegmentOfVertex(GoreStudioCloth cloth, int[] segmentOfBone, int corner)
        {
            var bone = cloth.StrongestBone[cloth.CornerVertex[corner]];
            return bone >= 0 && bone < segmentOfBone.Length ? segmentOfBone[bone] : 0;
        }

        /// <summary>Works out the pieces again (a rule changed) and paints them.</summary>
        public void RefreshCloth()
        {
            ClassifyCloth();
            Recolor();
        }

        #endregion
    }
}
