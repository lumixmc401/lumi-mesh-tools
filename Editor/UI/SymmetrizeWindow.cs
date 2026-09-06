using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Front end for <see cref="MeshSymmetrizer"/>: pick a renderer, pick the half to keep,
    /// look at it in the scene, then bake the result to a mesh asset.
    /// </summary>
    public sealed class SymmetrizeWindow : EditorWindow
    {
        const string DefaultOutputFolder = "Assets/LumiMeshTools/Generated";

        [SerializeField] Renderer _renderer;
        [SerializeField] string _outputFolder = DefaultOutputFolder;
        [SerializeField] bool _showAdvanced;

        readonly SymmetrizeOptions _options = new SymmetrizeOptions();

        Mesh _originalMesh;
        Mesh _previewMesh;
        SymmetrizeReport _report;
        string _error;
        List<SymmetryDetector.Candidate> _candidates;
        Vector2 _scroll;

        [MenuItem("Tools/Lumi Mesh Tools/Symmetrize")]
        public static void Open()
        {
            var window = GetWindow<SymmetrizeWindow>();
            window.titleContent = new GUIContent("Symmetrize");
            window.minSize = new Vector2(360f, 420f);
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
            if (IsPreviewing) return; // don't yank the preview out from under the user
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

            EditorGUILayout.LabelField($"{sourceMesh.name} — {sourceMesh.vertexCount:N0} verts, " +
                                       $"{sourceMesh.subMeshCount} submesh(es), {sourceMesh.blendShapeCount} blend shape(s)",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();
            DrawMirrorPlaneSection();

            EditorGUILayout.Space();
            DrawSideSection();

            EditorGUILayout.Space();
            DrawOptionsSection();

            EditorGUILayout.Space();
            DrawOutputSection();

            EditorGUILayout.Space();
            DrawActions();

            if (!string.IsNullOrEmpty(_error))
                EditorGUILayout.HelpBox(_error, MessageType.Error);

            DrawReport();

            EditorGUILayout.EndScrollView();
        }

        void DrawMirrorPlaneSection()
        {
            EditorGUILayout.LabelField("Mirror plane", EditorStyles.boldLabel);

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                _options.axis = EditorGUILayout.Popup("Axis", _options.axis, new[] { "X", "Y", "Z" });
                _options.planeOffset = EditorGUILayout.FloatField("Offset", _options.planeOffset);
                if (change.changed) RefreshPreview();
            }

            if (GUILayout.Button("Detect automatically")) Detect();

            if (_candidates == null || _candidates.Count == 0) return;

            var best = _candidates[0];
            var message = $"Best guess: {best.AxisName} at {best.offset:0.####} — {best.score:P0} of vertices already have a mirror partner.";
            var type = best.score > 0.85f ? MessageType.Info : MessageType.Warning;
            if (best.score <= 0.85f)
                message += "\nA low score is normal for a mesh that really is asymmetric, but check the plane gizmo in the scene before baking.";
            EditorGUILayout.HelpBox(message, type);
        }

        void DrawSideSection()
        {
            EditorGUILayout.LabelField("Side to keep", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("This half survives and is mirrored onto the other.", EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                bool negative = GUILayout.Toggle(!_options.keepPositiveSide, NegativeSideLabel(_options.axis), EditorStyles.miniButtonLeft, GUILayout.Height(22f));
                bool positive = GUILayout.Toggle(_options.keepPositiveSide, PositiveSideLabel(_options.axis), EditorStyles.miniButtonRight, GUILayout.Height(22f));
                if (change.changed)
                {
                    if (negative && _options.keepPositiveSide) _options.keepPositiveSide = false;
                    else if (positive && !_options.keepPositiveSide) _options.keepPositiveSide = true;
                    RefreshPreview();
                }
            }
        }

        void DrawOptionsSection()
        {
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Options", true);
            if (!_showAdvanced) return;

            using (new EditorGUI.IndentLevelScope())
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                _options.weldSeam = EditorGUILayout.Toggle(
                    new GUIContent("Weld seam", "Share the vertices that land on the plane between both halves so the join is watertight."),
                    _options.weldSeam);

                using (new EditorGUI.DisabledScope(!_options.weldSeam))
                {
                    _options.seamTolerance = EditorGUILayout.FloatField(
                        new GUIContent("Seam tolerance", "How close to the plane a vertex has to be to count as sitting on it."),
                        _options.seamTolerance);
                }

                _options.pairBlendShapes = EditorGUILayout.Toggle(
                    new GUIContent("Pair L/R blend shapes", "Drive the mirrored half of a blend shape from its opposite-side twin, so a one-sided shape stays one-sided."),
                    _options.pairBlendShapes);

                using (new EditorGUI.DisabledScope(!(_renderer is SkinnedMeshRenderer)))
                {
                    _options.mirrorBoneWeights = EditorGUILayout.Toggle(
                        new GUIContent("Mirror bone weights", "Remap bone indices on the mirrored half through L/R bone name matching."),
                        _options.mirrorBoneWeights);
                }

                if (change.changed) RefreshPreview();
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

                using (new EditorGUI.DisabledScope(SourceMeshOf(_renderer) == null))
                {
                    if (GUILayout.Button("Bake & Apply", GUILayout.Height(26f))) Bake();
                }
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

            foreach (var warning in _report.warnings)
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
        }

        // ---- Scene view -------------------------------------------------------------------

        void OnSceneGui(SceneView view)
        {
            if (_renderer == null) return;
            var mesh = SourceMeshOf(_renderer);
            if (mesh == null) return;

            var bounds = mesh.bounds;
            var matrix = _renderer.transform.localToWorldMatrix;

            int axis = Mathf.Clamp(_options.axis, 0, 2);
            int u = (axis + 1) % 3;
            int v = (axis + 2) % 3;

            var extents = bounds.extents * 1.15f;
            var center = bounds.center;
            center[axis] = _options.planeOffset;

            var corners = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                var corner = center;
                corner[u] += (i == 0 || i == 3) ? -extents[u] : extents[u];
                corner[v] += (i < 2) ? -extents[v] : extents[v];
                corners[i] = matrix.MultiplyPoint3x4(corner);
            }

            Handles.DrawSolidRectangleWithOutline(corners, new Color(0.2f, 0.6f, 1f, 0.06f), new Color(0.2f, 0.6f, 1f, 0.8f));
        }

        // ---- Actions ----------------------------------------------------------------------

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
            _report = null;
            _error = null;
            _candidates = null;
        }

        void Detect()
        {
            _error = null;
            var snapshot = ReadSnapshot();
            if (snapshot == null) return;

            _candidates = SymmetryDetector.Rank(snapshot.positions, out float tolerance);
            if (_candidates.Count == 0)
            {
                _error = "Could not find a mirror plane for this mesh.";
                return;
            }

            var best = _candidates[0];
            _options.axis = best.axis;
            _options.planeOffset = best.offset;
            _options.seamTolerance = tolerance;
            RefreshPreview();
        }

        MeshSnapshot ReadSnapshot()
        {
            var mesh = SourceMeshOf(_renderer);
            var snapshot = MeshSnapshot.Read(mesh, out string error);
            if (snapshot == null) _error = error;
            return snapshot;
        }

        Mesh BuildResult()
        {
            _error = null;
            var snapshot = ReadSnapshot();
            if (snapshot == null) return null;

            int[] boneMirror = null;
            if (_renderer is SkinnedMeshRenderer skinned && _options.mirrorBoneWeights)
                boneMirror = MeshSymmetrizer.BuildBoneMirrorMap(skinned.bones, out _);

            var report = new SymmetrizeReport();
            var result = MeshSymmetrizer.Build(snapshot, _options, boneMirror, report);
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
            if (IsPreviewing)
            {
                // Bake from the pristine source, not from whatever the preview left assigned.
                StopPreview();
            }

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
