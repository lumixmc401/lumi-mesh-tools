using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Front end for <see cref="MeshSymmetrizer"/>: pick a renderer, place the mirror plane,
    /// choose which regions take part, look at it in the scene, then bake the result.
    /// </summary>
    public sealed class SymmetrizeWindow : EditorWindow
    {
        const string DefaultOutputFolder = "Assets/LumiMeshTools/Generated";

        [SerializeField] Renderer _renderer;
        [SerializeField] string _outputFolder = DefaultOutputFolder;
        [SerializeField] int _axis;
        [SerializeField] float _axisOffset;
        [SerializeField] bool _customPlane;
        [SerializeField] bool _showRegions = true;
        [SerializeField] bool _showSeam;
        [SerializeField] bool _showChannels;

        readonly SymmetrizeOptions _options = new SymmetrizeOptions();
        readonly HashSet<int> _keptAsIs = new HashSet<int>();

        MeshSnapshot _snapshot;
        Mesh _snapshotOf;
        List<MeshIslands.Island> _islands;
        int[] _islandOfVertex;
        bool _scored;

        Mesh _originalMesh;
        Mesh _previewMesh;
        bool _previewDirty;
        SymmetrizeReport _report;
        string _error;
        SymmetryDetector.PlaneFit _fit;
        bool _hasFit;
        float[] _axisScores;

        bool _picking;
        int _hoveredIsland = -1;
        Vector2 _scroll;
        Vector2 _regionScroll;

        [MenuItem("Tools/Lumi Mesh Tools/Symmetrize")]
        public static void Open()
        {
            var window = GetWindow<SymmetrizeWindow>();
            window.titleContent = new GUIContent("Symmetrize");
            window.minSize = new Vector2(380f, 480f);
            window.Show();
        }

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGui;
            if (_renderer == null) PickFromSelection();
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGui;
            StopPreview();
        }

        void OnSelectionChange()
        {
            if (IsPreviewing || _picking) return;
            if (PickFromSelection()) Repaint();
        }

        bool IsPreviewing => _previewMesh != null;

        // ---- GUI --------------------------------------------------------------------------

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                var picked = (Renderer)EditorGUILayout.ObjectField("Renderer", _renderer, typeof(Renderer), true);
                if (change.changed) SetRenderer(picked);
            }

            var sourceMesh = SourceMeshOf(_renderer);
            if (_renderer == null)
            {
                EditorGUILayout.HelpBox("Pick a Skinned Mesh Renderer or a Mesh Renderer to symmetrize.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }
            if (sourceMesh == null)
            {
                EditorGUILayout.HelpBox("That renderer has no mesh assigned.", MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            EnsureSnapshot(sourceMesh);
            if (_snapshot == null)
            {
                EditorGUILayout.HelpBox(_error ?? "Could not read that mesh.", MessageType.Error);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.LabelField(
                $"{sourceMesh.name} — {sourceMesh.vertexCount:N0} verts, {sourceMesh.subMeshCount} submesh(es), " +
                $"{sourceMesh.blendShapeCount} blend shape(s), {_islands.Count} region(s)",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();
            DrawPlaneSection();

            EditorGUILayout.Space();
            DrawSideSection();

            EditorGUILayout.Space();
            DrawRegionsSection();

            EditorGUILayout.Space();
            DrawSeamSection();

            EditorGUILayout.Space();
            DrawChannelsSection();

            EditorGUILayout.Space();
            DrawOutputSection();

            EditorGUILayout.Space();
            DrawActions();

            if (!string.IsNullOrEmpty(_error))
                EditorGUILayout.HelpBox(_error, MessageType.Error);

            DrawReport();
            EditorGUILayout.EndScrollView();

            // Rebuilding on every drag frame makes the sliders crawl on a dense mesh, so the
            // preview catches up once the mouse is released.
            if (_previewDirty && GUIUtility.hotControl == 0)
            {
                _previewDirty = false;
                RefreshPreview();
            }
        }

        void DrawPlaneSection()
        {
            EditorGUILayout.LabelField("Mirror plane", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(_customPlane))
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                _axis = EditorGUILayout.Popup("Axis", _axis, new[] { "X", "Y", "Z" });
                _axisOffset = EditorGUILayout.FloatField("Offset", _axisOffset);
                if (change.changed) MarkDirty();
            }

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                _customPlane = EditorGUILayout.Toggle(
                    new GUIContent("Tilted plane", "Mirror about a free plane instead of a straight axis. Needed for a piece that was modelled at an angle."),
                    _customPlane);
                if (change.changed) MarkDirty();
            }

            if (_customPlane)
            {
                using (new EditorGUI.IndentLevelScope())
                using (var change = new EditorGUI.ChangeCheckScope())
                {
                    _options.planeNormal = EditorGUILayout.Vector3Field("Normal", _options.planeNormal);
                    _options.planePoint = EditorGUILayout.Vector3Field("Point", _options.planePoint);
                    EditorGUILayout.LabelField(" ", "Drag the handles in the scene view to place it.", EditorStyles.miniLabel);
                    if (change.changed) MarkDirty();
                }
            }

            if (GUILayout.Button(new GUIContent("Fit to mesh",
                "Searches for the plane this mesh is most nearly mirrored about, including a tilt, using only the regions set to mirror.")))
            {
                Fit();
            }

            if (!_hasFit) return;

            var message = $"Fitted {_fit.AxisName} at {Vector3.Dot(_fit.point, _fit.normal):0.####} — " +
                          $"{_fit.score:P0} of vertices have a mirror partner.";
            if (_fit.tiltDegrees > 0.25f)
                message += $"\nThe plane is tilted {_fit.tiltDegrees:0.0}° off {_fit.AxisName}; " +
                           "mirroring about the straight axis instead would fold a ridge down the middle.";
            if (_axisScores != null)
                message += $"\nAxis-aligned scores — X {_axisScores[0]:P0}, Y {_axisScores[1]:P0}, Z {_axisScores[2]:P0}.";
            if (_fit.score <= 0.85f)
                message += "\nA low score is normal for a mesh that really is asymmetric — check the plane in the scene " +
                           "before baking. If a large piece exists on one side only, press Score then Auto under Regions " +
                           "to set it aside and fit again: the fit only looks at the regions being mirrored.";

            EditorGUILayout.HelpBox(message, _fit.score > 0.85f ? MessageType.Info : MessageType.Warning);
        }

        void DrawSideSection()
        {
            EditorGUILayout.LabelField("Side to keep", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("This half survives and is mirrored onto the other.", EditorStyles.miniLabel);

            int axis = _customPlane ? _options.DominantAxis : _axis;
            using (new EditorGUILayout.HorizontalScope())
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                bool negative = GUILayout.Toggle(!_options.keepPositiveSide, NegativeSideLabel(axis), EditorStyles.miniButtonLeft, GUILayout.Height(22f));
                bool positive = GUILayout.Toggle(_options.keepPositiveSide, PositiveSideLabel(axis), EditorStyles.miniButtonRight, GUILayout.Height(22f));
                if (change.changed)
                {
                    if (negative && _options.keepPositiveSide) _options.keepPositiveSide = false;
                    else if (positive && !_options.keepPositiveSide) _options.keepPositiveSide = true;
                    MarkDirty();
                }
            }
        }

        void DrawRegionsSection()
        {
            _showRegions = EditorGUILayout.Foldout(_showRegions, $"Regions ({_islands.Count})", true);
            if (!_showRegions) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField(
                    "Unticked regions are copied through untouched — neither cut nor mirrored.",
                    EditorStyles.miniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(_picking ? "Stop picking" : "Pick in scene", EditorStyles.miniButtonLeft))
                    {
                        _picking = !_picking;
                        SceneView.RepaintAll();
                    }
                    if (GUILayout.Button("All", EditorStyles.miniButtonMid)) { _keptAsIs.Clear(); MarkDirty(); }
                    if (GUILayout.Button("Score", EditorStyles.miniButtonMid)) ScoreRegions();
                    if (GUILayout.Button(new GUIContent("Auto", "Keep every region that has almost no mirror partner as-is — the one-sided pieces."),
                        EditorStyles.miniButtonRight))
                    {
                        AutoKeepOneSidedRegions();
                    }
                }

                if (_picking)
                    EditorGUILayout.HelpBox("Click a piece in the scene view to toggle it.", MessageType.Info);

                _regionScroll = EditorGUILayout.BeginScrollView(_regionScroll, GUILayout.MaxHeight(160f));
                int hovered = -1;
                foreach (var island in _islands)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool mirrored = !_keptAsIs.Contains(island.index);
                        bool wants = EditorGUILayout.ToggleLeft(
                            $"Region {island.index} — {island.triangleCount:N0} tris",
                            mirrored, GUILayout.MinWidth(180f));
                        if (wants != mirrored)
                        {
                            if (wants) _keptAsIs.Remove(island.index);
                            else _keptAsIs.Add(island.index);
                            MarkDirty();
                        }

                        if (island.symmetryScore >= 0f)
                            EditorGUILayout.LabelField($"{island.symmetryScore:P0} symmetric", EditorStyles.miniLabel, GUILayout.Width(96f));
                    }

                    if (Event.current.type == EventType.Repaint &&
                        GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                    {
                        hovered = island.index;
                    }
                }
                EditorGUILayout.EndScrollView();

                if (hovered != _hoveredIsland)
                {
                    _hoveredIsland = hovered;
                    SceneView.RepaintAll();
                }
            }
        }

        void DrawSeamSection()
        {
            _showSeam = EditorGUILayout.Foldout(_showSeam, "Seam", true);
            if (!_showSeam) return;

            using (new EditorGUI.IndentLevelScope())
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                _options.weldSeam = EditorGUILayout.Toggle(
                    new GUIContent("Weld", "Share the vertices that land on the plane between both halves so the join is watertight."),
                    _options.weldSeam);

                using (new EditorGUI.DisabledScope(!_options.weldSeam))
                {
                    _options.seamTolerance = EditorGUILayout.FloatField(
                        new GUIContent("Tolerance", "How close to the plane a vertex has to be to count as sitting on it."),
                        _options.seamTolerance);
                }

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField("Relax across the join", EditorStyles.miniBoldLabel);
                _options.seamSmoothWidth = EditorGUILayout.FloatField(
                    new GUIContent("Radius", "How far out from the mirror plane the relax reaches, in mesh units. Zero turns it off. This is what flattens the ridge the mirror leaves down the middle."),
                    _options.seamSmoothWidth);

                using (new EditorGUI.DisabledScope(_options.seamSmoothWidth <= 0f))
                {
                    _options.seamFalloff = (FalloffCurve)EditorGUILayout.EnumPopup(
                        new GUIContent("Falloff", "How the influence fades from the plane out to the radius, as in Blender's proportional editing."),
                        _options.seamFalloff);
                    _options.seamSmoothStrength = EditorGUILayout.Slider("Strength", _options.seamSmoothStrength, 0f, 1f);
                    _options.seamSmoothIterations = EditorGUILayout.IntSlider("Iterations", _options.seamSmoothIterations, 1, 20);
                    _options.recalculateSeamNormals = EditorGUILayout.Toggle(
                        new GUIContent("Fix normals", "Blend recalculated normals into the smoothed band only, leaving hand-authored shading elsewhere alone."),
                        _options.recalculateSeamNormals);
                }

                if (_options.seamSmoothWidth <= 0f && _snapshot != null)
                {
                    if (GUILayout.Button("Suggest a radius from the mesh size", EditorStyles.miniButton))
                    {
                        _options.seamSmoothWidth = Mathf.Max(_snapshot.CalculateBounds().size.magnitude * 0.02f, 1e-4f);
                        MarkDirty();
                    }
                }

                if (change.changed) MarkDirty();
            }
        }

        void DrawChannelsSection()
        {
            _showChannels = EditorGUILayout.Foldout(_showChannels, "Blend shapes and skinning", true);
            if (!_showChannels) return;

            using (new EditorGUI.IndentLevelScope())
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                _options.pairBlendShapes = EditorGUILayout.Toggle(
                    new GUIContent("Pair L/R blend shapes", "Drive the mirrored half of a blend shape from its opposite-side twin, so a one-sided shape stays one-sided."),
                    _options.pairBlendShapes);

                using (new EditorGUI.DisabledScope(!(_renderer is SkinnedMeshRenderer)))
                {
                    _options.mirrorBoneWeights = EditorGUILayout.Toggle(
                        new GUIContent("Mirror bone weights", "Remap bone indices on the mirrored half through L/R bone name matching."),
                        _options.mirrorBoneWeights);
                }

                if (change.changed) MarkDirty();
            }
        }

        void DrawOutputSection()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _outputFolder = EditorGUILayout.TextField("Folder", _outputFolder);
                if (GUILayout.Button("…", GUILayout.Width(28f)))
                {
                    var picked = EditorUtility.SaveFolderPanel("Save generated meshes to", "Assets", "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        var relative = ToProjectRelativePath(picked);
                        if (relative != null) _outputFolder = relative;
                        else _error = "Pick a folder inside this project's Assets folder.";
                    }
                }
            }
        }

        void DrawActions()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (!IsPreviewing)
                {
                    if (GUILayout.Button("Preview", GUILayout.Height(26f))) StartPreview();
                }
                else if (GUILayout.Button("Stop preview", GUILayout.Height(26f)))
                {
                    StopPreview();
                }

                if (GUILayout.Button("Bake & Apply", GUILayout.Height(26f))) Bake();
            }

            if (IsPreviewing)
                EditorGUILayout.HelpBox("Previewing an unsaved mesh. Stop the preview or bake before leaving this window — closing it puts the original mesh back.", MessageType.Info);
        }

        void DrawReport()
        {
            if (_report == null) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Vertices  {_report.sourceVertexCount:N0} → {_report.resultVertexCount:N0}" +
                                       $"   (kept {_report.keptVertexCount:N0}, seam {_report.seamVertexCount:N0})", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Triangles  {_report.sourceTriangleCount:N0} → {_report.resultTriangleCount:N0}", EditorStyles.miniLabel);
            if (_report.keptAsIsTriangleCount > 0)
                EditorGUILayout.LabelField($"Kept as-is  {_report.keptAsIsTriangleCount:N0} tris", EditorStyles.miniLabel);
            if (_report.smoothedVertexCount > 0)
                EditorGUILayout.LabelField($"Smoothed  {_report.smoothedVertexCount:N0} verts near the seam", EditorStyles.miniLabel);

            foreach (var warning in _report.warnings)
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
        }

        // ---- Scene view -------------------------------------------------------------------

        void OnSceneGui(SceneView view)
        {
            if (_renderer == null || _snapshot == null) return;

            SyncPlane();
            var matrix = _renderer.transform.localToWorldMatrix;

            DrawPlaneGizmo(matrix);
            DrawRegionOutlines(matrix);

            if (_customPlane) DrawPlaneHandles();
            if (_picking) HandlePicking(matrix);
        }

        void DrawPlaneGizmo(Matrix4x4 matrix)
        {
            var bounds = _snapshot.CalculateBounds();
            var normal = _options.planeNormal.normalized;
            var u = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;
            var v = Vector3.Cross(normal, u).normalized;

            float extent = bounds.size.magnitude * 0.55f;
            var center = bounds.center - Vector3.Dot(bounds.center - _options.planePoint, normal) * normal;

            var corners = new[]
            {
                matrix.MultiplyPoint3x4(center - u * extent - v * extent),
                matrix.MultiplyPoint3x4(center - u * extent + v * extent),
                matrix.MultiplyPoint3x4(center + u * extent + v * extent),
                matrix.MultiplyPoint3x4(center + u * extent - v * extent),
            };
            Handles.DrawSolidRectangleWithOutline(corners, new Color(0.2f, 0.6f, 1f, 0.06f), new Color(0.2f, 0.6f, 1f, 0.8f));
        }

        void DrawRegionOutlines(Matrix4x4 matrix)
        {
            if (_islands == null) return;
            using (new Handles.DrawingScope(matrix))
            {
                foreach (var island in _islands)
                {
                    bool keptAsIs = _keptAsIs.Contains(island.index);
                    bool hovered = island.index == _hoveredIsland;
                    if (!keptAsIs && !hovered) continue;

                    Handles.color = hovered ? new Color(1f, 0.85f, 0.2f, 0.95f) : new Color(1f, 0.45f, 0.2f, 0.55f);
                    Handles.DrawWireCube(island.bounds.center, island.bounds.size);
                }
            }
        }

        void DrawPlaneHandles()
        {
            var transform = _renderer.transform;
            var worldPoint = transform.TransformPoint(_options.planePoint);
            var worldNormal = transform.TransformDirection(_options.planeNormal).normalized;
            var rotation = Quaternion.LookRotation(worldNormal);

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                var movedPoint = Handles.PositionHandle(worldPoint, rotation);
                var movedRotation = Handles.RotationHandle(rotation, movedPoint);
                if (!change.changed) return;

                Undo.RecordObject(this, "Move mirror plane");
                _options.planePoint = transform.InverseTransformPoint(movedPoint);
                _options.planeNormal = transform.InverseTransformDirection(movedRotation * Vector3.forward).normalized;
                MarkDirty();
                Repaint();
            }
        }

        void HandlePicking(Matrix4x4 matrix)
        {
            int control = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(control);

            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0 || e.alt) return;

            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            var inverse = matrix.inverse;
            var localRay = new Ray(inverse.MultiplyPoint3x4(ray.origin), inverse.MultiplyVector(ray.direction));

            int island = RaycastIsland(localRay);
            if (island < 0) return;

            if (!_keptAsIs.Remove(island)) _keptAsIs.Add(island);
            MarkDirty();
            e.Use();
            Repaint();
        }

        int RaycastIsland(Ray ray)
        {
            float nearest = float.MaxValue;
            int hitIsland = -1;
            var positions = _snapshot.positions;

            foreach (var submesh in _snapshot.submeshes)
            {
                for (int t = 0; t < submesh.Length; t += 3)
                {
                    if (!RayHitsTriangle(ray, positions[submesh[t]], positions[submesh[t + 1]], positions[submesh[t + 2]], out float distance))
                        continue;
                    if (distance >= nearest) continue;
                    nearest = distance;
                    hitIsland = _islandOfVertex[submesh[t]];
                }
            }
            return hitIsland;
        }

        /// <summary>Möller–Trumbore, hitting either face so back-facing pieces can still be picked.</summary>
        static bool RayHitsTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0f;
            var ab = b - a;
            var ac = c - a;
            var p = Vector3.Cross(ray.direction, ac);
            float determinant = Vector3.Dot(ab, p);
            if (Mathf.Abs(determinant) < 1e-12f) return false;

            float inverse = 1f / determinant;
            var toStart = ray.origin - a;
            float u = Vector3.Dot(toStart, p) * inverse;
            if (u < 0f || u > 1f) return false;

            var q = Vector3.Cross(toStart, ab);
            float v = Vector3.Dot(ray.direction, q) * inverse;
            if (v < 0f || u + v > 1f) return false;

            distance = Vector3.Dot(ac, q) * inverse;
            return distance > 0f;
        }

        // ---- State ------------------------------------------------------------------------

        bool PickFromSelection()
        {
            var go = Selection.activeGameObject;
            if (go == null) return false;
            var renderer = go.GetComponent<SkinnedMeshRenderer>() as Renderer ?? go.GetComponent<MeshRenderer>();
            if (renderer == null || renderer == _renderer) return false;
            SetRenderer(renderer);
            return true;
        }

        void SetRenderer(Renderer renderer)
        {
            if (renderer == _renderer) return;
            StopPreview();
            _renderer = renderer;
            _originalMesh = SourceMeshOf(renderer);
            _snapshot = null;
            _snapshotOf = null;
            _islands = null;
            _islandOfVertex = null;
            _keptAsIs.Clear();
            _report = null;
            _error = null;
            _hasFit = false;
            _scored = false;
        }

        void EnsureSnapshot(Mesh mesh)
        {
            if (_snapshot != null && _snapshotOf == mesh) return;

            _snapshot = MeshSnapshot.Read(mesh, out string error);
            _snapshotOf = mesh;
            _error = error;
            if (_snapshot == null) return;

            float weldTolerance = Mathf.Max(_snapshot.CalculateBounds().size.magnitude * 1e-4f, 1e-6f);
            _islandOfVertex = MeshIslands.Build(_snapshot, weldTolerance, out _islands);
            _keptAsIs.Clear();
            _scored = false;
        }

        void SyncPlane()
        {
            if (!_customPlane) _options.SetAxisPlane(_axis, _axisOffset);
            _options.islandOfVertex = _islandOfVertex;
            _options.keptAsIsIslands = _keptAsIs;
        }

        void MarkDirty()
        {
            SyncPlane();
            _previewDirty = true;
            SceneView.RepaintAll();
        }

        void Fit()
        {
            _error = null;
            if (_snapshot == null) return;

            // Fitting on the regions that are actually being mirrored is what lets a tilted
            // accessory be handled on its own: keep everything else as-is, and the fit follows
            // the piece you are pointing at rather than the whole garment.
            var active = new List<Vector3>(_snapshot.vertexCount);
            for (int i = 0; i < _snapshot.vertexCount; i++)
            {
                int island = _islandOfVertex[i];
                if (island < 0 || _keptAsIs.Contains(island)) continue;
                active.Add(_snapshot.positions[i]);
            }
            if (active.Count == 0)
            {
                _error = "Every region is set to keep as-is, so there is nothing to fit a plane to.";
                return;
            }

            _fit = SymmetryDetector.Fit(active, out _axisScores);
            _hasFit = true;

            _options.planeNormal = _fit.normal;
            _options.planePoint = _fit.point;
            _options.seamTolerance = _fit.tolerance;

            _axis = _fit.DominantAxis;
            _axisOffset = Vector3.Dot(_fit.point, _fit.normal);
            // Only hand over to the free plane when the tilt is real; a straight axis stays
            // easier to reason about and to nudge by hand.
            _customPlane = _fit.tiltDegrees > 0.25f;

            MarkDirty();
        }

        void ScoreRegions()
        {
            if (_snapshot == null) return;
            SyncPlane();
            MeshIslands.ScoreAgainstPlane(_snapshot, _islandOfVertex, _islands,
                _options.planeNormal, _options.planePoint, Mathf.Max(_options.seamTolerance, 1e-5f));
            _scored = true;
            Repaint();
        }

        void AutoKeepOneSidedRegions()
        {
            if (!_scored) ScoreRegions();
            _keptAsIs.Clear();
            foreach (var island in _islands)
                if (island.symmetryScore >= 0f && island.symmetryScore < 0.5f) _keptAsIs.Add(island.index);
            MarkDirty();
        }

        // ---- Build ------------------------------------------------------------------------

        Mesh BuildResult()
        {
            _error = null;
            if (_snapshot == null) return null;
            SyncPlane();

            int[] boneMirror = null;
            if (_renderer is SkinnedMeshRenderer skinned && _options.mirrorBoneWeights)
                boneMirror = MeshSymmetrizer.BuildBoneMirrorMap(skinned.bones, out _);

            var report = new SymmetrizeReport();
            var result = MeshSymmetrizer.Build(_snapshot, _options, boneMirror, report);
            _report = report;
            if (result == null && report.warnings.Count > 0) _error = report.warnings[0];
            return result;
        }

        void StartPreview()
        {
            if (_renderer == null) return;
            _originalMesh = SourceMeshOf(_renderer);

            var result = BuildResult();
            if (result == null) return;

            result.hideFlags = HideFlags.HideAndDontSave;
            _previewMesh = result;
            AssignMesh(_renderer, _previewMesh, withUndo: false);
        }

        void RefreshPreview()
        {
            if (!IsPreviewing) return;
            var previous = _previewMesh;
            _previewMesh = null;
            AssignMesh(_renderer, _originalMesh, withUndo: false);
            if (previous != null) DestroyImmediate(previous);
            StartPreview();
        }

        void StopPreview()
        {
            if (!IsPreviewing) return;
            var preview = _previewMesh;
            _previewMesh = null;
            if (_renderer != null && _originalMesh != null) AssignMesh(_renderer, _originalMesh, withUndo: false);
            if (preview != null) DestroyImmediate(preview);
        }

        void Bake()
        {
            // Bake from the pristine source, not from whatever the preview left assigned.
            if (IsPreviewing) StopPreview();

            var result = BuildResult();
            if (result == null) return;

            var folder = string.IsNullOrEmpty(_outputFolder) ? DefaultOutputFolder : _outputFolder.Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets"))
            {
                _error = "The output folder has to be inside Assets.";
                DestroyImmediate(result);
                return;
            }

            EnsureFolder(folder);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{result.name}.asset");
            AssetDatabase.CreateAsset(result, path);
            AssetDatabase.SaveAssets();

            Undo.RecordObject(_renderer, "Symmetrize Mesh");
            AssignMesh(_renderer, result, withUndo: true);
            _originalMesh = result;
            _snapshot = null; // the renderer now points at the rebuilt mesh; re-read it

            EditorUtility.SetDirty(_renderer);
            if (!EditorUtility.IsPersistent(_renderer))
                EditorSceneManager.MarkSceneDirty(_renderer.gameObject.scene);

            EditorGUIUtility.PingObject(result);
            Debug.Log($"[Lumi Mesh Tools] Symmetrized mesh saved to {path}", result);
        }

        // ---- Helpers ----------------------------------------------------------------------

        static Mesh SourceMeshOf(Renderer renderer)
        {
            switch (renderer)
            {
                case null: return null;
                case SkinnedMeshRenderer skinned: return skinned.sharedMesh;
                default:
                    var filter = renderer.GetComponent<MeshFilter>();
                    return filter != null ? filter.sharedMesh : null;
            }
        }

        static void AssignMesh(Renderer renderer, Mesh mesh, bool withUndo)
        {
            if (renderer == null) return;
            if (renderer is SkinnedMeshRenderer skinned)
            {
                // A Skinned Mesh Renderer keeps a skinning buffer sized for the mesh it already
                // had. Handing it one with a different vertex count without clearing the old
                // reference first leaves that buffer stale and the renderer quietly stops drawing
                // — one line in the console and an invisible garment. Clearing makes it rebuild.
                skinned.sharedMesh = null;
                skinned.sharedMesh = mesh;
                return;
            }
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null) return;
            if (withUndo) Undo.RecordObject(filter, "Symmetrize Mesh");
            filter.sharedMesh = mesh;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            var path = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = path + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(path, parts[i]);
                path = next;
            }
        }

        static string ToProjectRelativePath(string absolute)
        {
            var project = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            var picked = Path.GetFullPath(absolute).Replace('\\', '/');
            if (!picked.StartsWith(project)) return null;
            return "Assets" + picked.Substring(project.Length);
        }

        static string NegativeSideLabel(int axis)
        {
            switch (axis)
            {
                case 0: return "−X  (avatar's left)";
                case 1: return "−Y  (bottom)";
                default: return "−Z  (back)";
            }
        }

        static string PositiveSideLabel(int axis)
        {
            switch (axis)
            {
                case 0: return "+X  (avatar's right)";
                case 1: return "+Y  (top)";
                default: return "+Z  (front)";
            }
        }
    }
}
