using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GoreLab
{
    /// <summary>One skinned mesh a character can wear, and whether Unity lets the run time read it (the Read/Write switch of its model).</summary>
    internal sealed class GoreWornMesh
    {
        public Mesh Mesh;
        public string AssetPath;

        /// <summary>The import settings that hold the Read/Write switch, or null when the mesh is not made by a model importer (it cannot be changed here).</summary>
        public ModelImporter Importer;

        public bool Readable;
    }

    /// <summary>
    /// Finds the skinned meshes of whatever a character can wear, from what the user drops in the Gore Studio: a prefab, a folder of prefabs, or
    /// any ScriptableObject that has fields marked with <see cref="GoreWornAttribute"/> (the fields name the equipment; everything else in the
    /// asset is left alone). Knows nothing about any clothing package. The worn meshes need Read/Write because they are split at run time; this
    /// lists the ones that lack it and can switch it on.
    /// </summary>
    internal static class GoreWornScan
    {
        private const int MaxDepth = 6;
        private const BindingFlags FieldFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Every skinned mesh found in <paramref name="sources"/>, each once, by name. <paramref name="notes"/> says what was dropped that gave nothing.
        /// <paramref name="prefabs"/>, when given, is filled with the prefabs that carry those meshes (each once): what the studio puts on the character.
        /// </summary>
        internal static List<GoreWornMesh> Collect(IReadOnlyList<Object> sources, List<string> notes, List<GameObject> prefabs = null)
        {
            var scan = new Scan { Notes = notes, Prefabs = prefabs };
            notes.Clear();
            prefabs?.Clear();
            for (var i = 0; i < sources.Count; i++)
            {
                scan.Visit(sources[i], 0, true);
            }

            var result = new List<GoreWornMesh>(scan.Found.Values);
            result.Sort((a, b) => string.CompareOrdinal(a.Mesh.name, b.Mesh.name));
            return result;
        }

        private sealed class Scan
        {
            public readonly Dictionary<int, GoreWornMesh> Found = new Dictionary<int, GoreWornMesh>();
            public List<string> Notes;
            public List<GameObject> Prefabs;
            private readonly HashSet<int> _visited = new HashSet<int>();

            // A dropped or referenced asset. <paramref name="dropped"/> is true for what the user dropped (a data asset with nothing marked is worth a note).
            public void Visit(Object asset, int depth, bool dropped)
            {
                if (asset == null || depth > MaxDepth || !_visited.Add(asset.GetInstanceID()))
                {
                    return;
                }

                switch (asset)
                {
                    case GameObject go:
                        var skinned = false;
                        foreach (var renderer in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        {
                            Add(renderer.sharedMesh);
                            skinned |= renderer.sharedMesh != null;
                        }

                        if (skinned && Prefabs != null && !Prefabs.Contains(go))
                        {
                            Prefabs.Add(go);
                        }

                        break;
                    case Component component:
                        Visit(component.gameObject, depth, dropped);
                        break;
                    case Mesh mesh:
                        Add(mesh);
                        break;
                    case DefaultAsset _:
                        VisitFolder(AssetDatabase.GetAssetPath(asset), depth);
                        break;
                    case ScriptableObject data:
                        var before = Found.Count;
                        var marked = WalkFields(data, depth + 1);
                        if (dropped && !marked)
                        {
                            Notes.Add("'" + data.name + "' has no field marked with [GoreWorn], so nothing was taken from it.");
                        }
                        else if (dropped && Found.Count == before)
                        {
                            Notes.Add("'" + data.name + "' has [GoreWorn] fields but they refer to no skinned mesh.");
                        }

                        break;
                }
            }

            // Every prefab in a folder (and below).
            private void VisitFolder(string path, int depth)
            {
                if (string.IsNullOrEmpty(path) || !AssetDatabase.IsValidFolder(path))
                {
                    return;
                }

                foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { path }))
                {
                    Visit(AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid)), depth + 1, false);
                }
            }

            // The fields of <owner> with the attribute are read for what they refer to; a field with a class or a list of classes is looked into for
            // marked fields of its own. Returns whether any marked field was seen.
            private bool WalkFields(object owner, int depth)
            {
                if (owner == null || depth > MaxDepth)
                {
                    return false;
                }

                var marked = false;
                for (var type = owner.GetType(); type != null && type != typeof(object) && type != typeof(ScriptableObject) && type != typeof(Object); type = type.BaseType)
                {
                    foreach (var field in type.GetFields(FieldFlags | BindingFlags.DeclaredOnly))
                    {
                        if (field.IsStatic || field.IsNotSerialized || (!field.IsPublic && !field.IsDefined(typeof(SerializeField), true)))
                        {
                            continue;
                        }

                        var value = field.GetValue(owner);
                        if (value == null)
                        {
                            continue;
                        }

                        if (field.IsDefined(typeof(GoreWornAttribute), true))
                        {
                            marked = true;
                            TakeEverything(value, depth + 1);
                        }
                        else if (!(value is Object))
                        {
                            marked |= LookInto(value, depth + 1);
                        }
                    }
                }

                return marked;
            }

            // A field without the attribute that holds a serializable class, or a list of them: it may hold marked fields.
            private bool LookInto(object value, int depth)
            {
                if (value is string || value.GetType().IsPrimitive || value.GetType().IsEnum)
                {
                    return false;
                }

                if (value is IEnumerable list)
                {
                    var marked = false;
                    foreach (var item in list)
                    {
                        if (item != null && item is Object == false)
                        {
                            marked |= LookInto(item, depth);
                        }
                    }

                    return marked;
                }

                return Attribute.IsDefined(value.GetType(), typeof(SerializableAttribute)) && WalkFields(value, depth);
            }

            // What a marked field holds: an object reference, a list of them, or a class whose object references all count.
            private void TakeEverything(object value, int depth)
            {
                if (value == null || depth > MaxDepth)
                {
                    return;
                }

                if (value is Object asset)
                {
                    Visit(asset, depth, false);
                    return;
                }

                if (value is string || value.GetType().IsPrimitive || value.GetType().IsEnum)
                {
                    return;
                }

                if (value is IEnumerable list)
                {
                    foreach (var item in list)
                    {
                        TakeEverything(item, depth + 1);
                    }

                    return;
                }

                if (!Attribute.IsDefined(value.GetType(), typeof(SerializableAttribute)))
                {
                    return;
                }

                for (var type = value.GetType(); type != null && type != typeof(object); type = type.BaseType)
                {
                    foreach (var field in type.GetFields(FieldFlags | BindingFlags.DeclaredOnly))
                    {
                        if (!field.IsStatic && !field.IsNotSerialized && (field.IsPublic || field.IsDefined(typeof(SerializeField), true)))
                        {
                            TakeEverything(field.GetValue(value), depth + 1);
                        }
                    }
                }
            }

            private void Add(Mesh mesh)
            {
                if (mesh == null || Found.ContainsKey(mesh.GetInstanceID()))
                {
                    return;
                }

                var path = AssetDatabase.GetAssetPath(mesh);
                Found.Add(mesh.GetInstanceID(), new GoreWornMesh
                {
                    Mesh = mesh,
                    AssetPath = path,
                    Importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as ModelImporter,
                    Readable = mesh.isReadable
                });
            }
        }

        /// <summary>Switches Read/Write on for the models of the meshes that lack it (one reimport per model). Returns how many models were changed.</summary>
        internal static int MakeReadable(IReadOnlyList<GoreWornMesh> meshes)
        {
            var models = new HashSet<ModelImporter>();
            foreach (var mesh in meshes)
            {
                if (!mesh.Readable && mesh.Importer != null)
                {
                    models.Add(mesh.Importer);
                }
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var importer in models)
                {
                    importer.isReadable = true;
                    importer.SaveAndReimport();
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            return models.Count;
        }
    }
}
