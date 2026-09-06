using System.Collections.Generic;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Groups vertices that share a position. A UV seam or a hard edge splits one surface point
    /// into several vertices, and every pass that reasons about the *surface* rather than the
    /// vertex buffer — island detection, smoothing, normal recalculation — has to see them as
    /// one point or it will tear the mesh apart along those splits.
    /// </summary>
    public static class PositionWelder
    {
        /// <summary>Returns a group index per vertex, with groups numbered from zero.</summary>
        public static int[] Weld(Vector3[] positions, float tolerance, out int groupCount)
        {
            int n = positions.Length;
            var groupOf = new int[n];
            for (int i = 0; i < n; i++) groupOf[i] = -1;

            float cell = Mathf.Max(tolerance, 1e-6f);
            float sqrTolerance = cell * cell;
            var buckets = new Dictionary<(int, int, int), List<int>>(n);
            groupCount = 0;

            for (int i = 0; i < n; i++)
            {
                var p = positions[i];
                var key = (Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));

                int found = -1;
                for (int dx = -1; dx <= 1 && found < 0; dx++)
                for (int dy = -1; dy <= 1 && found < 0; dy++)
                for (int dz = -1; dz <= 1 && found < 0; dz++)
                {
                    if (!buckets.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket)) continue;
                    foreach (int other in bucket)
                    {
                        if ((positions[other] - p).sqrMagnitude > sqrTolerance) continue;
                        found = groupOf[other];
                        break;
                    }
                }

                groupOf[i] = found >= 0 ? found : groupCount++;

                if (!buckets.TryGetValue(key, out var own)) buckets[key] = own = new List<int>(4);
                own.Add(i);
            }

            return groupOf;
        }

        /// <summary>Inverts a group map into the list of vertices belonging to each group.</summary>
        public static List<int>[] MembersOf(int[] groupOf, int groupCount)
        {
            var members = new List<int>[groupCount];
            for (int i = 0; i < groupCount; i++) members[i] = new List<int>(2);
            for (int i = 0; i < groupOf.Length; i++) members[groupOf[i]].Add(i);
            return members;
        }
    }
}
