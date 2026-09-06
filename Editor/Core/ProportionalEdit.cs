using System.Collections.Generic;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Blender's proportional editing, applied to a mesh in Unity: move, turn or scale a
    /// selection, and let the surrounding surface follow by however much the falloff says.
    ///
    /// The case it was written for is a ring-shaped garment — a hem, a collar, a cuff — that
    /// sits high on one side and low on the other. Grab the ring, level it, and the body of the
    /// garment above follows smoothly instead of creasing where the correction stops.
    /// </summary>
    public static class ProportionalEdit
    {
        public sealed class Settings
        {
            /// <summary>Distance the correction holds at full strength before it starts to fade.</summary>
            public float plateau = 0f;

            /// <summary>Distance the fade itself takes, past the plateau.</summary>
            public float radius = 0.1f;

            public FalloffCurve curve = FalloffCurve.Smooth;

            /// <summary>
            /// Measure distance along the surface rather than straight through space. On a
            /// garment this is nearly always what you want: the far side of a skirt can be close
            /// in a straight line while being a long way round the fabric.
            /// </summary>
            public bool alongSurface = true;

            public float Reach => plateau + radius;
        }

        /// <summary>Per-vertex influence, from a selection given as welded group indices.</summary>
        public static float[] BuildWeights(MeshGraph graph, ICollection<int> selection, Settings settings)
        {
            var weights = new float[graph.groupOfVertex.Length];
            if (graph == null || selection == null || selection.Count == 0) return weights;

            float reach = Mathf.Max(settings.Reach, 1e-6f);
            var distance = settings.alongSurface
                ? graph.GeodesicDistance(selection, reach)
                : graph.EuclideanDistance(selection, reach);

            var byGroup = new float[graph.groupCount];
            for (int g = 0; g < graph.groupCount; g++)
            {
                float d = distance[g];
                if (float.IsPositiveInfinity(d) || d > reach) continue;
                byGroup[g] = Falloff.Weight(settings.curve, d, settings.plateau, settings.radius);
            }

            for (int i = 0; i < weights.Length; i++) weights[i] = byGroup[graph.groupOfVertex[i]];
            return weights;
        }

        /// <summary>
        /// Writes <paramref name="basePositions"/> transformed by <paramref name="transform"/>,
        /// scaled back towards where it started by each vertex's weight.
        /// </summary>
        public static void Apply(Vector3[] result, Vector3[] basePositions, float[] weights, Matrix4x4 transform)
        {
            for (int i = 0; i < basePositions.Length; i++)
            {
                float w = weights[i];
                var start = basePositions[i];
                result[i] = w <= 0f ? start : Vector3.LerpUnclamped(start, transform.MultiplyPoint3x4(start), w);
            }
        }

        /// <summary>
        /// The transform a handle describes: everything measured against the handle's rest state,
        /// so a handle sitting where it started is the identity and nothing moves.
        /// </summary>
        public static Matrix4x4 HandleTransform(Vector3 pivot, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            return Matrix4x4.TRS(position, rotation, scale) * Matrix4x4.Translate(-pivot);
        }

        /// <summary>Centre of a selection, used as the pivot the handle turns and scales about.</summary>
        public static Vector3 Centre(MeshGraph graph, ICollection<int> selection)
        {
            if (selection == null || selection.Count == 0) return Vector3.zero;
            var sum = Vector3.zero;
            foreach (int g in selection) sum += graph.positionOfGroup[g];
            return sum / selection.Count;
        }

        /// <summary>
        /// Recalculates normals where the edit reached and blends them into the mesh's own by the
        /// same falloff, so shading follows the new shape there while hand-authored normals
        /// elsewhere on the model are left exactly as they were.
        /// </summary>
        public static void BlendNormals(MeshGraph graph, Vector3[] positions, int[][] submeshes,
            Vector3[] sourceNormals, float[] weights, Vector3[] result)
        {
            if (sourceNormals == null) return;

            var accumulated = new Vector3[graph.groupCount];
            foreach (var submesh in submeshes)
            {
                for (int t = 0; t < submesh.Length; t += 3)
                {
                    int a = submesh[t], b = submesh[t + 1], c = submesh[t + 2];
                    // The un-normalised cross product is twice the area, which weights big
                    // triangles more heavily — the usual way to get a stable vertex normal.
                    var face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    accumulated[graph.groupOfVertex[a]] += face;
                    accumulated[graph.groupOfVertex[b]] += face;
                    accumulated[graph.groupOfVertex[c]] += face;
                }
            }

            for (int g = 0; g < graph.groupCount; g++)
                if (accumulated[g].sqrMagnitude > 1e-16f) accumulated[g].Normalize();

            for (int i = 0; i < result.Length; i++)
            {
                float w = weights[i];
                var recalculated = accumulated[graph.groupOfVertex[i]];
                result[i] = w <= 0f || recalculated.sqrMagnitude < 0.5f
                    ? sourceNormals[i]
                    : Vector3.Slerp(sourceNormals[i], recalculated, w).normalized;
            }
        }

        /// <summary>Every group within a radius of a point, for a brush-style selection.</summary>
        public static void AddWithin(MeshGraph graph, Vector3 point, float radius, HashSet<int> selection)
        {
            float sqr = radius * radius;
            for (int g = 0; g < graph.groupCount; g++)
                if ((graph.positionOfGroup[g] - point).sqrMagnitude <= sqr) selection.Add(g);
        }

        public static void RemoveWithin(MeshGraph graph, Vector3 point, float radius, HashSet<int> selection)
        {
            float sqr = radius * radius;
            var doomed = new List<int>();
            foreach (int g in selection)
                if ((graph.positionOfGroup[g] - point).sqrMagnitude <= sqr) doomed.Add(g);
            foreach (int g in doomed) selection.Remove(g);
        }
    }
}
