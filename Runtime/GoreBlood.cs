using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GoreLab
{
    /// <summary>
    /// Where the blood effects of the GoreLab come from and go back to. The default instantiates and destroys; an optional bridge (the
    /// <c>Pooling</c> folder, with Pool System) replaces <see cref="GoreBlood.Spawner"/> so they come from a pool.
    /// </summary>
    public interface IGoreSpawner
    {
        /// <summary>Gives an instance of <paramref name="prefab"/> at a place.</summary>
        GameObject Get(GameObject prefab, Vector3 position, Quaternion rotation);

        /// <summary>Takes an instance that <see cref="Get"/> gave back.</summary>
        void Release(GameObject instance);
    }

    /// <summary>The blood a cut sheds: one Particle System prefab played at the cut, spurting and then running until the bleed time is over.</summary>
    public static class GoreBlood
    {
        private sealed class DefaultSpawner : IGoreSpawner
        {
            public GameObject Get(GameObject prefab, Vector3 position, Quaternion rotation) => Object.Instantiate(prefab, position, rotation);

            public void Release(GameObject instance)
            {
                if (instance != null)
                {
                    Object.Destroy(instance);
                }
            }
        }

        private static readonly IGoreSpawner Default = new DefaultSpawner();
        private static IGoreSpawner _spawner;

        /// <summary>Where the effects come from. Set it to take them from a pool; null puts the default back.</summary>
        public static IGoreSpawner Spawner
        {
            get => _spawner ?? Default;
            set => _spawner = value;
        }

        /// <summary>
        /// Plays the profile's blood at <paramref name="position"/> flowing along <paramref name="direction"/> (the particles' forward), riding
        /// <paramref name="parent"/>. Returns the emitter that stops it, or null when the profile has no blood.
        /// </summary>
        internal static GoreBloodEmitter Play(GoreProfile profile, Vector3 position, Vector3 direction, Transform parent)
        {
            if (profile == null || profile.BloodPrefab == null)
            {
                return null;
            }

            var rotation = direction.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(direction) : Quaternion.identity;
            var spawner = Spawner;
            var instance = spawner.Get(profile.BloodPrefab, position, rotation);
            if (instance == null)
            {
                return null;
            }

            var transform = instance.transform;
            transform.SetParent(parent, true);
            transform.localScale = profile.BloodPrefab.transform.localScale * Mathf.Max(0.01f, profile.BloodScale);
            var emitter = new GoreBloodEmitter(instance, spawner);
            emitter.Begin(profile.BleedTime);
            return emitter;
        }
    }

    /// <summary>
    /// One running blood effect: it stops emitting after the bleed time, goes back to where it came from once its last drop has fallen, and can
    /// be stopped earlier (the part it rides was removed, the character came back to life). The only timer is a UniTask delay.
    /// </summary>
    public sealed class GoreBloodEmitter
    {
        private readonly GameObject _instance;
        private readonly IGoreSpawner _spawner;
        private readonly ParticleSystem[] _systems;
        private CancellationTokenSource _cancel;
        private bool _released;

        internal GoreBloodEmitter(GameObject instance, IGoreSpawner spawner)
        {
            _instance = instance;
            _spawner = spawner;
            _systems = instance.GetComponentsInChildren<ParticleSystem>(true);
        }

        /// <summary>Whether the effect still exists (it has not been released).</summary>
        public bool IsActive => !_released;

        internal void Begin(float bleedTime)
        {
            var longest = 0.5f;
            var duration = 0f;
            for (var i = 0; i < _systems.Length; i++)
            {
                var system = _systems[i];
                var main = system.main;
                var life = main.startLifetime;
                longest = Mathf.Max(longest, life.mode == ParticleSystemCurveMode.Curve || life.mode == ParticleSystemCurveMode.TwoCurves ? life.curveMultiplier : life.constantMax);
                duration = Mathf.Max(duration, main.loop ? 0f : main.duration);
                system.Clear(true);
                system.Play(true);
            }

            _cancel = new CancellationTokenSource();
            Run(bleedTime > 0f ? bleedTime : Mathf.Max(duration, 0.1f), longest, _cancel.Token).Forget();
        }

        private async UniTaskVoid Run(float bleedTime, float tail, CancellationToken token)
        {
            var canceled = await UniTask.Delay(TimeSpan.FromSeconds(bleedTime), DelayType.DeltaTime, PlayerLoopTiming.Update, token).SuppressCancellationThrow();
            if (canceled)
            {
                return;
            }

            StopEmitting();
            canceled = await UniTask.Delay(TimeSpan.FromSeconds(tail), DelayType.DeltaTime, PlayerLoopTiming.Update, token).SuppressCancellationThrow();
            if (!canceled)
            {
                Release();
            }
        }

        /// <summary>Stops the blood at once and gives the effect back (what is in the air vanishes with it).</summary>
        public void Stop() => Release();

        private void StopEmitting()
        {
            if (_instance == null)
            {
                return;
            }

            for (var i = 0; i < _systems.Length; i++)
            {
                if (_systems[i] != null)
                {
                    _systems[i].Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
        }

        private void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            if (_cancel != null)
            {
                _cancel.Cancel();
                _cancel.Dispose();
                _cancel = null;
            }

            if (_instance == null)
            {
                return;
            }

            for (var i = 0; i < _systems.Length; i++)
            {
                if (_systems[i] != null)
                {
                    _systems[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }

            _instance.transform.SetParent(null, true);
            _spawner.Release(_instance);
        }
    }
}
