using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Proportional editing for a mesh in the scene: grab a ring or a patch, move, turn or scale
    /// it, and the surrounding surface follows by however much the falloff says.
    ///
    /// Written for the garment that is simply worn crooked — a hem or a collar sitting high on
    /// one side and low on the other. Select the ring, press Level, and the body of the garment
    /// above comes along with it.
    /// </summary>
    public sealed class ProportionalEditWindow : EditorWindow
    {
        const string DefaultOutputFolder = "Assets/LumiMeshTools/Generated";
        const int MaxUndoSteps = 24;

        enum SelectMode { Loop, Island, Brush, Band }
        enum HandleMode { Move, Rotate, Scale }

        [SerializeField] Renderer _renderer;
        [SerializeField] string _outputFolder = DefaultOutputFolder;
        [SerializeField] SelectMode _selectMode = SelectMode.Loop;
        [SerializeField] HandleMode _handleMode = HandleMode.Rotate;
        [SerializeField] float _brushRadius = 0.02f;
        [SerializeField] float _stitchDistance;
        [SerializeField] bool _fixNormals = true;
        [SerializeField] bool _showLoops = true;
        [SerializeField] List<Renderer> _bodyRenderers = new List<Renderer>();
        [SerializeField] Transform _planeSource;
        [SerializeField] bool _rigidTrim = true;
        [SerializeField] float _clearanceWeight = 1f;
        [SerializeField] bool _showBodySection = true;
        [SerializeField] int _bandAxis = 1;        // 0 right, 1 up, 2 forward, in world terms
        [SerializeField] bool _bandAbove = true;
        [SerializeField] float _bandCut = 0.62f;   // where the cut sits across the mesh, 0..1

        readonly ProportionalEdit.Settings _settings = new ProportionalEdit.Settings();
        readonly HashSet<int> _selection = new HashSet<int>();
        readonly List<Vector3[]> _undo = new List<Vector3[]>();

        MeshSnapshot _snapshot;
        Mesh _snapshotOf;
        MeshGraph _graph;
        int[] _islandOfVertex;

        Vector3[] _committed;   // positions after every applied edit
        Vector3[] _working;     // committed, plus whatever the handle is doing right now
        Vector3[] _normals;
        float[] _weights;
        float[] _touched;       // the most any vertex has ever been moved by, for normals

        Vector3 _pivot;
        Vector3 _handlePosition;
        Quaternion _handleRotation = Quaternion.identity;
        Vector3 _handleScale = Vector3.one;

        BodyReference _body;
        HashSet<int> _trimShells;
        string _fitSummary;

        Mesh _originalMesh;
        Mesh _previewMesh;
        string _error;
        Vector2 _scroll;

        [MenuItem("Tools/Lumi Mesh Tools/Proportional Edit")]
        public static void Open()
        {
            var window = GetWindow<ProportionalEditWindow>();
            window.titleContent = new GUIContent("Proportional Edit");
            window.minSize = new Vector2(380f, 460f);
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
            if (_previewMesh != null) return; // don't yank the model out from under an edit
            if (PickFromSelection()) Repaint();
        }

        bool HasPendingEdit =>
            _handlePosition != _pivot || _handleRotation != Quaternion.identity || _handleScale != Vector3.one;

        // ---- GUI ----------------------------------------------------------------------------

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                var picked = (Renderer)EditorGUILayout.ObjectField("Renderer", _renderer, typeof(Renderer), true);
                if (change.changed) SetRenderer(picked);
            }

            // While a preview is live the renderer is holding our throwaway copy. Asking the
            // renderer for "the" mesh would feed that copy back in as the source, and since it is
            // never the mesh we snapshotted, the re-read below would fire every single frame.
            var sourceMesh = _previewMesh != null ? _originalMesh : SourceMeshOf(_renderer);
            if (_previewMesh == null && _renderer != null) _originalMesh = sourceMesh;
            if (_renderer == null || sourceMesh == null)
            {
                EditorGUILayout.HelpBox(
                    _renderer == null
                        ? "Pick a Skinned Mesh Renderer or a Mesh Renderer to edit."
                        : "That renderer has no mesh assigned.",
                    _renderer == null ? MessageType.Info : MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            EnsureMesh(sourceMesh);
            if (_snapshot == null)
            {
                EditorGUILayout.HelpBox(_error ?? "Could not read that mesh.", MessageType.Error);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.LabelField(
                $"{sourceMesh.name} — {sourceMesh.vertexCount:N0} verts, {sourceMesh.subMeshCount} submesh(es), " +
                $"{_graph.boundaryLoops.Count} open loop(s)",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();
            DrawSelectionSection();

            EditorGUILayout.Space();
            DrawFalloffSection();

            EditorGUILayout.Space();
            DrawBodySection();

            EditorGUILayout.Space();
            DrawEditSection();

            EditorGUILayout.Space();
            DrawOutputSection();

            if (!string.IsNullOrEmpty(_error))
                EditorGUILayout.HelpBox(_error, MessageType.Error);

            EditorGUILayout.EndScrollView();
        }

        void DrawSelectionSection()
        {
            EditorGUILayout.LabelField("Selection", EditorStyles.boldLabel);

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                _selectMode = (SelectMode)GUILayout.Toolbar((int)_selectMode,
                    new[] { "Loop", "Island", "Brush", "Band" }, GUILayout.Height(20f));
                if (change.changed)
                {
                    if (_selectMode == SelectMode.Band) SelectBand();
                    SceneView.RepaintAll();
                }
            }

            switch (_selectMode)
            {
                case SelectMode.Loop:
                    EditorGUILayout.LabelField(
                        "Click near a hem, collar or cuff in the scene to grab that whole ring.",
                        EditorStyles.miniLabel);
                    break;
                case SelectMode.Island:
                    EditorGUILayout.LabelField("Click a piece to select the whole connected shell.", EditorStyles.miniLabel);
                    break;
                case SelectMode.Brush:
                    EditorGUILayout.LabelField("Drag on the model to add, hold Shift to remove.", EditorStyles.miniLabel);
                    using (var change = new EditorGUI.ChangeCheckScope())
                    {
                        _brushRadius = EditorGUILayout.FloatField("Brush radius", _brushRadius);
                        if (change.changed) SceneView.RepaintAll();
                    }
                    break;
                case SelectMode.Band:
                    EditorGUILayout.LabelField(
                        "Everything past a cut line. Click the model where the crooked part starts, " +
                        "or drag the slider.", EditorStyles.miniLabel);
                    using (var change = new EditorGUI.ChangeCheckScope())
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            _bandAxis = EditorGUILayout.Popup("Direction", _bandAxis,
                                new[] { "Left / right", "Up / down", "Front / back" });
                            _bandAbove = GUILayout.Toggle(_bandAbove,
                                _bandAbove ? "Keep the far side" : "Keep the near side",
                                EditorStyles.miniButton, GUILayout.Width(120f));
                        }
                        _bandCut = EditorGUILayout.Slider("Cut at", _bandCut, 0f, 1f);
                        if (change.changed) SelectBand();
                    }
                    break;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{_selection.Count:N0} point(s) selected", EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(_selection.Count == 0))
                {
                    if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(60f)))
                    {
                        CancelEdit();
                        _selection.Clear();
                        RebuildWeights();
                    }
                }
            }

            _showLoops = EditorGUILayout.ToggleLeft("Show open loops in the scene", _showLoops);
        }

        void DrawFalloffSection()
        {
            EditorGUILayout.LabelField("Falloff", EditorStyles.boldLabel);

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                _settings.plateau = EditorGUILayout.FloatField(
                    new GUIContent("Offset", "How far past the selection the correction stays at full strength before it starts to fade. Without this the surface just outside the selection barely moves while the selection moves fully, and that step shows up as a crease."),
                    _settings.plateau);
                _settings.radius = EditorGUILayout.FloatField(
                    new GUIContent("Radius", "How far the fade itself runs, past the offset."),
                    _settings.radius);
                _settings.curve = (FalloffCurve)EditorGUILayout.EnumPopup("Curve", _settings.curve);
                _settings.alongSurface = EditorGUILayout.Toggle(
                    new GUIContent("Along the surface", "Measure distance across the fabric instead of straight through space, so an edit on a hem does not reach the far side of the skirt."),
                    _settings.alongSurface);

                if (change.changed) RebuildWeights();
            }

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                _stitchDistance = EditorGUILayout.FloatField(
                    new GUIContent("Stitch pieces within",
                        "Lace, charms and straps are usually separate shells laid on the garment. Measuring along the surface would walk straight past them and leave them behind, so anything this close to another piece is treated as attached to it."),
                    _stitchDistance);
                if (change.changed) RebuildGraph();
            }

            if (GUILayout.Button("Suggest sizes from the mesh", EditorStyles.miniButton))
            {
                float extent = _snapshot.CalculateBounds().size.magnitude;
                _settings.plateau = extent * 0.02f;
                _settings.radius = extent * 0.15f;
                RebuildWeights();
            }

            int reached = 0;
            if (_weights != null)
                foreach (float w in _weights) if (w > 0f) reached++;
            EditorGUILayout.LabelField($"{reached:N0} vertices are within reach", EditorStyles.miniLabel);
        }

        void DrawBodySection()
        {
            _showBodySection = EditorGUILayout.Foldout(_showBodySection, "Body reference", true,
                EditorStyles.foldoutHeader);
            if (!_showBodySection) return;

            EditorGUILayout.HelpBox(
                "A garment is rarely symmetric enough to say where its own centre is, so the mirror " +
                "plane is taken from the body instead. The body is also what keeps the correction " +
                "wearable: without it, levelling a waistband can lift it clean off the hips.",
                MessageType.None);

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                for (int i = 0; i < _bodyRenderers.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _bodyRenderers[i] = (Renderer)EditorGUILayout.ObjectField(
                            i == 0 ? "Body meshes" : " ", _bodyRenderers[i], typeof(Renderer), true);
                        if (GUILayout.Button("-", GUILayout.Width(22f)))
                        {
                            _bodyRenderers.RemoveAt(i);
                            GUI.changed = true;
                            break;
                        }
                    }
                }
                if (GUILayout.Button("Add a body mesh"))
                {
                    _bodyRenderers.Add(null);
                    GUI.changed = true;
                }

                _planeSource = (Transform)EditorGUILayout.ObjectField(
                    new GUIContent("Centre from",
                        "Transform whose right axis seeds the mirror plane - the avatar root, or its " +
                        "hips. The exact offset is then fitted to the body mesh itself."),
                    _planeSource, typeof(Transform), true);

                if (change.changed)
                {
                    _body = null;
                    _fitSummary = null;
                }
            }

            var bodies = ActiveBodyRenderers();
            if (bodies.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Assign the avatar's body mesh - the skin, not clothing. On avatars split into " +
                    "several skins, list every one the garment overlaps.",
                    MessageType.Info);
                return;
            }

            EnsureBody();
            if (_body != null && !float.IsNaN(_body.symmetryResidual))
                EditorGUILayout.LabelField(
                    $"Reference: {_body.positions.Length:N0} body points, symmetric to " +
                    $"{_body.symmetryResidual * 1000f:0.00} mm",
                    EditorStyles.miniLabel);
            if (_body != null)
                foreach (var warning in _body.warnings)
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);

            _clearanceWeight = EditorGUILayout.Slider(
                new GUIContent("Stay on the body",
                    "How much keeping an even gap to the body matters against getting symmetric. " +
                    "Raise it if the fit pulls the garment off the skin; lower it if the garment " +
                    "stays stubbornly crooked."),
                _clearanceWeight, 0f, 4f);

            using (new EditorGUI.DisabledScope(_selection.Count == 0 || _body == null || !_body.IsUsable))
            {
                if (GUILayout.Button(new GUIContent("Fit to body",
                    "Solves for the rotation and offset that make the selection sit symmetrically " +
                    "on the body without lifting off it. Nothing is replaced - the result is an " +
                    "ordinary edit you can still adjust, apply or cancel."), GUILayout.Height(24f)))
                {
                    FitToBody();
                }
            }

            if (!string.IsNullOrEmpty(_fitSummary))
                EditorGUILayout.HelpBox(_fitSummary, MessageType.Info);
        }

        List<Renderer> ActiveBodyRenderers()
        {
            var result = new List<Renderer>();
            foreach (var r in _bodyRenderers)
                if (r != null && !result.Contains(r)) result.Add(r);
            return result;
        }

        void EnsureBody()
        {
            if (_body != null || _renderer == null) return;
            var bodies = ActiveBodyRenderers();
            if (bodies.Count == 0) return;

            var source = _planeSource;
            if (source == null && _renderer is SkinnedMeshRenderer skinned && skinned.rootBone != null)
                source = skinned.rootBone;
            _body = BodyReference.Build(_renderer, bodies, source);
        }

        void FitToBody()
        {
            EnsureBody();
            if (_body == null || !_body.IsUsable || _committed == null || _weights == null) return;

            var settings = new BodyFit.Settings { clearanceWeight = _clearanceWeight };
            var report = BodyFit.Solve(_committed, _weights, _pivot, _body, settings);

            _handlePosition = report.position;
            _handleRotation = report.rotation;
            _handleScale = Vector3.one;
            UpdateWorking();

            float angle = Quaternion.Angle(Quaternion.identity, report.rotation);
            float shift = (report.position - _pivot).magnitude * 1000f;
            _fitSummary =
                $"Turned {angle:0.0} deg, moved {shift:0.0} mm.\n" +
                $"Off-centre: {report.mirrorBefore * 1000f:0.0} mm -> {report.mirrorAfter * 1000f:0.0} mm.  " +
                $"Unevenness against the body: {report.clearanceBefore * 1000f:0.0} mm -> " +
                $"{report.clearanceAfter * 1000f:0.0} mm.\n" +
                "Apply to keep it, Cancel to back it out. What is left over is the two sides " +
                "genuinely being different shapes, which is the design and should stay.";
            Repaint();
        }

        void DrawEditSection()
        {
            EditorGUILayout.LabelField("Edit", EditorStyles.boldLabel);

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                _handleMode = (HandleMode)GUILayout.Toolbar((int)_handleMode,
                    new[] { "Move", "Rotate", "Scale" }, GUILayout.Height(20f));
                if (change.changed) SceneView.RepaintAll();
            }

            using (new EditorGUI.DisabledScope(_selection.Count < 3))
            {
                if (GUILayout.Button(new GUIContent("Level the selection",
                    "Fits a plane to the selected points and turns it square to the nearest axis — the fix for a hem or collar worn higher on one side.")))
                {
                    LevelSelection();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!HasPendingEdit))
                {
                    if (GUILayout.Button("Apply", GUILayout.Height(24f))) CommitEdit();
                    if (GUILayout.Button("Cancel", GUILayout.Height(24f))) CancelEdit();
                }
                using (new EditorGUI.DisabledScope(_undo.Count == 0))
                {
                    if (GUILayout.Button("Undo", GUILayout.Height(24f))) UndoEdit();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset all")) ResetAll();
                using (new EditorGUI.DisabledScope(_selection.Count == 0))
                {
                    _fixNormals = GUILayout.Toggle(_fixNormals, "Fix normals", EditorStyles.miniButton);
                    using (var trimChange = new EditorGUI.ChangeCheckScope())
                    {
                        _rigidTrim = GUILayout.Toggle(_rigidTrim, new GUIContent("Rigid trim",
                            "Move lace, buckles and charms as whole pieces instead of stretching " +
                            "them across the falloff."), EditorStyles.miniButton);
                        if (trimChange.changed) UpdateWorking();
                    }
                }
                if (GUILayout.Button("Bake & Apply")) Bake();
            }

            EditorGUILayout.LabelField($"{_undo.Count} applied edit(s)", EditorStyles.miniLabel);

            if (_previewMesh != null)
                EditorGUILayout.HelpBox(
                    "Editing an unsaved copy of the mesh. Bake to keep the result — closing this window puts the original back.",
                    MessageType.Info);
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
                    if (string.IsNullOrEmpty(picked)) return;
                    var relative = ToProjectRelativePath(picked);
                    if (relative != null) _outputFolder = relative;
                    else _error = "Pick a folder inside this project's Assets folder.";
                }
            }
        }

        // ---- Scene --------------------------------------------------------------------------

        void OnSceneGui(SceneView view)
        {
            if (_renderer == null || _snapshot == null || _graph == null) return;

            var matrix = _renderer.transform.localToWorldMatrix;
            using (new Handles.DrawingScope(matrix))
            {
                DrawLoops();
                DrawSelection();
                DrawHandle();
            }

            if (GUIUtility.hotControl == 0) HandlePicking(matrix);
        }

        void DrawLoops()
        {
            if (!_showLoops) return;
            Handles.color = new Color(0.3f, 0.75f, 1f, 0.5f);
            foreach (var loop in _graph.boundaryLoops)
            {
                if (loop.Length < 3) continue;
                var points = new Vector3[loop.Length + 1];
                for (int i = 0; i < loop.Length; i++) points[i] = _graph.positionOfGroup[loop[i]];
                points[loop.Length] = points[0];
                Handles.DrawAAPolyLine(2f, points);
            }
        }

        void DrawSelection()
        {
            if (_selection.Count == 0) return;

            Handles.color = new Color(1f, 0.7f, 0.15f, 0.95f);
            foreach (var loop in _graph.boundaryLoops)
            {
                if (!_selection.Contains(loop[0])) continue;
                var points = new Vector3[loop.Length + 1];
                for (int i = 0; i < loop.Length; i++) points[i] = _graph.positionOfGroup[loop[i]];
                points[loop.Length] = points[0];
                Handles.DrawAAPolyLine(4f, points);
            }

            // Drawing every selected point on a dense brush selection would crawl, and the
            // outline of where it sits reads better than a cloud of dots anyway.
            var bounds = new Bounds(_pivot, Vector3.zero);
            foreach (int g in _selection) bounds.Encapsulate(_graph.positionOfGroup[g]);
            Handles.color = new Color(1f, 0.7f, 0.15f, 0.4f);
            Handles.DrawWireCube(bounds.center, bounds.size);
        }

        void DrawHandle()
        {
            if (_selection.Count == 0) return;

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                switch (_handleMode)
                {
                    case HandleMode.Move:
                        _handlePosition = Handles.PositionHandle(_handlePosition, _handleRotation);
                        break;
                    case HandleMode.Rotate:
                        _handleRotation = Handles.RotationHandle(_handleRotation, _handlePosition);
                        break;
                    default:
                        float size = HandleUtility.GetHandleSize(_handlePosition);
                        _handleScale = Handles.ScaleHandle(_handleScale, _handlePosition, _handleRotation, size);
                        break;
                }

                if (!change.changed) return;
                UpdateWorking();
                Repaint();
            }
        }

        void HandlePicking(Matrix4x4 matrix)
        {
            int control = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(control);

            var e = Event.current;
            bool drag = _selectMode == SelectMode.Brush && e.type == EventType.MouseDrag && e.button == 0;
            if ((e.type != EventType.MouseDown || e.button != 0 || e.alt) && !drag) return;

            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            var inverse = matrix.inverse;
            var localRay = new Ray(inverse.MultiplyPoint3x4(ray.origin), inverse.MultiplyVector(ray.direction));

            if (!MeshRaycast.Raycast(_snapshot, localRay, out int vertex, out Vector3 point)) return;

            // A new selection replaces the pending edit's frame of reference, so settle it first.
            if (HasPendingEdit && !drag) CommitEdit();

            switch (_selectMode)
            {
                case SelectMode.Loop:
                {
                    int loop = _graph.NearestBoundaryLoop(point);
                    if (loop < 0)
                    {
                        _error = "This mesh has no open edge loops to grab.";
                        return;
                    }
                    if (!e.shift) _selection.Clear();
                    foreach (int g in _graph.boundaryLoops[loop]) _selection.Add(g);
                    break;
                }
                case SelectMode.Island:
                {
                    int island = _islandOfVertex[vertex];
                    if (!e.shift) _selection.Clear();
                    for (int i = 0; i < _islandOfVertex.Length; i++)
                        if (_islandOfVertex[i] == island) _selection.Add(_graph.groupOfVertex[i]);
                    break;
                }
                case SelectMode.Band:
                {
                    var axis = BandAxis();
                    MeasureBand(axis, out float low, out float high);
                    if (high - low > 1e-6f)
                        _bandCut = Mathf.Clamp01((Vector3.Dot(point, axis) - low) / (high - low));
                    SelectBand();
                    break;
                }
                default:
                    if (e.shift) ProportionalEdit.RemoveWithin(_graph, point, _brushRadius, _selection);
                    else ProportionalEdit.AddWithin(_graph, point, _brushRadius, _selection);
                    break;
            }

            _error = null;
            RebuildWeights();
            e.Use();
            Repaint();
        }

        /// <summary>
        /// The chosen world direction, in the mesh's own space. Garments are modelled in every
        /// orientation going — the piece this was built against has its own +Z pointing at the
        /// ceiling — so the direction has to be named in world terms and converted, not assumed.
        /// </summary>
        Vector3 BandAxis()
        {
            var world = _bandAxis == 0 ? Vector3.right : _bandAxis == 2 ? Vector3.forward : Vector3.up;
            if (_renderer == null) return world;
            var local = _renderer.transform.worldToLocalMatrix.MultiplyVector(world);
            return local.sqrMagnitude > 1e-8f ? local.normalized : world;
        }

        void MeasureBand(Vector3 axis, out float low, out float high)
        {
            low = float.MaxValue;
            high = float.MinValue;
            if (_graph == null) return;
            for (int g = 0; g < _graph.groupCount; g++)
            {
                float d = Vector3.Dot(_graph.positionOfGroup[g], axis);
                if (d < low) low = d;
                if (d > high) high = d;
            }
        }

        void SelectBand()
        {
            if (_graph == null) return;
            if (HasPendingEdit) CommitEdit();

            var axis = BandAxis();
            MeasureBand(axis, out float low, out float high);
            if (high - low <= 1e-6f) return;

            float cut = Mathf.Lerp(low, high, _bandCut);
            _selection.Clear();
            for (int g = 0; g < _graph.groupCount; g++)
            {
                float d = Vector3.Dot(_graph.positionOfGroup[g], axis);
                if (_bandAbove ? d >= cut : d <= cut) _selection.Add(g);
            }

            _error = null;
            RebuildWeights();
            SceneView.RepaintAll();
        }

        // ---- State --------------------------------------------------------------------------

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
            _body = null;
            _trimShells = null;
            _fitSummary = null;
            _originalMesh = SourceMeshOf(renderer);
            _snapshot = null;
            _snapshotOf = null;
            _graph = null;
            _selection.Clear();
            _undo.Clear();
            _error = null;
        }

        void EnsureMesh(Mesh mesh)
        {
            // Our own preview copy is never a source: re-snapshotting it would fold the preview
            // back into the thing being previewed.
            if (mesh == null || mesh == _previewMesh) return;
            if (_snapshot != null && _snapshotOf == mesh) return;

            _snapshot = MeshSnapshot.Read(mesh, out string error);
            _snapshotOf = mesh;
            _error = error;
            if (_snapshot == null) return;

            float extent = _snapshot.CalculateBounds().size.magnitude;
            float weldTolerance = Mathf.Max(extent * 1e-4f, 1e-6f);
            if (_stitchDistance <= 0f) _stitchDistance = extent * 0.01f;
            _graph = MeshGraph.Build(_snapshot, weldTolerance, _stitchDistance);
            _islandOfVertex = MeshIslands.Build(_snapshot, weldTolerance, out _);
            _trimShells = null;

            _committed = (Vector3[])_snapshot.positions.Clone();
            _working = (Vector3[])_committed.Clone();
            _normals = _snapshot.normals != null ? (Vector3[])_snapshot.normals.Clone() : null;
            _touched = new float[_committed.Length];
            _selection.Clear();
            _undo.Clear();

            if (_settings.radius <= 0f) _settings.radius = extent * 0.15f;
            if (_brushRadius <= 0f) _brushRadius = extent * 0.02f;

            RebuildWeights();
        }

        void RebuildGraph()
        {
            if (_snapshot == null) return;
            float extent = _snapshot.CalculateBounds().size.magnitude;
            float weldTolerance = Mathf.Max(extent * 1e-4f, 1e-6f);
            _graph = MeshGraph.Build(_snapshot, weldTolerance, Mathf.Max(0f, _stitchDistance));
            _graph.RefreshPositions(_committed);
            RebuildWeights();
        }

        void RebuildWeights()
        {
            if (_graph == null) return;
            _weights = ProportionalEdit.BuildWeights(_graph, _selection, _settings);
            _pivot = ProportionalEdit.Centre(_graph, _selection);
            ResetHandle();
            UpdateWorking();
        }

        void ResetHandle()
        {
            _handlePosition = _pivot;
            _handleRotation = Quaternion.identity;
            _handleScale = Vector3.one;
        }

        void UpdateWorking()
        {
            if (_committed == null) return;

            if (_weights == null || _selection.Count == 0)
            {
                System.Array.Copy(_committed, _working, _committed.Length);
            }
            else
            {
                var transform = ProportionalEdit.HandleTransform(_pivot, _handlePosition, _handleRotation, _handleScale);
                ProportionalEdit.Apply(_working, _committed, _weights, transform);

                if (_rigidTrim)
                {
                    if (_trimShells == null) _trimShells = ProportionalEdit.TrimShells(_islandOfVertex);
                    ProportionalEdit.ApplyRigidShells(_working, _committed, _weights, _islandOfVertex,
                        _trimShells, _pivot, _handlePosition, _handleRotation, _handleScale);
                }
            }

            PushToPreview();
        }

        void PushToPreview()
        {
            if (_renderer == null || _originalMesh == null) return;

            if (_previewMesh == null)
            {
                _previewMesh = Instantiate(_originalMesh);
                _previewMesh.name = _originalMesh.name + " (editing)";
                _previewMesh.hideFlags = HideFlags.HideAndDontSave;
                AssignMesh(_renderer, _previewMesh, withUndo: false);
            }

            _previewMesh.SetVertices(_working);

            if (_fixNormals && _normals != null)
            {
                var blend = new float[_touched.Length];
                for (int i = 0; i < blend.Length; i++)
                    blend[i] = Mathf.Max(_touched[i], _weights != null ? _weights[i] : 0f);

                var updated = new Vector3[_normals.Length];
                ProportionalEdit.BlendNormals(_graph, _working, _snapshot.submeshes, _snapshot.normals, blend, updated);
                _previewMesh.SetNormals(updated);
            }

            _previewMesh.RecalculateBounds();
            SceneView.RepaintAll();
        }

        void CommitEdit()
        {
            if (!HasPendingEdit) return;

            if (_undo.Count >= MaxUndoSteps) _undo.RemoveAt(0);
            _undo.Add((Vector3[])_committed.Clone());

            System.Array.Copy(_working, _committed, _working.Length);
            if (_weights != null)
                for (int i = 0; i < _touched.Length; i++) _touched[i] = Mathf.Max(_touched[i], _weights[i]);

            // The surface has moved, so distances and the pivot have to be measured on the new shape.
            _graph.RefreshPositions(_committed);
            _pivot = ProportionalEdit.Centre(_graph, _selection);
            _weights = ProportionalEdit.BuildWeights(_graph, _selection, _settings);

            ResetHandle();
            UpdateWorking();
        }

        void CancelEdit()
        {
            ResetHandle();
            UpdateWorking();
        }

        void UndoEdit()
        {
            if (_undo.Count == 0) return;
            _committed = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _graph.RefreshPositions(_committed);
            RebuildWeights();
        }

        void ResetAll()
        {
            if (_snapshot == null) return;
            _committed = (Vector3[])_snapshot.positions.Clone();
            _touched = new float[_committed.Length];
            _undo.Clear();
            _graph.RefreshPositions(_committed);
            RebuildWeights();
        }

        void LevelSelection()
        {
            var points = new List<Vector3>(_selection.Count);
            foreach (int g in _selection) points.Add(_graph.positionOfGroup[g]);
            if (points.Count < 3) return;

            // The least-spread principal axis of a ring is the normal of the plane it lies in.
            var centroid = SymmetryDetector.Centroid(points);
            var axes = SymmetryDetector.PrincipalAxes(points, centroid);
            var normal = axes[2];

            var target = NearestAxis(normal);
            if (Vector3.Dot(normal, target) < 0f) target = -target;

            _handlePosition = _pivot;
            _handleRotation = Quaternion.FromToRotation(normal, target);
            _handleScale = Vector3.one;
            UpdateWorking();
        }

        static Vector3 NearestAxis(Vector3 direction)
        {
            float x = Mathf.Abs(direction.x), y = Mathf.Abs(direction.y), z = Mathf.Abs(direction.z);
            if (x >= y && x >= z) return Vector3.right;
            return y >= z ? Vector3.up : Vector3.forward;
        }

        void StopPreview()
        {
            if (_previewMesh == null) return;
            var preview = _previewMesh;
            _previewMesh = null;
            if (_renderer != null && _originalMesh != null) AssignMesh(_renderer, _originalMesh, withUndo: false);
            DestroyImmediate(preview);
        }

        void Bake()
        {
            if (_snapshot == null) return;
            CommitEdit();

            var folder = string.IsNullOrEmpty(_outputFolder)
                ? DefaultOutputFolder
                : _outputFolder.Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets"))
            {
                _error = "The output folder has to be inside Assets.";
                return;
            }

            // Cloning the source carries every channel the editor never touched — blend shapes,
            // UVs, bind poses — so only the positions and normals need writing back.
            var baked = Instantiate(_originalMesh);
            baked.name = _originalMesh.name + "_Edited";
            baked.SetVertices(_committed);

            if (_fixNormals && _snapshot.normals != null)
            {
                var updated = new Vector3[_snapshot.normals.Length];
                ProportionalEdit.BlendNormals(_graph, _committed, _snapshot.submeshes, _snapshot.normals, _touched, updated);
                baked.SetNormals(updated);
            }
            baked.RecalculateBounds();

            EnsureFolder(folder);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{baked.name}.asset");
            AssetDatabase.CreateAsset(baked, path);
            AssetDatabase.SaveAssets();

            StopPreview();

            Undo.RecordObject(_renderer, "Proportional Edit");
            AssignMesh(_renderer, baked, withUndo: true);
            _originalMesh = baked;
            _snapshot = null; // the renderer points at the edited mesh now; re-read it

            EditorUtility.SetDirty(_renderer);
            if (!EditorUtility.IsPersistent(_renderer))
                EditorSceneManager.MarkSceneDirty(_renderer.gameObject.scene);

            EditorGUIUtility.PingObject(baked);
            Debug.Log($"[Lumi Mesh Tools] Edited mesh saved to {path}", baked);
        }

        // ---- Helpers ------------------------------------------------------------------------

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
            if (withUndo) Undo.RecordObject(filter, "Proportional Edit");
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
    }
}
