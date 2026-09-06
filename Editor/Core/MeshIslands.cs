using System.Collections.Generic;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Splits a mesh into connected shells. A garment's deliberately asymmetric parts — a bag on
    /// one shoulder, a ribbon on one side — are nearly always their own shell, so this is the
    /// handle you grab to say "leave that piece alone".
    /// </summary>
    public static class MeshIslands
    {
        public sealed class Island
        {
            public int index;
            public int vertexCount;
            public int triangleCount;
            public Bounds bounds;

            /// <summary>How lopsided this island is about a plane, filled in by the UI on demand.</summary>
            public float symmetryScore = -1f;
        }

        /// <summary>
        /// Returns an island index per vertex. Vertices that no triangle uses get -1, since they
        /// belong to no shell and are dropped by the rebuild anyway.
        /// </summary>
        public static int[] Build(MeshSnapshot src, float weldTolerance, out List<Island> islands)
        {
            int n = src.vertexCount;
            var groupOf = PositionWelder.Weld(src.positions, weldTolerance, out int groupCount);

            var parent = new int[groupCount];
            for (int i = 0; i < groupCount; i++) parent[i] = i;

            int Find(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]]; // halve the path as we go
                    i = parent[i];
                }
                return i;
            }

            void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }

            var used = new bool[n];
            foreach (var submesh in src.submeshes)
            {
                for (int t = 0; t < submesh.Length; t += 3)
                {
                    int a = submesh[t], b = submesh[t + 1], c = submesh[t + 2];
                    used[a] = used[b] = used[c] = true;
                    Union(groupOf[a], groupOf[b]);
                    Union(groupOf[b], groupOf[c]);
                }
            }

            // Number the shells in the order their first vertex appears, so the list in the UI is
            // stable between runs.
            var islandOfRoot = new Dictionary<int, int>(groupCount);
            var islandOfVertex = new int[n];
            islands = new List<Island>();

            for (int i = 0; i < n; i++)
            {
                if (!used[i]) { islandOfVertex[i] = -1; continue; }

                int root = Find(groupOf[i]);
                if (!islandOfRoot.TryGetValue(root, out int island))
                {
                    island = islands.Count;
                    islandOfRoot[root] = island;
                    islands.Add(new Island { index = island, bounds = new Bounds(src.positions[i], Vector3.zero) });
                }
                islandOfVertex[i] = island;
                islands[island].vertexCount++;
                islands[island].bounds.Encapsulate(src.positions[i]);
            }

            foreach (var submesh in src.submeshes)
                for (int t = 0; t < submesh.Length; t += 3)
                    islands[islandOfVertex[submesh[t]]].triangleCount++;

            return islandOfVertex;
        }

        /// <summary>
        /// Fraction of an island's vertices that already have a mirror partner within the island.
        /// A ribbon that only exists on one side scores near zero, which is a strong hint that it
        /// is one of the pieces you want to leave alone.
        /// </summary>
        public static void ScoreAgainstPlane(MeshSnapshot src, int[] islandOfVertex, List<Island> islands,
            Vector3 planeNormal, Vector3 planePoint, float tolerance)
        {
            var byIsland = new List<Vector3>[islands.Count];
            for (int i = 0; i < islands.Count; i++) byIsland[i] = new List<Vector3>(islands[i].vertexCount);
            for (int i = 0; i < src.vertexCount; i++)
            {
                int island = islandOfVertex[i];
                if (island >= 0) byIsland[island].Add(src.positions[i]);
            }

            var normal = planeNormal.normalized;
            for (int i = 0; i < islands.Count; i++)
                islands[i].symmetryScore = SymmetryDetector.Score(byIsland[i], normal, planePoint, tolerance);
        }
    }
}
