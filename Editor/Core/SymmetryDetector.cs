using System;
using System.Collections.Generic;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Guesses which plane a mesh is (roughly) mirrored about. On a mesh that is already
    /// asymmetric the score never reaches 1, but the correct axis still scores far above the
    /// other two, and the score is surfaced in the UI so the guess can be sanity checked.
    /// </summary>
    public static class SymmetryDetector
    {
        public struct Candidate
        {
            public int axis;
            public float offset;
            public float score; // fraction of sampled vertices that found a mirror partner

            public string AxisName => axis == 0 ? "X" : axis == 1 ? "Y" : "Z";
        }

        const int MaxSamples = 4000;

        public static List<Candidate> Rank(Vector3[] positions, out float tolerance)
        {
            var results = new List<Candidate>();
            tolerance = 0f;
            if (positions == null || positions.Length == 0) return results;

            var min = positions[0];
            var max = positions[0];
            for (int i = 1; i < positions.Length; i++)
            {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }
            var size = max - min;
            var center = (min + max) * 0.5f;

            tolerance = Mathf.Max(size.magnitude * 0.002f, 1e-5f);
            var grid = new VertexGrid(positions, tolerance);

            for (int axis = 0; axis < 3; axis++)
            {
                if (size[axis] <= tolerance) continue; // flat on this axis, nothing to mirror
                foreach (var offset in OffsetCandidates(center[axis]))
                {
                    results.Add(new Candidate
                    {
                        axis = axis,
                        offset = offset,
                        score = Score(positions, grid, axis, offset, tolerance),
                    });
                }
            }

            results.Sort((a, b) => b.score.CompareTo(a.score));
            return results;
        }

        static IEnumerable<float> OffsetCandidates(float boundsCenter)
        {
            // Zero first: almost every avatar and outfit FBX is authored around the origin, and
            // the bounds centre of an asymmetric mesh is pulled off the true plane by the very
            // asymmetry we are about to fix.
            yield return 0f;
            if (Mathf.Abs(boundsCenter) > 1e-4f) yield return boundsCenter;
        }

        static float Score(Vector3[] positions, VertexGrid grid, int axis, float offset, float tolerance)
        {
            int stride = Mathf.Max(1, positions.Length / MaxSamples);
            int sampled = 0, matched = 0;
            for (int i = 0; i < positions.Length; i += stride)
            {
                var mirrored = positions[i];
                mirrored[axis] = 2f * offset - mirrored[axis];
                sampled++;
                if (grid.HasVertexNear(mirrored, tolerance)) matched++;
            }
            return sampled == 0 ? 0f : (float)matched / sampled;
        }

        /// <summary>Uniform hash grid, used to answer "is there a vertex near this point".</summary>
        sealed class VertexGrid
        {
            readonly Dictionary<(int, int, int), List<int>> _cells = new Dictionary<(int, int, int), List<int>>();
            readonly Vector3[] _positions;
            readonly float _cell;

            public VertexGrid(Vector3[] positions, float cellSize)
            {
                _positions = positions;
                _cell = Mathf.Max(cellSize, 1e-6f);
                for (int i = 0; i < positions.Length; i++)
                {
                    var key = Key(positions[i]);
                    if (!_cells.TryGetValue(key, out var bucket))
                        _cells[key] = bucket = new List<int>(4);
                    bucket.Add(i);
                }
            }

            (int, int, int) Key(Vector3 p) => (
                Mathf.FloorToInt(p.x / _cell),
                Mathf.FloorToInt(p.y / _cell),
                Mathf.FloorToInt(p.z / _cell));

            public bool HasVertexNear(Vector3 point, float radius)
            {
                var key = Key(point);
                float sqr = radius * radius;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!_cells.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket))
                        continue;
                    foreach (int i in bucket)
                        if ((_positions[i] - point).sqrMagnitude <= sqr) return true;
                }
                return false;
            }
        }
    }
}
