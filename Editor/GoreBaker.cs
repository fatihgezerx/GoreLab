using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GoreLab
{
    /// <summary>
    /// The setup of a <see cref="GoreCharacter"/>, done once in the editor: it splits the skinned meshes of a character into the parts that
    /// can come off (with <see cref="GoreSplitter"/>), saves them as mesh assets next to a <see cref="GoreProfile"/>, and builds the switched-off
    /// renderers of the parts and lids under the character. Nothing of this runs in a build; the meshes and the renderers are data.
    /// </summary>
    internal static class GoreBaker
    {
        /// <summary>Where new profiles and their meshes go unless the setup says otherwise.</summary>
        internal const string DefaultFolder = "Assets/Scripts/GoreLab/Data";

        private const string PartsName = "GoreParts";
        private const string CapMaterialPath = "Assets/Scripts/GoreLab/Content/Materials/Gore Cap.mat";

        /// <summary>One cut that was asked for: its name, the humanoid part it is (or None) and the bone it is made at.</summary>
        internal sealed class CutSpec
        {
            public string Name = "Cut";
            public GoreLimb Limb = GoreLimb.None;
            public Transform Bone;

            /// <summary>The material of this cut's lid and stump; null = the default.</summary>
            public Material Fill;
        }

        /// <summary>One skinned mesh to set up and whether its cuts get lids (the skin yes; clothes usually not).</summary>
        internal sealed class SourceSpec
        {
            public SkinnedMeshRenderer Renderer;
            public bool Lids = true;

            /// <summary>Vertices assigned to a cut, or kept out of it, by hand (null = none).</summary>
            public GoreVertexOverride[] Overrides;
        }

        /// <summary>What to set up.</summary>
        internal sealed class Request
        {
            public GameObject Character;
            public List<SourceSpec> Sources = new List<SourceSpec>();
            public List<CutSpec> Cuts = new List<CutSpec>();

            /// <summary>An existing profile to fill again (its mesh assets are updated in place), or null for a new one.</summary>
            public GoreProfile Profile;

            /// <summary>The folder of a new profile and its meshes; empty = <see cref="DefaultFolder"/>.</summary>
            public string Folder;

            /// <summary>The material of the lids; null = a red default made on the way.</summary>
            public Material CapMaterial;

            /// <summary>What each worn mesh does when a cut happens (from the studio's Cloth parts card); null = leave the profile's rules as they are.</summary>
            public GoreClothRule[] ClothRules;
        }

        /// <summary>What the setup did.</summary>
        internal sealed class Result
        {
            public bool Success;
            public string Message = string.Empty;
            public readonly List<string> Warnings = new List<string>();
            public GoreProfile Profile;
            public GoreCharacter Character;
        }

        /// <summary>The 13 cuts of a humanoid, from its avatar. Empty when the animator is not humanoid.</summary>
        internal static List<CutSpec> HumanoidCuts(Animator animator)
        {
            var result = new List<CutSpec>();
            if (animator == null || !animator.isHuman)
            {
                return result;
            }

            AddHumanoid(result, animator, GoreLimb.Head, HumanBodyBones.Head);
            AddHumanoid(result, animator, GoreLimb.LeftUpperArm, HumanBodyBones.LeftUpperArm);
            AddHumanoid(result, animator, GoreLimb.LeftLowerArm, HumanBodyBones.LeftLowerArm);
            AddHumanoid(result, animator, GoreLimb.LeftHand, HumanBodyBones.LeftHand);
            AddHumanoid(result, animator, GoreLimb.RightUpperArm, HumanBodyBones.RightUpperArm);
            AddHumanoid(result, animator, GoreLimb.RightLowerArm, HumanBodyBones.RightLowerArm);
            AddHumanoid(result, animator, GoreLimb.RightHand, HumanBodyBones.RightHand);
            AddHumanoid(result, animator, GoreLimb.LeftUpperLeg, HumanBodyBones.LeftUpperLeg);
            AddHumanoid(result, animator, GoreLimb.LeftLowerLeg, HumanBodyBones.LeftLowerLeg);
            AddHumanoid(result, animator, GoreLimb.LeftFoot, HumanBodyBones.LeftFoot);
            AddHumanoid(result, animator, GoreLimb.RightUpperLeg, HumanBodyBones.RightUpperLeg);
            AddHumanoid(result, animator, GoreLimb.RightLowerLeg, HumanBodyBones.RightLowerLeg);
            AddHumanoid(result, animator, GoreLimb.RightFoot, HumanBodyBones.RightFoot);
            return result;
        }

        private static void AddHumanoid(List<CutSpec> list, Animator animator, GoreLimb limb, HumanBodyBones bone)
        {
            var transform = animator.GetBoneTransform(bone);
            if (transform != null)
            {
                list.Add(new CutSpec { Name = limb.ToString(), Limb = limb, Bone = transform });
            }
        }

        /// <summary>The skinned mesh renderers of a character that can be set up (the ones this setup made are left out).</summary>
        internal static List<SkinnedMeshRenderer> FindSources(GameObject character)
        {
            var result = new List<SkinnedMeshRenderer>();
            if (character == null)
            {
                return result;
            }

            foreach (var renderer in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh != null && !IsGenerated(renderer.transform))
                {
                    result.Add(renderer);
                }
            }

            return result;
        }

        // A name that can be part of a file name: the characters Windows and Unity refuse become underscores.
        internal static string Safe(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == '/' || chars[i] == '\\')
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private static bool IsGenerated(Transform transform) => transform.parent != null && transform.parent.name == PartsName;

        /// <summary>
        /// Sets the character up. Never throws: what went wrong is in <see cref="Result.Message"/>, what is only doubtful in
        /// <see cref="Result.Warnings"/>. An earlier setup of the same character is replaced; a profile that was given is filled again and its
        /// mesh assets are updated in place, so every character that uses it keeps its references.
        /// </summary>
        internal static Result Bake(Request request)
        {
            var result = new Result();
            try
            {
                return Run(request, result);
            }
            catch (InvalidOperationException exception)
            {
                // Unity refused to hand over a mesh's data (a mesh that is not readable and that the editor will not read either).
                result.Success = false;
                result.Message = "Unity refused to give the editor the data of a mesh: tick Read/Write in the Model tab of the Import Settings of the character's model, press Apply, set up again and untick it afterwards (the parts are saved as assets; the original does not need it at run time). Unity said: " + exception.Message;
                Debug.LogException(exception);
                return result;
            }
            catch (Exception exception)
            {
                result.Success = false;
                result.Message = "The setup failed: " + exception.Message;
                Debug.LogException(exception);
                return result;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static Result Run(Request request, Result result)
        {
            if (!Validate(request, result))
            {
                return result;
            }

            var character = request.Character;
            var cuts = request.Cuts;
            var cutCount = cuts.Count;

            var tree = BuildTree(cuts);
            var parent = tree.Parent;
            var below = tree.Below;

            // An earlier setup goes first.
            var existing = character.GetComponent<GoreCharacter>();
            if (existing != null)
            {
                Clear(existing);
            }

            GoreBloodAssets.Mend();
            var profile = request.Profile;
            var folder = string.IsNullOrEmpty(request.Folder) ? DefaultFolder : request.Folder;
            if (profile == null)
            {
                EnsureFolder(folder);
                profile = ScriptableObject.CreateInstance<GoreProfile>();
                var profilePath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Safe(character.name)} Gore Profile.asset");
                AssetDatabase.CreateAsset(profile, profilePath);

                // A new profile bleeds with the default blood (clear the field of the profile for none).
                profile.BloodPrefab = GoreBloodAssets.DefaultBlood();
            }

            var profilePathNow = AssetDatabase.GetAssetPath(profile);
            var meshFolder = Path.GetDirectoryName(profilePathNow).Replace('\\', '/') + "/" + profile.name + " Meshes";
            EnsureFolder(meshFolder);

            var capMaterial = request.CapMaterial != null ? request.CapMaterial : DefaultCapMaterial();
            var oldSets = profile.MeshSets ?? Array.Empty<GoreMeshSet>();

            // Cuts of the profile.
            var profileCuts = new GoreCut[cutCount];
            for (var i = 0; i < cutCount; i++)
            {
                profileCuts[i] = new GoreCut
                {
                    Name = cuts[i].Name,
                    Limb = cuts[i].Limb,
                    BonePath = AnimationUtility.CalculateTransformPath(cuts[i].Bone, character.transform),
                    Parent = parent[i],
                    Below = below[i].ToArray(),
                    WornWhole = cuts[i].Limb == GoreLimb.Head,
                    FillMaterial = cuts[i].Fill
                };
            }

            var sets = new GoreMeshSet[request.Sources.Count];
            var bakedSources = new List<(SourceSpec spec, GoreSplitResult split, MeshArrays arrays)>();

            // The meshes of an earlier setup are deleted and made again. Rewriting a mesh Unity has already loaded (CopySerialized) leaves its
            // drawing data at the old size: every renderer that uses it stops with "does not match the expected mesh data size and vertex stride".
            foreach (var oldSet in oldSets)
            {
                DeleteAll(oldSet);
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                for (var s = 0; s < request.Sources.Count; s++)
                {
                    var spec = request.Sources[s];
                    var renderer = spec.Renderer;
                    EditorUtility.DisplayProgressBar("GoreLab", "Splitting " + renderer.name, (float)s / request.Sources.Count);

                    var arrays = ReadMesh(renderer.sharedMesh);
                    var input = BuildInput(renderer, arrays, spec.Lids, tree, spec.Overrides);
                    var split = GoreSplitter.Split(input);

                    // From here on every index points into the refined vertices: the source ones, then the ones made on the cut lines.
                    arrays = Refine(arrays, split);
                    foreach (var warning in split.Warnings)
                    {
                        // A mesh that does not cover a segment is normal (boots have nothing on an arm); that is judged over all meshes below.
                        if (!warning.StartsWith("Segment ", StringComparison.Ordinal))
                        {
                            result.Warnings.Add($"{renderer.name}: {warning}");
                        }
                    }

                    WarnAboutDroppedData(renderer, result);

                    var set = new GoreMeshSet
                    {
                        Name = renderer.name,
                        SourceMesh = renderer.sharedMesh,
                        BindPoses = arrays.BindPoses,
                        Overrides = spec.Overrides ?? Array.Empty<GoreVertexOverride>(),
                        Lids = spec.Lids,
                        Segments = new Mesh[cutCount + 1],
                        Materials = new SegmentMaterials[cutCount + 1],
                        Stumps = new Mesh[cutCount],
                        LimbCaps = new Mesh[cutCount]
                    };

                    var prefix = $"{meshFolder}/{Safe(renderer.name)}";
                    for (var segment = 0; segment <= cutCount; segment++)
                    {
                        var part = split.Segments[segment];
                        var label = segment == 0 ? "Body" : cuts[segment - 1].Name;
                        set.Materials[segment] = new SegmentMaterials { Source = part.SubmeshSource };
                        if (part.IsEmpty)
                        {
                            continue;
                        }

                        var mesh = BuildPart(arrays, part, $"{renderer.name} {label}");
                        set.Segments[segment] = SaveMesh(mesh, $"{prefix} Part {segment} {Safe(label)}.asset");
                    }

                    // The lids that follow this mesh's own cut line (what the studio shows): a fan round the closed seam, one on the part and one on the
                    // body. A mesh whose line does not close gets none.
                    if (spec.Lids)
                    {
                        for (var cut = 0; cut < cutCount; cut++)
                        {
                            var cap = split.Caps[cut];
                            if (cap == null)
                            {
                                continue;
                            }

                            set.LimbCaps[cut] = SaveMesh(BuildCap(arrays, cap, false, $"{renderer.name} {cuts[cut].Name} lid", null), $"{prefix} Lid {cut} {Safe(cuts[cut].Name)}.asset");
                            set.Stumps[cut] = SaveMesh(BuildCap(arrays, cap, true, $"{renderer.name} {cuts[cut].Name} stump", BoneRedirect(renderer, cuts[cut].Bone)), $"{prefix} Stump {cut} {Safe(cuts[cut].Name)}.asset");
                        }
                    }

                    sets[s] = set;
                    bakedSources.Add((spec, split, arrays));
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // A cut that no mesh has any skin for comes off with nothing: that is worth a word.
            for (var cut = 0; cut < cutCount; cut++)
            {
                var covered = false;
                foreach (var set in sets)
                {
                    covered |= set.Segments[cut + 1] != null;
                }

                if (!covered)
                {
                    result.Warnings.Add($"No mesh has any skin that follows the bone of '{cuts[cut].Name}': it would come off with nothing. Check the bone, or remove the cut.");
                }
            }

            profile.Cuts = profileCuts;
            profile.MeshSets = sets;
            profile.Sections = BuildSections(cuts, bakedSources);
            if (request.ClothRules != null)
            {
                profile.ClothRules = request.ClothRules;
            }

            EditorUtility.SetDirty(profile);

            // The renderers of the parts, switched off, in a container next to the originals.
            var component = existing != null ? existing : Undo.AddComponent<GoreCharacter>(character);
            var rendererSets = new GoreRendererSet[sets.Length];
            for (var s = 0; s < sets.Length; s++)
            {
                rendererSets[s] = BuildRenderers(request.Sources[s].Renderer, sets[s], cuts, capMaterial);
            }

            var bones = new Transform[cutCount];
            for (var i = 0; i < cutCount; i++)
            {
                bones[i] = cuts[i].Bone;
            }

            var so = new SerializedObject(component);
            so.FindProperty("_profile").objectReferenceValue = profile;
            WriteBones(so.FindProperty("_bones"), bones);
            WriteSets(so.FindProperty("_sets"), rendererSets);
            so.ApplyModifiedPropertiesWithoutUndo();
            MarkDirty(component);

            AssetDatabase.SaveAssets();
            result.Success = true;
            result.Profile = profile;
            result.Character = component;
            result.Message = $"Set up '{character.name}': {cutCount} cuts, {sets.Length} skinned mesh{(sets.Length == 1 ? string.Empty : "es")}. Profile: {profilePathNow}";
            return result;
        }

        /// <summary>
        /// Takes an earlier setup off a character: the renderers of the parts and lids are deleted and the component forgets them (the profile
        /// and its mesh assets stay).
        /// </summary>
        internal static void Clear(GoreCharacter character)
        {
            if (character == null)
            {
                return;
            }

            // Every container of parts under the character (a character whose meshes sit under different parents has several).
            var containers = new List<GameObject>();
            foreach (var child in character.GetComponentsInChildren<Transform>(true))
            {
                if (child != null && child.name == PartsName && child != character.transform)
                {
                    containers.Add(child.gameObject);
                }
            }

            foreach (var container in containers)
            {
                if (container != null)
                {
                    Undo.DestroyObjectImmediate(container);
                }
            }

            var so = new SerializedObject(character);
            so.FindProperty("_sets").arraySize = 0;
            so.FindProperty("_bones").arraySize = 0;
            so.ApplyModifiedPropertiesWithoutUndo();
            MarkDirty(character);
        }

        private static bool Validate(Request request, Result result)
        {
            if (request == null || request.Character == null)
            {
                result.Message = "Choose the character: the object at the top of its hierarchy.";
                return false;
            }

            if (EditorUtility.IsPersistent(request.Character) && PrefabStageUtility.GetCurrentPrefabStage() == null)
            {
                result.Message = "Put the character in the scene (or open its prefab) first: the parts are built under it.";
                return false;
            }

            if (request.Sources == null || request.Sources.Count == 0)
            {
                result.Message = "The character has no skinned mesh to set up.";
                return false;
            }

            // Unity lets a script read any mesh outside Play mode; Read/Write is only about the player.
            if (Application.isPlaying)
            {
                result.Message = "Set up in edit mode, not in Play mode: Unity lets a script read the meshes only outside Play mode.";
                return false;
            }

            var seen = new HashSet<SkinnedMeshRenderer>();
            foreach (var source in request.Sources)
            {
                var renderer = source.Renderer;
                if (renderer == null || renderer.sharedMesh == null)
                {
                    result.Message = "A skinned mesh renderer has no mesh.";
                    return false;
                }

                if (!seen.Add(renderer) || !renderer.transform.IsChildOf(request.Character.transform))
                {
                    result.Message = $"'{renderer.name}' is listed twice or is not part of the character.";
                    return false;
                }

                var mesh = renderer.sharedMesh;
                if (mesh.vertexCount == 0 || mesh.boneWeights.Length != mesh.vertexCount || renderer.bones.Length == 0)
                {
                    result.Message = $"'{renderer.name}' has no skin weights or no bones: it is not a skinned mesh that can be cut.";
                    return false;
                }

                if (mesh.bindposes.Length != renderer.bones.Length)
                {
                    result.Message = $"'{renderer.name}': the number of bind poses and of bones differ; the mesh does not match its renderer.";
                    return false;
                }
            }

            if (request.Cuts == null || request.Cuts.Count == 0)
            {
                result.Message = "There is no cut. Add the bones the character can be cut at (for a humanoid: Humanoid cuts).";
                return false;
            }

            var names = new HashSet<string>();
            var bones = new HashSet<Transform>();
            foreach (var cut in request.Cuts)
            {
                if (cut.Bone == null || string.IsNullOrWhiteSpace(cut.Name))
                {
                    result.Message = "Every cut needs a name and a bone.";
                    return false;
                }

                if (!cut.Bone.IsChildOf(request.Character.transform) || cut.Bone == request.Character.transform)
                {
                    result.Message = $"The bone of '{cut.Name}' is not below the character.";
                    return false;
                }

                if (!names.Add(cut.Name) || !bones.Add(cut.Bone))
                {
                    result.Message = $"'{cut.Name}' repeats a name or a bone of another cut.";
                    return false;
                }
            }

            // A cut whose bone (and everything below it) no mesh follows would be an empty part.
            foreach (var cut in request.Cuts)
            {
                var used = false;
                foreach (var source in request.Sources)
                {
                    foreach (var bone in source.Renderer.bones)
                    {
                        if (bone != null && (bone == cut.Bone || bone.IsChildOf(cut.Bone)))
                        {
                            used = true;
                            break;
                        }
                    }

                    if (used)
                    {
                        break;
                    }
                }

                if (!used)
                {
                    result.Warnings.Add($"No skinned mesh follows the bone of '{cut.Name}' or any bone below it, so that cut comes off with nothing.");
                }
            }

            return true;
        }

        /// <summary>How the cuts nest: for each cut its parent (the deepest cut above it, or -1), the cuts below it, and the same as a segment tree.</summary>
        internal sealed class CutTree
        {
            /// <summary>Per cut, the deepest cut above it, or -1.</summary>
            public int[] Parent;

            /// <summary>Per cut, every cut below it.</summary>
            public List<int>[] Below;

            /// <summary>Per segment (the body is 0, cut n is n + 1), the segment it is cut from; -1 for the body.</summary>
            public int[] SegmentParent;

            /// <summary>The cut a bone is the bone of.</summary>
            public Dictionary<Transform, int> CutOfBone;
        }

        /// <summary>Works out how the cuts nest from their bones. Every cut needs a bone.</summary>
        internal static CutTree BuildTree(List<CutSpec> cuts)
        {
            var cutCount = cuts.Count;
            var tree = new CutTree
            {
                Parent = new int[cutCount],
                Below = new List<int>[cutCount],
                SegmentParent = new int[cutCount + 1],
                CutOfBone = new Dictionary<Transform, int>()
            };

            for (var i = 0; i < cutCount; i++)
            {
                tree.Below[i] = new List<int>();
            }

            for (var i = 0; i < cutCount; i++)
            {
                var deepest = -1;
                for (var j = 0; j < cutCount; j++)
                {
                    if (j != i && cuts[i].Bone.IsChildOf(cuts[j].Bone) && (deepest < 0 || cuts[j].Bone.IsChildOf(cuts[deepest].Bone)))
                    {
                        deepest = j;
                    }
                }

                tree.Parent[i] = deepest;
                for (var j = 0; j < cutCount; j++)
                {
                    if (j != i && cuts[j].Bone.IsChildOf(cuts[i].Bone))
                    {
                        tree.Below[i].Add(j);
                    }
                }
            }

            tree.SegmentParent[0] = -1;
            for (var i = 0; i < cutCount; i++)
            {
                tree.SegmentParent[i + 1] = tree.Parent[i] >= 0 ? tree.Parent[i] + 1 : 0;
                tree.CutOfBone[cuts[i].Bone] = i;
            }

            return tree;
        }

        // The splitter's input for one renderer: its arrays and which segment each of its bones belongs to.
        private static GoreSplitInput BuildInput(SkinnedMeshRenderer renderer, MeshArrays arrays, bool lids, CutTree tree, GoreVertexOverride[] overrides)
        {
            var bones = renderer.bones;
            var boneSegment = new int[bones.Length];
            for (var b = 0; b < boneSegment.Length; b++)
            {
                boneSegment[b] = SegmentOfBone(bones[b], tree.CutOfBone);
            }

            return new GoreSplitInput
            {
                Positions = arrays.Vertices,
                Weights = arrays.Weights,
                Submeshes = arrays.Triangles,
                BoneSegment = boneSegment,
                SegmentParent = tree.SegmentParent,
                Lids = lids,
                Overrides = overrides
            };
        }

        /// <summary>
        /// Splits one renderer's mesh for the cuts of <paramref name="tree"/> without making any asset: what the studio shows before anything is
        /// set up. Edit mode only; the mesh must be one <see cref="Validate"/> would accept.
        /// </summary>
        internal static GoreSplitResult Analyze(SkinnedMeshRenderer renderer, bool lids, CutTree tree, GoreVertexOverride[] overrides)
        {
            var arrays = ReadMesh(renderer.sharedMesh);
            return GoreSplitter.Split(BuildInput(renderer, arrays, lids, tree, overrides));
        }

        // The segment a bone belongs to: the first cut at or above it, else the body.
        private static int SegmentOfBone(Transform bone, Dictionary<Transform, int> cutOfBone)
        {
            for (var t = bone; t != null; t = t.parent)
            {
                if (cutOfBone.TryGetValue(t, out var cut))
                {
                    return cut + 1;
                }
            }

            return 0;
        }

        #region Mesh data

        // Everything of a source mesh the parts copy from.
        private sealed class MeshArrays
        {
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector4[] Tangents;
            public Color32[] Colors;
            public readonly List<Vector4>[] Uvs = new List<Vector4>[4];
            public readonly int[] UvDimension = new int[4];
            public BoneWeight[] Weights;
            public Matrix4x4[] BindPoses;
            public Bounds Bounds;
            public int[][] Triangles;
        }

        private static MeshArrays ReadMesh(Mesh mesh)
        {
            var arrays = new MeshArrays
            {
                Vertices = mesh.vertices,
                Normals = mesh.normals,
                Tangents = mesh.tangents.Length == mesh.vertexCount ? mesh.tangents : Array.Empty<Vector4>(),
                Colors = mesh.colors32,
                Weights = mesh.boneWeights,
                BindPoses = mesh.bindposes,
                Bounds = mesh.bounds,
                Triangles = new int[mesh.subMeshCount][]
            };

            for (var channel = 0; channel < 4; channel++)
            {
                var attribute = VertexAttribute.TexCoord0 + channel;
                if (mesh.HasVertexAttribute(attribute))
                {
                    arrays.UvDimension[channel] = mesh.GetVertexAttributeDimension(attribute);
                    arrays.Uvs[channel] = new List<Vector4>(mesh.vertexCount);
                    mesh.GetUVs(channel, arrays.Uvs[channel]);
                }
            }

            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                arrays.Triangles[s] = mesh.GetSubMesh(s).topology == MeshTopology.Triangles ? mesh.GetTriangles(s) : Array.Empty<int>();
            }

            return arrays;
        }

        // The mesh data of the refined vertex list: the source vertices as they were, then every vertex the splitter made on a cut line, blended
        // from the two vertices it lies between (position and weights come from the splitter, which needed them for the cuts that follow).
        private static MeshArrays Refine(MeshArrays source, GoreSplitResult split)
        {
            var refs = split.Vertices;
            var count = refs.Length;
            var original = source.Vertices.Length;
            var result = new MeshArrays
            {
                Vertices = split.Positions,
                Weights = split.Weights,
                BindPoses = source.BindPoses,
                Bounds = source.Bounds,
                Triangles = source.Triangles,
                Normals = Array.Empty<Vector3>(),
                Tangents = Array.Empty<Vector4>(),
                Colors = Array.Empty<Color32>()
            };

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
                var uvs = source.Uvs[channel];
                result.UvDimension[channel] = source.UvDimension[channel];
                if (uvs == null)
                {
                    continue;
                }

                var refined = new List<Vector4>(count);
                refined.AddRange(uvs);
                for (var i = original; i < count; i++)
                {
                    refined.Add(Vector4.Lerp(refined[refs[i].A], refined[refs[i].B], refs[i].T));
                }

                result.Uvs[channel] = refined;
            }

            return result;
        }

        // The mesh of one part: the vertices the splitter picked, with all their attributes, and the triangles per submesh.
        private static Mesh BuildPart(MeshArrays source, GoreSegmentPart part, string name)
        {
            var map = part.VertexMap;
            var mesh = new Mesh { name = name, indexFormat = map.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = Pick(source.Vertices, map);
            if (source.Normals.Length == source.Vertices.Length)
            {
                mesh.normals = Pick(source.Normals, map);
            }

            if (source.Tangents.Length == source.Vertices.Length)
            {
                mesh.tangents = Pick(source.Tangents, map);
            }

            if (source.Colors.Length == source.Vertices.Length)
            {
                mesh.colors32 = Pick(source.Colors, map);
            }

            for (var channel = 0; channel < 4; channel++)
            {
                if (source.Uvs[channel] != null)
                {
                    SetUvs(mesh, channel, source.Uvs[channel], source.UvDimension[channel], map);
                }
            }

            mesh.boneWeights = Pick(source.Weights, map);
            mesh.bindposes = source.BindPoses;
            mesh.subMeshCount = part.SubmeshTriangles.Length;
            for (var s = 0; s < part.SubmeshTriangles.Length; s++)
            {
                mesh.SetTriangles(part.SubmeshTriangles[s], s, false);
            }

            mesh.bounds = source.Bounds;
            return mesh;
        }

        private static void SetUvs(Mesh mesh, int channel, List<Vector4> uvs, int dimension, int[] map)
        {
            if (dimension <= 2)
            {
                var list = new List<Vector2>(map.Length);
                foreach (var index in map)
                {
                    list.Add(uvs[index]);
                }

                mesh.SetUVs(channel, list);
            }
            else if (dimension == 3)
            {
                var list = new List<Vector3>(map.Length);
                foreach (var index in map)
                {
                    list.Add(uvs[index]);
                }

                mesh.SetUVs(channel, list);
            }
            else
            {
                var list = new List<Vector4>(map.Length);
                foreach (var index in map)
                {
                    list.Add(uvs[index]);
                }

                mesh.SetUVs(channel, list);
            }
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

        // A lid: positions and bone weights follow the skin it closes, the normal points out of the part (the stump: the other way), and the
        // flat mapping lets a wound texture sit on it.
        private static Mesh BuildCap(MeshArrays source, GoreCapPart cap, bool stump, string name, int[] redirect)
        {
            var count = cap.Vertices.Length;
            var vertices = new Vector3[count];
            var normals = new Vector3[count];
            var uvs = new Vector2[count];
            var weights = new BoneWeight[count];
            for (var i = 0; i < count; i++)
            {
                var vertex = cap.Vertices[i];
                vertices[i] = vertex.Source >= 0 ? source.Vertices[vertex.Source] : vertex.Position;
                weights[i] = vertex.Source >= 0 ? source.Weights[vertex.Source] : vertex.Weights;
                if (redirect != null)
                {
                    weights[i] = Redirect(weights[i], redirect);
                }

                weights[i] = Sanitize(weights[i], source.BindPoses.Length);

                normals[i] = stump ? -vertex.Normal : vertex.Normal;
                uvs[i] = vertex.Uv;
            }

            var mesh = new Mesh { name = name, indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.boneWeights = weights;
            mesh.bindposes = source.BindPoses;
            mesh.subMeshCount = 1;
            mesh.SetTriangles(stump ? cap.StumpTriangles : cap.LimbTriangles, 0, false);
            mesh.bounds = source.Bounds;
            return mesh;
        }

        // For the stump: the bone each bone of the renderer is replaced with. The bones of the part that comes off (the cut's bone and everything
        // below it) become the nearest bone above the cut that the renderer has, so the stump stays on the body and does not follow the hidden limb.
        private static int[] BoneRedirect(SkinnedMeshRenderer renderer, Transform cutBone)
        {
            var bones = renderer.bones;
            var target = -1;
            for (var t = cutBone.parent; t != null && target < 0; t = t.parent)
            {
                target = Array.IndexOf(bones, t);
            }

            var result = new int[bones.Length];
            for (var b = 0; b < bones.Length; b++)
            {
                var inPart = bones[b] != null && (bones[b] == cutBone || bones[b].IsChildOf(cutBone));
                result[b] = inPart && target >= 0 ? target : b;
            }

            return result;
        }

        // Bone weights with their bones replaced; weights that land on the same bone are added up.
        private static BoneWeight Redirect(BoneWeight weights, int[] redirect)
        {
            var bones = new int[4];
            var amounts = new float[4];
            var count = 0;
            for (var k = 0; k < 4; k++)
            {
                var bone = k == 0 ? weights.boneIndex0 : k == 1 ? weights.boneIndex1 : k == 2 ? weights.boneIndex2 : weights.boneIndex3;
                var amount = k == 0 ? weights.weight0 : k == 1 ? weights.weight1 : k == 2 ? weights.weight2 : weights.weight3;
                if (amount <= 0f)
                {
                    continue;
                }

                var target = bone >= 0 && bone < redirect.Length ? redirect[bone] : bone;
                var slot = -1;
                for (var i = 0; i < count; i++)
                {
                    if (bones[i] == target)
                    {
                        slot = i;
                        break;
                    }
                }

                if (slot < 0)
                {
                    slot = count++;
                    bones[slot] = target;
                }

                amounts[slot] += amount;
            }

            var total = amounts[0] + amounts[1] + amounts[2] + amounts[3];
            if (total <= 0f)
            {
                return weights;
            }

            return new BoneWeight
            {
                boneIndex0 = bones[0], weight0 = amounts[0] / total,
                boneIndex1 = bones[1], weight1 = amounts[1] / total,
                boneIndex2 = bones[2], weight2 = amounts[2] / total,
                boneIndex3 = bones[3], weight3 = amounts[3] / total
            };
        }

        // Bone indices inside the renderer's bone list and weights that add up to one; a vertex with nothing valid follows bone 0.
        private static BoneWeight Sanitize(BoneWeight w, int boneCount)
        {
            if (boneCount <= 0)
            {
                return w;
            }

            if (w.boneIndex0 < 0 || w.boneIndex0 >= boneCount) { w.boneIndex0 = 0; w.weight0 = 0f; }
            if (w.boneIndex1 < 0 || w.boneIndex1 >= boneCount) { w.boneIndex1 = 0; w.weight1 = 0f; }
            if (w.boneIndex2 < 0 || w.boneIndex2 >= boneCount) { w.boneIndex2 = 0; w.weight2 = 0f; }
            if (w.boneIndex3 < 0 || w.boneIndex3 >= boneCount) { w.boneIndex3 = 0; w.weight3 = 0f; }
            var total = w.weight0 + w.weight1 + w.weight2 + w.weight3;
            if (float.IsNaN(total) || total <= 1e-6f)
            {
                return new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            }

            w.weight0 /= total;
            w.weight1 /= total;
            w.weight2 /= total;
            w.weight3 /= total;
            return w;
        }

        // What a part cannot keep of its source, said in words.
        private static void WarnAboutDroppedData(SkinnedMeshRenderer renderer, Result result)
        {
            var mesh = renderer.sharedMesh;
            if (mesh.blendShapeCount > 0)
            {
                result.Warnings.Add($"{renderer.name}: its blend shapes are not kept on the parts (a face stops moving after the first cut).");
            }

            for (var channel = 4; channel < 8; channel++)
            {
                if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + channel))
                {
                    result.Warnings.Add($"{renderer.name}: its UV channels from {channel} up are not kept on the parts.");
                    break;
                }
            }

            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetSubMesh(s).topology != MeshTopology.Triangles)
                {
                    result.Warnings.Add($"{renderer.name}: submesh {s} is not made of triangles and is left out of the parts.");
                }
            }

            var perVertex = mesh.GetBonesPerVertex();
            for (var v = 0; v < perVertex.Length; v++)
            {
                if (perVertex[v] > 4)
                {
                    result.Warnings.Add($"{renderer.name}: some vertices follow more than four bones; the parts keep the four strongest, so they may deform a little differently from the original.");
                    break;
                }
            }
        }

        // Saves a mesh as a new asset.
        private static Mesh SaveMesh(Mesh fresh, string path)
        {
            AssetDatabase.CreateAsset(fresh, AssetDatabase.GenerateUniqueAssetPath(path));
            return fresh;
        }

        // Per cut, where the cut line of the character's own meshes (the ones with Lids on) crosses the limb and how wide it is, in the space of the
        // cut's bone (from the bind poses, so the pose does not matter). A worn piece is cut by a cut only where it goes round the limb at about this
        // place and size: that is what tells a glove or a sleeve from a strand of hair or a cape.
        private static GoreCutSection[] BuildSections(IReadOnlyList<CutSpec> cuts, List<(SourceSpec spec, GoreSplitResult split, MeshArrays arrays)> baked)
        {
            var sections = new GoreCutSection[cuts.Count];
            var points = new List<Vector3>();
            for (var cut = 0; cut < cuts.Count; cut++)
            {
                sections[cut] = new GoreCutSection();
                var bone = cuts[cut].Bone;
                if (bone == null)
                {
                    continue;
                }

                points.Clear();
                foreach (var (spec, split, arrays) in baked)
                {
                    if (!spec.Lids || split.SeamPoints == null || cut >= split.SeamPoints.Length || split.SeamPoints[cut] == null)
                    {
                        continue;
                    }

                    var index = Array.IndexOf(spec.Renderer.bones, bone);
                    if (index < 0)
                    {
                        continue;
                    }

                    foreach (var point in split.SeamPoints[cut])
                    {
                        points.Add(arrays.BindPoses[index].MultiplyPoint3x4(point));
                    }
                }

                if (GoreCutSectionMeasure.TryMeasure(points, out var center, out var radius))
                {
                    sections[cut].Center = center;
                    sections[cut].Radius = radius;
                    sections[cut].Valid = true;
                }
            }

            return sections;
        }

        // Deletes the mesh assets of a set of an earlier setup.
        private static void DeleteAll(GoreMeshSet old)
        {
            if (old == null)
            {
                return;
            }

            DeleteMeshes(old.Segments);
            DeleteMeshes(old.Stumps);
            DeleteMeshes(old.LimbCaps);
        }

        private static void DeleteMeshes(Mesh[] meshes)
        {
            if (meshes == null)
            {
                return;
            }

            foreach (var mesh in meshes)
            {
                if (mesh != null && AssetDatabase.Contains(mesh))
                {
                    AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(mesh));
                }
            }
        }

        #endregion

        #region Renderers

        // The switched-off renderers of every part and lid of one source, next to it, with its bones, materials and render settings.
        private static GoreRendererSet BuildRenderers(SkinnedMeshRenderer source, GoreMeshSet set, List<CutSpec> cuts, Material capMaterial)
        {
            // Next to the source; a source on the root of the character gets its container below it, so it is always inside the character.
            var parent = source.transform.parent != null ? source.transform.parent : source.transform;
            var container = parent.Find(PartsName);
            if (container == null)
            {
                var go = new GameObject(PartsName);
                Undo.RegisterCreatedObjectUndo(go, "Gore parts");
                go.layer = source.gameObject.layer;
                go.transform.SetParent(parent, false);
                container = go.transform;
            }

            var materials = source.sharedMaterials;
            var cutCount = cuts.Count;
            var result = new GoreRendererSet
            {
                Source = source,
                Segments = new SkinnedMeshRenderer[cutCount + 1],
                Stumps = new SkinnedMeshRenderer[cutCount],
                LimbCaps = new SkinnedMeshRenderer[cutCount]
            };

            for (var segment = 0; segment <= cutCount; segment++)
            {
                if (set.Segments[segment] == null)
                {
                    continue;
                }

                var subset = new Material[set.Materials[segment].Source.Length];
                for (var i = 0; i < subset.Length; i++)
                {
                    var index = set.Materials[segment].Source[i];
                    subset[i] = index >= 0 && index < materials.Length ? materials[index] : null;
                }

                var label = segment == 0 ? "Body" : cuts[segment - 1].Name;
                result.Segments[segment] = NewRenderer(source, container, $"{source.name} {label}", set.Segments[segment], subset, true);
            }

            for (var cut = 0; cut < cutCount; cut++)
            {
                if (set.Stumps[cut] != null)
                {
                    result.Stumps[cut] = NewRenderer(source, container, $"{source.name} {cuts[cut].Name} stump", set.Stumps[cut], new[] { cuts[cut].Fill != null ? cuts[cut].Fill : capMaterial }, false);
                }

                if (set.LimbCaps[cut] != null)
                {
                    result.LimbCaps[cut] = NewRenderer(source, container, $"{source.name} {cuts[cut].Name} lid", set.LimbCaps[cut], new[] { cuts[cut].Fill != null ? cuts[cut].Fill : capMaterial }, false);
                }
            }

            return result;
        }

        private static SkinnedMeshRenderer NewRenderer(SkinnedMeshRenderer source, Transform container, string name, Mesh mesh, Material[] materials, bool castShadows)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Gore part");
            go.layer = source.gameObject.layer;
            var t = go.transform;
            t.SetParent(container, false);
            t.localPosition = source.transform.localPosition;
            t.localRotation = source.transform.localRotation;
            t.localScale = source.transform.localScale;

            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = source.bones;
            renderer.rootBone = source.rootBone;
            renderer.sharedMaterials = materials;
            renderer.localBounds = source.localBounds;
            renderer.updateWhenOffscreen = source.updateWhenOffscreen;
            renderer.quality = source.quality;
            renderer.shadowCastingMode = castShadows ? source.shadowCastingMode : ShadowCastingMode.Off;
            renderer.receiveShadows = source.receiveShadows;
            renderer.lightProbeUsage = source.lightProbeUsage;
            renderer.reflectionProbeUsage = source.reflectionProbeUsage;
            renderer.motionVectorGenerationMode = source.motionVectorGenerationMode;
            renderer.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
            renderer.renderingLayerMask = source.renderingLayerMask;
            renderer.probeAnchor = source.probeAnchor;
            renderer.skinnedMotionVectors = source.skinnedMotionVectors;
            renderer.forceMatrixRecalculationPerRender = source.forceMatrixRecalculationPerRender;
            renderer.staticShadowCaster = source.staticShadowCaster;
            renderer.enabled = false;
            return renderer;
        }

        private static void WriteBones(SerializedProperty array, Transform[] bones)
        {
            array.arraySize = bones.Length;
            for (var i = 0; i < bones.Length; i++)
            {
                array.GetArrayElementAtIndex(i).objectReferenceValue = bones[i];
            }
        }

        private static void WriteSets(SerializedProperty array, GoreRendererSet[] sets)
        {
            array.arraySize = sets.Length;
            for (var s = 0; s < sets.Length; s++)
            {
                var element = array.GetArrayElementAtIndex(s);
                element.FindPropertyRelative(nameof(GoreRendererSet.Source)).objectReferenceValue = sets[s].Source;
                WriteRenderers(element.FindPropertyRelative(nameof(GoreRendererSet.Segments)), sets[s].Segments);
                WriteRenderers(element.FindPropertyRelative(nameof(GoreRendererSet.Stumps)), sets[s].Stumps);
                WriteRenderers(element.FindPropertyRelative(nameof(GoreRendererSet.LimbCaps)), sets[s].LimbCaps);
            }
        }

        private static void WriteRenderers(SerializedProperty array, SkinnedMeshRenderer[] renderers)
        {
            array.arraySize = renderers.Length;
            for (var i = 0; i < renderers.Length; i++)
            {
                array.GetArrayElementAtIndex(i).objectReferenceValue = renderers[i];
            }
        }

        #endregion

        #region Assets and scene

        // The red material of the lids, made once.
        internal static Material DefaultCapMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(CapMaterialPath);
            if (material != null)
            {
                return material;
            }

            EnsureFolder(Path.GetDirectoryName(CapMaterialPath).Replace('\\', '/'));
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            material = new Material(shader) { name = "Gore Cap" };
            var color = new Color(0.42f, 0.03f, 0.04f, 1f);
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }

            // Raw flesh is matt: a smooth flat lid would catch the sun in one blinding spot.
            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", 0.15f);
            }

            if (material.HasProperty("_SpecularHighlights"))
            {
                material.SetFloat("_SpecularHighlights", 0f);
                material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            }

            if (material.HasProperty("_EnvironmentReflections"))
            {
                material.SetFloat("_EnvironmentReflections", 0f);
                material.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            }

            AssetDatabase.CreateAsset(material, CapMaterialPath);
            return material;
        }

        internal static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        internal static void MarkDirty(Component component)
        {
            EditorUtility.SetDirty(component);
            if (PrefabUtility.IsPartOfPrefabInstance(component))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }

            // A prefab that is being edited without a stage (the studio) lives in a preview scene, which cannot be marked dirty.
            var scene = component.gameObject.scene;
            if (scene.IsValid() && !EditorSceneManager.IsPreviewScene(scene))
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        #endregion
    }
}
