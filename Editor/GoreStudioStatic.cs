using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GoreLab
{
    /// <summary>What the user chose for one static mesh (a Mesh Filter with a Mesh Renderer) of the dropped object. Plain data: kept while the studio is open.</summary>
    internal sealed class GoreStaticSetup
    {
        /// <summary>The filter and renderer of the studio's working copy.</summary>
        public MeshFilter Filter;

        public MeshRenderer Renderer;

        /// <summary>The sibling-index path of the filter's object from the root (the same in the real object), and its label for the Mesh popup.</summary>
        public string Key = string.Empty;

        public GUIContent Label = GUIContent.none;

        /// <summary>The mesh as it was found.</summary>
        public Mesh Mesh;

        /// <summary>Something to tell about this mesh (its profile belongs to another mesh, and so on).</summary>
        public string Note = string.Empty;

        public GoreBreakMode Mode = GoreBreakMode.Manual;

        /// <summary>Pieces that are patches of the surface, or columns through the object; and the way a Through division goes. Set from the mesh the first time it is read, unless a profile gave them.</summary>
        public GoreBreakShape Shape = GoreBreakShape.Surface;

        public GoreBreakAxis Axis = GoreBreakAxis.Auto;
        public bool ShapeChosen;
        public int RandomPieces = 8;
        public int RandomSeed = 1;

        /// <summary>The number in the field, and the number that the preview (and so the setup) uses; they differ until Preview is pressed.</summary>
        public int ExtraTriangles;

        public int CommittedExtra;

        /// <summary>The triangles of the source (flat numbers) that may be split for the extra ones; null = every triangle.</summary>
        public bool[] CommittedSelection;

        /// <summary>Manual: the owner of every triangle of the finer mesh (null = nothing drawn yet), and how many pieces there are.</summary>
        public int[] ManualOwners;

        public int ManualPieces;
        public int SelectedPiece = -1;

        public Material Fill;
        public float LidUvScale = 1f;
        public float Density = 600f;
        public float DespawnTime = 25f;
        public float Spin = 1.5f;
    }

    /// <summary>
    /// The state of the Gore Studio for a static mesh: the meshes of the dropped object and what was chosen for each, and, for the one in the preview,
    /// its data, the plan (the finer mesh and the owner of every triangle, made by <see cref="GoreBreakPlanner"/> so the preview is what is saved), the
    /// selection structure and the coloured meshes of the preview. No IMGUI in here. Small meshes are planned at once; big ones on a worker thread,
    /// after a short wait while the user is still moving a slider.
    /// </summary>
    internal sealed class GoreStudioStatic : IDisposable
    {
        /// <summary>Above this many triangles the plan is made on a worker thread (below it, it is faster than the wait).</summary>
        internal const int SyncTriangleLimit = 6000;

        /// <summary>How long a slider has to rest before the division of a big mesh starts.</summary>
        internal const double DebounceSeconds = 0.2;

        /// <summary>The most triangles the field "More triangles" takes.</summary>
        internal const int MaxExtraTriangles = 100000;

        /// <summary>Random: the fewest pieces.</summary>
        internal const int MinRandomPieces = 2;

        /// <summary>The fewest pieces a mesh needs to break (the setup refuses fewer).</summary>
        internal const int MinPieces = 2;

        // A fixed light for the flat shading, the same as the body parts have.
        private static readonly Vector3 ShadeLight = new Vector3(0.35f, 0.7f, 0.62f).normalized;

        // The triangles that were added are tinted this way, and their edges drawn in this colour.
        private static readonly Color32 NewTint = new Color32(255, 214, 64, 255);
        private static readonly Color32 NewEdgeColor = new Color32(255, 232, 110, 255);
        private static readonly Color32 WireColor = new Color32(10, 10, 12, 110);
        private const float NewTintAmount = 0.55f;

        // What is not the chosen piece is dimmed, like the parts of a character that are not the chosen cut.
        private const float DimmedShade = 0.32f;
        private const float DimmedOpacity = 0.75f;

        private static readonly int[] NoOwners = Array.Empty<int>();
        private static readonly string NeedPiecesText = "Make at least " + MinPieces + " pieces: select faces in Edit mode and press New piece.";
        private static readonly string TooFewTrianglesText = "The mesh has too few triangles to divide: add triangles.";

        private readonly Action _repaint;
        private readonly List<GoreStaticSetup> _setups = new List<GoreStaticSetup>();
        private readonly Dictionary<string, GoreStaticSetup> _remembered = new Dictionary<string, GoreStaticSetup>();
        private GameObject _source;
        private GameObject _instance;
        private string _activeKey = string.Empty;

        // The worker.
        private sealed class Job
        {
            internal int Generation;
            internal bool IsExtra;
            internal GoreMeshData Data;
            internal GoreBreakSettings Settings;
            internal GoreBreakPlan Plan;
            internal string Error;

            internal Job Run()
            {
                try
                {
                    Plan = GoreBreakPlanner.Plan(Data, Settings);
                }
                catch (Exception exception)
                {
                    Error = exception.Message;
                }

                return this;
            }
        }

        private Task<Job> _task;
        private int _extraGeneration;
        private int _divideGeneration;
        private bool _extraPending;
        private bool _dividePending;
        private double _requestTime;
        private bool _subscribed;

        private readonly List<string> _extraNotes = new List<string>();
        private readonly List<string> _divideNotes = new List<string>();
        private string _manualNote = string.Empty;

        private Color32[] _soupColors = Array.Empty<Color32>();
        private float[] _shade = Array.Empty<float>();
        private Color32[] _palette = Array.Empty<Color32>();
        private bool[] _faces = Array.Empty<bool>();
        private int _faceVersion = -1;
        private readonly StringBuilder _text = new StringBuilder(128);

        /// <summary>Creates the state; <paramref name="repaint"/> is called when something finished on its own (a plan made on the worker).</summary>
        internal GoreStudioStatic(Action repaint)
        {
            _repaint = repaint;
        }

        #region State

        /// <summary>The meshes of the dropped object that can be set up.</summary>
        internal IReadOnlyList<GoreStaticSetup> Setups => _setups;

        /// <summary>The names for the Mesh popup (one per setup).</summary>
        internal string[] MeshNames = Array.Empty<string>();

        /// <summary>The number of the mesh in the preview.</summary>
        internal int ActiveIndex { get; private set; }

        /// <summary>The setup of the mesh in the preview, or null when the object has none.</summary>
        internal GoreStaticSetup Current => ActiveIndex >= 0 && ActiveIndex < _setups.Count ? _setups[ActiveIndex] : null;

        /// <summary>Whether the object has a mesh to set up.</summary>
        internal bool HasSetups => _setups.Count > 0;

        /// <summary>What stops the mesh from being used at all (not readable, no triangles, a plan that failed), or null.</summary>
        internal string Problem { get; private set; }

        /// <summary>Whether the problem is that the editor cannot read the mesh (so the Make readable button is offered).</summary>
        internal bool CanMakeReadable { get; private set; }

        /// <summary>The source mesh as plain arrays (object space) and the number of its triangles.</summary>
        internal GoreMeshData SourceData { get; private set; }

        internal int SourceTriangles { get; private set; }

        /// <summary>Whether the surface is closed (a crate, a table top) so the broken edges get lids, or open (a wall).</summary>
        internal bool Closed { get; private set; }

        /// <summary>The finer mesh the pieces are cut from (the source when no triangles are added), the flat corners of it, and which triangles are new.</summary>
        internal GoreMeshData Working { get; private set; }

        internal int[] Corners { get; private set; } = NoOwners;
        internal bool[] IsNew { get; private set; } = Array.Empty<bool>();
        internal int TriangleCount { get; private set; }
        internal int Added { get; private set; }

        /// <summary>
        /// The mesh that is drawn, coloured and picked, with its flat corners, new-triangle flags and world positions. It is <see cref="Working"/>, except for a
        /// Random division that goes Through: that plan cuts the triangles along the borders, so its mesh is a finer one. <see cref="Owner"/> is per triangle of this mesh.
        /// </summary>
        internal GoreMeshData Display { get; private set; }

        internal int[] DisplayCorners { get; private set; } = NoOwners;
        internal bool[] DisplayIsNew { get; private set; } = Array.Empty<bool>();
        internal int DisplayTriangleCount { get; private set; }
        internal Vector3[] DisplayWorld { get; private set; } = Array.Empty<Vector3>();

        /// <summary>The lines of every edge of the mesh that is drawn.</summary>
        internal Mesh WireMesh => _displayWire != null ? _displayWire : (Edit != null ? Edit.Wire : null);

        private Mesh _displayWire;
        private Matrix4x4 _matrix = Matrix4x4.identity;

        /// <summary>The piece of every triangle of <see cref="Display"/> (-1 = the rest), and how many pieces there are.</summary>
        internal int[] Owner { get; private set; } = NoOwners;

        internal int OwnerPieces { get; private set; }

        /// <summary>Per piece the number of triangles; the last entry is the rest. And the same as text, for the list.</summary>
        internal int[] Counts { get; private set; } = new int[1];

        internal string[] CountTexts { get; private set; } = { "0" };

        /// <summary>The selection structure of <see cref="Working"/> in the world of the preview, and the world position of every vertex.</summary>
        internal GoreStudioMeshEdit Edit { get; private set; }

        /// <summary>Changes whenever anything that is drawn changed (the working mesh, the owners).</summary>
        internal int Serial { get; private set; }

        /// <summary>Changes only when the working mesh and its selection structure were replaced (the selection, and its undo steps, are gone then).</summary>
        internal int MeshSerial { get; private set; }

        /// <summary>The preview's flat-coloured mesh (world space, no index sharing) and the lines of the new triangles.</summary>
        internal Mesh SoupMesh { get; private set; }

        internal Mesh NewEdgeMesh { get; private set; }

        /// <summary>Whether the colours have to be written again (done by <see cref="Recolor"/>).</summary>
        internal bool ColorsDirty { get; set; }

        /// <summary>The alpha of the coloured mesh: 255 is opaque, lower is the X-ray view.</summary>
        internal byte Opacity { get; set; } = 255;

        /// <summary>Whether the new triangles are tinted and their edges drawn: after Preview, and while the extra triangles are being worked on.</summary>
        internal bool ShowNew { get; private set; }

        /// <summary>A plan is being made on the worker thread.</summary>
        internal bool Busy => _task != null;

        /// <summary>Whether a mesh is ready in the preview.</summary>
        internal bool HasSoup => SoupMesh != null;

        /// <summary>What the planner and the pieces noticed (a count that was raised, pieces that no longer fit).</summary>
        internal readonly List<string> Notes = new List<string>();

        /// <summary>The faces (all three vertices selected) of the working mesh, counted for the current selection.</summary>
        internal int FaceCount { get; private set; }

        /// <summary>The lines of text of the cards, kept so a repaint builds none.</summary>
        internal string TrianglesText = string.Empty;

        internal string PiecesText = string.Empty;
        internal string ExtraHint = string.Empty;
        internal string FacesText = string.Empty;

        /// <summary>Whether the field "More triangles" differs from what the preview shows (press Preview).</summary>
        internal bool ExtraStale
        {
            get
            {
                var setup = Current;
                return setup != null && setup.ExtraTriangles != setup.CommittedExtra;
            }
        }

        #endregion

        #region Loading

        /// <summary>
        /// Takes the object the studio shows. The same object again does nothing. A new working copy of the same prefab (after Apply) keeps what was
        /// chosen for each mesh; another prefab starts clean.
        /// </summary>
        internal void Attach(GameObject source, GameObject instance)
        {
            if (instance == null || instance == _instance)
            {
                return;
            }

            var keep = Current != null ? Current.Key : _activeKey;
            Release();
            if (source != _source)
            {
                _remembered.Clear();
                keep = string.Empty;
            }

            _source = source;
            _instance = instance;
            _setups.Clear();
            var names = new List<string>();
            foreach (var filter in instance.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || !filter.TryGetComponent(out MeshRenderer renderer))
                {
                    continue;
                }

                var key = IndexPath(filter.transform, instance.transform);
                if (!_remembered.TryGetValue(key, out var setup) || setup.Mesh != mesh)
                {
                    setup = NewSetup(filter, mesh);
                    _remembered[key] = setup;
                }

                var path = AnimationUtility.CalculateTransformPath(filter.transform, instance.transform);
                setup.Filter = filter;
                setup.Renderer = renderer;
                setup.Key = key;
                setup.Label = new GUIContent(filter.name, path.Length == 0 ? filter.name : path);
                _setups.Add(setup);
                names.Add(filter.name + " (" + IndexTriangles(mesh).ToString("N0") + " triangles)");
            }

            MeshNames = names.ToArray();
            var start = 0;
            for (var i = 0; i < _setups.Count; i++)
            {
                if (_setups[i].Key == keep)
                {
                    start = i;
                }
            }

            Activate(start);
        }

        /// <summary>Lets go of the object (the studio switched to another kind of target). What was chosen is remembered.</summary>
        internal void Detach()
        {
            if (_instance == null && _setups.Count == 0)
            {
                return;
            }

            _activeKey = Current != null ? Current.Key : _activeKey;
            Release();
            _instance = null;
            _setups.Clear();
            MeshNames = Array.Empty<string>();
            Problem = null;
        }

        /// <summary>Forgets what was chosen for a mesh (after Apply, so it is read back from the profile that was just written).</summary>
        internal void Forget(string key)
        {
            _remembered.Remove(key);
        }

        /// <summary>Puts the mesh with this number in the preview.</summary>
        internal void Activate(int index)
        {
            Release();
            ActiveIndex = _setups.Count == 0 ? 0 : Mathf.Clamp(index, 0, _setups.Count - 1);
            Problem = null;
            CanMakeReadable = false;
            var setup = Current;
            if (setup == null)
            {
                return;
            }

            _activeKey = setup.Key;
            LoadSource(setup);
            if (Problem == null)
            {
                _extraPending = true;
                Pump();
            }
        }

        // A new setup for a mesh: from the profile it already has, or the defaults.
        private static GoreStaticSetup NewSetup(MeshFilter filter, Mesh mesh)
        {
            var setup = new GoreStaticSetup { Mesh = mesh };
            var defaults = ScriptableObject.CreateInstance<GoreBreakProfile>();
            setup.RandomPieces = defaults.RandomPieces;
            setup.RandomSeed = defaults.RandomSeed;
            setup.LidUvScale = defaults.LidUvScale;
            setup.Density = defaults.Density;
            setup.DespawnTime = defaults.DespawnTime;
            setup.Spin = defaults.Spin;
            Object.DestroyImmediate(defaults);

            var breakable = filter.GetComponent<GoreBreakable>();
            var profile = breakable != null ? breakable.Profile : null;
            if (profile == null || profile.PieceCount == 0)
            {
                return setup;
            }

            if (profile.SourceMesh != mesh)
            {
                setup.Note = "Its profile '" + profile.name + "' was made for another mesh, so it is not used: this mesh is set up as new.";
                return setup;
            }

            setup.Mode = profile.Mode;
            setup.Shape = profile.Shape;
            setup.Axis = profile.Axis;
            setup.ShapeChosen = true;
            setup.RandomPieces = Mathf.Max(MinRandomPieces, profile.RandomPieces);
            setup.RandomSeed = profile.RandomSeed;
            setup.ExtraTriangles = Mathf.Clamp(profile.ExtraTriangles, 0, MaxExtraTriangles);
            setup.CommittedExtra = setup.ExtraTriangles;
            setup.Fill = profile.FillMaterial;
            setup.LidUvScale = profile.LidUvScale;
            setup.Density = profile.Density;
            setup.DespawnTime = profile.DespawnTime;
            setup.Spin = profile.Spin;
            var triangles = IndexTriangles(mesh);
            if (setup.CommittedExtra > 0 && profile.ExtraSelection != null && profile.ExtraSelection.Length > 0)
            {
                var selection = new bool[triangles];
                foreach (var number in profile.ExtraSelection)
                {
                    if (number >= 0 && number < triangles)
                    {
                        selection[number] = true;
                    }
                }

                setup.CommittedSelection = selection;
            }

            if (setup.Mode == GoreBreakMode.Manual && profile.ManualOwners != null && profile.ManualOwners.Length > 0)
            {
                setup.ManualOwners = (int[])profile.ManualOwners.Clone();
                setup.ManualPieces = GoreStaticPieces.PieceCountOf(setup.ManualOwners);
            }

            return setup;
        }

        // Reads the mesh of the setup into plain arrays and works out what does not depend on the extra triangles.
        private void LoadSource(GoreStaticSetup setup)
        {
            var data = GoreBreakBaker.ReadSource(setup.Mesh, out var problem);
            if (data == null)
            {
                Problem = problem;
                CanMakeReadable = !Application.isPlaying && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(setup.Mesh)) is ModelImporter;
                return;
            }

            var corners = GoreMeshTopology.Flatten(data, out _);
            if (corners.Length == 0 || data.Vertices.Length == 0)
            {
                Problem = "The mesh '" + setup.Mesh.name + "' has no triangles.";
                return;
            }

            SourceData = data;
            SourceTriangles = corners.Length / 3;
            if (!setup.ShapeChosen)
            {
                // A thin object (a wall, a plank) breaks through; anything else breaks in patches of its surface.
                GorePartitioner.ThinAxis(data, out var thin);
                setup.Shape = thin ? GoreBreakShape.Through : GoreBreakShape.Surface;
                setup.ShapeChosen = true;
            }

            Closed = GoreMeshTopology.IsClosed(corners, GoreMeshTopology.Weld(data.Vertices));
            if (setup.CommittedSelection != null && setup.CommittedSelection.Length != SourceTriangles)
            {
                setup.CommittedSelection = null;
            }
        }

        // Frees what the mesh in the preview made (its settings stay in the setup).
        private void Release()
        {
            _extraGeneration++;
            _divideGeneration++;
            _extraPending = false;
            _dividePending = false;
            var setup = Current;
            if (setup != null && setup.Renderer != null)
            {
                setup.Renderer.enabled = true;
            }

            FreeMeshes();
            Edit?.Dispose();
            Edit = null;
            SourceData = null;
            Working = null;
            Display = null;
            DisplayCorners = NoOwners;
            DisplayIsNew = Array.Empty<bool>();
            DisplayWorld = Array.Empty<Vector3>();
            DisplayTriangleCount = 0;
            Corners = NoOwners;
            IsNew = Array.Empty<bool>();
            Owner = NoOwners;
            OwnerPieces = 0;
            TriangleCount = 0;
            SourceTriangles = 0;
            Added = 0;
            FaceCount = 0;
            _faceVersion = -1;
            _extraNotes.Clear();
            _divideNotes.Clear();
            _manualNote = string.Empty;
            Notes.Clear();
            ShowNew = false;
            Serial++;
            MeshSerial++;
        }

        private void FreeMeshes()
        {
            if (SoupMesh != null)
            {
                Object.DestroyImmediate(SoupMesh);
                SoupMesh = null;
            }

            if (NewEdgeMesh != null)
            {
                Object.DestroyImmediate(NewEdgeMesh);
                NewEdgeMesh = null;
            }

            if (_displayWire != null)
            {
                Object.DestroyImmediate(_displayWire);
                _displayWire = null;
            }
        }

        /// <summary>Frees the meshes and stops the worker's callbacks.</summary>
        public void Dispose()
        {
            Release();
            Unsubscribe();
            _setups.Clear();
            _remembered.Clear();
            _instance = null;
        }

        private static string IndexPath(Transform transform, Transform root)
        {
            var path = string.Empty;
            for (var t = transform; t != null && t != root; t = t.parent)
            {
                path = t.GetSiblingIndex() + (path.Length == 0 ? string.Empty : "/" + path);
            }

            return path;
        }

        private static int IndexTriangles(Mesh mesh)
        {
            var count = 0;
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetSubMesh(s).topology == MeshTopology.Triangles)
                {
                    count += (int)(mesh.GetIndexCount(s) / 3);
                }
            }

            return count;
        }

        #endregion

        #region Planning

        /// <summary>Makes whatever is waiting: the finer mesh, then the division. Called at the start of every layout pass, and by the worker's timer.</summary>
        internal void Pump()
        {
            for (var guard = 0; guard < 4; guard++)
            {
                if (Problem != null || SourceData == null || _task != null)
                {
                    break;
                }

                if (_extraPending)
                {
                    if (RunExtraStage())
                    {
                        continue;
                    }

                    break;
                }

                if (_dividePending)
                {
                    if (RunDivideStage())
                    {
                        continue;
                    }

                    break;
                }

                break;
            }

            if (_task != null || _extraPending || _dividePending)
            {
                Subscribe();
            }
        }

        // The finer mesh. True when it is done (the caller goes on to the next stage), false when it was handed to the worker.
        private bool RunExtraStage()
        {
            _extraPending = false;
            var setup = Current;
            var settings = new GoreBreakSettings
            {
                Mode = GoreBreakMode.Manual,
                ExtraTriangles = setup.CommittedExtra,
                ExtraSelection = setup.CommittedExtra > 0 ? setup.CommittedSelection : null
            };

            if (setup.CommittedExtra <= 0 || SourceTriangles + setup.CommittedExtra <= SyncTriangleLimit)
            {
                try
                {
                    ApplyWorking(GoreBreakPlanner.Plan(SourceData, settings));
                }
                catch (Exception exception)
                {
                    Fail(exception.Message);
                    return false;
                }

                _dividePending = setup.Mode == GoreBreakMode.Random;
                return true;
            }

            StartJob(true, SourceData, settings);
            return false;
        }

        // The division of the finer mesh into random pieces (Manual needs none).
        private bool RunDivideStage()
        {
            var setup = Current;
            _dividePending = false;
            if (setup.Mode != GoreBreakMode.Random || Working == null)
            {
                return true;
            }

            // A Through division cuts the triangles along the borders, so it works on any mesh with a triangle.
            if (TriangleCount < MinRandomPieces && setup.Shape != GoreBreakShape.Through)
            {
                ShowWorking();
                Owner = AllRest(DisplayTriangleCount);
                OwnerPieces = 0;
                _divideNotes.Clear();
                _divideNotes.Add("The mesh has fewer than " + MinRandomPieces + " triangles: add triangles to be able to divide it.");
                OwnerChanged();
                return true;
            }

            var settings = new GoreBreakSettings { Mode = GoreBreakMode.Random, RandomPieces = setup.RandomPieces, RandomSeed = setup.RandomSeed, Shape = setup.Shape, Axis = setup.Axis };
            if (TriangleCount <= SyncTriangleLimit)
            {
                try
                {
                    ApplyDivide(GoreBreakPlanner.Plan(Working, settings));
                }
                catch (Exception exception)
                {
                    Fail(exception.Message);
                    return false;
                }

                return true;
            }

            // A big mesh waits until the slider has rested a moment, then goes to the worker.
            if (EditorApplication.timeSinceStartup - _requestTime < DebounceSeconds)
            {
                _dividePending = true;
                return false;
            }

            StartJob(false, Working, settings);
            return false;
        }

        private void StartJob(bool isExtra, GoreMeshData data, GoreBreakSettings settings)
        {
            var job = new Job { Generation = isExtra ? _extraGeneration : _divideGeneration, IsExtra = isExtra, Data = data, Settings = settings };
            _task = Task.Run(job.Run);
            Subscribe();
        }

        private void Subscribe()
        {
            if (!_subscribed)
            {
                _subscribed = true;
                EditorApplication.update += Tick;
            }
        }

        private void Unsubscribe()
        {
            if (_subscribed)
            {
                _subscribed = false;
                EditorApplication.update -= Tick;
            }
        }

        // Runs while a plan is being made or waiting for the slider to rest; takes itself off the update when there is nothing left.
        private void Tick()
        {
            var serial = Serial;
            var wasBusy = _task != null;
            if (_task != null && _task.IsCompleted)
            {
                Finish();
            }

            Pump();
            if (_task == null && !_extraPending && !_dividePending)
            {
                Unsubscribe();
            }

            if (Serial != serial || wasBusy != (_task != null))
            {
                _repaint();
            }
        }

        private void Finish()
        {
            var task = _task;
            _task = null;
            Job job;
            try
            {
                job = task.Result;
            }
            catch (AggregateException exception)
            {
                Fail(exception.InnerException != null ? exception.InnerException.Message : exception.Message);
                return;
            }

            if (job.Error != null)
            {
                Fail(job.Error);
                return;
            }

            if (job.IsExtra)
            {
                if (job.Generation == _extraGeneration)
                {
                    ApplyWorking(job.Plan);
                    _dividePending = Current.Mode == GoreBreakMode.Random;
                }
            }
            else if (job.Generation == _divideGeneration)
            {
                ApplyDivide(job.Plan);
            }
        }

        private void Fail(string message)
        {
            Problem = "The mesh could not be divided: " + message;
            _extraPending = false;
            _dividePending = false;
            Serial++;
        }

        // The finer mesh arrived: the selection structure, the coloured meshes and the owners are made again from it.
        private void ApplyWorking(GoreBreakPlan plan)
        {
            var setup = Current;
            Working = plan.Mesh;
            IsNew = plan.IsNew;
            Added = plan.Added;
            Corners = GoreMeshTopology.Flatten(Working, out _);
            TriangleCount = Corners.Length / 3;
            _extraNotes.Clear();
            _extraNotes.AddRange(plan.Notes);

            Edit?.Dispose();
            _matrix = setup.Filter.transform.localToWorldMatrix;
            Edit = GoreStudioMeshEdit.FromStatic(Working, _matrix);
            _faces = new bool[TriangleCount];
            _faceVersion = -1;

            // On the original triangles the faces that were chosen for the extra ones are selected again, so they can be changed.
            if (setup.CommittedExtra == 0 && setup.CommittedSelection != null)
            {
                SelectTriangles(setup.CommittedSelection);
            }

            Display = null;
            ShowWorking();
            SyncOwners();
            Serial++;
            MeshSerial++;
        }

        // The working mesh is what is drawn (Manual, and Random on the surface).
        private void ShowWorking()
        {
            if (ReferenceEquals(Display, Working) && SoupMesh != null)
            {
                return;
            }

            Display = Working;
            DisplayCorners = Corners;
            DisplayIsNew = IsNew;
            DisplayTriangleCount = TriangleCount;
            DisplayWorld = Edit.SourcePosition;
            BuildSoup();
        }

        // A plan that cut the triangles (Random, Through) has a mesh of its own: that is what is drawn, picked and counted.
        private void ShowPlan(GoreBreakPlan plan)
        {
            Display = plan.Mesh;
            DisplayCorners = GoreMeshTopology.Flatten(plan.Mesh, out _);
            DisplayIsNew = plan.IsNew;
            DisplayTriangleCount = DisplayCorners.Length / 3;
            var vertices = plan.Mesh.Vertices;
            var world = new Vector3[vertices.Length];
            for (var v = 0; v < world.Length; v++)
            {
                world[v] = _matrix.MultiplyPoint3x4(vertices[v]);
            }

            DisplayWorld = world;
            BuildSoup();
        }

        private void ApplyDivide(GoreBreakPlan plan)
        {
            if (ReferenceEquals(plan.Mesh, Working))
            {
                ShowWorking();
            }
            else
            {
                ShowPlan(plan);
            }

            Owner = plan.Owner;
            OwnerPieces = plan.PieceCount;
            _divideNotes.Clear();
            _divideNotes.AddRange(plan.Notes);
            OwnerChanged();
        }

        // Owners for the mode: the hand-drawn ones (when they fit this mesh), or all rest until the division arrives.
        private void SyncOwners()
        {
            var setup = Current;
            _manualNote = string.Empty;
            if (setup.Mode == GoreBreakMode.Manual)
            {
                // Hand-drawn pieces are per triangle of the working mesh.
                ShowWorking();
                if (setup.ManualOwners != null && setup.ManualOwners.Length != TriangleCount)
                {
                    _manualNote = "The pieces drawn by hand no longer fit the mesh (its triangles changed), so they were dropped.";
                    setup.ManualOwners = null;
                    setup.ManualPieces = 0;
                    setup.SelectedPiece = -1;
                }

                Owner = setup.ManualOwners ?? AllRest(TriangleCount);
                OwnerPieces = setup.ManualPieces;
                _divideNotes.Clear();
            }
            else
            {
                if (Owner.Length != DisplayTriangleCount)
                {
                    Owner = AllRest(DisplayTriangleCount);
                    OwnerPieces = 0;
                }

                _dividePending = true;
                _requestTime = EditorApplication.timeSinceStartup;
            }

            OwnerChanged();
        }

        private int[] _allRest = NoOwners;

        private int[] AllRest(int count)
        {
            if (_allRest.Length != count)
            {
                _allRest = new int[count];
                for (var t = 0; t < _allRest.Length; t++)
                {
                    _allRest[t] = -1;
                }
            }

            return _allRest;
        }

        // The owners changed: recount, and everything that shows them is made again.
        private void OwnerChanged()
        {
            var setup = Current;
            if (setup.SelectedPiece >= OwnerPieces)
            {
                setup.SelectedPiece = -1;
            }

            Counts = new int[OwnerPieces + 1];
            GoreStaticPieces.Count(Owner, OwnerPieces, Counts);
            CountTexts = new string[Counts.Length];
            for (var i = 0; i < Counts.Length; i++)
            {
                CountTexts[i] = Counts[i].ToString("N0");
            }

            Notes.Clear();
            Notes.AddRange(_extraNotes);
            Notes.AddRange(_divideNotes);
            if (_manualNote.Length > 0)
            {
                Notes.Add(_manualNote);
            }

            ColorsDirty = true;
            Serial++;
            RefreshTexts();
        }

        /// <summary>Asks for the random division to be made again (the slider, the seed or the mode changed).</summary>
        internal void RequestDivide()
        {
            _divideGeneration++;
            _dividePending = Current != null && Current.Mode == GoreBreakMode.Random;
            _requestTime = EditorApplication.timeSinceStartup;
        }

        #endregion

        #region Settings

        /// <summary>Switches between Manual and Random.</summary>
        internal void SetMode(GoreBreakMode mode)
        {
            var setup = Current;
            if (setup == null || setup.Mode == mode)
            {
                return;
            }

            setup.Mode = mode;
            setup.SelectedPiece = -1;
            _divideGeneration++;
            if (Working != null)
            {
                SyncOwners();
            }
        }

        /// <summary>Chooses patches of the surface or columns through the object. Random divides again; hand-drawn pieces stay (only the lids depend on it).</summary>
        internal void SetShape(GoreBreakShape shape)
        {
            var setup = Current;
            if (setup != null && setup.Shape != shape)
            {
                setup.Shape = shape;
                RequestDivide();
                RefreshTexts();
            }
        }

        /// <summary>Chooses the axis a Through division goes along (Auto = the thinnest).</summary>
        internal void SetAxis(GoreBreakAxis axis)
        {
            var setup = Current;
            if (setup != null && setup.Axis != axis)
            {
                setup.Axis = axis;
                RequestDivide();
                RefreshTexts();
            }
        }

        /// <summary>The axis a Through division uses: the one chosen, or for Auto the thinnest of the working mesh.</summary>
        internal GoreBreakAxis AxisUsed
        {
            get
            {
                var setup = Current;
                if (setup == null || Working == null)
                {
                    return GoreBreakAxis.Auto;
                }

                return setup.Axis != GoreBreakAxis.Auto ? setup.Axis : GorePartitioner.ThinAxis(Working, out _);
            }
        }

        /// <summary>The most pieces the Random slider offers: 100 for Through (columns are cut out of the triangles), else the number of triangles up to 100.</summary>
        internal int MaxRandomPieces()
        {
            var setup = Current;
            return GorePartitioner.MaxPieces(TriangleCount, setup != null && setup.Shape == GoreBreakShape.Through);
        }

        /// <summary>Sets how many random pieces there are.</summary>
        internal void SetRandomPieces(int pieces)
        {
            var setup = Current;
            pieces = Mathf.Clamp(pieces, MinRandomPieces, Mathf.Max(MinRandomPieces, MaxRandomPieces()));
            if (setup != null && setup.RandomPieces != pieces)
            {
                setup.RandomPieces = pieces;
                RequestDivide();
            }
        }

        /// <summary>Sets the seed of the random division.</summary>
        internal void SetSeed(int seed)
        {
            var setup = Current;
            if (setup != null && setup.RandomSeed != seed)
            {
                setup.RandomSeed = seed;
                RequestDivide();
            }
        }

        /// <summary>Sets the number in the field "More triangles" (nothing is made until Preview).</summary>
        internal void SetExtra(int count)
        {
            var setup = Current;
            if (setup != null)
            {
                setup.ExtraTriangles = Mathf.Clamp(count, 0, MaxExtraTriangles);
                if (count > 0)
                {
                    SetShowNew(true);
                }

                RefreshTexts();
            }
        }

        /// <summary>Whether the new triangles are tinted and outlined.</summary>
        internal void SetShowNew(bool show)
        {
            if (ShowNew != show)
            {
                ShowNew = show;
                ColorsDirty = true;
            }
        }

        /// <summary>
        /// Makes the extra triangles of the field: the finer mesh is shown, with the new triangles tinted. The faces selected now (on the original
        /// triangles) are the ones that may be split; none selected = everywhere. Pieces drawn by hand are dropped when the triangles change.
        /// </summary>
        internal void Preview()
        {
            var setup = Current;
            if (setup == null || Working == null || Busy)
            {
                return;
            }

            var count = Mathf.Clamp(setup.ExtraTriangles, 0, MaxExtraTriangles);
            var selection = setup.CommittedSelection;
            if (count > 0 && setup.CommittedExtra == 0)
            {
                RefreshSelection();
                selection = FaceCount > 0 ? (bool[])_faces.Clone() : null;
            }

            if (count == setup.CommittedExtra && SameSelection(selection, setup.CommittedSelection))
            {
                SetShowNew(count > 0);
                return;
            }

            if (setup.ManualPieces > 0)
            {
                setup.ManualOwners = null;
                setup.ManualPieces = 0;
                setup.SelectedPiece = -1;
                _manualNote = "The pieces drawn by hand were dropped because the triangles changed.";
            }

            setup.CommittedExtra = count;
            setup.CommittedSelection = selection;
            SetShowNew(count > 0);
            _extraGeneration++;
            _divideGeneration++;
            _extraPending = true;
            Pump();
        }

        /// <summary>Takes the extra triangles back: the original triangles are shown again, with the faces that were chosen for the extra ones selected.</summary>
        internal void ResetExtra()
        {
            var setup = Current;
            if (setup != null)
            {
                setup.ExtraTriangles = 0;
                Preview();
            }
        }

        private static bool SameSelection(bool[] a, bool[] b)
        {
            if (a == null || b == null)
            {
                return a == b;
            }

            if (a.Length != b.Length)
            {
                return false;
            }

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region Selection and pieces

        // Selects the vertices of the given triangles (flat numbers of the working mesh).
        private void SelectTriangles(bool[] triangles)
        {
            Edit.Clear();
            for (var t = 0; t < triangles.Length && t * 3 + 2 < Corners.Length; t++)
            {
                if (triangles[t])
                {
                    Edit.Set(Edit.Group[Corners[t * 3]], true);
                    Edit.Set(Edit.Group[Corners[t * 3 + 1]], true);
                    Edit.Set(Edit.Group[Corners[t * 3 + 2]], true);
                }
            }
        }

        /// <summary>Counts the faces of the selection again when the selection changed. Cheap when it did not.</summary>
        internal void RefreshSelection()
        {
            if (Edit == null)
            {
                FaceCount = 0;
                return;
            }

            if (Edit.Version == _faceVersion)
            {
                return;
            }

            _faceVersion = Edit.Version;
            FaceCount = GoreStaticPieces.FacesOf(Corners, Edit.Group, Edit.Selected, _faces);
            RefreshTexts();
        }

        /// <summary>Whether Manual pieces can be drawn now.</summary>
        internal bool CanDraw => Edit != null && Current != null && Current.Mode == GoreBreakMode.Manual && !Busy;

        /// <summary>Makes a new piece of the selected faces (they leave the piece they were in) and chooses it. Returns how many triangles it got.</summary>
        internal int NewPiece()
        {
            RefreshSelection();
            if (!CanDraw || FaceCount == 0)
            {
                return 0;
            }

            var setup = Current;
            var owners = EnsureManual();
            var pieces = setup.ManualPieces;
            var moved = GoreStaticPieces.NewPiece(owners, _faces, ref pieces);
            setup.ManualPieces = pieces;
            setup.SelectedPiece = moved > 0 ? pieces - 1 : setup.SelectedPiece;
            AfterPieceChange();
            return moved;
        }

        /// <summary>Gives the selected faces to a piece. Returns how many changed owner.</summary>
        internal int AssignTo(int piece)
        {
            RefreshSelection();
            if (!CanDraw || FaceCount == 0 || piece < 0 || piece >= Current.ManualPieces)
            {
                return 0;
            }

            var moved = GoreStaticPieces.Assign(EnsureManual(), _faces, piece);
            AfterPieceChange();
            return moved;
        }

        /// <summary>Takes the selected faces out of a piece (they become the rest). Returns how many were taken out.</summary>
        internal int RemoveFrom(int piece)
        {
            RefreshSelection();
            if (!CanDraw || FaceCount == 0 || piece < 0 || piece >= Current.ManualPieces)
            {
                return 0;
            }

            var moved = GoreStaticPieces.Remove(EnsureManual(), _faces, piece);
            AfterPieceChange();
            return moved;
        }

        /// <summary>Deletes a piece: its triangles become the rest.</summary>
        internal void DeletePiece(int piece)
        {
            var setup = Current;
            if (!CanDraw || setup.ManualOwners == null || piece < 0 || piece >= setup.ManualPieces)
            {
                return;
            }

            var pieces = setup.ManualPieces;
            GoreStaticPieces.Delete(setup.ManualOwners, ref pieces, piece);
            setup.ManualPieces = pieces;
            setup.SelectedPiece = setup.SelectedPiece == piece ? -1 : setup.SelectedPiece > piece ? setup.SelectedPiece - 1 : setup.SelectedPiece;
            AfterPieceChange();
        }

        /// <summary>Chooses a piece (or none with -1): it is shown in full colour, the others dimmed.</summary>
        internal void SelectPiece(int piece)
        {
            var setup = Current;
            if (setup != null && setup.SelectedPiece != piece)
            {
                setup.SelectedPiece = piece >= 0 && piece < OwnerPieces ? piece : -1;
                ColorsDirty = true;
            }
        }

        /// <summary>Clears the selection of the working mesh.</summary>
        internal void ClearSelection()
        {
            Edit?.Clear();
        }

        private int[] EnsureManual()
        {
            var setup = Current;
            if (setup.ManualOwners == null || setup.ManualOwners.Length != TriangleCount)
            {
                setup.ManualOwners = new int[TriangleCount];
                for (var t = 0; t < setup.ManualOwners.Length; t++)
                {
                    setup.ManualOwners[t] = -1;
                }

                setup.ManualPieces = 0;
            }

            return setup.ManualOwners;
        }

        private void AfterPieceChange()
        {
            Owner = Current.ManualOwners ?? AllRest(TriangleCount);
            OwnerPieces = Current.ManualPieces;
            OwnerChanged();
        }

        /// <summary>What an undo step needs of the pieces drawn by hand.</summary>
        internal sealed class OwnerSnapshot
        {
            internal int[] Owners;
            internal int Pieces;
            internal int Selected;
        }

        /// <summary>Copies the pieces drawn by hand (for an undo step).</summary>
        internal OwnerSnapshot Snapshot()
        {
            var setup = Current;
            return new OwnerSnapshot { Owners = setup.ManualOwners != null ? (int[])setup.ManualOwners.Clone() : null, Pieces = setup.ManualPieces, Selected = setup.SelectedPiece };
        }

        /// <summary>Puts back what <see cref="Snapshot"/> copied.</summary>
        internal void Restore(OwnerSnapshot snapshot)
        {
            var setup = Current;
            if (setup == null || Working == null || (snapshot.Owners != null && snapshot.Owners.Length != TriangleCount))
            {
                return;
            }

            setup.ManualOwners = snapshot.Owners != null ? (int[])snapshot.Owners.Clone() : null;
            setup.ManualPieces = snapshot.Pieces;
            setup.SelectedPiece = snapshot.Selected;
            if (setup.Mode == GoreBreakMode.Manual)
            {
                AfterPieceChange();
            }
        }

        /// <summary>The label of a piece for the hover text: "Piece 3 - 120 triangles", or "Rest - 40 triangles".</summary>
        internal string PieceLabel(int piece)
        {
            _text.Clear();
            if (piece < 0 || piece >= OwnerPieces)
            {
                _text.Append("Rest - ").Append(Counts[Counts.Length - 1].ToString("N0"));
            }
            else
            {
                _text.Append("Piece ").Append(piece + 1).Append(" - ").Append(CountTexts[piece]);
            }

            return _text.Append(" triangles").ToString();
        }

        #endregion

        #region Preview meshes

        // The mesh that is drawn as loose, flat-coloured triangles in the world (so each can have its own piece colour), the edges of the new triangles
        // and, when it is not the working mesh, all its edges.
        private void BuildSoup()
        {
            FreeMeshes();
            var world = DisplayWorld;
            var corners = DisplayCorners;
            var triangles = DisplayTriangleCount;
            var normals = new Vector3[world.Length];
            for (var t = 0; t < triangles; t++)
            {
                var a = corners[t * 3];
                var b = corners[t * 3 + 1];
                var c = corners[t * 3 + 2];
                var face = Vector3.Cross(world[b] - world[a], world[c] - world[a]);
                normals[a] += face;
                normals[b] += face;
                normals[c] += face;
            }

            var count = triangles * 3;
            var positions = new Vector3[count];
            var indices = new int[count];
            _shade = new float[count];
            _soupColors = new Color32[count];
            for (var i = 0; i < count; i++)
            {
                var vertex = corners[i];
                positions[i] = world[vertex];
                indices[i] = i;
                var normal = normals[vertex];
                var light = normal.sqrMagnitude > 1e-18f ? Mathf.Max(0f, Vector3.Dot(normal.normalized, ShadeLight)) : 0.5f;
                _shade[i] = 0.5f + 0.5f * light;
            }

            SoupMesh = new Mesh { name = "Gore Studio pieces", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            SoupMesh.vertices = positions;
            SoupMesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
            SoupMesh.RecalculateBounds();

            if (Added > 0)
            {
                NewEdgeMesh = LinesMesh("Gore Studio new edges", world, corners, DisplayIsNew, NewEdgeColor);
            }

            if (!ReferenceEquals(Display, Working))
            {
                _displayWire = LinesMesh("Gore Studio plan edges", world, corners, null, WireColor);
            }

            ColorsDirty = true;
        }

        // Every edge of the triangles (only the new ones when <paramref name="only"/> is given) as lines in one colour.
        private static Mesh LinesMesh(string name, Vector3[] world, int[] corners, bool[] only, Color32 color)
        {
            var lines = new List<Vector3>(corners.Length * 2);
            for (var t = 0; t < corners.Length / 3; t++)
            {
                if (only != null && !only[t])
                {
                    continue;
                }

                for (var k = 0; k < 3; k++)
                {
                    lines.Add(world[corners[t * 3 + k]]);
                    lines.Add(world[corners[t * 3 + (k + 1) % 3]]);
                }
            }

            var indices = new int[lines.Count];
            var colors = new Color32[lines.Count];
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = i;
                colors[i] = color;
            }

            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(lines);
            mesh.SetColors(colors);
            mesh.SetIndices(indices, MeshTopology.Lines, 0, false);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Writes the colours of the pieces into the coloured mesh: a colour per piece, the rest grey, the new triangles tinted, all but the chosen piece dimmed.</summary>
        internal void Recolor()
        {
            ColorsDirty = false;
            var setup = Current;
            if (SoupMesh == null || setup == null || Owner.Length != DisplayTriangleCount)
            {
                return;
            }

            if (_palette.Length != OwnerPieces + 1)
            {
                _palette = new Color32[OwnerPieces + 1];
            }

            for (var p = 0; p < OwnerPieces; p++)
            {
                _palette[p] = GoreStaticPalette.Of(p);
            }

            _palette[OwnerPieces] = GoreStaticPalette.RestColor;
            var chosen = setup.SelectedPiece;
            var showNew = ShowNew && Added > 0;
            var dimmedAlpha = Opacity == 255 ? Opacity : (byte)(Opacity * DimmedOpacity);
            for (var t = 0; t < DisplayTriangleCount; t++)
            {
                var piece = Owner[t];
                var color = _palette[piece >= 0 && piece < OwnerPieces ? piece : OwnerPieces];
                if (showNew && DisplayIsNew[t])
                {
                    color = Color32.Lerp(color, NewTint, NewTintAmount);
                }

                var on = chosen < 0 || piece == chosen;
                var alpha = on ? Opacity : dimmedAlpha;
                for (var k = 0; k < 3; k++)
                {
                    var scale = _shade[t * 3 + k] * (on ? 1f : DimmedShade);
                    _soupColors[t * 3 + k] = new Color32((byte)(color.r * scale), (byte)(color.g * scale), (byte)(color.b * scale), alpha);
                }
            }

            SoupMesh.SetColors(_soupColors);
        }

        #endregion

        #region Texts and readiness

        /// <summary>Builds the lines of text of the cards (called when something they show changed).</summary>
        internal void RefreshTexts()
        {
            var setup = Current;
            if (setup == null || Working == null)
            {
                return;
            }

            // The count of the mesh that is drawn: with a Through division it includes the triangles cut along the borders.
            TrianglesText = DisplayTriangleCount != SourceTriangles ? SourceTriangles.ToString("N0") + " -> " + DisplayTriangleCount.ToString("N0") : SourceTriangles.ToString("N0");
            if (setup.Mode == GoreBreakMode.Random)
            {
                PiecesText = OwnerPieces.ToString();
            }
            else
            {
                var rest = Counts[Counts.Length - 1];
                PiecesText = setup.ManualPieces + (rest > 0 ? " + rest" : string.Empty);
            }

            FacesText = FaceCount == 0 ? "No faces selected" : GoreGUI.Plural(FaceCount, "face", "faces") + " selected";
            _text.Clear();
            if (Busy)
            {
                _text.Append("Working...");
            }
            else if (ExtraStale)
            {
                _text.Append("Press Preview to see the new triangles.");
                if (setup.ManualPieces > 0)
                {
                    _text.Append(" The pieces drawn by hand will be dropped.");
                }
            }
            else if (setup.CommittedExtra > 0)
            {
                _text.Append(Added.ToString("N0")).Append(" new triangles (yellow), on ");
                _text.Append(setup.CommittedSelection != null ? "the faces that were selected." : "every face.");
            }
            else if (FaceCount > 0)
            {
                _text.Append(FaceCount).Append(FaceCount == 1 ? " face selected: it gets" : " faces selected: they get").Append(" the extra triangles.");
            }
            else
            {
                _text.Append("No face selected: the extra triangles go everywhere.");
            }

            ExtraHint = _text.ToString();
        }

        /// <summary>Why the mesh cannot be set up now, or an empty string when it can.</summary>
        internal string ApplyBlocker()
        {
            var setup = Current;
            if (setup == null)
            {
                return "There is no mesh to set up.";
            }

            if (Application.isPlaying)
            {
                return "Leave Play mode to set up.";
            }

            if (Problem != null || Working == null)
            {
                return "The mesh has a problem (see the Mesh card).";
            }

            if (Busy || _extraPending || _dividePending)
            {
                return "Working...";
            }

            if (ExtraStale)
            {
                return "The extra triangles changed: press Preview first.";
            }

            if (setup.Mode == GoreBreakMode.Random)
            {
                return OwnerPieces >= MinPieces ? string.Empty : TooFewTrianglesText;
            }

            if (setup.ManualPieces < MinPieces || GoreStaticPieces.NonEmpty(Counts, setup.ManualPieces) + (Counts[Counts.Length - 1] > 0 ? 1 : 0) < MinPieces)
            {
                return NeedPiecesText;
            }

            return string.Empty;
        }

        /// <summary>The settings the setup uses: exactly what the preview shows.</summary>
        internal GoreBreakSettings BuildSettings()
        {
            var setup = Current;
            return new GoreBreakSettings
            {
                Mode = setup.Mode,
                ExtraTriangles = setup.CommittedExtra,
                ExtraSelection = setup.CommittedExtra > 0 ? setup.CommittedSelection : null,
                RandomPieces = setup.RandomPieces,
                RandomSeed = setup.RandomSeed,
                Shape = setup.Shape,
                Axis = setup.Axis,
                ManualOwners = setup.Mode == GoreBreakMode.Manual && setup.ManualOwners != null && setup.ManualOwners.Length == TriangleCount ? setup.ManualOwners : null,
                ManualPieces = setup.ManualPieces
            };
        }

        #endregion
    }
}
