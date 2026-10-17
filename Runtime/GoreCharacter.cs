using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace GoreLab
{
    /// <summary>The renderers made from one skinned mesh of a <see cref="GoreCharacter"/>: the original, the part for every segment, the lids of every cut.</summary>
    [Serializable]
    public sealed class GoreRendererSet
    {
        [Tooltip("The character's own renderer: drawn until the first cut.")]
        public SkinnedMeshRenderer Source;

        [Tooltip("Per segment (the body first, then one for each cut), the renderer of its part. Null when the segment has no skin in this mesh.")]
        public SkinnedMeshRenderer[] Segments = Array.Empty<SkinnedMeshRenderer>();

        [Tooltip("Per cut, the stump on the body. Null when there is none.")]
        public SkinnedMeshRenderer[] Stumps = Array.Empty<SkinnedMeshRenderer>();

        [Tooltip("Per cut, the lid on the part that comes off. Null when there is none.")]
        public SkinnedMeshRenderer[] LimbCaps = Array.Empty<SkinnedMeshRenderer>();
    }

    /// <summary>
    /// A character that can lose parts of its body. Set up once in the editor (Tools &gt; Gore Studio): its skinned
    /// meshes are split into a part for every cut and the cuts get lids (a stump on the body, a lid on the part that comes off). Until the
    /// first cut the character is drawn by its own renderers, so it costs nothing extra. Then <see cref="Sever(GoreLimb, Vector3)"/> takes a
    /// part off: its skin is hidden on the body, the stump shows, and the part, a snapshot of its pose, flies off on a body of its own.
    /// </summary>
    /// <remarks>
    /// The character keeps animating whatever it was playing; a cut does not touch the Animator or any controller. The bone of a cut is
    /// squeezed to almost nothing from then on (until <see cref="ResetCharacter"/>) so whatever hangs on that bone (a hit collider, a weapon in
    /// the hand) goes with it, and the skin that stays on the body is made to follow the bone above the cut instead (helper transforms
    /// stand in for the bones of the part), so the hidden part does not drag the skin around the stump with its animation. Everything is
    /// event driven: no Update, and the only timer is the part's own removal (see <see cref="GoreDetached"/>).
    /// <see cref="ResetCharacter"/> brings every part back, for a pooled character that is used again.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed partial class GoreCharacter : MonoBehaviour
    {
        // The share of the box of a part its collider fills, so a part rests on its flesh, not on the corners of its box.
        private const float ColliderShare = 0.85f;
        private const float MinMass = 0.3f;
        private const float MaxMass = 40f;

        // The scale a cut's bone is squeezed to: not zero, so the matrices stay invertible and normals do not turn into NaN.
        private const float CollapsedScale = 0.001f;

        // How a worn piece that flies on its own is thrown, compared with the part: its share of the force and how far it strays from the direction.
        private const float FlyForceMin = 0.5f;
        private const float FlyForceMax = 1.1f;
        private const float FlyScatter = 0.35f;

        [SerializeField] private GoreProfile _profile;

        // Written by the setup: the renderers made from each skinned mesh, and the bone of every cut. Nothing to set by hand.
        [SerializeField] [HideInInspector] private GoreRendererSet[] _sets = Array.Empty<GoreRendererSet>();
        [SerializeField] [HideInInspector] private Transform[] _bones = Array.Empty<Transform>();

        private bool[] _gone;
        private bool[][] _stumpShown;
        private bool[] _sourceWasOn;
        private Vector3[] _boneScale;
        private bool[] _boneSqueezed;
        private Transform[][] _originalBones;
        private Transform[][] _currentBones;
        private readonly List<Transform> _helpers = new List<Transform>(8);
        private readonly List<Matrix4x4> _helperPoses = new List<Matrix4x4>(8);
        private bool _cutMode;
        private bool _warnedBindPose;
        private bool _warnedNoBindPoses;
        private Collider[] _ownColliders;
        private readonly List<GoreDetached> _detached = new List<GoreDetached>(4);
        private GoreBloodEmitter[] _stumpBlood = Array.Empty<GoreBloodEmitter>();

        /// <summary>Raised after a part came off: the cut (a number of the profile) and the part, which is already flying.</summary>
        public event Action<int, GameObject> Severed;

        /// <summary>Raised after <see cref="ResetCharacter"/> put every part back.</summary>
        public event Action Restored;

        /// <summary>The profile this character was set up with.</summary>
        public GoreProfile Profile => _profile;

        /// <summary>The renderers of each skinned mesh that was set up.</summary>
        public IReadOnlyList<GoreRendererSet> Sets => _sets;

        /// <summary>Whether the setup was done: a profile with cuts and a renderer set for each of its meshes. A character that is not ready cuts nothing.</summary>
        public bool IsReady
        {
            get
            {
                if (_profile == null || _profile.CutCount == 0 || _sets == null || _sets.Length == 0 || _sets.Length != _profile.SetCount)
                {
                    return false;
                }

                foreach (var set in _sets)
                {
                    if (set == null || set.Source == null || set.Segments == null || set.Segments.Length != _profile.CutCount + 1
                        || set.Stumps == null || set.Stumps.Length != _profile.CutCount || set.LimbCaps == null || set.LimbCaps.Length != _profile.CutCount)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>The number of cuts (0 until the setup is done).</summary>
        public int CutCount => IsReady ? _profile.CutCount : 0;

        /// <summary>Whether any part has come off yet.</summary>
        public bool IsCut => _cutMode;

        /// <summary>The number of the cut with this name, or -1.</summary>
        public int IndexOf(string cutName) => _profile != null ? _profile.IndexOf(cutName) : -1;

        /// <summary>The number of the cut of a humanoid's <paramref name="limb"/>, or -1 when the profile has none.</summary>
        public int IndexOf(GoreLimb limb) => _profile != null ? _profile.IndexOf(limb) : -1;

        /// <summary>The name of cut number <paramref name="cut"/>.</summary>
        public string CutName(int cut) => Valid(cut) ? _profile.Cuts[cut].Name : string.Empty;

        /// <summary>The humanoid part of cut number <paramref name="cut"/>.</summary>
        public GoreLimb CutLimb(int cut) => Valid(cut) ? _profile.Cuts[cut].Limb : GoreLimb.None;

        /// <summary>The bone the cut is made at (where blood and sounds belong), or null.</summary>
        public Transform CutBone(int cut) => Valid(cut) && cut < _bones.Length ? _bones[cut] : null;

        /// <summary>Whether the part of cut number <paramref name="cut"/> is off: cut itself, or taken along with a part above it.</summary>
        public bool IsSevered(int cut) => Valid(cut) && _gone != null && _gone.Length == _profile.CutCount && _gone[cut];

        /// <summary>Whether a humanoid's <paramref name="limb"/> is off. False for a limb the profile has no cut for.</summary>
        public bool IsSevered(GoreLimb limb) => IsSevered(IndexOf(limb));

        /// <summary>The parts that are flying or lying about right now.</summary>
        public IReadOnlyList<GoreDetached> Detached => _detached;

        /// <summary>
        /// Takes a humanoid's <paramref name="limb"/> off, with everything below it, and throws it with <paramref name="force"/> (an impulse in
        /// world space; zero lets it drop). Returns the part, or null when nothing came off (not set up, no such cut, already off).
        /// </summary>
        public GameObject Sever(GoreLimb limb, Vector3 force) => Sever(IndexOf(limb), force);

        /// <summary>The same, for the cut with <paramref name="cutName"/> (any rig).</summary>
        public GameObject Sever(string cutName, Vector3 force) => Sever(IndexOf(cutName), force);

        /// <summary>The same, for cut number <paramref name="cut"/> of the profile.</summary>
        public GameObject Sever(int cut, Vector3 force)
        {
            if (!IsReady || !Valid(cut) || !HasSkin(cut))
            {
                return null;
            }

            EnsureState();
            if (_gone[cut])
            {
                return null;
            }

            EnterCutMode();

            var definition = _profile.Cuts[cut];
            var root = new GameObject("Gore " + definition.Name);
            root.layer = gameObject.layer;
            root.transform.SetPositionAndRotation(transform.position, transform.rotation);
            var detached = root.AddComponent<GoreDetached>();

            TakeOff(cut, root.transform, detached, force);
            for (var i = 0; i < definition.Below.Length; i++)
            {
                TakeOff(definition.Below[i], root.transform, detached, force);
            }

            // The piece of a worn garment on the body side of the cut flies off too when the user asked for it.
            FlyBodySides(cut, force);

            // The lid on the part, at its cut end, goes with it; the stump closes the body.
            for (var s = 0; s < View.Length; s++)
            {
                var limbCap = View[s].LimbCaps[cut];
                if (limbCap != null)
                {
                    Snapshot(limbCap, root.transform, detached);
                }

                var stump = View[s].Stumps[cut];
                if (stump != null)
                {
                    stump.enabled = true;
                    _stumpShown[s][cut] = true;
                }
            }

            // The hidden part keeps animating: the skin that stays on the body stops following its bones (after the snapshots), so it cannot drag
            // the skin round the stump, and the bone is squeezed so what hangs on it goes with it.
            Redirect(cut);
            Squeeze(cut);

            if (!Physicalize(root, force))
            {
                // Nothing of this part had any skin: there is nothing to throw.
                Destroy(root);
                return null;
            }

            Bleed(cut, definition, root.transform, detached);
            detached.Begin(this, cut, definition.Limb, _profile.DespawnTime);
            _detached.Add(detached);
            Severed?.Invoke(cut, root);
            return root;
        }

        /// <summary>
        /// Puts every part back: the cut-off parts are removed, the stumps hidden, the squeezed bones restored and the character is drawn by its
        /// own renderers again. For a character that is pooled and used again.
        /// </summary>
        public void ResetCharacter()
        {
            for (var i = _detached.Count - 1; i >= 0; i--)
            {
                if (_detached[i] != null)
                {
                    _detached[i].ReleaseBlood();
                    Destroy(_detached[i].gameObject);
                }
            }

            _detached.Clear();
            StopStumpBlood();
            if (!IsReady)
            {
                return;
            }

            EnsureState();
            Array.Clear(_gone, 0, _gone.Length);
            RestoreBones();
            for (var cut = 0; cut < _boneSqueezed.Length; cut++)
            {
                if (_boneSqueezed[cut] && cut < _bones.Length && _bones[cut] != null)
                {
                    _bones[cut].localScale = _boneScale[cut];
                }

                _boneSqueezed[cut] = false;
            }

            for (var s = 0; s < View.Length; s++)
            {
                Array.Clear(_stumpShown[s], 0, _stumpShown[s].Length);
                var set = View[s];
                for (var i = 0; i < set.Segments.Length; i++)
                {
                    SetEnabled(set.Segments[i], false);
                }

                for (var i = 0; i < set.Stumps.Length; i++)
                {
                    SetEnabled(set.Stumps[i], false);
                    SetEnabled(set.LimbCaps[i], false);
                }

                // The original goes back to what it was before the first cut (a mesh that was off on purpose stays off).
                if (_cutMode)
                {
                    SetEnabled(set.Source, _sourceWasOn[s]);
                }
            }

            _cutMode = false;
            Restored?.Invoke();

            // The character may have been dressed again while it was a corpse.
            RefreshEquipment();
        }

        internal void Forget(GoreDetached part) => _detached.Remove(part);

        // The blood of a cut: it flows out of the stump on the body (riding the bone above the cut, away from the body) and out of the end of the
        // part that came off (riding the part, back towards the body). The blood of a stump that was lower on the same limb stops: its skin is gone.
        private void Bleed(int cut, GoreCut definition, Transform part, GoreDetached detached)
        {
            if (_stumpBlood.Length != _profile.CutCount)
            {
                _stumpBlood = new GoreBloodEmitter[_profile.CutCount];
            }

            for (var i = 0; i < definition.Below.Length; i++)
            {
                StopStumpBlood(definition.Below[i]);
            }

            if (_profile.BloodPrefab == null)
            {
                return;
            }

            var bone = CutBone(cut);
            if (bone == null)
            {
                return;
            }

            var above = bone.parent;
            var outward = above != null ? bone.position - above.position : bone.up;
            if (outward.sqrMagnitude < 1e-8f)
            {
                outward = transform.up;
            }

            outward.Normalize();
            StopStumpBlood(cut);
            _stumpBlood[cut] = GoreBlood.Play(_profile, bone.position, outward, above != null ? above : transform);
            detached.AddBlood(GoreBlood.Play(_profile, bone.position, -outward, part));
        }

        private void StopStumpBlood(int cut)
        {
            if (cut >= 0 && cut < _stumpBlood.Length && _stumpBlood[cut] != null)
            {
                _stumpBlood[cut].Stop();
                _stumpBlood[cut] = null;
            }
        }

        private void StopStumpBlood()
        {
            for (var i = 0; i < _stumpBlood.Length; i++)
            {
                StopStumpBlood(i);
            }
        }

        private bool Valid(int cut) => _profile != null && cut >= 0 && cut < _profile.CutCount;

        private void EnsureState()
        {
            var cuts = _profile.CutCount;
            if (_gone == null || _gone.Length != cuts || _stumpShown == null || _stumpShown.Length != View.Length)
            {
                _gone = new bool[cuts];
                _boneScale = new Vector3[cuts];
                _boneSqueezed = new bool[cuts];
                _sourceWasOn = new bool[View.Length];
                _originalBones = new Transform[View.Length][];
                _currentBones = new Transform[View.Length][];
                _stumpShown = new bool[View.Length][];
                for (var s = 0; s < View.Length; s++)
                {
                    _stumpShown[s] = new bool[cuts];
                    _originalBones[s] = View[s].Source != null ? View[s].Source.bones : Array.Empty<Transform>();
                    _currentBones[s] = _originalBones[s];
                }
            }
        }

        // The first cut: the character is drawn by its parts from now on (together they look like the originals), and the originals are switched
        // off. A mesh that is off or inactive now (a helmet that is not worn) gets no parts shown; the materials the original has now (a look
        // that was applied, a flash) are carried over to its parts.
        private void EnterCutMode()
        {
            if (_cutMode)
            {
                return;
            }

            // What every set shows is decided before any original is switched off: several sets can share one original (a worn mesh made as a set
            // that follows and a set that flies), and the first of them must not hide the original from the others.
            var shown = new bool[View.Length];
            for (var s = 0; s < View.Length; s++)
            {
                var source = View[s].Source;
                shown[s] = source != null && source.enabled && source.gameObject.activeInHierarchy;
                _sourceWasOn[s] = source != null && source.enabled;
            }

            for (var s = 0; s < View.Length; s++)
            {
                var set = View[s];
                var source = set.Source;
                if (!shown[s])
                {
                    continue;
                }

                var materials = source.sharedMaterials;
                var map = ViewData[s].Materials;
                for (var i = 0; i < set.Segments.Length; i++)
                {
                    var part = set.Segments[i];
                    if (part == null)
                    {
                        continue;
                    }

                    if (map != null && i < map.Length && map[i] != null)
                    {
                        var sourceMesh = ViewData[s].SourceMesh;
                        part.sharedMaterials = PartMaterials(materials, map[i].Source, sourceMesh != null ? sourceMesh.subMeshCount : materials.Length);
                    }

                    part.enabled = true;
                }

                source.enabled = false;
            }

            _cutMode = true;
        }

        // The materials of a part: the ones of the submeshes it holds, and then the extra materials the source has beyond its submeshes. A material
        // list longer than the submesh count draws the last submesh again with each extra material: a look system (N-Hance skins) layers chest, glove and
        // trouser skins over the body that way, so the part must keep them. They belong to the source's last submesh, so a part gets them only when
        // it holds that submesh (the parts keep the submeshes in the source's order, so it is then the part's last one).
        internal static Material[] PartMaterials(Material[] materials, int[] pick, int sourceSubmeshes)
        {
            var extra = sourceSubmeshes > 0 && materials.Length > sourceSubmeshes && pick.Length > 0 && pick[pick.Length - 1] == sourceSubmeshes - 1 ? materials.Length - sourceSubmeshes : 0;
            var result = new Material[pick.Length + extra];
            for (var m = 0; m < pick.Length; m++)
            {
                result[m] = pick[m] >= 0 && pick[m] < materials.Length ? materials[pick[m]] : null;
            }

            for (var e = 0; e < extra; e++)
            {
                result[pick.Length + e] = materials[sourceSubmeshes + e];
            }

            return result;
        }

        // One segment of the part that comes off (the cut itself or one below it): its skin goes into the snapshot and is hidden on the body,
        // and so are the stumps that were closing cuts on it.
        private void TakeOff(int cut, Transform parent, GoreDetached detached, Vector3 force)
        {
            if (_gone[cut])
            {
                return;
            }

            _gone[cut] = true;
            for (var s = 0; s < View.Length; s++)
            {
                var segment = View[s].Segments[GoreProfile.SegmentOf(cut)];
                if (segment != null && segment.enabled)
                {
                    // A worn piece the user set to fly flies off on its own; everything else goes with the part.
                    if (ViewData[s].Fly)
                    {
                        Fly(segment, cut, force);
                    }
                    else
                    {
                        Snapshot(segment, parent, detached);
                    }

                    segment.enabled = false;
                }

                // The stumps that sit on this segment (the cuts whose parent is this cut) go with it.
                for (var c = 0; c < _stumpShown[s].Length; c++)
                {
                    var stump = View[s].Stumps[c];
                    if (_stumpShown[s][c] && _profile.Cuts[c].Parent == cut && stump != null)
                    {
                        Snapshot(stump, parent, detached);
                        stump.enabled = false;
                        _stumpShown[s][c] = false;
                    }
                }
            }
        }

        // Worn garments cut across by this cut whose body side is set to fly: the piece on the side the cut is made from becomes a body of its own.
        private void FlyBodySides(int cut, Vector3 force)
        {
            var parentSegment = _profile.Cuts[cut].Parent >= 0 ? GoreProfile.SegmentOf(_profile.Cuts[cut].Parent) : 0;
            for (var s = 0; s < View.Length; s++)
            {
                if (!ViewData[s].BodyFly || GoreProfile.SegmentOf(cut) >= View[s].Segments.Length || View[s].Segments[GoreProfile.SegmentOf(cut)] == null)
                {
                    continue;
                }

                var side = parentSegment < View[s].Segments.Length ? View[s].Segments[parentSegment] : null;
                if (side != null && side.enabled)
                {
                    Fly(side, cut, force);
                    side.enabled = false;
                }
            }
        }

        // A worn piece that leaves the part it belongs to: it becomes a body of its own, thrown the way the part is but not along the same path.
        private void Fly(SkinnedMeshRenderer segment, int cut, Vector3 force)
        {
            var root = new GameObject("Gore " + segment.name);
            root.layer = gameObject.layer;
            root.transform.SetPositionAndRotation(transform.position, transform.rotation);
            var piece = root.AddComponent<GoreDetached>();
            Snapshot(segment, root.transform, piece);

            // Lighter and scattered, so the piece does not follow the part like a shadow.
            var thrown = force * Random.Range(FlyForceMin, FlyForceMax) + Random.insideUnitSphere * (force.magnitude * FlyScatter);
            if (!Physicalize(root, thrown))
            {
                Destroy(root);
                return;
            }

            piece.Begin(this, cut, _profile.Cuts[cut].Limb, _profile.DespawnTime);
            _detached.Add(piece);
        }

        // Makes the skin that stays follow the bone above the cut wherever it followed the cut's bone or one below it. The skin round a cut
        // is blended between the two bones (that is how a joint bends); once the part is gone the blend must not pull towards a limb that is not
        // there, or the ring of skin is dragged out of line with the lid. A helper transform stands in for each bone of the part: it sits under the
        // bone above the cut at the offset the two bones had in the bind pose, so the renderers follow it exactly as the old bone would have
        // followed the bone above. Done once per cut and renderer; the offsets come from the bind poses kept in the profile.
        private void Redirect(int cut)
        {
            var cutBone = cut < _bones.Length ? _bones[cut] : null;
            if (cutBone == null)
            {
                return;
            }

            for (var s = 0; s < View.Length; s++)
            {
                var poses = ViewData[s].BindPoses;
                var original = _originalBones[s];
                if (poses == null || poses.Length != original.Length)
                {
                    // A worn piece that goes whole may have no bind poses (they come with its mesh): nothing to correct for it, and nothing to say.
                    if (ViewData[s].Whole)
                    {
                        continue;
                    }

                    if (!_warnedNoBindPoses)
                    {
                        _warnedNoBindPoses = true;
                        Debug.LogWarning("[GoreLab] The profile of '" + name + "' has no bind poses (it was made by an older setup): the skin round a cut will be dragged by the hidden part. Set the character up again.", this);
                    }

                    continue;
                }

                var target = -1;
                for (var t = cutBone.parent; t != null && target < 0; t = t.parent)
                {
                    target = Array.IndexOf(original, t);
                }

                if (target < 0)
                {
                    continue;
                }

                var next = (Transform[])_currentBones[s].Clone();
                var changed = false;
                for (var i = 0; i < original.Length; i++)
                {
                    var bone = original[i];
                    if (bone == null || (bone != cutBone && !bone.IsChildOf(cutBone)))
                    {
                        continue;
                    }

                    next[i] = Helper(original[target], poses[target] * poses[i].inverse);
                    changed = true;
                    if (next[i] == original[target] && !_warnedBindPose)
                    {
                        _warnedBindPose = true;
                        Debug.LogWarning("[GoreLab] A bone of '" + name + "' has a bind pose that cannot be used (zero, mirrored or not a number): the skin on it follows the bone above the cut without its offset. Set the character up again, or check the rig.", this);
                    }
                }

                if (!changed)
                {
                    continue;
                }

                _currentBones[s] = next;
                SetBones(View[s], next);
            }
        }

        // A transform under <parent> at <offset>, shared by every renderer that needs the same one.
        private Transform Helper(Transform parent, Matrix4x4 offset)
        {
            // A zero, mirrored or not-a-number offset cannot be a transform: the skin follows the bone above the cut itself.
            var determinant = offset.determinant;
            if (float.IsNaN(determinant) || float.IsInfinity(determinant) || determinant < 1e-9f)
            {
                return parent;
            }

            for (var i = 0; i < _helpers.Count; i++)
            {
                if (_helpers[i] != null && _helpers[i].parent == parent && Same(_helperPoses[i], offset))
                {
                    return _helpers[i];
                }
            }

            var go = new GameObject("Gore Bone");
            go.layer = parent.gameObject.layer;
            var helper = go.transform;
            helper.SetParent(parent, false);
            helper.localPosition = offset.GetColumn(3);
            helper.localRotation = offset.rotation;
            helper.localScale = offset.lossyScale;
            _helpers.Add(helper);
            _helperPoses.Add(offset);
            return helper;
        }

        private static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (var i = 0; i < 16; i++)
            {
                if (Mathf.Abs(a[i] - b[i]) > 1e-5f)
                {
                    return false;
                }
            }

            return true;
        }

        private static void SetBones(GoreRendererSet set, Transform[] bones)
        {
            for (var i = 0; i < set.Segments.Length; i++)
            {
                SetBones(set.Segments[i], bones);
            }

            for (var i = 0; i < set.Stumps.Length; i++)
            {
                SetBones(set.Stumps[i], bones);
                SetBones(set.LimbCaps[i], bones);
            }
        }

        private static void SetBones(SkinnedMeshRenderer renderer, Transform[] bones)
        {
            if (renderer != null)
            {
                renderer.bones = bones;
            }
        }

        // Every renderer follows its own bones again, and the helpers are gone.
        private void RestoreBones()
        {
            if (_helpers.Count == 0)
            {
                return;
            }

            for (var s = 0; s < View.Length; s++)
            {
                _currentBones[s] = _originalBones[s];
                SetBones(View[s], _originalBones[s]);
            }

            for (var i = 0; i < _helpers.Count; i++)
            {
                if (_helpers[i] != null)
                {
                    Destroy(_helpers[i].gameObject);
                }
            }

            _helpers.Clear();
            _helperPoses.Clear();
        }

        // Squeezes the bone of a cut (and so everything below it) to almost nothing, remembering its scale for the reset.
        private void Squeeze(int cut)
        {
            if (cut >= _bones.Length || _bones[cut] == null || _boneSqueezed[cut])
            {
                return;
            }

            _boneScale[cut] = _bones[cut].localScale;
            _boneSqueezed[cut] = true;
            _bones[cut].localScale = Vector3.one * CollapsedScale;
        }

        // A copy of a renderer's skin in its pose of this moment, on a plain mesh renderer under the part's body. The skin is worked out here
        // from the bones as they are now (the same sum the GPU does: every vertex follows up to four bones by its weights) instead of asking the
        // renderer to bake itself: a renderer that was only just switched on has not been skinned yet, and a bake then gives the bind pose, not the
        // pose the bones are in, so the skin would not sit where the bone-based pieces (the lids, the blood) are.
        private static void Snapshot(SkinnedMeshRenderer renderer, Transform parent, GoreDetached detached)
        {
            var source = renderer.sharedMesh;
            if (source == null)
            {
                return;
            }

            // A worn mesh that cannot be read cannot be skinned here: it is carried on a frozen copy of its bones instead.
            if (!source.isReadable)
            {
                Freeze(renderer, parent);
                return;
            }

            var mesh = SkinNow(renderer, source, parent);
            detached.Own(mesh);
            var go = new GameObject(renderer.name);
            go.layer = renderer.gameObject.layer;
            var t = go.transform;
            t.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = renderer.sharedMaterials;
            meshRenderer.shadowCastingMode = renderer.shadowCastingMode;
            meshRenderer.receiveShadows = renderer.receiveShadows;
            meshRenderer.lightProbeUsage = renderer.lightProbeUsage;
            meshRenderer.reflectionProbeUsage = renderer.reflectionProbeUsage;
            meshRenderer.renderingLayerMask = renderer.renderingLayerMask;
            meshRenderer.probeAnchor = renderer.probeAnchor;
            meshRenderer.staticShadowCaster = renderer.staticShadowCaster;
        }

        // A skinned renderer as it is now, without reading its mesh: a copy of every bone it follows is made under <paramref name="parent"/> at the
        // bone's pose of this moment, and a renderer of the same mesh follows the copies. The copies never move, so it is a frozen pose, drawn
        // by the GPU like any skinned mesh.
        private static void Freeze(SkinnedMeshRenderer source, Transform parent)
        {
            var go = new GameObject(source.name);
            go.layer = source.gameObject.layer;
            go.transform.SetParent(parent, false);

            var bones = source.bones;
            var frozen = new Transform[bones.Length];
            for (var i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null)
                {
                    frozen[i] = go.transform;
                    continue;
                }

                var copy = new GameObject(bones[i].name).transform;
                copy.SetParent(go.transform, false);
                copy.SetPositionAndRotation(bones[i].position, bones[i].rotation);
                copy.localScale = bones[i].lossyScale;
                frozen[i] = copy;
            }

            var rootIndex = source.rootBone != null ? Array.IndexOf(bones, source.rootBone) : -1;
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = source.sharedMesh;
            renderer.bones = frozen;
            renderer.rootBone = rootIndex >= 0 ? frozen[rootIndex] : (frozen.Length > 0 ? frozen[0] : go.transform);
            renderer.sharedMaterials = source.sharedMaterials;
            renderer.localBounds = source.localBounds;
            renderer.updateWhenOffscreen = true;
            renderer.quality = source.quality;
            renderer.shadowCastingMode = source.shadowCastingMode;
            renderer.receiveShadows = source.receiveShadows;
            renderer.lightProbeUsage = source.lightProbeUsage;
            renderer.reflectionProbeUsage = source.reflectionProbeUsage;
            renderer.renderingLayerMask = source.renderingLayerMask;
        }

        // The mesh of a skinned renderer as it looks now, in the space of <paramref name="parent"/>.
        private static Mesh SkinNow(SkinnedMeshRenderer renderer, Mesh source, Transform parent)
        {
            var vertices = source.vertices;
            var normals = source.normals;
            var tangents = source.tangents;
            var weights = source.boneWeights;
            var bind = source.bindposes;
            var bones = renderer.bones;
            var toParent = parent.worldToLocalMatrix;

            // One matrix per bone: from the mesh as it was bound to the bone, to the bone now, to the part.
            var matrices = new Matrix4x4[Mathf.Max(bones.Length, bind.Length)];
            var rigid = toParent * renderer.transform.localToWorldMatrix;
            for (var i = 0; i < matrices.Length; i++)
            {
                matrices[i] = i < bones.Length && i < bind.Length && bones[i] != null ? toParent * bones[i].localToWorldMatrix * bind[i] : rigid;
            }

            var hasNormals = normals.Length == vertices.Length;
            var hasTangents = tangents.Length == vertices.Length;
            var hasWeights = weights.Length == vertices.Length;
            var skinned = new Vector3[vertices.Length];
            var skinnedNormals = hasNormals ? new Vector3[vertices.Length] : null;
            var skinnedTangents = hasTangents ? new Vector4[vertices.Length] : null;
            for (var v = 0; v < vertices.Length; v++)
            {
                if (!hasWeights)
                {
                    skinned[v] = rigid.MultiplyPoint3x4(vertices[v]);
                    if (hasNormals)
                    {
                        skinnedNormals[v] = rigid.MultiplyVector(normals[v]).normalized;
                    }

                    if (hasTangents)
                    {
                        var tangent = rigid.MultiplyVector(tangents[v]);
                        skinnedTangents[v] = new Vector4(tangent.x, tangent.y, tangent.z, tangents[v].w);
                    }

                    continue;
                }

                var w = weights[v];
                var position = Vector3.zero;
                var normal = Vector3.zero;
                var tangentSum = Vector3.zero;
                var total = 0f;
                for (var k = 0; k < 4; k++)
                {
                    var weight = k == 0 ? w.weight0 : k == 1 ? w.weight1 : k == 2 ? w.weight2 : w.weight3;
                    if (weight <= 0f)
                    {
                        continue;
                    }

                    var index = k == 0 ? w.boneIndex0 : k == 1 ? w.boneIndex1 : k == 2 ? w.boneIndex2 : w.boneIndex3;
                    var matrix = index >= 0 && index < matrices.Length ? matrices[index] : rigid;
                    position += matrix.MultiplyPoint3x4(vertices[v]) * weight;
                    if (hasNormals)
                    {
                        normal += matrix.MultiplyVector(normals[v]) * weight;
                    }

                    if (hasTangents)
                    {
                        tangentSum += matrix.MultiplyVector(tangents[v]) * weight;
                    }

                    total += weight;
                }

                if (total <= 0f)
                {
                    position = rigid.MultiplyPoint3x4(vertices[v]);
                    normal = hasNormals ? rigid.MultiplyVector(normals[v]) : Vector3.zero;
                    tangentSum = hasTangents ? rigid.MultiplyVector(tangents[v]) : Vector3.zero;
                    total = 1f;
                }

                skinned[v] = position / total;
                if (hasNormals)
                {
                    skinnedNormals[v] = normal.normalized;
                }

                if (hasTangents)
                {
                    var tangent = tangentSum.normalized;
                    skinnedTangents[v] = new Vector4(tangent.x, tangent.y, tangent.z, tangents[v].w);
                }
            }

            var mesh = new Mesh { name = renderer.name + " (cut)", indexFormat = source.indexFormat };
            mesh.SetVertices(skinned);
            if (hasNormals)
            {
                mesh.SetNormals(skinnedNormals);
            }

            if (hasTangents)
            {
                mesh.SetTangents(skinnedTangents);
            }

            CopyUvs(source, mesh, vertices.Length);

            var colors = source.colors32;
            if (colors.Length == vertices.Length)
            {
                mesh.colors32 = colors;
            }

            mesh.subMeshCount = source.subMeshCount;
            for (var sub = 0; sub < source.subMeshCount; sub++)
            {
                mesh.SetTriangles(source.GetTriangles(sub), sub, false);
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        // The texture coordinates of a mesh, channel by channel, with the number of components each had.
        private static void CopyUvs(Mesh source, Mesh mesh, int count)
        {
            var uvs = new List<Vector4>(count);
            for (var channel = 0; channel < 4; channel++)
            {
                var attribute = VertexAttribute.TexCoord0 + channel;
                if (!source.HasVertexAttribute(attribute))
                {
                    continue;
                }

                source.GetUVs(channel, uvs);
                if (uvs.Count != count)
                {
                    continue;
                }

                var dimension = source.GetVertexAttributeDimension(attribute);
                if (dimension <= 2)
                {
                    var two = new List<Vector2>(count);
                    for (var i = 0; i < count; i++)
                    {
                        two.Add(uvs[i]);
                    }

                    mesh.SetUVs(channel, two);
                }
                else if (dimension == 3)
                {
                    var three = new List<Vector3>(count);
                    for (var i = 0; i < count; i++)
                    {
                        three.Add(uvs[i]);
                    }

                    mesh.SetUVs(channel, three);
                }
                else
                {
                    mesh.SetUVs(channel, uvs);
                }
            }
        }

        // A body for the part: a box that fits its skin, a weight from its size, and the throw. False when there is no skin to give a body to.
        private bool Physicalize(GameObject root, Vector3 force)
        {
            var bounds = new Bounds();
            var any = false;
            // A frozen worn piece has no mesh filter: its bounds come from its renderer, relative to its root bone.
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>();
            for (var i = 0; i < skinned.Length; i++)
            {
                var space = skinned[i].rootBone != null ? skinned[i].rootBone : skinned[i].transform;
                var local = skinned[i].localBounds;
                var extents = local.extents;
                for (var corner = 0; corner < 8; corner++)
                {
                    var point = local.center + new Vector3(
                        (corner & 1) == 0 ? -extents.x : extents.x,
                        (corner & 2) == 0 ? -extents.y : extents.y,
                        (corner & 4) == 0 ? -extents.z : extents.z);
                    var inRoot = root.transform.InverseTransformPoint(space.TransformPoint(point));
                    if (!any)
                    {
                        bounds = new Bounds(inRoot, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(inRoot);
                    }
                }
            }

            var meshFilters = root.GetComponentsInChildren<MeshFilter>();
            for (var i = 0; i < meshFilters.Length; i++)
            {
                var filter = meshFilters[i];
                if (filter.sharedMesh == null)
                {
                    continue;
                }

                var local = filter.sharedMesh.bounds;
                var extents = local.extents;
                for (var corner = 0; corner < 8; corner++)
                {
                    var point = local.center + new Vector3(
                        (corner & 1) == 0 ? -extents.x : extents.x,
                        (corner & 2) == 0 ? -extents.y : extents.y,
                        (corner & 4) == 0 ? -extents.z : extents.z);
                    var inRoot = root.transform.InverseTransformPoint(filter.transform.TransformPoint(point));
                    if (!any)
                    {
                        bounds = new Bounds(inRoot, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(inRoot);
                    }
                }
            }

            if (!any)
            {
                return false;
            }

            var box = root.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = bounds.size * ColliderShare;

            var body = root.AddComponent<Rigidbody>();
            body.mass = Mathf.Clamp(bounds.size.x * bounds.size.y * bounds.size.z * _profile.Density, MinMass, MaxMass);
            body.centerOfMass = bounds.center;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // The part does not collide with the body it came from (the colliders that are active now; one switched on later, like a ragdoll's, does).
            if (_ownColliders == null)
            {
                _ownColliders = GetComponentsInChildren<Collider>(true);
            }

            for (var i = 0; i < _ownColliders.Length; i++)
            {
                var own = _ownColliders[i];
                if (own != null && own.enabled && own.gameObject.activeInHierarchy)
                {
                    Physics.IgnoreCollision(box, own, true);
                }
            }

            if (force.sqrMagnitude > 0f)
            {
                body.AddForce(force, ForceMode.Impulse);
            }

            if (_profile.Spin > 0f)
            {
                body.AddTorque(Random.onUnitSphere * (_profile.Spin * Mathf.PI * 2f), ForceMode.VelocityChange);
            }

            return true;
        }

        private static void SetEnabled(Renderer renderer, bool enabled)
        {
            if (renderer != null)
            {
                renderer.enabled = enabled;
            }
        }
    }
}
