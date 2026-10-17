using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace GoreLab
{
    /// <summary>
    /// A static object (a table, a crate, a statue, a wall) that can break into pieces. Set up once in the editor (Tools &gt; GoreLab &gt;
    /// Gore Studio): the mesh is divided into pieces and each piece is a mesh asset of its own. Until the first break the object is drawn by its
    /// own renderer, so it costs nothing extra. Then <see cref="Break"/> (a blow at a point) or <see cref="Sever"/> (one piece) takes pieces off: they
    /// leave the object and fly on bodies of their own; the pieces that stay keep the object whole to look at.
    /// </summary>
    /// <remarks>
    /// Put it on the object that has the <c>MeshFilter</c> and <c>MeshRenderer</c>. Everything is event driven: no Update, and the only timer is a
    /// piece's own removal (see <see cref="GoreDetached"/>). The pieces are made once, at the first break. The object's own collider stays until
    /// every piece is gone, so what is left of the object keeps blocking where it stood. A piece does not collide with the object it came from
    /// or with the other pieces that came off it (they overlap at the moment of the break and would burst apart). The mesh of a statically batched
    /// object cannot be taken apart at run time by Unity; leave such an object out of static batching.
    /// <see cref="ResetBreakable"/> puts everything back, for an object that is pooled and used again.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class GoreBreakable : MonoBehaviour
    {
        // The share of the box of a piece its collider fills, so a piece rests on its body, not on the corners of its box.
        private const float ColliderShare = 0.9f;

        // The thinnest collider a piece gets, in meters (a shard of a wall has almost no thickness).
        private const float MinThickness = 0.02f;
        private const float MinMass = 0.05f;
        private const float MaxMass = 400f;

        [SerializeField] private GoreBreakProfile _profile;

        // Written by the studio. Nothing to set by hand.
        [SerializeField] [HideInInspector] private MeshFilter _filter;
        [SerializeField] [HideInInspector] private MeshRenderer _renderer;

        private Transform _container;
        private Transform[] _pieces = Array.Empty<Transform>();
        private bool[] _gone = Array.Empty<bool>();
        private int _usable;
        private int _goneCount;
        private bool _broken;
        private bool _sourceWasOn;
        private Collider[] _ownColliders = Array.Empty<Collider>();
        private bool[] _colliderWasOn = Array.Empty<bool>();
        private Rigidbody _ownBody;
        private bool _resolved;
        private readonly List<GoreDetached> _detached = new List<GoreDetached>(8);
        private readonly List<Collider> _debris = new List<Collider>(8);

        /// <summary>Raised after a piece came off: its number in the profile and the piece, which is already flying.</summary>
        public event Action<int, GameObject> Severed;

        /// <summary>Raised after <see cref="ResetBreakable"/> put every piece back.</summary>
        public event Action Restored;

        /// <summary>The profile this object was set up with.</summary>
        public GoreBreakProfile Profile => _profile;

        /// <summary>Whether the setup was done: a profile with pieces and a mesh and renderer to take apart. An object that is not ready breaks nothing.</summary>
        public bool IsReady => _profile != null && _profile.PieceCount > 0 && Resolve();

        /// <summary>The number of pieces (0 until the setup is done).</summary>
        public int PieceCount => IsReady ? _profile.PieceCount : 0;

        /// <summary>The number of pieces that have come off.</summary>
        public int BrokenCount => _goneCount;

        /// <summary>Whether any piece has come off yet.</summary>
        public bool IsBroken => _goneCount > 0;

        /// <summary>Whether every piece has come off: nothing of the object is left.</summary>
        public bool IsAllBroken => _broken && _goneCount >= _usable;

        /// <summary>The pieces that are flying or lying about right now.</summary>
        public IReadOnlyList<GoreDetached> Detached => _detached;

        /// <summary>Whether piece number <paramref name="piece"/> of the profile has come off.</summary>
        public bool IsPieceBroken(int piece) => piece >= 0 && piece < _gone.Length && _gone[piece];

        /// <summary>
        /// A blow at <paramref name="point"/> (world space): breaks off every piece within <paramref name="radius"/> meters of it and throws each away
        /// from the point with an impulse of <paramref name="force"/> (kg m/s) at the point itself, less the farther the piece is. A radius of zero
        /// or less, or infinity, breaks every piece that is still on, all with the full force. The rest stay where they are. Returns how many came off.
        /// </summary>
        public int Break(Vector3 point, float force, float radius)
        {
            if (!IsReady)
            {
                return 0;
            }

            EnterBreakMode();
            var everything = radius <= 0f || float.IsInfinity(radius) || float.IsNaN(radius);
            var count = 0;
            for (var piece = 0; piece < _pieces.Length; piece++)
            {
                if (_gone[piece] || _pieces[piece] == null)
                {
                    continue;
                }

                var distance = DistanceTo(piece, point, out var away);
                if (!everything && distance > radius)
                {
                    continue;
                }

                var share = everything ? 1f : Mathf.Max(0f, 1f - distance / radius);
                if (Sever(piece, away * (force * share)) != null)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Takes piece number <paramref name="piece"/> off and throws it with <paramref name="impulse"/> (world space; zero lets it drop). Returns the piece, or
        /// null when nothing came off (not set up, no such piece, already off).
        /// </summary>
        public GameObject Sever(int piece, Vector3 impulse)
        {
            if (!IsReady || piece < 0 || piece >= _profile.PieceCount)
            {
                return null;
            }

            EnterBreakMode();
            if (_gone[piece] || _pieces[piece] == null)
            {
                return null;
            }

            var data = _profile.Pieces[piece];
            var body = _pieces[piece];
            body.SetParent(null, true);
            body.localScale = body.lossyScale;
            _pieces[piece] = null;
            _gone[piece] = true;
            _goneCount++;

            var scale = body.localScale;
            var box = body.gameObject.AddComponent<BoxCollider>();
            var size = data.Size * ColliderShare;
            var floor = MinThickness / Mathf.Max(0.0001f, Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z))));
            box.center = data.Center;
            box.size = new Vector3(Mathf.Max(size.x, floor), Mathf.Max(size.y, floor), Mathf.Max(size.z, floor));

            var rigidbody = body.gameObject.AddComponent<Rigidbody>();
            rigidbody.mass = Mathf.Clamp(data.Volume * Mathf.Abs(scale.x * scale.y * scale.z) * _profile.Density, MinMass, MaxMass);
            rigidbody.centerOfMass = data.Center;
            rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // It does not collide with the object it came from (what is left of it keeps its own collider), nor with the other pieces that came off.
            for (var i = 0; i < _ownColliders.Length; i++)
            {
                if (_ownColliders[i] != null)
                {
                    Physics.IgnoreCollision(box, _ownColliders[i], true);
                }
            }

            for (var i = _debris.Count - 1; i >= 0; i--)
            {
                if (_debris[i] == null)
                {
                    _debris.RemoveAt(i);
                }
                else
                {
                    Physics.IgnoreCollision(box, _debris[i], true);
                }
            }

            _debris.Add(box);

            // An object that was moving (a falling crate) throws its pieces with it.
            if (_ownBody != null && !_ownBody.isKinematic)
            {
                rigidbody.linearVelocity = _ownBody.linearVelocity;
                rigidbody.angularVelocity = _ownBody.angularVelocity;
            }

            if (impulse.sqrMagnitude > 0f)
            {
                rigidbody.AddForce(impulse, ForceMode.Impulse);
            }

            if (_profile.Spin > 0f)
            {
                rigidbody.AddTorque(Random.onUnitSphere * (_profile.Spin * Mathf.PI * 2f), ForceMode.VelocityChange);
            }

            var detached = body.gameObject.AddComponent<GoreDetached>();
            detached.Begin(this, piece, _profile.DespawnTime);
            _detached.Add(detached);

            if (_goneCount >= _usable)
            {
                SwitchColliders(false);
            }

            Severed?.Invoke(piece, body.gameObject);
            return body.gameObject;
        }

        /// <summary>
        /// Puts every piece back: the pieces that came off are removed, the object is drawn by its own renderer and collider again. For an object that
        /// is pooled and used again.
        /// </summary>
        public void ResetBreakable()
        {
            for (var i = _detached.Count - 1; i >= 0; i--)
            {
                if (_detached[i] != null)
                {
                    Destroy(_detached[i].gameObject);
                }
            }

            _detached.Clear();
            _debris.Clear();
            if (!_broken)
            {
                return;
            }

            if (_container != null)
            {
                Destroy(_container.gameObject);
            }

            _container = null;
            _pieces = Array.Empty<Transform>();
            Array.Clear(_gone, 0, _gone.Length);
            _goneCount = 0;
            _broken = false;
            if (_renderer != null)
            {
                _renderer.enabled = _sourceWasOn;
            }

            SwitchColliders(true);
            Restored?.Invoke();
        }

        internal void Forget(GoreDetached piece) => _detached.Remove(piece);

        // Finds the mesh and renderer of this object (the studio wrote them; this only covers an object that was added by hand).
        private bool Resolve()
        {
            if (_resolved)
            {
                return _filter != null && _renderer != null;
            }

            if (_filter == null)
            {
                _filter = GetComponent<MeshFilter>();
            }

            if (_renderer == null)
            {
                _renderer = GetComponent<MeshRenderer>();
            }

            _ownBody = GetComponent<Rigidbody>();
            _resolved = true;
            return _filter != null && _renderer != null;
        }

        // The distance from a world point to a piece (to its box, so a point touching a big piece is at distance zero) and the direction to throw it.
        private float DistanceTo(int piece, Vector3 point, out Vector3 away)
        {
            var data = _profile.Pieces[piece];
            var local = _pieces[piece].InverseTransformPoint(point);
            var half = data.Size * 0.5f;
            var closest = new Vector3(
                Mathf.Clamp(local.x, data.Center.x - half.x, data.Center.x + half.x),
                Mathf.Clamp(local.y, data.Center.y - half.y, data.Center.y + half.y),
                Mathf.Clamp(local.z, data.Center.z - half.z, data.Center.z + half.z));
            var distance = (_pieces[piece].TransformPoint(closest) - point).magnitude;
            away = _pieces[piece].TransformPoint(data.Center) - point;
            away = away.sqrMagnitude > 1e-8f ? away.normalized : transform.up;
            return distance;
        }

        // Makes the pieces (once) and swaps the object's own mesh for them. They are assembled where the mesh was, so nothing seems to change.
        private void EnterBreakMode()
        {
            if (_broken)
            {
                return;
            }

            var source = _renderer.sharedMaterials;
            var fill = _profile.FillMaterial != null ? _profile.FillMaterial : (source.Length > 0 ? source[0] : null);
            var count = _profile.PieceCount;
            _container = new GameObject("Gore Pieces").transform;
            _container.gameObject.layer = gameObject.layer;
            _container.SetParent(transform, false);
            _pieces = new Transform[count];
            _gone = new bool[count];
            _usable = 0;
            for (var i = 0; i < count; i++)
            {
                var data = _profile.Pieces[i];
                if (data == null || data.Mesh == null)
                {
                    continue;
                }

                var go = new GameObject(string.IsNullOrEmpty(data.Name) ? "Gore piece " + i : data.Name);
                go.layer = gameObject.layer;
                var t = go.transform;
                t.SetParent(_container, false);
                go.AddComponent<MeshFilter>().sharedMesh = data.Mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                var materials = new Material[data.Materials.Length];
                for (var m = 0; m < materials.Length; m++)
                {
                    var index = data.Materials[m];
                    materials[m] = index >= 0 && index < source.Length ? source[index] : fill;
                }

                renderer.sharedMaterials = materials;
                CopyRendererSettings(_renderer, renderer);
                _pieces[i] = t;
                _usable++;
            }

            _ownColliders = GetComponents<Collider>();
            _colliderWasOn = new bool[_ownColliders.Length];
            for (var i = 0; i < _ownColliders.Length; i++)
            {
                _colliderWasOn[i] = _ownColliders[i] != null && _ownColliders[i].enabled;
            }

            _sourceWasOn = _renderer.enabled;
            _renderer.enabled = false;
            _broken = true;
        }

        private void SwitchColliders(bool restore)
        {
            for (var i = 0; i < _ownColliders.Length; i++)
            {
                if (_ownColliders[i] != null)
                {
                    _ownColliders[i].enabled = restore && _colliderWasOn[i];
                }
            }
        }

        private static void CopyRendererSettings(Renderer from, Renderer to)
        {
            to.shadowCastingMode = from.shadowCastingMode;
            to.receiveShadows = from.receiveShadows;
            to.lightProbeUsage = from.lightProbeUsage;
            to.reflectionProbeUsage = from.reflectionProbeUsage;
            to.motionVectorGenerationMode = from.motionVectorGenerationMode;
            to.allowOcclusionWhenDynamic = from.allowOcclusionWhenDynamic;
            to.renderingLayerMask = from.renderingLayerMask;
            to.probeAnchor = from.probeAnchor;
        }
    }
}
