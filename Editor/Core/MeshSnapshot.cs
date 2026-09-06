using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// A plain-array copy of everything a Mesh carries, so the rebuild passes can work on it
    /// without touching the read-only mesh that came out of the FBX importer.
    /// </summary>
    public sealed class MeshSnapshot
    {
        public const int UvChannels = 8;

        public string name;
        public int vertexCount;

        public Vector3[] positions;
        public Vector3[] normals;   // null when the mesh has none
        public Vector4[] tangents;  // null when the mesh has none
        public Color[] colors;      // null when the mesh has none

        public readonly List<Vector4>[] uvs = new List<Vector4>[UvChannels];
        public readonly int[] uvDimensions = new int[UvChannels];

        public BoneWeight[] boneWeights; // null when the mesh is not skinned
        public Matrix4x4[] bindposes;    // null when the mesh is not skinned

        public int[][] submeshes;

        public readonly List<BlendShape> blendShapes = new List<BlendShape>();

        public sealed class BlendShape
        {
            public string name;
            public readonly List<Frame> frames = new List<Frame>();
        }

        public sealed class Frame
        {
            public float weight;
            public Vector3[] deltaVertices;
            public Vector3[] deltaNormals;  // null when the frame carries none
            public Vector3[] deltaTangents; // null when the frame carries none
        }

        /// <summary>
        /// Reads a mesh into a snapshot. Returns null and fills <paramref name="error"/> when the
        /// mesh is something the rebuild cannot handle.
        /// </summary>
        public static MeshSnapshot Read(Mesh mesh, out string error)
        {
            error = null;
            if (mesh == null) { error = "No mesh."; return null; }

            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                if (mesh.GetTopology(i) != MeshTopology.Triangles)
                {
                    error = $"Submesh {i} uses {mesh.GetTopology(i)} topology; only triangle meshes are supported.";
                    return null;
                }
            }

            var s = new MeshSnapshot
            {
                name = mesh.name,
                vertexCount = mesh.vertexCount,
                positions = mesh.vertices,
            };

            // Meshes imported without 'Read/Write Enabled' are still readable from editor code,
            // but a stripped mesh hands back nothing, and that is worth naming plainly.
            if (s.positions == null || s.positions.Length != s.vertexCount)
            {
                error = "Could not read the mesh vertices. Tick 'Read/Write Enabled' on the model importer and re-import.";
                return null;
            }

            if (mesh.HasVertexAttribute(VertexAttribute.Normal)) s.normals = mesh.normals;
            if (mesh.HasVertexAttribute(VertexAttribute.Tangent)) s.tangents = mesh.tangents;
            if (mesh.HasVertexAttribute(VertexAttribute.Color)) s.colors = mesh.colors;

            for (int ch = 0; ch < UvChannels; ch++)
            {
                int dim = mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0 + ch);
                if (dim <= 0) continue;
                var list = new List<Vector4>(s.vertexCount);
                mesh.GetUVs(ch, list);
                if (list.Count != s.vertexCount) continue;
                s.uvs[ch] = list;
                s.uvDimensions[ch] = dim;
            }

            var weights = mesh.boneWeights;
            if (weights != null && weights.Length == s.vertexCount)
            {
                s.boneWeights = weights;
                s.bindposes = mesh.bindposes;
            }

            s.submeshes = new int[mesh.subMeshCount][];
            for (int i = 0; i < mesh.subMeshCount; i++) s.submeshes[i] = mesh.GetTriangles(i);

            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                var bs = new BlendShape { name = mesh.GetBlendShapeName(shape) };
                int frameCount = mesh.GetBlendShapeFrameCount(shape);
                for (int f = 0; f < frameCount; f++)
                {
                    var dv = new Vector3[s.vertexCount];
                    var dn = new Vector3[s.vertexCount];
                    var dt = new Vector3[s.vertexCount];
                    mesh.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                    bs.frames.Add(new Frame
                    {
                        weight = mesh.GetBlendShapeFrameWeight(shape, f),
                        deltaVertices = dv,
                        deltaNormals = IsAllZero(dn) ? null : dn,
                        deltaTangents = IsAllZero(dt) ? null : dt,
                    });
                }
                s.blendShapes.Add(bs);
            }

            return s;
        }

        static bool IsAllZero(Vector3[] values)
        {
            for (int i = 0; i < values.Length; i++)
                if (values[i] != Vector3.zero) return false;
            return true;
        }

        public Bounds CalculateBounds()
        {
            if (positions.Length == 0) return new Bounds();
            var min = positions[0];
            var max = positions[0];
            for (int i = 1; i < positions.Length; i++)
            {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }
    }
}
