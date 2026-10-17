using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GoreLab
{
    // Equipment: skinned meshes that are put on the character at run time (armour, hair, boots, a helmet) and that the setup in the editor
    // never saw. The first time the character is enabled (and whenever RefreshEquipment is called) every enabled skinned renderer that is not one of
    // the character's own is split along the same cuts, in the background, and given parts like the prepared meshes have. The result is kept for
    // the mesh, so the next character that wears the same piece costs nothing. A mesh needs Read/Write for this (Unity gives no other way to
    // read a mesh at run time).
    public sealed partial class GoreCharacter
    {
        private GoreRendererSet[] _view;
        private GoreMeshSet[] _viewData;
        private readonly List<GoreRendererSet> _extraSets = new List<GoreRendererSet>(8);
        private readonly List<GoreMeshSet> _extraData = new List<GoreMeshSet>(8);
        private readonly List<(SkinnedMeshRenderer renderer, GoreMeshSet data, Vector3[][] seams)> _equipment = new List<(SkinnedMeshRenderer, GoreMeshSet, Vector3[][])>(8);
        private readonly HashSet<SkinnedMeshRenderer> _known = new HashSet<SkinnedMeshRenderer>();
        private bool _started;
        private bool _preparing;
        private bool _prepareAgain;
        private int _retries;

        /// <summary>Whether the equipment is being split right now (the first cut before it is done leaves the equipment on the body).</summary>
        public bool IsPreparingEquipment => _preparing;

        /// <summary>How many worn pieces (skinned meshes the editor setup did not know) take part in a cut.</summary>
        public int EquipmentCount => _equipment.Count;

        // The renderer sets of the setup, then the ones made for the equipment.
        private GoreRendererSet[] View
        {
            get
            {
                if (_view == null)
                {
                    BuildView();
                }

                return _view;
            }
        }

        private GoreMeshSet[] ViewData
        {
            get
            {
                if (_viewData == null)
                {
                    BuildView();
                }

                return _viewData;
            }
        }

        private void BuildView()
        {
            var baked = _sets != null ? _sets.Length : 0;
            _view = new GoreRendererSet[baked + _extraSets.Count];
            _viewData = new GoreMeshSet[baked + _extraSets.Count];
            for (var i = 0; i < baked; i++)
            {
                _view[i] = _sets[i];
                _viewData[i] = _profile != null && i < _profile.SetCount ? _profile.MeshSets[i] : null;
            }

            for (var i = 0; i < _extraSets.Count; i++)
            {
                _view[baked + i] = _extraSets[i];
                _viewData[baked + i] = _extraData[i];
            }
        }

        private void Start()
        {
            _started = true;
            RefreshEquipment();
        }

        private void OnEnable()
        {
            // A pooled character that is dressed again: look at what it wears now.
            if (_started)
            {
                RefreshEquipment();
            }
        }

        /// <summary>
        /// Looks at what the character wears and makes every new piece cuttable (in the background; the pieces that were already seen are kept).
        /// Called by the character itself when it starts and when it is enabled again; call it after dressing the character at any other time.
        /// A piece that was put on after the first cut has to wait for <see cref="ResetCharacter"/>.
        /// </summary>
        public void RefreshEquipment()
        {
            if (!IsReady)
            {
                return;
            }

            _retries = 0;
            if (_preparing)
            {
                _prepareAgain = true;
                return;
            }

            PrepareEquipmentAsync().Forget();
        }

        private async UniTaskVoid PrepareEquipmentAsync()
        {
            _preparing = true;
            var token = this.GetCancellationTokenOnDestroy();
            try
            {
                // A piece of equipment ties its bones to the character's skeleton in its own first frame.
                var wait = Mathf.Max(1, _profile.EquipmentFrames);
                await UniTask.DelayFrame(wait, PlayerLoopTiming.Update, token);
                do
                {
                    _prepareAgain = false;
                    if (_cutMode)
                    {
                        break;
                    }

                    // What was taken off (a pooled character dressed again) goes first, so the list never grows without end.
                    if (Prune())
                    {
                        _view = null;
                        _viewData = null;
                    }

                    if (_profile.CutEquipment)
                    {
                        await PrepareCandidatesAsync(token);
                    }

                    if (_prepareAgain && !token.IsCancellationRequested)
                    {
                        await UniTask.DelayFrame(wait, PlayerLoopTiming.Update, token);
                    }
                }
                while (_prepareAgain && !token.IsCancellationRequested);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _preparing = false;
            }
        }

        private async UniTask PrepareCandidatesAsync(CancellationToken token)
        {
            var candidates = FindEquipment();
            for (var i = 0; i < candidates.Count && !_cutMode; i++)
            {
                var renderer = candidates[i];
                if (renderer == null)
                {
                    continue;
                }

                // What the user decided for this piece in the studio comes first: a piece that stays on the body, or goes whole with a cut chosen by hand
                // (following the part or flying off), needs no Read/Write at all.
                var rule = _profile.RuleFor(renderer.sharedMesh);
                if (rule != null && rule.Mode == GoreClothMode.Whole)
                {
                    // No owner = the body: the piece stays.
                    var owner = rule.Owner >= 0 && rule.Owner < _profile.CutCount ? GoreProfile.SegmentOf(rule.Owner) : 0;
                    AddWhole(renderer, owner, rule.Flies && owner > 0);
                    continue;
                }

                var pieces = rule != null && rule.Mode == GoreClothMode.Pieces;
                var forced = rule != null && (rule.Mode == GoreClothMode.Split || pieces);

                // A worn mesh that cannot be read cannot be looked into: it goes whole with the part its bones mostly belong to (hair and a helmet with the
                // head, a cape on the body) and needs no Read/Write. Only a piece that has to be split (a glove, a sleeve) needs it.
                if (!renderer.sharedMesh.isReadable)
                {
                    if (forced)
                    {
                        Debug.LogWarning("[GoreLab] The worn mesh '" + renderer.sharedMesh.name + "' is set to be split but is not readable (switch Read/Write on in its model's import settings): it goes whole with the part it mostly belongs to.", renderer.sharedMesh);
                    }

                    AddWhole(renderer, MostlySegment(renderer), forced && !pieces && rule.Flies);
                    continue;
                }

                if (!pieces)
                {
                    await PrepareVariantAsync(renderer, forced, null, forced && rule.Flies, rule != null && rule.Mode == GoreClothMode.Split && rule.BodyFlies, token);
                    continue;
                }

                // Pieces: the pieces that share what they do when cut (follow, fly, body side flies) are made as one set each; a piece is left out of
                // the sets it is not in.
                var groups = PieceGroups(rule);
                for (var g = 0; g < groups.Count; g++)
                {
                    if (!await PrepareVariantAsync(renderer, true, groups[g].segments, groups[g].fly, groups[g].bodyFly, token))
                    {
                        break;
                    }
                }
            }
        }

        // Makes the parts of one worn mesh (in the background) and adds them as a set. False when the piece was not added and the caller should stop.
        private async UniTask<bool> PrepareVariantAsync(SkinnedMeshRenderer renderer, bool forced, int[] pieceSegment, bool fly, bool bodyFly, CancellationToken token)
        {
            var segments = BoneSegments(renderer.bones);

            // The shared task has no owner of its own that could cancel it for the others; this character only stops waiting for it.
            var prepared = await GoreEquipmentCache.Get(renderer.sharedMesh, _profile, segments, SegmentParents(), CutHints(renderer, forced), pieceSegment).AttachExternalCancellation(token);
            if (renderer == null || _cutMode)
            {
                return false;
            }

            if (prepared == null)
            {
                // It cannot be cut (not readable, no weights): said once by the cache, and not looked at again.
                _known.Add(renderer);
                return false;
            }

            // The bones may have been tied to the skeleton while the mesh was being split: then the mapping was made too early.
            if (!SameSegments(segments, BoneSegments(renderer.bones)))
            {
                if (_retries++ < MaxRetries)
                {
                    _prepareAgain = true;
                }

                return false;
            }

            return AddEquipment(renderer, prepared, fly, bodyFly);
        }

        private const int MaxRetries = 3;

        private static bool SameSegments(int[] a, int[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        // Forgets the pieces that were destroyed (unequipped, or the character was dressed again) and removes their parts.
        private bool Prune()
        {
            var changed = false;
            for (var i = _equipment.Count - 1; i >= 0; i--)
            {
                if (_equipment[i].renderer != null)
                {
                    continue;
                }

                DestroyParts(_extraSets[i]);
                _extraSets.RemoveAt(i);
                _extraData.RemoveAt(i);
                _equipment.RemoveAt(i);
                changed = true;
            }

            _known.RemoveWhere(IsDestroyed);
            return changed;
        }

        private static bool IsDestroyed(SkinnedMeshRenderer renderer) => renderer == null;

        private static void DestroyParts(GoreRendererSet set)
        {
            for (var i = 0; i < set.Segments.Length; i++)
            {
                if (set.Segments[i] != null)
                {
                    Destroy(set.Segments[i].gameObject);
                }
            }
        }

        // Whether any skin is left to take off for this cut: a part that exists and is, or will be, shown. A cut with none must not change
        // the state of the character (squeeze a bone, hide a stump) and come back with nothing.
        private bool HasSkin(int cut)
        {
            var below = _profile.Cuts[cut].Below;
            var view = View;
            for (var s = 0; s < view.Length; s++)
            {
                var set = view[s];
                if (set == null)
                {
                    continue;
                }

                if (ShowsSegment(set, GoreProfile.SegmentOf(cut)))
                {
                    return true;
                }

                for (var i = 0; i < below.Length; i++)
                {
                    if (ShowsSegment(set, GoreProfile.SegmentOf(below[i])))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool ShowsSegment(GoreRendererSet set, int segment)
        {
            var part = segment < set.Segments.Length ? set.Segments[segment] : null;
            if (part == null)
            {
                return false;
            }

            if (_cutMode)
            {
                return part.enabled;
            }

            var source = set.Source;
            return source != null && source.enabled && source.gameObject.activeInHierarchy;
        }

        // Enabled skinned renderers under the character that are neither its own sources and parts nor already prepared.
        private List<SkinnedMeshRenderer> FindEquipment()
        {
            if (_known.Count == 0)
            {
                for (var i = 0; i < _sets.Length; i++)
                {
                    Know(_sets[i]);
                }
            }

            var result = new List<SkinnedMeshRenderer>(8);
            var all = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var i = 0; i < all.Length; i++)
            {
                var renderer = all[i];
                if (renderer.sharedMesh != null && renderer.enabled && renderer.gameObject.activeInHierarchy && renderer.bones.Length > 0
                    && !_known.Contains(renderer) && renderer.GetComponentInParent<GoreIgnore>() == null)
                {
                    result.Add(renderer);
                }
            }

            return result;
        }

        private void Know(GoreRendererSet set)
        {
            if (set == null)
            {
                return;
            }

            if (set.Source != null)
            {
                _known.Add(set.Source);
            }

            AddAll(set.Segments);
            AddAll(set.Stumps);
            AddAll(set.LimbCaps);
        }

        private void AddAll(SkinnedMeshRenderer[] renderers)
        {
            if (renderers == null)
            {
                return;
            }

            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    _known.Add(renderers[i]);
                }
            }
        }

        // The segment of every bone of a renderer: the first cut at or above it, else the body.
        private int[] BoneSegments(Transform[] bones)
        {
            var cutOfBone = new Dictionary<Transform, int>(_bones.Length);
            for (var i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] != null)
                {
                    cutOfBone[_bones[i]] = i;
                }
            }

            var result = new int[bones.Length];
            for (var b = 0; b < bones.Length; b++)
            {
                for (var t = bones[b]; t != null; t = t.parent)
                {
                    if (cutOfBone.TryGetValue(t, out var cut))
                    {
                        result[b] = cut + 1;
                        break;
                    }
                }
            }

            return result;
        }

        // For each cut: where its bone is in this renderer's bones, and where and how wide the cut of the naked meshes is (from the profile).
        private GoreCutHint[] CutHints(SkinnedMeshRenderer renderer, bool force)
        {
            var bones = renderer.bones;
            var sections = _profile.Sections;
            var hints = new GoreCutHint[_bones.Length];
            for (var cut = 0; cut < hints.Length; cut++)
            {
                var index = _bones[cut] != null ? Array.IndexOf(bones, _bones[cut]) : -1;
                var section = sections != null && cut < sections.Length ? sections[cut] : null;
                var keepWhole = _profile.Cuts[cut].WornWhole;
                hints[cut] = section != null && section.Valid
                    ? new GoreCutHint(index, section.Center, section.Radius, true, keepWhole, force)
                    : new GoreCutHint(index, Vector3.zero, 0f, false, keepWhole, force);
            }

            return hints;
        }

        // The sets a Pieces rule makes. Each piece is cut (by its bone weights: -1), or goes whole with the body (0) or with a cut's segment. The pieces
        // that stay with their part share a set (one that follows, one that follows with a flying body side); every piece that flies is a set of its
        // own, because a set flies off as one body: that is how each piece flies on its own. In the array of a set the other pieces are -3.
        private List<(int[] segments, bool fly, bool bodyFly)> PieceGroups(GoreClothRule rule)
        {
            var owners = rule.PieceOwners ?? Array.Empty<int>();
            var segment = new int[owners.Length];
            var fly = new bool[owners.Length];
            var body = new bool[owners.Length];
            var staying = new bool[2];
            for (var i = 0; i < owners.Length; i++)
            {
                var cut = rule.PieceCut != null && i < rule.PieceCut.Length && rule.PieceCut[i];
                segment[i] = cut ? -1 : owners[i] == -1 ? 0 : owners[i] >= 0 && owners[i] < _profile.CutCount ? GoreProfile.SegmentOf(owners[i]) : -1;

                // Only a piece that goes with a part, or is cut, can fly: one that stays on the body stays.
                fly[i] = (cut || segment[i] > 0) && rule.PieceFlies != null && i < rule.PieceFlies.Length && rule.PieceFlies[i];
                body[i] = cut && rule.PieceBodyFlies != null && i < rule.PieceBodyFlies.Length && rule.PieceBodyFlies[i];
                if (!fly[i])
                {
                    staying[body[i] ? 1 : 0] = true;
                }
            }

            var result = new List<(int[], bool, bool)>(8);
            for (var g = 0; g < 2; g++)
            {
                if (!staying[g])
                {
                    continue;
                }

                var array = new int[owners.Length];
                for (var i = 0; i < array.Length; i++)
                {
                    array[i] = !fly[i] && body[i] == (g == 1) ? segment[i] : -3;
                }

                result.Add((array, false, g == 1));
            }

            for (var i = 0; i < owners.Length; i++)
            {
                if (!fly[i])
                {
                    continue;
                }

                var array = new int[owners.Length];
                for (var k = 0; k < array.Length; k++)
                {
                    array[k] = k == i ? segment[k] : -3;
                }

                result.Add((array, true, body[i]));
            }

            return result;
        }

        // The segment each segment is cut from (-1 for the body).
        private int[] SegmentParents()
        {
            var cuts = _profile.CutCount;
            var result = new int[cuts + 1];
            result[0] = -1;
            for (var i = 0; i < cuts; i++)
            {
                result[i + 1] = _profile.Cuts[i].Parent >= 0 ? _profile.Cuts[i].Parent + 1 : 0;
            }

            return result;
        }

        // Gives a prepared piece its part renderers (switched off, next to the piece) and puts it in the character's list of meshes.
        private bool AddEquipment(SkinnedMeshRenderer source, GorePrepared prepared, bool fly, bool bodyFly)
        {
            var cuts = _profile.CutCount;
            if (_cutMode || prepared.Segments.Length != cuts + 1)
            {
                return false;
            }

            var container = PartsContainer(source);
            var materials = source.sharedMaterials;
            var set = new GoreRendererSet
            {
                Source = source,
                Segments = new SkinnedMeshRenderer[cuts + 1],
                Stumps = new SkinnedMeshRenderer[cuts],
                LimbCaps = new SkinnedMeshRenderer[cuts]
            };

            var data = new GoreMeshSet
            {
                Name = source.name,
                SourceMesh = source.sharedMesh,
                BindPoses = prepared.BindPoses,
                Fly = fly,
                BodyFly = bodyFly,
                Lids = true,
                Segments = new Mesh[cuts + 1],
                Materials = new SegmentMaterials[cuts + 1],
                Stumps = new Mesh[cuts],
                LimbCaps = new Mesh[cuts]
            };

            for (var segment = 0; segment <= cuts; segment++)
            {
                var mesh = prepared.Segments[segment];
                if (mesh == null)
                {
                    continue;
                }

                var pick = prepared.SubmeshSource[segment];
                var subset = PartMaterials(materials, pick, source.sharedMesh.subMeshCount);

                var label = segment == 0 ? "Body" : _profile.Cuts[segment - 1].Name;
                data.Segments[segment] = mesh;
                data.Materials[segment] = new SegmentMaterials { Source = pick };
                set.Segments[segment] = NewPart(source, container, source.name + " " + label, mesh, subset);
            }

            _extraSets.Add(set);
            _extraData.Add(data);
            _equipment.Add((source, data, prepared.SeamPoints));
            Know(set);

            // The view (baked sets, then these) is made again next time it is asked for; this only happens before the first cut.
            _view = null;
            _viewData = null;
            return true;
        }

        private const string EquipmentParts = "GoreParts (equipment)";

        // Next to the piece, where its part renderers are kept.
        private static Transform PartsContainer(SkinnedMeshRenderer source)
        {
            var parent = source.transform.parent != null ? source.transform.parent : source.transform;
            var container = parent.Find(EquipmentParts);
            if (container == null)
            {
                var go = new GameObject(EquipmentParts);
                go.layer = source.gameObject.layer;
                go.transform.SetParent(parent, false);
                container = go.transform;
            }

            return container;
        }

        // The segment most of a renderer's bones belong to (the body when it is a tie).
        private int MostlySegment(SkinnedMeshRenderer renderer)
        {
            var segments = BoneSegments(renderer.bones);
            var counts = new int[_profile.CutCount + 1];
            for (var i = 0; i < segments.Length; i++)
            {
                if (renderer.bones[i] != null)
                {
                    counts[segments[i]]++;
                }
            }

            var best = 0;
            for (var s = 1; s < counts.Length; s++)
            {
                if (counts[s] > counts[best])
                {
                    best = s;
                }
            }

            return best;
        }

        // A worn piece that is not split: one renderer of the same mesh stands for it in the part given (by the user, or the one its bones mostly belong to). Nothing of the mesh is
        // copied or read; the renderer is shown in place of the piece after the first cut and, when its part comes off, carried on a frozen copy of
        // its bones.
        private void AddWhole(SkinnedMeshRenderer source, int segment, bool fly)
        {
            if (_cutMode)
            {
                return;
            }

            var cuts = _profile.CutCount;
            var mesh = source.sharedMesh;
            var materials = source.sharedMaterials;
            var set = new GoreRendererSet
            {
                Source = source,
                Segments = new SkinnedMeshRenderer[cuts + 1],
                Stumps = new SkinnedMeshRenderer[cuts],
                LimbCaps = new SkinnedMeshRenderer[cuts]
            };

            var identity = new int[materials.Length];
            for (var i = 0; i < identity.Length; i++)
            {
                identity[i] = i;
            }

            var data = new GoreMeshSet
            {
                Name = source.name,
                SourceMesh = mesh,
                BindPoses = BindPosesOf(mesh),
                Whole = true,
                Fly = fly,
                Lids = false,
                Segments = new Mesh[cuts + 1],
                Materials = new SegmentMaterials[cuts + 1],
                Stumps = new Mesh[cuts],
                LimbCaps = new Mesh[cuts]
            };

            data.Segments[segment] = mesh;
            data.Materials[segment] = new SegmentMaterials { Source = identity };
            set.Segments[segment] = NewPart(source, PartsContainer(source), source.name + " whole", mesh, materials);

            _extraSets.Add(set);
            _extraData.Add(data);
            _equipment.Add((source, data, null));
            Know(set);
            _view = null;
            _viewData = null;
        }

        // The bind poses of a mesh that may not be readable (they are small and usually available; without them the piece is simply not corrected
        // when a cut hides the bones it follows).
        private static Matrix4x4[] BindPosesOf(Mesh mesh)
        {
            try
            {
                return mesh.bindposes;
            }
            catch (Exception)
            {
                return Array.Empty<Matrix4x4>();
            }
        }

        // A switched-off renderer that follows the same bones as the piece it was cut from.
        private static SkinnedMeshRenderer NewPart(SkinnedMeshRenderer source, Transform container, string name, Mesh mesh, Material[] materials)
        {
            var go = new GameObject(name);
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
            renderer.shadowCastingMode = source.shadowCastingMode;
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
    }

    /// <summary>What was made from one worn mesh: the parts of every segment and the points of its cut lines.</summary>
    internal sealed class GorePrepared
    {
        public Mesh[] Segments;
        public int[][] SubmeshSource;
        public Vector3[][] SeamPoints;
        public Matrix4x4[] BindPoses;
    }

    /// <summary>The parts and lids made at run time for worn meshes, kept by mesh so the next character that wears them costs nothing.</summary>
    internal static class GoreEquipmentCache
    {
        private readonly struct Key : IEquatable<Key>
        {
            private readonly int _mesh;
            private readonly int _profile;
            private readonly int _bones;

            public Key(int mesh, int profile, int bones)
            {
                _mesh = mesh;
                _profile = profile;
                _bones = bones;
            }

            public bool Equals(Key other) => _mesh == other._mesh && _profile == other._profile && _bones == other._bones;

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() => (_mesh * 397 ^ _profile) * 397 ^ _bones;
        }

        private static readonly Dictionary<Key, UniTask<GorePrepared>> Entries = new Dictionary<Key, UniTask<GorePrepared>>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
            Entries.Clear();
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        // The meshes are let go of (not destroyed: a character that lives on may still show them); unused ones are freed by Unity.
        private static void OnSceneUnloaded(Scene scene)
        {
            Entries.Clear();
        }

        /// <summary>The prepared parts of a worn mesh for this profile, made once (null when the mesh cannot be cut).</summary>
        internal static UniTask<GorePrepared> Get(Mesh mesh, GoreProfile profile, int[] boneSegment, int[] segmentParent, GoreCutHint[] hints, int[] pieceSegment)
        {
            var hash = 17;
            for (var i = 0; i < boneSegment.Length; i++)
            {
                hash = hash * 31 + boneSegment[i];
            }

            if (pieceSegment != null)
            {
                hash = hash * 31 + 7;
                for (var i = 0; i < pieceSegment.Length; i++)
                {
                    hash = hash * 31 + pieceSegment[i];
                }
            }

            var key = new Key(mesh.GetInstanceID(), profile.GetInstanceID(), hash);
            if (Entries.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var task = Prepare(mesh, boneSegment, segmentParent, hints, pieceSegment).Preserve();
            Entries[key] = task;
            return task;
        }

        private static async UniTask<GorePrepared> Prepare(Mesh mesh, int[] boneSegment, int[] segmentParent, GoreCutHint[] hints, int[] pieceSegment)
        {
            try
            {
                if (!mesh.isReadable)
                {
                    return null;
                }

                var data = GoreRuntimeMeshes.Read(mesh);

                // The splitting is plain arrays: it runs on a worker thread and the frame never waits for it.
                var processed = await UniTask.RunOnThreadPool(() => GoreMeshPipeline.Process(data, boneSegment, segmentParent, hints, pieceSegment));
                if (processed == null)
                {
                    Debug.LogWarning("[GoreLab] The worn mesh '" + mesh.name + "' has no bone weights, so it cannot be cut and stays on the body.", mesh);
                    return null;
                }

                var prepared = new GorePrepared
                {
                    Segments = new Mesh[processed.Parts.Length],
                    SubmeshSource = new int[processed.Parts.Length][],
                    SeamPoints = processed.SeamPoints,
                    BindPoses = data.BindPoses
                };

                for (var i = 0; i < processed.Parts.Length; i++)
                {
                    var part = processed.Parts[i];
                    if (part == null)
                    {
                        continue;
                    }

                    prepared.Segments[i] = GoreRuntimeMeshes.Build(part, data, mesh.name + " part " + i);
                    prepared.SubmeshSource[i] = part.SubmeshSource;
                }

                return prepared;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return null;
            }
        }
    }
}
