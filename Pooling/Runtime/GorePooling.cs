using PoolSystem;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// Connects the GoreLab to the Pool System: the blood effects come from a pool instead of <c>Instantiate</c> and go back to it
    /// instead of being destroyed. Nothing to add to a character or a scene; it takes over <see cref="GoreBlood.Spawner"/> by itself.
    /// </summary>
    /// <remarks>
    /// A prefab is pooled when it is in the project's Pool Data (so it has a <c>Poolable</c> and a pool type) and the Pool Manager was
    /// initialized; every other prefab is instantiated and destroyed as usual. Delete the <c>Pooling</c> folder of the GoreLab and the
    /// blood works the same, only without a pool.
    /// </remarks>
    public static class GorePooling
    {
        private sealed class PoolSpawner : IGoreSpawner
        {
            public GameObject Get(GameObject prefab, Vector3 position, Quaternion rotation)
            {
                if (PoolManager.IsInitialized && prefab.TryGetComponent<Poolable>(out var poolable) && PoolManager.IsRegistered(poolable.PoolType))
                {
                    return PoolManager.Get(poolable.PoolType, position, rotation);
                }

                return Object.Instantiate(prefab, position, rotation);
            }

            public void Release(GameObject instance)
            {
                if (instance != null && !PoolManager.TryRelease(instance))
                {
                    Object.Destroy(instance);
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => GoreBlood.Spawner = new PoolSpawner();
    }
}
