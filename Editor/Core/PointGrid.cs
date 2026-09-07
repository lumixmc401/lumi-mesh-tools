using System.Collections.Generic;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Uniform hash grid over a point cloud, answering "which point is closest to here".
    /// Cheap enough to rebuild inside an optimiser's inner loop.
    /// </summary>
    public sealed class PointGrid
    {
        // How far out the ring search will go before giving up and scanning everything. A query
        // far outside the cloud would otherwise walk an unbounded number of empty cells.
        const int MaxRings = 20;

        readonly Dictionary<(int, int, int), List<int>> _cells = new Dictionary<(int, int, int), List<int>>();
        readonly IReadOnlyList<Vector3> _points;
        readonly float _cell;

        public PointGrid(IReadOnlyList<Vector3> points, float cellSize)
        {
            _points = points;
            _cell = Mathf.Max(cellSize, 1e-6f);
            for (int i = 0; i < points.Count; i++)
            {
                var key = Key(points[i]);
                if (!_cells.TryGetValue(key, out var bucket)) _cells[key] = bucket = new List<int>(4);
                bucket.Add(i);
            }
        }

        public int Count => _points.Count;

        (int, int, int) Key(Vector3 p) => (
            Mathf.FloorToInt(p.x / _cell),
            Mathf.FloorToInt(p.y / _cell),
            Mathf.FloorToInt(p.z / _cell));

        /// <summary>
        /// Index of the nearest point, or -1 only when the cloud is empty. A query far outside the
        /// cloud falls back to a full scan rather than returning "nothing found" — a silent miss
        /// here reads downstream as "no data" and quietly empties whatever is being measured.
        /// </summary>
        public int Nearest(Vector3 point, out float distance)
        {
            distance = float.PositiveInfinity;
            if (_points.Count == 0) return -1;

            var key = Key(point);
            int best = -1;
            float bestSqr = float.MaxValue;

            for (int ring = 0; ring <= MaxRings; ring++)
            {
                Scan(key, ring, point, ref best, ref bestSqr);

                // Everything within ring*cell of the query cell has now been visited, so a hit
                // closer than that cannot be beaten by anything further out.
                if (best >= 0)
                {
                    float covered = ring * _cell;
                    if (bestSqr <= covered * covered) break;
                }
            }

            if (best < 0)
            {
                for (int i = 0; i < _points.Count; i++)
                {
                    float sqr = (_points[i] - point).sqrMagnitude;
                    if (sqr < bestSqr) { bestSqr = sqr; best = i; }
                }
            }

            distance = Mathf.Sqrt(bestSqr);
            return best;
        }

        /// <summary>Distance to the nearest point, without caring which one it was.</summary>
        public float NearestDistance(Vector3 point)
        {
            Nearest(point, out float distance);
            return distance;
        }

        void Scan((int, int, int) key, int ring, Vector3 point, ref int best, ref float bestSqr)
        {
            for (int dx = -ring; dx <= ring; dx++)
            for (int dy = -ring; dy <= ring; dy++)
            for (int dz = -ring; dz <= ring; dz++)
            {
                // Only the shell of the cube: the interior was covered by a smaller ring.
                if (ring > 0 && Mathf.Abs(dx) < ring && Mathf.Abs(dy) < ring && Mathf.Abs(dz) < ring)
                    continue;
                if (!_cells.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket))
                    continue;
                foreach (int i in bucket)
                {
                    float sqr = (_points[i] - point).sqrMagnitude;
                    if (sqr < bestSqr) { bestSqr = sqr; best = i; }
                }
            }
        }
    }
}
