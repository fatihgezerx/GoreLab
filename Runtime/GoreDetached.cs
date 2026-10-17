using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// A part that was cut off a <see cref="GoreCharacter"/>, or a piece that broke off a <see cref="GoreBreakable"/>: a snapshot of the part's skin as
    /// it was at the moment of the cut (a piece is its mesh as it was baked), on a body of its own. It removes itself after the profile's Despawn Time
    /// and frees the meshes it made. Nothing has to be added by hand.
    /// </summary>
    public sealed class GoreDetached : MonoBehaviour
    {
        private readonly List<Mesh> _meshes = new List<Mesh>(4);
        private readonly List<GoreBloodEmitter> _blood = new List<GoreBloodEmitter>(1);

        /// <summary>The character the part was cut from (may be gone by now); null for a piece of a <see cref="GoreBreakable"/>.</summary>
        public GoreCharacter Source { get; private set; }

        /// <summary>The object the piece broke off (may be gone by now); null for a part of a <see cref="GoreCharacter"/>.</summary>
        public GoreBreakable Breakable { get; private set; }

        /// <summary>The piece of the break profile it is, or -1 for a part of a character.</summary>
        public int Piece { get; private set; } = -1;

        /// <summary>The cut it came off, as a number of the profile.</summary>
        public int Cut { get; private set; }

        /// <summary>The humanoid part it is, or <see cref="GoreLimb.None"/>.</summary>
        public GoreLimb Limb { get; private set; }

        internal void Begin(GoreCharacter source, int cut, GoreLimb limb, float despawnSeconds)
        {
            Source = source;
            Cut = cut;
            Limb = limb;
            if (despawnSeconds > 0f)
            {
                DespawnAsync(despawnSeconds).Forget();
            }
        }

        internal void Begin(GoreBreakable source, int piece, float despawnSeconds)
        {
            Breakable = source;
            Piece = piece;
            if (despawnSeconds > 0f)
            {
                DespawnAsync(despawnSeconds).Forget();
            }
        }

        internal void Own(Mesh mesh) => _meshes.Add(mesh);

        // A blood effect that rides this part.
        internal void AddBlood(GoreBloodEmitter emitter)
        {
            if (emitter != null)
            {
                _blood.Add(emitter);
            }
        }

        // Gives the blood back before the part is destroyed (a pooled effect must not be destroyed together with its parent).
        internal void ReleaseBlood()
        {
            for (var i = 0; i < _blood.Count; i++)
            {
                _blood[i].Stop();
            }

            _blood.Clear();
        }

        private async UniTaskVoid DespawnAsync(float seconds)
        {
            var token = this.GetCancellationTokenOnDestroy();
            var canceled = await UniTask.Delay(System.TimeSpan.FromSeconds(seconds), DelayType.DeltaTime, PlayerLoopTiming.Update, token).SuppressCancellationThrow();
            if (!canceled)
            {
                ReleaseBlood();
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            if (Source != null)
            {
                Source.Forget(this);
            }

            if (Breakable != null)
            {
                Breakable.Forget(this);
            }

            for (var i = 0; i < _meshes.Count; i++)
            {
                if (_meshes[i] != null)
                {
                    Destroy(_meshes[i]);
                }
            }

            _meshes.Clear();
        }
    }
}
