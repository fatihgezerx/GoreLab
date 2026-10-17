using UnityEditor;
using UnityEngine;
using CutSpec = GoreLab.GoreBaker.CutSpec;
using SourceSpec = GoreLab.GoreBaker.SourceSpec;

namespace GoreLab
{
    /// <summary>
    /// Sets the studio's character up for real. The studio shows a working copy, so the request is rebuilt on the thing the user dropped in:
    /// a prefab asset is opened without the scene (<see cref="PrefabUtility.LoadPrefabContents"/>), set up and saved; a scene object is set
    /// up where it stands. Bones and renderers are found again by their path from the character's root.
    /// </summary>
    internal static class GoreStudioApply
    {
        /// <summary>Sets <paramref name="model"/>'s character up. Never throws: the outcome is in the result.</summary>
        internal static GoreBaker.Result Apply(GoreStudioModel model)
        {
            var result = new GoreBaker.Result();
            var source = model.Source;
            if (source == null || model.Instance == null)
            {
                result.Message = "Drop a character into the Target field first.";
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

                var request = BuildRequest(model, root);
                if (request == null)
                {
                    result.Message = "The character in the studio no longer matches '" + source.name + "' (a bone or a mesh was renamed, moved or removed). Drop it in again.";
                    return result;
                }

                result = GoreBaker.Bake(request);
                if (result.Success && opened)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, assetPath, out var saved);
                    if (!saved)
                    {
                        result.Success = false;
                        result.Message = "The parts were made, but Unity could not save the prefab '" + assetPath + "' (is it read-only, or inside a package?).";
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

        // The studio's choices, with every bone and renderer taken from the real character instead of the working copy. Null when one is missing.
        private static GoreBaker.Request BuildRequest(GoreStudioModel model, GameObject root)
        {
            var existing = root.GetComponent<GoreCharacter>();
            var request = new GoreBaker.Request
            {
                Character = root,
                Profile = existing != null ? existing.Profile : null,
                Folder = GoreBaker.DefaultFolder,
                CapMaterial = null,
                ClothRules = model.ClothRulesToSave()
            };

            foreach (var row in model.Rows)
            {
                if (!row.Cut || row.Renderer == null)
                {
                    continue;
                }

                var mapped = Map(row.Renderer.transform, model.Instance.transform, root.transform);
                var renderer = mapped != null ? mapped.GetComponent<SkinnedMeshRenderer>() : null;
                if (renderer == null)
                {
                    return null;
                }

                request.Sources.Add(new SourceSpec { Renderer = renderer, Lids = row.Lids, Overrides = row.Edit != null ? row.Edit.ToOverrides(model.Cuts) : null });
            }

            foreach (var cut in model.Cuts)
            {
                var bone = Map(cut.Bone, model.Instance.transform, root.transform);
                if (bone == null)
                {
                    return null;
                }

                request.Cuts.Add(new CutSpec { Name = cut.Name, Limb = cut.Limb, Bone = bone, Fill = cut.Fill });
            }

            return request;
        }

        // The transform at the same place of another copy of the hierarchy: found by the sibling index of every step (names can repeat), and
        // only accepted when the names agree, so a renamed or moved bone is noticed instead of taken for another one.
        internal static Transform Map(Transform transform, Transform from, Transform to)
        {
            if (transform == null)
            {
                return null;
            }

            if (transform == from)
            {
                return to;
            }

            var steps = new System.Collections.Generic.List<Transform>();
            for (var t = transform; t != null && t != from; t = t.parent)
            {
                steps.Add(t);
            }

            var current = to;
            for (var i = steps.Count - 1; i >= 0; i--)
            {
                var index = steps[i].GetSiblingIndex();
                if (index >= current.childCount)
                {
                    return null;
                }

                current = current.GetChild(index);
                if (current.name != steps[i].name)
                {
                    return null;
                }
            }

            return current;
        }
    }
}
