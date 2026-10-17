using UnityEditor;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// Sets one static mesh of the studio's object up for real. The studio shows a working copy, so the request is rebuilt on the thing the user
    /// dropped in: a prefab asset is opened without the scene (<see cref="PrefabUtility.LoadPrefabContents"/>), set up and saved; a scene object is
    /// set up where it stands. The mesh object is found again by the sibling index of every step from the root.
    /// </summary>
    internal static class GoreStaticApply
    {
        /// <summary>Sets the mesh of <paramref name="setup"/> up. Never throws: the outcome is in the result.</summary>
        internal static GoreBreakBaker.Result Apply(GoreStudioStatic state, GameObject source, GameObject instance)
        {
            var result = new GoreBreakBaker.Result();
            var setup = state.Current;
            if (source == null || instance == null || setup == null)
            {
                result.Message = "Drop an object into the Target field first.";
                return result;
            }

            GameObject root = null;
            var assetPath = string.Empty;
            var opened = false;
            try
            {
                if (EditorUtility.IsPersistent(source))
                {
                    assetPath = AssetDatabase.GetAssetPath(source);
                    var type = PrefabUtility.GetPrefabAssetType(source);
                    if (type == PrefabAssetType.Model || type == PrefabAssetType.NotAPrefab || type == PrefabAssetType.MissingAsset)
                    {
                        result.Message = "'" + source.name + "' is a model file, not a prefab: parts cannot be saved into it. Drag the model into a scene, save it as a prefab, and drop that prefab here.";
                        return result;
                    }

                    root = PrefabUtility.LoadPrefabContents(assetPath);
                    opened = true;
                }
                else
                {
                    root = source;
                }

                var request = BuildRequest(state, setup, instance, root);
                if (request == null)
                {
                    result.Message = "The mesh in the studio no longer matches '" + source.name + "' (an object or its mesh was renamed, moved or changed). Drop it in again.";
                    return result;
                }

                result = GoreBreakBaker.Bake(request);
                if (result.Success)
                {
                    // The numbers that live on the profile itself.
                    var profile = result.Profile;
                    Undo.RecordObject(profile, "Gore break setup");
                    profile.Density = Mathf.Max(0.1f, setup.Density);
                    profile.DespawnTime = Mathf.Max(0f, setup.DespawnTime);
                    profile.Spin = Mathf.Max(0f, setup.Spin);
                    EditorUtility.SetDirty(profile);
                    if (opened)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, assetPath, out var saved);
                        if (!saved)
                        {
                            result.Success = false;
                            result.Message = "The pieces were made, but Unity could not save the prefab '" + assetPath + "' (is it read-only, or inside a package?).";
                        }
                    }
                }

                return result;
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception);
                result.Success = false;
                result.Message = "The setup failed: " + exception.Message;
                return result;
            }
            finally
            {
                if (opened && root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        // The studio's choices on the real object. Null when the mesh object cannot be found again, or its mesh is not the one that was shown.
        private static GoreBreakBaker.Request BuildRequest(GoreStudioStatic state, GoreStaticSetup setup, GameObject instance, GameObject root)
        {
            var mapped = GoreStudioApply.Map(setup.Filter.transform, instance.transform, root.transform);
            if (mapped == null || !mapped.TryGetComponent(out MeshFilter filter) || !mapped.TryGetComponent(out MeshRenderer renderer) || filter.sharedMesh != setup.Mesh)
            {
                return null;
            }

            // The profile this object already has is filled again (its mesh assets are replaced) when it was made for this mesh; another mesh's is left alone.
            var existing = mapped.GetComponent<GoreBreakable>();
            var profile = existing != null ? existing.Profile : null;
            if (profile != null && profile.SourceMesh != null && profile.SourceMesh != setup.Mesh)
            {
                profile = null;
            }

            return new GoreBreakBaker.Request
            {
                Filter = filter,
                Renderer = renderer,
                Profile = profile,
                Folder = GoreBaker.DefaultFolder,
                Settings = state.BuildSettings(),
                Fill = setup.Fill,
                LidUvScale = Mathf.Max(0.01f, setup.LidUvScale)
            };
        }
    }
}
