using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// The setup of a <see cref="GoreBreakable"/>, done once in the editor: it divides one static mesh into pieces (with <see cref="GoreBreakPlanner"/>
    /// and <see cref="GoreBreakBuilder"/>), saves each piece as a mesh asset next to a <see cref="GoreBreakProfile"/> and puts the component on the
    /// object. Nothing of this runs in a build; the meshes are data.
    /// </summary>
    internal static class GoreBreakBaker
    {
        /// <summary>What to set up.</summary>
        internal sealed class Request
        {
            /// <summary>The mesh to divide: the object that has it gets the component.</summary>
            public MeshFilter Filter;

            /// <summary>The renderer that draws the mesh.</summary>
            public MeshRenderer Renderer;

            /// <summary>An existing profile to fill again (its mesh assets are replaced), or null for a new one.</summary>
            public GoreBreakProfile Profile;

            /// <summary>The folder of a new profile and its meshes; empty = <see cref="GoreBaker.DefaultFolder"/>.</summary>
            public string Folder;

            /// <summary>How the mesh is divided.</summary>
            public GoreBreakSettings Settings = new GoreBreakSettings();

            /// <summary>The material of the broken faces; null = the default dark material.</summary>
            public Material Fill;

            /// <summary>Texture tiles per unit of the mesh space across a lid.</summary>
            public float LidUvScale = 1f;
        }

        /// <summary>What the setup did.</summary>
        internal sealed class Result
        {
            public bool Success;
            public string Message = string.Empty;
            public readonly List<string> Warnings = new List<string>();
            public GoreBreakProfile Profile;
            public GoreBreakable Breakable;
        }

        /// <summary>
        /// The data of a mesh for planning and building. Null with <paramref name="problem"/> set when Unity will not give it (in Play mode, or a mesh
        /// the editor refuses anyway: <see cref="MakeReadable"/> switches Read/Write on in its model).
        /// </summary>
        internal static GoreMeshData ReadSource(Mesh mesh, out string problem)
        {
            problem = null;
            if (mesh == null)
            {
                problem = "There is no mesh.";
                return null;
            }

            // Unity lets the editor read any mesh outside Play mode (as the character setup does), so Read/Write is only asked for when it refuses.
            if (!mesh.isReadable && Application.isPlaying)
            {
                problem = "The mesh '" + mesh.name + "' is not readable in Play mode: leave Play mode, or tick Read/Write in the Model tab of the Import Settings of its model.";
                return null;
            }

            try
            {
                return GoreRuntimeMeshes.Read(mesh);
            }
            catch (InvalidOperationException exception)
            {
                problem = "Unity refused to give the editor the data of the mesh '" + mesh.name + "': " + exception.Message;
                return null;
            }
        }

        /// <summary>Switches Read/Write on in the model a mesh comes from (the pieces are saved as assets, so the original does not need it at run time). False when the mesh is not from a model file.</summary>
        internal static bool MakeReadable(Mesh mesh)
        {
            var path = AssetDatabase.GetAssetPath(mesh);
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer))
            {
                return false;
            }

            if (!importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }

            return true;
        }

        /// <summary>
        /// Sets the object up. Never throws: what went wrong is in <see cref="Result.Message"/>, what is only doubtful in <see cref="Result.Warnings"/>. A
        /// profile that was given is filled again and its mesh assets are replaced, so every object that uses it keeps its reference to the profile.
        /// </summary>
        internal static Result Bake(Request request)
        {
            var result = new Result();
            try
            {
                return Run(request, result);
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
            if (request.Filter == null || request.Renderer == null || request.Filter.sharedMesh == null)
            {
                result.Message = "Pick an object with a mesh (a Mesh Filter and a Mesh Renderer) first.";
                return result;
            }

            var source = ReadSource(request.Filter.sharedMesh, out var problem);
            if (source == null)
            {
                result.Message = problem;
                return result;
            }

            var settings = request.Settings;
            var plan = GoreBreakPlanner.Plan(source, settings);
            result.Warnings.AddRange(plan.Notes);
            var built = GoreBreakBuilder.Build(plan.Mesh, plan.Owner, plan.PieceCount, Mathf.Max(0.01f, request.LidUvScale), 0.05f, plan.Through, plan.PlanarLids ? plan.AxisUsed : GoreBreakAxis.Auto);
            result.Warnings.AddRange(built.Warnings);
            var usable = 0;
            foreach (var piece in built.Pieces)
            {
                if (piece != null)
                {
                    usable++;
                }
            }

            if (usable < 2)
            {
                result.Message = "The mesh came out as fewer than two pieces: nothing would break.";
                return result;
            }

            var gameObject = request.Filter.gameObject;
            var folder = string.IsNullOrEmpty(request.Folder) ? GoreBaker.DefaultFolder : request.Folder;
            var profile = request.Profile;
            if (profile == null)
            {
                GoreBaker.EnsureFolder(folder);
                profile = ScriptableObject.CreateInstance<GoreBreakProfile>();
                var profilePath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{GoreBaker.Safe(gameObject.name)} Gore Break Profile.asset");
                AssetDatabase.CreateAsset(profile, profilePath);
            }

            var profilePathNow = AssetDatabase.GetAssetPath(profile);
            var meshFolder = Path.GetDirectoryName(profilePathNow).Replace('\\', '/') + "/" + profile.name + " Meshes";
            GoreBaker.EnsureFolder(meshFolder);

            // The meshes of an earlier setup are deleted and made again: rewriting a mesh Unity has already loaded leaves its drawing data at the old size.
            if (profile.Pieces != null)
            {
                foreach (var old in profile.Pieces)
                {
                    if (old != null && old.Mesh != null && AssetDatabase.Contains(old.Mesh))
                    {
                        AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(old.Mesh));
                    }
                }
            }

            var pieces = new List<GoreBreakPiece>(usable);
            AssetDatabase.StartAssetEditing();
            try
            {
                var prefix = $"{meshFolder}/{GoreBaker.Safe(request.Filter.sharedMesh.name)}";
                for (var i = 0; i < built.Pieces.Length; i++)
                {
                    var arrays = built.Pieces[i];
                    if (arrays == null)
                    {
                        continue;
                    }

                    EditorUtility.DisplayProgressBar("GoreLab", "Saving piece " + (i + 1), (float)i / built.Pieces.Length);
                    var number = pieces.Count + 1;
                    var name = i == built.RestPiece ? "Rest" : "Piece " + number.ToString("00");
                    var mesh = GoreRuntimeMeshes.BuildStatic(arrays.Part, request.Filter.sharedMesh.name + " " + name);
                    AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath($"{prefix} {name}.asset"));
                    pieces.Add(new GoreBreakPiece
                    {
                        Name = name,
                        Mesh = mesh,
                        Materials = arrays.Part.SubmeshSource,
                        Center = arrays.Center,
                        Size = arrays.Bounds.size,
                        Volume = arrays.Volume
                    });
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            Undo.RecordObject(profile, "Gore break setup");
            profile.SourceMesh = request.Filter.sharedMesh;
            profile.Pieces = pieces.ToArray();
            profile.Closed = built.Closed;
            profile.FillMaterial = request.Fill != null ? request.Fill : GoreBaker.DefaultCapMaterial();
            profile.Mode = settings.Mode;
            profile.Shape = settings.Shape;
            profile.Axis = settings.Axis;
            profile.RandomPieces = Mathf.Max(2, settings.RandomPieces);
            profile.RandomSeed = settings.RandomSeed;
            profile.ExtraTriangles = Mathf.Max(0, settings.ExtraTriangles);
            profile.ExtraSelection = SelectedNumbers(settings.ExtraSelection);
            profile.ManualOwners = settings.Mode == GoreBreakMode.Manual && settings.ManualOwners != null ? (int[])settings.ManualOwners.Clone() : Array.Empty<int>();
            profile.LidUvScale = Mathf.Max(0.01f, request.LidUvScale);
            EditorUtility.SetDirty(profile);

            var component = gameObject.GetComponent<GoreBreakable>();
            if (component == null)
            {
                component = Undo.AddComponent<GoreBreakable>(gameObject);
            }

            var serialized = new SerializedObject(component);
            serialized.FindProperty("_profile").objectReferenceValue = profile;
            serialized.FindProperty("_filter").objectReferenceValue = request.Filter;
            serialized.FindProperty("_renderer").objectReferenceValue = request.Renderer;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            GoreBaker.MarkDirty(component);
            AssetDatabase.SaveAssets();

            result.Success = true;
            result.Profile = profile;
            result.Breakable = component;
            result.Message = $"'{gameObject.name}' breaks into {pieces.Count} pieces" + (built.Closed ? "." : " (thin shards: the mesh is not closed, so the broken edges have no lids).");
            return result;
        }

        // The numbers of the triangles that are ticked, to keep the choice in the profile.
        private static int[] SelectedNumbers(bool[] selected)
        {
            if (selected == null)
            {
                return Array.Empty<int>();
            }

            var list = new List<int>();
            for (var i = 0; i < selected.Length; i++)
            {
                if (selected[i])
                {
                    list.Add(i);
                }
            }

            return list.ToArray();
        }
    }
}
