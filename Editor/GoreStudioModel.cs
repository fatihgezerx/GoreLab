using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using CutSpec = GoreLab.GoreBaker.CutSpec;

namespace GoreLab
{
    /// <summary>The kind of rig the character has: what decides where the automatic cuts go.</summary>
    internal enum GoreRigType
    {
        /// <summary>A Mecanim humanoid: the thirteen cuts come from its avatar.</summary>
        Humanoid,

        /// <summary>Any other skeleton: the cuts are bones picked by hand.</summary>
        Generic
    }

    /// <summary>What the character is made of.</summary>
    internal enum GoreMeshKind
    {
        /// <summary>Skinned meshes that follow bones: cut along the bones.</summary>
        Skinned,

        /// <summary>Meshes with no bones (a table, a crate, a statue): broken into pieces (see <see cref="GoreStudioStatic"/>).</summary>
        Static
    }

    /// <summary>How the studio paints the character.</summary>
    internal enum GoreShading
    {
        /// <summary>The character as it is (its own materials).</summary>
        Model,

        /// <summary>Every part flat-coloured: where each cut will separate the mesh.</summary>
        Segments
    }

    /// <summary>One skinned mesh of the character in the studio, with what the splitter made of it.</summary>
    internal sealed class GoreStudioRow
    {
        public SkinnedMeshRenderer Renderer;
        public bool Cut = true;
        public bool Lids;
        public bool Show = true;

        /// <summary>A look (see <see cref="IGoreLookPreview"/>) hides this body mesh because a piece of clothing replaces it. Only its skin is not drawn: its lids and cut lines still are.</summary>
        public bool HiddenByLook;

        public int Vertices;
        public bool HasBlendShapes;
        public string Problem;

        /// <summary>For a mesh that is left out (Cut off): the name of the cut most of its skin follows, or null. Left out, it stays whole on the body.</summary>
        public string StaysWith;

        public GUIContent Label;
        public string VertexText;

        /// <summary>What the selection tools work on: the vertices in the preview's pose, welded, with edges and triangles. Null when the mesh cannot be posed.</summary>
        public GoreStudioMeshEdit Edit;

        // What the analysis made (null / empty until it ran).
        public GoreSplitResult Split;
        public Vector3[] World = Array.Empty<Vector3>();
        public int[] TriangleVertices = Array.Empty<int>();
        public int[] TriangleSegment = Array.Empty<int>();
        public Mesh SegmentMesh;
        public Mesh LidMesh;

        /// <summary>The cut every vertex of <see cref="LidMesh"/> belongs to (so the lid of the chosen cut can be painted stronger), and its colours.</summary>
        public int[] LidCut = Array.Empty<int>();
        public Color32[] LidColors = Array.Empty<Color32>();
        public float[] SoupShade = Array.Empty<float>();
        public int[] SoupSegment = Array.Empty<int>();
        public Color32[] SoupColors = Array.Empty<Color32>();
    }

    /// <summary>One line of the checks card.</summary>
    internal struct GoreStudioCheck
    {
        public MessageType Type;
        public string Text;
    }

    /// <summary>
    /// What the Gore Studio knows about the character in its preview: the working copy of the prefab, what was detected (rig, mesh kind),
    /// the skinned meshes and the cuts the user chose, and the result of running the splitter on them (the parts as coloured meshes in the
    /// pose the preview shows, the cut rings, the lids). No IMGUI in here. The analysis is redone only when something changed (Dirty),
    /// never per repaint.
    /// </summary>
    internal sealed partial class GoreStudioModel : IDisposable
    {
        private static readonly Color32 BodyColor = new Color32(138, 142, 150, 255);
        private static readonly Color32 LidColor = new Color32(214, 38, 38, 255);
        private static readonly Color32 ChosenLidColor = new Color32(255, 30, 30, 235);

        // A fixed light for the flat shading of the coloured parts (from the front, above and a little to the right).
        private static readonly Vector3 ShadeLight = new Vector3(0.35f, 0.7f, 0.62f).normalized;

        /// <summary>What the user dropped in (an asset or a scene object).</summary>
        public GameObject Source;

        /// <summary>The working copy shown in the preview.</summary>
        public GameObject Instance;

        public GoreRigType Rig;
        public GoreRigType DetectedRig;
        public bool RigOverridden;
        public GoreMeshKind Kind;
        public GoreMeshKind DetectedKind;
        public bool KindOverridden;

        public readonly List<GoreStudioRow> Rows = new List<GoreStudioRow>();
        public readonly List<CutSpec> Cuts = new List<CutSpec>();
        public readonly List<GoreStudioCheck> Checks = new List<GoreStudioCheck>();
        public GoreCharacter Existing;
        public Animator Animator;
        public bool HasError;

        /// <summary>Set when the cuts or the meshes changed: <see cref="Rebuild"/> redoes the analysis.</summary>
        public bool Dirty = true;

        /// <summary>The cut that is highlighted (a number of <see cref="Cuts"/>), or -1.</summary>
        public int SelectedCut = -1;

        /// <summary>The body mesh picked as an object (a number of <see cref="Rows"/>), or -1. Exclusive with <see cref="SelectedCloth"/>.</summary>
        public int SelectedRow = -1;

        /// <summary>The alpha (0 to 255) of the coloured parts: 255 is opaque, a lower value is the X-ray view. Takes effect at the next <see cref="Recolor"/>.</summary>
        public byte Opacity = 255;

        /// <summary>Whether a body mesh is picked and it is cut and shown (so it is drawn and the tools can work on it).</summary>
        public bool HasSelectedRow => SelectedRow >= 0 && SelectedRow < Rows.Count && Rows[SelectedRow].Cut && Rows[SelectedRow].Show;

        /// <summary>Whether a piece of clothing is picked.</summary>
        public bool HasSelectedCloth => SelectedCloth >= 0 && SelectedCloth < Cloth.Count;

        /// <summary>
        /// The body mesh the tools of the Edit mode work on, taken from the body mesh that was picked when the mode was entered (the picking itself is let
        /// go of at once): a number of <see cref="Rows"/>, or -1 = every shown body mesh.
        /// </summary>
        public int EditRow = -1;

        /// <summary>Whether the tools of the Edit mode have nothing to edit: a piece of clothing is picked (in the panel) while editing.</summary>
        public bool EditsNothing => HasSelectedCloth;

        /// <summary>The colour of each segment (the body first, then one per cut).</summary>
        public Color32[] Palette = { BodyColor };

        /// <summary>The cut rings: a line from <c>LineFrom[i]</c> to <c>LineTo[i]</c> belongs to cut <c>LineCut[i]</c>.</summary>
        public readonly List<Vector3> LineFrom = new List<Vector3>();
        public readonly List<Vector3> LineTo = new List<Vector3>();
        public readonly List<int> LineCut = new List<int>();

        /// <summary>The bounds of the character in the preview.</summary>
        public Bounds Bounds = new Bounds(Vector3.up, Vector3.one * 2f);

        private GoreBaker.CutTree _tree;
        private bool[] _segmentOn = Array.Empty<bool>();

        /// <summary>How the cuts nest (per segment, the segment it is cut from; -1 for the body). Null until the cuts were analysed.</summary>
        public int[] SegmentParent => _tree != null ? _tree.SegmentParent : null;

        /// <summary>The text of the first error among the checks, or an empty string.</summary>
        public string FirstError()
        {
            foreach (var check in Checks)
            {
                if (check.Type == MessageType.Error)
                {
                    return check.Text;
                }
            }

            return string.Empty;
        }

        /// <summary>Whether the character has anything to show.</summary>
        public bool HasTarget => Source != null && Instance != null;

        /// <summary>Whether the studio can set this skinned character up (a static mesh is set up through <see cref="GoreStudioStatic"/>, which has its own readiness).</summary>
        public bool CanApply => HasTarget && Kind == GoreMeshKind.Skinned && !HasError && !Application.isPlaying;

        #region Loading

        /// <summary>Takes a new character: the dropped object and the working copy of it that is already in the preview scene.</summary>
        public void Load(GameObject source, GameObject instance)
        {
            ClearAnalysis();
            foreach (var cloth in Cloth)
            {
                FreeCloth(cloth);
            }

            Cloth.Clear();
            ClothRules.Clear();
            SelectedCloth = -1;
            SelectedRow = -1;
            foreach (var row in Rows)
            {
                row.Edit?.Dispose();
            }

            Rows.Clear();
            Cuts.Clear();
            Checks.Clear();
            Source = source;
            Instance = instance;
            SelectedCut = -1;
            Existing = null;
            Animator = null;
            RigOverridden = false;
            KindOverridden = false;
            if (instance == null)
            {
                Dirty = true;
                return;
            }

            Existing = instance.GetComponent<GoreCharacter>();
            Animator = instance.GetComponentInChildren<Animator>(true);
            DetectedRig = Animator != null && Animator.isHuman ? GoreRigType.Humanoid : GoreRigType.Generic;
            Rig = DetectedRig;

            var sources = GoreBaker.FindSources(instance);
            DetectedKind = sources.Count > 0 || !HasStaticMesh(instance) ? GoreMeshKind.Skinned : GoreMeshKind.Static;
            Kind = DetectedKind;

            for (var i = 0; i < sources.Count; i++)
            {
                var renderer = sources[i];
                var mesh = renderer.sharedMesh;
                var path = AnimationUtility.CalculateTransformPath(renderer.transform, instance.transform);
                var row = new GoreStudioRow
                {
                    Renderer = renderer,
                    Vertices = mesh.vertexCount,
                    HasBlendShapes = mesh.blendShapeCount > 0,
                    Problem = FindProblem(renderer),
                    Label = new GUIContent(renderer.name, path.Length == 0 ? renderer.name : path),
                    VertexText = mesh.vertexCount.ToString("N0") + " vertices"
                };

                if (row.Problem == null)
                {
                    // The pose the preview shows, read once: the analysis and the selection tools both work from it.
                    try
                    {
                        row.Edit = GoreStudioMeshEdit.Build(renderer);
                    }
                    catch (InvalidOperationException)
                    {
                        row.Problem = FindProblem(renderer);
                    }

                    if (row.Edit == null && row.Problem == null)
                    {
                        row.Problem = $"'{renderer.name}' could not be posed for the preview (is its object inactive?). Untick Cut for it, or activate it in the prefab.";
                    }
                }

                Rows.Add(row);

            }

            // Every mesh closes its cuts to begin with: a character is often several skin meshes (head, torso, hands), and a cut that passes
            // through one without a lid shows its inside. Untick Lids for a hollow piece of clothing, or a mesh that must stay open.
            for (var i = 0; i < Rows.Count; i++)
            {
                Rows[i].Lids = true;
            }

            if (!LoadEarlierSetup())
            {
                FillAutomatic();
            }

            LoadClothRules();

            RefreshBounds();
            Dirty = true;
        }

        private static bool HasStaticMesh(GameObject root)
        {
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null)
                {
                    return true;
                }
            }

            return false;
        }

        // For a mesh that is left out: the cut most of its skin follows (by the strongest bone of every vertex), or null when it is mostly body.
        private string StrayCut(GoreStudioRow row)
        {
            var bones = row.Renderer.bones;
            BoneWeight[] weights;
            try
            {
                weights = row.Renderer.sharedMesh.boneWeights;
            }
            catch (InvalidOperationException)
            {
                return null;
            }

            if (weights.Length == 0 || bones.Length == 0)
            {
                return null;
            }

            var perCut = new int[Cuts.Count];
            for (var v = 0; v < weights.Length; v++)
            {
                var w = weights[v];
                var index = w.boneIndex0;
                var best = w.weight0;
                if (w.weight1 > best) { best = w.weight1; index = w.boneIndex1; }
                if (w.weight2 > best) { best = w.weight2; index = w.boneIndex2; }
                if (w.weight3 > best) { index = w.boneIndex3; }
                if (index < 0 || index >= bones.Length)
                {
                    continue;
                }

                for (var t = bones[index]; t != null; t = t.parent)
                {
                    if (_tree.CutOfBone.TryGetValue(t, out var cut))
                    {
                        perCut[cut]++;
                        break;
                    }
                }
            }

            var winner = 0;
            for (var c = 1; c < perCut.Length; c++)
            {
                if (perCut[c] > perCut[winner])
                {
                    winner = c;
                }
            }

            return perCut.Length > 0 && perCut[winner] * 2 > weights.Length ? Cuts[winner].Name : null;
        }

        // What makes a renderer impossible to cut, or null. Read once per load: boneWeights copies the array.
        private static string FindProblem(SkinnedMeshRenderer renderer)
        {
            if (Application.isPlaying)
            {
                return "The studio reads meshes in edit mode: leave Play mode.";
            }

            try
            {
                var mesh = renderer.sharedMesh;
                if (mesh.vertexCount == 0 || renderer.bones.Length == 0 || mesh.boneWeights.Length != mesh.vertexCount)
                {
                    return $"'{renderer.name}' has no skin weights or no bones, so it cannot be cut. Untick Cut for it.";
                }

                if (mesh.bindposes.Length != renderer.bones.Length)
                {
                    return $"'{renderer.name}': the number of bind poses and of bones differ; the mesh does not match its renderer. Untick Cut for it.";
                }
            }
            catch (InvalidOperationException)
            {
                var model = AssetDatabase.GetAssetPath(renderer.sharedMesh);
                return $"Unity does not let the editor read the mesh of '{renderer.name}'. Tick Read/Write in the Model tab of the Import Settings of {(string.IsNullOrEmpty(model) ? "its model" : model)} and press Apply, set up, and untick it again.";
            }

            return null;
        }

        // The cuts, the lids and the included meshes of an earlier setup of this character. False when there is none.
        private bool LoadEarlierSetup()
        {
            var profile = Existing != null ? Existing.Profile : null;
            if (profile == null || profile.CutCount == 0 || profile.SetCount == 0)
            {
                return false;
            }

            var matched = 0;
            foreach (var row in Rows)
            {
                if (FindSet(profile, row.Renderer.sharedMesh) != null)
                {
                    matched++;
                }
            }

            if (matched > 0)
            {
                foreach (var row in Rows)
                {
                    var set = FindSet(profile, row.Renderer.sharedMesh);
                    row.Cut = set != null;
                    if (set != null)
                    {
                        row.Lids = set.Lids;
                    }
                }
            }

            Cuts.Clear();
            for (var i = 0; i < profile.CutCount; i++)
            {
                var cut = profile.Cuts[i];
                if (cut == null)
                {
                    continue;
                }

                var bone = Existing.CutBone(i);
                if (bone == null && !string.IsNullOrEmpty(cut.BonePath))
                {
                    bone = Instance.transform.Find(cut.BonePath);
                }

                Cuts.Add(new CutSpec { Name = cut.Name, Limb = cut.Limb, Bone = bone, Fill = cut.FillMaterial });
            }

            // The decisions by hand that were saved with the profile.
            foreach (var row in Rows)
            {
                var set = FindSet(profile, row.Renderer.sharedMesh);
                if (set == null || set.Overrides == null || row.Edit == null)
                {
                    continue;
                }

                foreach (var entry in set.Overrides)
                {
                    if (entry.Cut >= 0 && entry.Cut < profile.CutCount && entry.Vertex >= 0 && entry.Vertex < row.Edit.Group.Length)
                    {
                        var cut = profile.Cuts[entry.Cut];
                        var bone = Existing.CutBone(entry.Cut);
                        if (bone == null && cut != null && !string.IsNullOrEmpty(cut.BonePath))
                        {
                            bone = Instance.transform.Find(cut.BonePath);
                        }

                        row.Edit.SetOverride(bone, row.Edit.Group[entry.Vertex], entry.Kind);
                    }
                }
            }

            return true;
        }

        private static GoreMeshSet FindSet(GoreProfile profile, Mesh source)
        {
            for (var i = 0; i < profile.SetCount; i++)
            {
                var set = profile.MeshSets[i];
                if (set != null && set.SourceMesh == source)
                {
                    return set;
                }
            }

            return null;
        }

        /// <summary>
        /// The edit data of the meshes the selection tools work on: included, shown and posed. Only the picked body mesh when one is picked; none
        /// while a piece of clothing is picked (it is set up in the Cloth parts card). Fills <paramref name="result"/>.
        /// </summary>
        public void ActiveEdits(List<GoreStudioMeshEdit> result)
        {
            result.Clear();
            if (EditsNothing)
            {
                return;
            }

            // A body mesh picked in the panel while editing wins; else the one that was picked when the mode was entered; else all.
            var only = HasSelectedRow ? SelectedRow : EditRow >= 0 && EditRow < Rows.Count ? EditRow : -1;
            for (var i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                if ((only < 0 || i == only) && row.Cut && row.Show && row.Edit != null && row.Problem == null)
                {
                    result.Add(row.Edit);
                }
            }
        }

        /// <summary>The cuts the rig suggests: the thirteen of a humanoid; none for a generic rig (its bones are picked by hand).</summary>
        public void FillAutomatic()
        {
            Cuts.Clear();
            if (Rig == GoreRigType.Humanoid && Animator != null)
            {
                Cuts.AddRange(GoreBaker.HumanoidCuts(Animator));
            }

            SelectedCut = -1;
            Dirty = true;
        }

        /// <summary>Every bone the character's skinned meshes follow, in the order of the hierarchy, for the "add a cut" menu.</summary>
        public List<Transform> BonesInUse()
        {
            var set = new HashSet<Transform>();
            foreach (var row in Rows)
            {
                if (row.Renderer == null)
                {
                    continue;
                }

                foreach (var bone in row.Renderer.bones)
                {
                    if (bone != null)
                    {
                        set.Add(bone);
                    }
                }
            }

            var ordered = new List<Transform>(set.Count);
            if (Instance != null)
            {
                foreach (var child in Instance.GetComponentsInChildren<Transform>(true))
                {
                    if (set.Contains(child) && child != Instance.transform)
                    {
                        ordered.Add(child);
                    }
                }
            }

            return ordered;
        }

        #endregion

        #region Analysis

        /// <summary>The bounds of the character's renderers in the preview (for framing the camera).</summary>
        public void RefreshBounds()
        {
            var any = false;
            var bounds = new Bounds(Vector3.up, Vector3.one);
            if (Instance != null)
            {
                foreach (var renderer in Instance.GetComponentsInChildren<Renderer>(false))
                {
                    if (!any)
                    {
                        bounds = renderer.bounds;
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(renderer.bounds);
                    }
                }
            }

            Bounds = bounds;
        }

        /// <summary>Redoes the analysis when something changed: splits the included meshes for the cuts, builds the coloured parts, the lids and the rings.</summary>
        public void Rebuild()
        {
            if (!Dirty)
            {
                return;
            }

            Dirty = false;
            ClearAnalysis();
            Checks.Clear();
            HasError = false;

            if (!HasTarget)
            {
                AddCheck(MessageType.Warning, "Drop a character prefab into the Target field.");
                return;
            }

            if (Application.isPlaying)
            {
                AddCheck(MessageType.Error, "The studio reads meshes in edit mode: leave Play mode.");
                return;
            }

            // A static mesh is analysed by GoreStudioStatic, not here.
            if (Kind == GoreMeshKind.Static)
            {
                return;
            }

            if (Rows.Count == 0)
            {
                AddCheck(MessageType.Error, "The character has no skinned mesh to set up.");
                return;
            }

            var included = 0;
            var withLids = 0;
            foreach (var row in Rows)
            {
                if (!row.Cut)
                {
                    continue;
                }

                included++;
                if (row.Lids)
                {
                    withLids++;
                }

                if (row.Problem != null)
                {
                    AddCheck(MessageType.Error, row.Problem);
                }

                if (row.HasBlendShapes)
                {
                    AddCheck(MessageType.Warning, $"'{row.Renderer.name}' has blend shapes: they are not kept on the parts (the parts have none).");
                }
            }

            if (included == 0)
            {
                AddCheck(MessageType.Error, "No skinned mesh is included: tick Cut for at least one.");
            }
            else if (included > 1)
            {
                AddCheck(MessageType.Info, $"{included} skinned meshes are included: they are all cut along the same bones.");
            }

            if (included > 0 && withLids == 0)
            {
                AddCheck(MessageType.Info, "No mesh has Lids on: the cuts will be open (no stump, no lid), and no cut ring is drawn.");
            }

            var cutsValid = CheckCuts();
            if (Existing != null && Existing.Profile != null)
            {
                AddCheck(MessageType.Info, "This character was set up before: applying again replaces its parts and fills its profile again.");
            }

            if (!cutsValid || HasError)
            {
                return;
            }

            // The analysis itself.
            for (var i = 0; i < Cuts.Count; i++)
            {
                if (Cuts[i].Bone == null)
                {
                    return;
                }
            }

            _tree = GoreBaker.BuildTree(Cuts);
            BuildPalette();
            foreach (var row in Rows)
            {
                row.StaysWith = !row.Cut && row.Renderer != null && row.Problem == null ? StrayCut(row) : null;
            }

            var covered = new bool[Cuts.Count];
            foreach (var row in Rows)
            {
                if (!row.Cut || row.Problem != null || row.Renderer == null)
                {
                    continue;
                }

                try
                {
                    AnalyzeRow(row);
                }
                catch (InvalidOperationException)
                {
                    row.Problem = $"Unity does not let the editor read the mesh of '{row.Renderer.name}' (tick Read/Write in its model's Import Settings).";
                    AddCheck(MessageType.Error, row.Problem);
                    ClearRow(row);
                    continue;
                }
                catch (Exception exception)
                {
                    row.Problem = $"'{row.Renderer.name}' could not be analysed: {exception.Message}";
                    AddCheck(MessageType.Error, row.Problem);
                    Debug.LogException(exception);
                    ClearRow(row);
                    continue;
                }

                if (row.Problem != null)
                {
                    AddCheck(MessageType.Error, row.Problem);
                    ClearRow(row);
                    continue;
                }

                if (row.Split == null)
                {
                    continue;
                }

                for (var cut = 0; cut < Cuts.Count; cut++)
                {
                    covered[cut] |= !row.Split.Segments[cut + 1].IsEmpty;
                }

                // Which side of each cut every vertex is on by its weights: what a region fill of a decision by hand starts from.
                row.Edit?.SetNatural(row.Split.SourceShares, Cuts.Count, _tree.SegmentParent);

                foreach (var warning in row.Split.Warnings)
                {
                    // A mesh that does not cover a segment is normal (boots have nothing on an arm); that is judged over all meshes below.
                    if (!warning.StartsWith("Segment ", StringComparison.Ordinal))
                    {
                        AddCheck(MessageType.Warning, $"{row.Renderer.name}: {warning}");
                    }
                }
            }

            for (var cut = 0; cut < Cuts.Count; cut++)
            {
                if (!covered[cut])
                {
                    AddCheck(MessageType.Warning, $"No mesh has any skin that follows the bone of '{Cuts[cut].Name}': it would come off with nothing.");
                }
            }

            ClassifyCloth();
            Recolor();
        }

        private bool CheckCuts()
        {
            if (Cuts.Count == 0)
            {
                AddCheck(MessageType.Error, Rig == GoreRigType.Humanoid
                    ? "There is no cut. Press Humanoid cuts, or add the bones by hand."
                    : "There is no cut. Press Add cut and pick the bones the character can be cut at.");
                return false;
            }

            var valid = true;
            for (var i = 0; i < Cuts.Count; i++)
            {
                var cut = Cuts[i];
                var label = string.IsNullOrWhiteSpace(cut.Name) ? "Cut " + (i + 1) : "'" + cut.Name + "'";
                if (string.IsNullOrWhiteSpace(cut.Name))
                {
                    AddCheck(MessageType.Error, $"{label} has no name.");
                    valid = false;
                }

                if (cut.Bone == null)
                {
                    AddCheck(MessageType.Error, $"{label} has no bone.");
                    valid = false;
                }
                else if (!cut.Bone.IsChildOf(Instance.transform) || cut.Bone == Instance.transform)
                {
                    AddCheck(MessageType.Error, $"The bone of {label} is not below the character.");
                    valid = false;
                }

                for (var j = i + 1; j < Cuts.Count; j++)
                {
                    var other = Cuts[j];
                    if (!string.IsNullOrWhiteSpace(cut.Name) && string.Equals(cut.Name, other.Name, StringComparison.Ordinal))
                    {
                        AddCheck(MessageType.Error, $"Two cuts are called '{cut.Name}'. Names have to be different.");
                        valid = false;
                    }

                    if (cut.Bone != null && cut.Bone == other.Bone)
                    {
                        AddCheck(MessageType.Error, $"{label} and '{other.Name}' use the same bone '{cut.Bone.name}'.");
                        valid = false;
                    }

                    if (cut.Limb != GoreLimb.None && cut.Limb == other.Limb)
                    {
                        AddCheck(MessageType.Warning, $"{label} and '{other.Name}' are both {cut.Limb}: cutting by limb reaches only the first one.");
                    }
                }
            }

            return valid;
        }

        private void AddCheck(MessageType type, string text)
        {
            Checks.Add(new GoreStudioCheck { Type = type, Text = text });
            if (type == MessageType.Error)
            {
                HasError = true;
            }
        }

        private void BuildPalette()
        {
            Palette = new Color32[Cuts.Count + 1];
            Palette[0] = BodyColor;
            for (var cut = 0; cut < Cuts.Count; cut++)
            {
                // A humanoid's parts have fixed colours that can be read at a glance: the left side is blue and green, the right side orange and pink, the
                // head purple; along a limb the colour goes from dark (the upper part) to light (the hand or foot). Any other cut gets a golden-ratio hue.
                Palette[cut + 1] = Cuts[cut].Limb != GoreLimb.None ? LimbColorOf(Cuts[cut].Limb) : Color.HSVToRGB((cut * 0.618034f) % 1f, 0.75f, 0.95f);
            }
        }

        private static Color32 LimbColorOf(GoreLimb limb)
        {
            switch (limb)
            {
                case GoreLimb.Head: return new Color32(150, 70, 210, 255);
                case GoreLimb.LeftUpperArm: return new Color32(30, 85, 215, 255);
                case GoreLimb.LeftLowerArm: return new Color32(60, 150, 245, 255);
                case GoreLimb.LeftHand: return new Color32(150, 210, 255, 255);
                case GoreLimb.RightUpperArm: return new Color32(210, 65, 25, 255);
                case GoreLimb.RightLowerArm: return new Color32(250, 125, 40, 255);
                case GoreLimb.RightHand: return new Color32(255, 195, 110, 255);
                case GoreLimb.LeftUpperLeg: return new Color32(15, 125, 95, 255);
                case GoreLimb.LeftLowerLeg: return new Color32(40, 185, 135, 255);
                case GoreLimb.LeftFoot: return new Color32(140, 235, 195, 255);
                case GoreLimb.RightUpperLeg: return new Color32(175, 30, 105, 255);
                case GoreLimb.RightLowerLeg: return new Color32(230, 85, 155, 255);
                case GoreLimb.RightFoot: return new Color32(255, 165, 205, 255);
                default: return BodyColor;
            }
        }

        // Splits one mesh, finds where its vertices are in the preview's pose, and builds the coloured parts, the lids and the rings from that.
        private void AnalyzeRow(GoreStudioRow row)
        {
            var renderer = row.Renderer;
            var split = GoreBaker.Analyze(renderer, row.Lids, _tree, row.Edit.ToOverrides(Cuts));
            row.Split = split;

            // The pose the preview shows: the source vertices are read once (the edit data); the ones made on a cut line lie between two others.
            var sourcePosition = row.Edit.SourcePosition;
            var original = sourcePosition.Length;
            var count = split.Positions.Length;
            var world = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                if (i < original)
                {
                    world[i] = sourcePosition[i];
                }
                else
                {
                    var reference = split.Vertices[i];
                    world[i] = Vector3.Lerp(world[reference.A], world[reference.B], reference.T);
                }
            }

            row.World = world;
            BuildSoup(row);
            BuildLids(row);
            BuildRings(row);
        }

        // The parts as one mesh of loose triangles (so every triangle can be coloured by its segment), with smooth fixed-light shading.
        private static void BuildSoup(GoreStudioRow row)
        {
            var split = row.Split;
            var world = row.World;
            var triangleCount = 0;
            foreach (var part in split.Segments)
            {
                foreach (var list in part.SubmeshTriangles)
                {
                    triangleCount += list.Length / 3;
                }
            }

            var triangleVertices = new int[triangleCount * 3];
            var triangleSegment = new int[triangleCount];
            var normals = new Vector3[world.Length];
            var t = 0;
            for (var segment = 0; segment < split.Segments.Length; segment++)
            {
                var part = split.Segments[segment];
                foreach (var list in part.SubmeshTriangles)
                {
                    for (var k = 0; k + 2 < list.Length; k += 3)
                    {
                        var a = part.VertexMap[list[k]];
                        var b = part.VertexMap[list[k + 1]];
                        var c = part.VertexMap[list[k + 2]];
                        triangleVertices[t * 3] = a;
                        triangleVertices[t * 3 + 1] = b;
                        triangleVertices[t * 3 + 2] = c;
                        triangleSegment[t] = segment;
                        var face = Vector3.Cross(world[b] - world[a], world[c] - world[a]);
                        normals[a] += face;
                        normals[b] += face;
                        normals[c] += face;
                        t++;
                    }
                }
            }

            var soupCount = triangleCount * 3;
            var positions = new Vector3[soupCount];
            var indices = new int[soupCount];
            row.SoupShade = new float[soupCount];
            row.SoupSegment = new int[soupCount];
            row.SoupColors = new Color32[soupCount];
            for (var i = 0; i < soupCount; i++)
            {
                var vertex = triangleVertices[i];
                positions[i] = world[vertex];
                indices[i] = i;
                row.SoupSegment[i] = triangleSegment[i / 3];
                var normal = normals[vertex];
                var light = normal.sqrMagnitude > 1e-18f ? Mathf.Max(0f, Vector3.Dot(normal.normalized, ShadeLight)) : 0.5f;
                row.SoupShade[i] = 0.5f + 0.5f * light;
            }

            row.TriangleVertices = triangleVertices;
            row.TriangleSegment = triangleSegment;
            var mesh = new Mesh { name = "Gore Studio segments", hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = positions;
            mesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
            mesh.RecalculateBounds();
            row.SegmentMesh = mesh;
        }

        // The lids of the cuts as translucent fans, where they will close the parts (drawn through the skin: they lie inside it).
        private static void BuildLids(GoreStudioRow row)
        {
            var split = row.Split;
            var positions = new List<Vector3>();
            var cutOf = new List<int>();
            for (var cut = 0; cut < split.Caps.Length; cut++)
            {
                var cap = split.Caps[cut];
                if (cap == null)
                {
                    continue;
                }

                var capWorld = new Vector3[cap.Vertices.Length];
                for (var i = 0; i < capWorld.Length; i++)
                {
                    if (cap.Vertices[i].Source >= 0)
                    {
                        capWorld[i] = row.World[cap.Vertices[i].Source];
                    }
                }

                for (var loop = 0; loop < cap.LoopFirst.Length; loop++)
                {
                    var first = cap.LoopFirst[loop];
                    var length = cap.LoopLength[loop];
                    var centre = Vector3.zero;
                    for (var i = 0; i < length; i++)
                    {
                        centre += capWorld[first + i];
                    }

                    capWorld[first + length] = length > 0 ? centre / length : centre;
                }

                for (var i = 0; i < cap.LimbTriangles.Length; i++)
                {
                    positions.Add(capWorld[cap.LimbTriangles[i]]);
                    cutOf.Add(cut);
                }
            }

            if (positions.Count == 0)
            {
                return;
            }

            var indices = new int[positions.Count];
            var colors = new Color32[positions.Count];
            var lid = new Color32(LidColor.r, LidColor.g, LidColor.b, 90);
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = i;
                colors[i] = lid;
            }

            var mesh = new Mesh { name = "Gore Studio lids", hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(positions);
            mesh.SetColors(colors);
            mesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
            mesh.RecalculateBounds();
            row.LidMesh = mesh;
            row.LidCut = cutOf.ToArray();
            row.LidColors = colors;
        }

        // The rings the cuts make: one line per edge of every loop.
        private void BuildRings(GoreStudioRow row)
        {
            var split = row.Split;
            for (var cut = 0; cut < split.Caps.Length; cut++)
            {
                var cap = split.Caps[cut];
                if (cap == null)
                {
                    continue;
                }

                for (var loop = 0; loop < cap.LoopFirst.Length; loop++)
                {
                    var first = cap.LoopFirst[loop];
                    var length = cap.LoopLength[loop];
                    for (var i = 0; i < length; i++)
                    {
                        var a = cap.Vertices[first + i].Source;
                        var b = cap.Vertices[first + (i + 1) % length].Source;
                        if (a < 0 || b < 0)
                        {
                            continue;
                        }

                        LineFrom.Add(row.World[a]);
                        LineTo.Add(row.World[b]);
                        LineCut.Add(cut);
                    }
                }
            }
        }

        #endregion

        #region Colour

        /// <summary>Paints the parts: all at full colour, or only the selected cut (and what comes off with it) with the rest dimmed.</summary>
        public void Recolor()
        {
            var segments = Cuts.Count + 1;
            if (_segmentOn.Length != segments)
            {
                _segmentOn = new bool[segments];
            }

            var any = SelectedCut >= 0 && SelectedCut < Cuts.Count && _tree != null;
            for (var s = 0; s < segments; s++)
            {
                _segmentOn[s] = !any;
            }

            if (any)
            {
                _segmentOn[SelectedCut + 1] = true;
                foreach (var below in _tree.Below[SelectedCut])
                {
                    _segmentOn[below + 1] = true;
                }
            }

            // The lid of the chosen body part is strong, the other lids are faint (all the same when no part is chosen).
            foreach (var lidRow in Rows)
            {
                if (lidRow.LidMesh == null)
                {
                    continue;
                }

                for (var i = 0; i < lidRow.LidColors.Length; i++)
                {
                    var chosen = any && lidRow.LidCut[i] == SelectedCut;
                    lidRow.LidColors[i] = chosen ? ChosenLidColor : new Color32(LidColor.r, LidColor.g, LidColor.b, (byte)(any ? 45 : 90));
                }

                lidRow.LidMesh.SetColors(lidRow.LidColors);
            }

            // A picked object (a piece of clothing or a body mesh) stays at full colour: everything else is dimmed.
            var focus = HasSelectedCloth || HasSelectedRow;
            for (var r = 0; r < Rows.Count; r++)
            {
                var row = Rows[r];
                if (row.SegmentMesh != null)
                {
                    Paint(row.SegmentMesh, row.SoupColors, row.SoupSegment, row.SoupShade, focus, focus ? (r == SelectedRow ? 1 : 0) : -1, null, -1);
                }
            }

            for (var c = 0; c < Cloth.Count; c++)
            {
                var cloth = Cloth[c];
                if (cloth.Classified && cloth.SegmentMesh != null)
                {
                    Paint(cloth.SegmentMesh, cloth.SoupColors, cloth.SoupSegment, cloth.SoupShade, focus, focus ? (c == SelectedCloth ? 1 : 0) : -1, cloth.SoupPiece, c == SelectedCloth ? cloth.SelectedPiece : -1);
                }
            }
        }

        // Colours the corners of one coloured mesh by their segment. <paramref name="dimAll"/>: everything is dimmed; <paramref name="force"/> 1 = this one is
        // not dimmed whatever the selected cut says (the piece of clothing in focus), 0 = dimmed, -1 = decided by the selected cut.
        private void Paint(Mesh mesh, Color32[] colors, int[] soupSegment, float[] shade, bool dimAll, int force, int[] soupIsland, int piece)
        {
            for (var i = 0; i < colors.Length; i++)
            {
                var segment = soupSegment[i];
                var color = segment < Palette.Length ? Palette[segment] : BodyColor;
                bool on;
                if (force >= 0)
                {
                    on = force == 1 && (piece < 0 || soupIsland == null || i >= soupIsland.Length || soupIsland[i] == piece);
                }
                else
                {
                    on = !dimAll && segment < _segmentOn.Length && _segmentOn[segment];
                }

                var scale = shade[i] * (on ? 1f : 0.32f);

                // In the X-ray view a dimmed part is a little more see-through than the rest, but never gone.
                var alpha = on || Opacity == 255 ? Opacity : (byte)(Opacity * 0.75f);
                colors[i] = new Color32((byte)(color.r * scale), (byte)(color.g * scale), (byte)(color.b * scale), alpha);
            }

            mesh.SetColors(colors);
        }

        /// <summary>The colour a cut's ring is drawn in.</summary>
        public Color32 CutColor(int cut) => cut + 1 < Palette.Length ? Palette[cut + 1] : BodyColor;

        #endregion

        #region Cleanup

        private void ClearAnalysis()
        {
            foreach (var row in Rows)
            {
                ClearRow(row);
            }

            LineFrom.Clear();
            LineTo.Clear();
            LineCut.Clear();
            foreach (var cloth in Cloth)
            {
                cloth.Classified = false;
                cloth.Result = string.Empty;
            }

            _tree = null;
        }

        // Forgets what the analysis made of one mesh.
        private static void ClearRow(GoreStudioRow row)
        {
            row.Split = null;
            row.World = Array.Empty<Vector3>();
            row.TriangleVertices = Array.Empty<int>();
            row.TriangleSegment = Array.Empty<int>();
            row.SoupShade = Array.Empty<float>();
            row.SoupSegment = Array.Empty<int>();
            row.SoupColors = Array.Empty<Color32>();
            if (row.SegmentMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(row.SegmentMesh);
                row.SegmentMesh = null;
            }

            if (row.LidMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(row.LidMesh);
                row.LidMesh = null;
            }
        }

        /// <summary>Frees the meshes the analysis and the selection tools made.</summary>
        public void Dispose()
        {
            ClearAnalysis();
            foreach (var cloth in Cloth)
            {
                FreeCloth(cloth);
            }

            Cloth.Clear();
            foreach (var row in Rows)
            {
                row.Edit?.Dispose();
                row.Edit = null;
            }
        }

        #endregion
    }
}
