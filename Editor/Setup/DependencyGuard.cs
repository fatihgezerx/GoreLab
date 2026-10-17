using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace GoreLab.Setup
{
    /// <summary>
    /// Keeps GoreLab from ever breaking a project that doesn't have its dependency yet.
    /// </summary>
    /// <remarks>
    /// This assembly references nothing, so it always compiles. Whenever scripts reload or an assembly definition appears or
    /// disappears, it looks for UniTask's assembly definition and sets or clears <c>HAS_UNITASK</c>. GoreLab's own
    /// assemblies list that symbol as a Define Constraint, so while UniTask is missing they are simply left out of
    /// compilation - no errors - and this guard offers to install it through the Package Manager. It also keeps
    /// <c>HAS_GORE_LAB</c> set while GoreLab is in the project, so code that uses it from outside (the Combat System
    /// bridge, say) can be left out of compilation once it's removed. When GoreLab is deleted, the guard clears its own
    /// symbol and sets the shared one to what is still installed, so other systems that need UniTask keep compiling.
    /// </remarks>
    [InitializeOnLoad]
    internal sealed class DependencyGuard : AssetPostprocessor, IActiveBuildTargetChanged
    {
        internal const string SystemName = "GoreLab";
        private const string DeclinedKey = "GoreLab.Setup.DeclinedDependencies";
        private const string SetupAsmdefFile = "GoreLab.Setup.asmdef";
        private const string OwnDefine = "HAS_GORE_LAB";

        /// <summary>Everything GoreLab uses.</summary>
        internal static readonly Dependency[] Dependencies =
        {
            new Dependency("UniTask", "UniTask", "HAS_UNITASK", "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask", "com.cysharp.unitask"),
        };

        /// <summary>Optional systems: only their symbol is kept (set while they are in the project, cleared when they are not); nothing is offered or installed.</summary>
        private static readonly (string Assembly, string Define)[] Optional =
        {
            ("PoolSystem.Runtime", "HAS_POOL_SYSTEM"),
            // The studio-style Inspectors and Gore Studio window (GoreLab.Editor.Studio); without it Unity's plain Inspectors and the IMGUI window are used.
            ("Veneer.Editor", "HAS_VENEER"),
        };

        private static AddAndRemoveRequest _packageRequest;

        static DependencyGuard()
        {
            Events.registeringPackages += OnRegisteringPackages;
            EditorApplication.delayCall += () => Refresh(true);
        }

        public int callbackOrder => 0;

        public void OnActiveBuildTargetChanged(BuildTarget previousTarget, BuildTarget newTarget) => Refresh(false);

        // An assembly definition appeared or disappeared (a package or folder added / deleted): update the
        // symbols right away, during this import, so the compilation that follows already uses them.
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            // GoreLab itself is being deleted: clear its own symbol, and set the shared ones to what is actually
            // installed now (other systems' assemblies need them). Also forget an earlier "Not now", so a fresh copy asks again.
            if (ContainsFile(deleted, SetupAsmdefFile))
            {
                SessionState.EraseString(DeclinedKey);
                var symbols = new Dictionary<string, bool> { [OwnDefine] = false };
                var assemblies = FindAssemblyDefinitions();
                foreach (var (assembly, define) in Optional)
                {
                    symbols[define] = assemblies.ContainsKey(assembly);
                }

                foreach (var dependency in Dependencies)
                {
                    symbols[dependency.Define] = assemblies.ContainsKey(dependency.Assembly);
                }

                ApplyDefines(symbols);
                return;
            }

            // Imported again (e.g. a newer copy): ask again too.
            if (ContainsFile(imported, SetupAsmdefFile))
            {
                SessionState.EraseString(DeclinedKey);
            }

            if (ContainsAsmdef(imported) || ContainsAsmdef(deleted) || ContainsAsmdef(moved))
            {
                Refresh(false);
            }
        }

        // A package is about to be removed: clear its symbol before its code disappears, so nothing
        // tries to compile against it in between.
        private static void OnRegisteringPackages(PackageRegistrationEventArgs args)
        {
            var symbols = new Dictionary<string, bool>();
            foreach (var removed in args.removed)
            {
                foreach (var dependency in Dependencies)
                {
                    if (dependency.PackageName == removed.name)
                    {
                        symbols[dependency.Define] = false;
                    }
                }
            }

            ApplyDefines(symbols);
        }

        /// <summary>Updates every symbol; returns true if anything is missing. Offers to install it when <paramref name="prompt"/>.</summary>
        internal static bool Refresh(bool prompt)
        {
            var assemblies = FindAssemblyDefinitions();

            // GoreLab was deleted, but this code is still loaded: Unity keeps the old scripts while the project has
            // compile errors. Its symbols were cleared when it was deleted, so don't set any of them again.
            if (!assemblies.ContainsKey(Path.GetFileNameWithoutExtension(SetupAsmdefFile)))
            {
                return false;
            }

            var symbols = new Dictionary<string, bool> { [OwnDefine] = true };
            var missing = new List<Dependency>();
            foreach (var (assembly, define) in Optional)
            {
                symbols[define] = assemblies.ContainsKey(assembly);
            }

            foreach (var dependency in Dependencies)
            {
                var present = assemblies.ContainsKey(dependency.Assembly);
                symbols[dependency.Define] = present;
                if (!present)
                {
                    missing.Add(dependency);
                }
            }

            ApplyDefines(symbols);

            if (missing.Count == 0)
            {
                return false;
            }

            // Asked whenever something is missing, unless "Not now" was already picked in this editor session.
            if (prompt && _packageRequest == null && !Application.isBatchMode && !WasDeclined(missing))
            {
                Prompt(missing);
            }

            return true;
        }

        /// <summary>Every assembly definition in Assets and Packages: file name (the assembly name, by convention) -> path.</summary>
        internal static Dictionary<string, string> FindAssemblyDefinitions()
        {
            var result = new Dictionary<string, string>();
            foreach (var guid in AssetDatabase.FindAssets("t:AssemblyDefinitionAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                result[Path.GetFileNameWithoutExtension(path)] = path;
            }

            return result;
        }

        private static void Prompt(List<Dependency> missing)
        {
            var message = new StringBuilder();
            message.Append(SystemName).AppendLine(" needs these packages:").AppendLine();
            foreach (var dependency in missing)
            {
                message.Append("• ").AppendLine(dependency.Name);
            }

            message.AppendLine().Append("Until they are installed, ").Append(SystemName)
                .Append(" is left out of compilation, so the project keeps compiling.");

            if (EditorUtility.DisplayDialog(SystemName, message.ToString(), "Install", "Not now"))
            {
                Install(missing);
            }
            else
            {
                Decline(missing);
            }
        }

        // Not asked about these again in this editor session (see WasDeclined).
        private static void Decline(List<Dependency> dependencies)
        {
            var names = new List<string>();
            foreach (var dependency in dependencies)
            {
                names.Add(dependency.Name);
            }

            SessionState.SetString(DeclinedKey, string.Join("\n", names));
        }

        private static bool WasDeclined(List<Dependency> missing)
        {
            var declined = new HashSet<string>(SessionState.GetString(DeclinedKey, string.Empty).Split('\n'));
            foreach (var dependency in missing)
            {
                if (!declined.Contains(dependency.Name))
                {
                    return false;
                }
            }

            return true;
        }

        private static void Install(List<Dependency> dependencies)
        {
            var packages = new List<string>();
            foreach (var dependency in dependencies)
            {
                packages.Add(dependency.InstallId);
            }

            Debug.Log($"[{SystemName}] Installing: {string.Join(", ", packages)}");
            _packageRequest = Client.AddAndRemove(packages.ToArray());
            EditorApplication.update -= WaitForInstall;
            EditorApplication.update += WaitForInstall;
        }

        private static void WaitForInstall()
        {
            if (_packageRequest == null || !_packageRequest.IsCompleted)
            {
                return;
            }

            if (_packageRequest.Status == StatusCode.Failure)
            {
                Debug.LogError($"[{SystemName}] Couldn't install the packages: {_packageRequest.Error?.message}\n" +
                               "Git URLs need Git installed. You can also add them in Window > Package Manager > + > Add package from git URL.");
            }

            _packageRequest = null;
            EditorApplication.update -= WaitForInstall;
        }

        private static void ApplyDefines(Dictionary<string, bool> symbols)
        {
            if (symbols.Count == 0)
            {
                return;
            }

            var target = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            PlayerSettings.GetScriptingDefineSymbols(target, out var current);

            var defines = new List<string>(current);
            var changed = false;
            foreach (var pair in symbols)
            {
                var has = defines.Contains(pair.Key);
                if (pair.Value && !has)
                {
                    defines.Add(pair.Key);
                    changed = true;
                }
                else if (!pair.Value && has)
                {
                    defines.Remove(pair.Key);
                    changed = true;
                }
            }

            if (changed)
            {
                PlayerSettings.SetScriptingDefineSymbols(target, defines.ToArray());
            }
        }

        private static bool ContainsFile(string[] paths, string fileName)
        {
            foreach (var path in paths)
            {
                if (Path.GetFileName(path) == fileName)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsAsmdef(string[] paths)
        {
            foreach (var path in paths)
            {
                if (path.EndsWith(".asmdef"))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>One package GoreLab uses.</summary>
    internal readonly struct Dependency
    {
        /// <summary>Shown to the user.</summary>
        public readonly string Name;

        /// <summary>Its assembly definition's name; the dependency counts as installed when it exists.</summary>
        public readonly string Assembly;

        /// <summary>Scripting define symbol set while it is installed.</summary>
        public readonly string Define;

        /// <summary>A Package Manager id (package name or git URL).</summary>
        public readonly string InstallId;

        /// <summary>The package name, to notice it being removed.</summary>
        public readonly string PackageName;

        public Dependency(string name, string assembly, string define, string installId, string packageName)
        {
            Name = name;
            Assembly = assembly;
            Define = define;
            InstallId = installId;
            PackageName = packageName;
        }
    }
}
