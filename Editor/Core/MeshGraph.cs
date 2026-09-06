using System.Collections.Generic;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// The mesh seen as a surface rather than a vertex buffer: welded points, who touches whom,
    /// where the open edges run, and how far apart two points are *along the fabric*.
    ///
    /// Everything here works on welded groups, because a UV seam splits one surface point into
    /// several vertices and any of these answers would come out wrong — and any edit built on
    /// them would tear the mesh open along those splits.
    /// </summary>
    public sealed class MeshGraph
    {
        public readonly int[] groupOfVertex;
        public readonly int groupCount;
        public readonly List<int>[] verticesOfGroup;
        public readonly Vector3[] positionOfGroup;
        public readonly List<int>[] neighbours;

        /// <summary>
        /// Open edge loops — a hem, a collar, a cuff. These are the rings on a garment, which is
        /// what makes them the natural thing to grab hold of.
        /// </summary>
        public readonly List<int[]> boundaryLoops = new List<int[]>();

        MeshGraph(int[] groupOfVertex, int groupCount, List<int>[] verticesOfGroup,
            Vector3[] positionOfGroup, List<int>[] neighbours)
        {
            this.groupOfVertex = groupOfVertex;
            this.groupCount = groupCount;
            this.verticesOfGroup = verticesOfGroup;
            this.positionOfGroup = positionOfGroup;
            this.neighbours = neighbours;
        }

        /// <summary>
        /// Builds the graph. <paramref name="stitchDistance"/> bridges pieces that are separate
        /// shells but sit against each other — the lace, charms and straps laid on top of a
        /// garment are usually their own shells, and without a bridge an edit on the garment
        /// would walk right past them and leave them hanging in the air.
        /// </summary>
        public static MeshGraph Build(MeshSnapshot src, float weldTolerance, float stitchDistance = 0f)
        {
            var groupOf = PositionWelder.Weld(src.positions, weldTolerance, out int groupCount);
            var members = PositionWelder.MembersOf(groupOf, groupCount);

            var positions = new Vector3[groupCount];
            for (int g = 0; g < groupCount; g++) positions[g] = src.positions[members[g][0]];

            var neighbourSets = new HashSet<int>[groupCount];
            for (int g = 0; g < groupCount; g++) neighbourSets[g] = new HashSet<int>();

            // An edge used by one triangle is an open edge; used by two, it is interior.
            var edgeUse = new Dictionary<long, int>();

            void Touch(int a, int b)
            {
                if (a == b) return;
                neighbourSets[a].Add(b);
                neighbourSets[b].Add(a);
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                edgeUse.TryGetValue(key, out int count);
                edgeUse[key] = count + 1;
            }

            foreach (var submesh in src.submeshes)
            {
                for (int t = 0; t < submesh.Length; t += 3)
                {
                    int a = groupOf[submesh[t]], b = groupOf[submesh[t + 1]], c = groupOf[submesh[t + 2]];
                    Touch(a, b);
                    Touch(b, c);
                    Touch(c, a);
                }
            }

            var neighbours = new List<int>[groupCount];
            for (int g = 0; g < groupCount; g++) neighbours[g] = new List<int>(neighbourSets[g]);

            var graph = new MeshGraph(groupOf, groupCount, members, positions, neighbours);
            graph.TraceBoundaryLoops(edgeUse);
            if (stitchDistance > 0f) graph.StitchShells(stitchDistance);
            return graph;
        }

        /// <summary>
        /// Adds a bridge wherever two separate shells come within reach of each other, just enough
        /// of them to join every piece into one. Only pieces in different shells are joined, so a
        /// dense mesh does not end up wired to itself across every fold.
        /// </summary>
        void StitchShells(float distance)
        {
            var parent = new int[groupCount];
            for (int g = 0; g < groupCount; g++) parent[g] = g;

            int Find(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }
                return i;
            }

            for (int g = 0; g < groupCount; g++)
                foreach (int n in neighbours[g])
                {
                    int a = Find(g), b = Find(n);
                    if (a != b) parent[a] = b;
                }

            var buckets = new Dictionary<(int, int, int), List<int>>(groupCount);
            for (int g = 0; g < groupCount; g++)
            {
                var key = Key(positionOfGroup[g], distance);
                if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new List<int>(4);
                bucket.Add(g);
            }

            float sqrDistance = distance * distance;
            for (int g = 0; g < groupCount; g++)
            {
                var key = Key(positionOfGroup[g], distance);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!buckets.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket))
                        continue;
                    foreach (int h in bucket)
                    {
                        if (h == g) continue;
                        int a = Find(g), b = Find(h);
                        if (a == b) continue;
                        if ((positionOfGroup[h] - positionOfGroup[g]).sqrMagnitude > sqrDistance) continue;

                        neighbours[g].Add(h);
                        neighbours[h].Add(g);
                        parent[a] = b;
                    }
                }
            }
        }

        void TraceBoundaryLoops(Dictionary<long, int> edgeUse)
        {
            var openNeighbours = new Dictionary<int, List<int>>();
            var openEdges = new HashSet<long>();

            foreach (var pair in edgeUse)
            {
                if (pair.Value != 1) continue;
                openEdges.Add(pair.Key);

                int a = (int)(pair.Key >> 32);
                int b = (int)(pair.Key & 0xffffffff);
                if (!openNeighbours.TryGetValue(a, out var listA)) openNeighbours[a] = listA = new List<int>(2);
                if (!openNeighbours.TryGetValue(b, out var listB)) openNeighbours[b] = listB = new List<int>(2);
                listA.Add(b);
                listB.Add(a);
            }

            var walked = new HashSet<long>();
            foreach (var start in openNeighbours.Keys)
            {
                foreach (int first in openNeighbours[start])
                {
                    long firstEdge = EdgeKey(start, first);
                    if (walked.Contains(firstEdge)) continue;

                    var loop = new List<int> { start };
                    int previous = start;
                    int current = first;
                    walked.Add(firstEdge);

                    while (current != start)
                    {
                        loop.Add(current);

                        int next = -1;
                        foreach (int candidate in openNeighbours[current])
                        {
                            if (candidate == previous) continue;
                            long edge = EdgeKey(current, candidate);
                            if (walked.Contains(edge)) continue;
                            next = candidate;
                            walked.Add(edge);
                            break;
                        }

                        // A non-manifold border can dead-end. Keep what was traced and move on.
                        if (next < 0) break;
                        previous = current;
                        current = next;
                    }

                    if (loop.Count >= 3) boundaryLoops.Add(loop.ToArray());
                }
            }

            boundaryLoops.Sort((a, b) => b.Length.CompareTo(a.Length));
        }

        static long EdgeKey(int a, int b) =>
            a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        /// <summary>
        /// Re-reads the group positions after the mesh has been deformed. The topology is
        /// unchanged, so only the coordinates need refreshing — but they must be, or distances
        /// and pivots would go on being measured against the shape before the edit.
        /// </summary>
        public void RefreshPositions(Vector3[] positions)
        {
            for (int g = 0; g < groupCount; g++) positionOfGroup[g] = positions[verticesOfGroup[g][0]];
        }

        /// <summary>Index of the welded group nearest to a point, or -1 when the mesh is empty.</summary>
        public int NearestGroup(Vector3 point)
        {
            int best = -1;
            float nearest = float.MaxValue;
            for (int g = 0; g < groupCount; g++)
            {
                float sqr = (positionOfGroup[g] - point).sqrMagnitude;
                if (sqr >= nearest) continue;
                nearest = sqr;
                best = g;
            }
            return best;
        }

        /// <summary>The boundary loop passing closest to a point, or -1 when there are none.</summary>
        public int NearestBoundaryLoop(Vector3 point)
        {
            int best = -1;
            float nearest = float.MaxValue;
            for (int i = 0; i < boundaryLoops.Count; i++)
            {
                foreach (int g in boundaryLoops[i])
                {
                    float sqr = (positionOfGroup[g] - point).sqrMagnitude;
                    if (sqr >= nearest) continue;
                    nearest = sqr;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// Distance from the seeds measured along the surface, capped at
        /// <paramref name="maxDistance"/>. Following the fabric rather than flying through the
        /// air is what stops an edit on a hem from reaching the far side of the skirt.
        /// </summary>
        public float[] GeodesicDistance(ICollection<int> seeds, float maxDistance)
        {
            var distance = new float[groupCount];
            for (int g = 0; g < groupCount; g++) distance[g] = float.PositiveInfinity;

            var queue = new MinHeap(Mathf.Max(64, seeds.Count * 4));
            foreach (int seed in seeds)
            {
                distance[seed] = 0f;
                queue.Push(0f, seed);
            }

            while (queue.TryPop(out float d, out int g))
            {
                if (d > distance[g]) continue;
                foreach (int n in neighbours[g])
                {
                    float step = d + Vector3.Distance(positionOfGroup[g], positionOfGroup[n]);
                    if (step > maxDistance || step >= distance[n]) continue;
                    distance[n] = step;
                    queue.Push(step, n);
                }
            }

            return distance;
        }

        /// <summary>Straight-line distance from the seeds, capped at <paramref name="maxDistance"/>.</summary>
        public float[] EuclideanDistance(ICollection<int> seeds, float maxDistance)
        {
            var distance = new float[groupCount];
            for (int g = 0; g < groupCount; g++) distance[g] = float.PositiveInfinity;

            float cell = Mathf.Max(maxDistance, 1e-5f);
            var buckets = new Dictionary<(int, int, int), List<int>>(seeds.Count);
            foreach (int seed in seeds)
            {
                var key = Key(positionOfGroup[seed], cell);
                if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new List<int>(4);
                bucket.Add(seed);
            }

            for (int g = 0; g < groupCount; g++)
            {
                var p = positionOfGroup[g];
                var key = Key(p, cell);
                float nearest = float.PositiveInfinity;

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!buckets.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket))
                        continue;
                    foreach (int seed in bucket)
                    {
                        float sqr = (positionOfGroup[seed] - p).sqrMagnitude;
                        if (sqr < nearest) nearest = sqr;
                    }
                }

                if (!float.IsPositiveInfinity(nearest)) distance[g] = Mathf.Sqrt(nearest);
            }

            return distance;
        }

        static (int, int, int) Key(Vector3 p, float cell) => (
            Mathf.FloorToInt(p.x / cell),
            Mathf.FloorToInt(p.y / cell),
            Mathf.FloorToInt(p.z / cell));

        /// <summary>Binary heap keyed by distance, enough for a Dijkstra front.</summary>
        sealed class MinHeap
        {
            float[] _keys;
            int[] _values;
            int _count;

            public MinHeap(int capacity)
            {
                _keys = new float[capacity];
                _values = new int[capacity];
            }

            public void Push(float key, int value)
            {
                if (_count == _keys.Length)
                {
                    System.Array.Resize(ref _keys, _count * 2);
                    System.Array.Resize(ref _values, _count * 2);
                }

                int i = _count++;
                _keys[i] = key;
                _values[i] = value;

                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_keys[parent] <= _keys[i]) break;
                    Swap(parent, i);
                    i = parent;
                }
            }

            public bool TryPop(out float key, out int value)
            {
                if (_count == 0)
                {
                    key = 0f;
                    value = -1;
                    return false;
                }

                key = _keys[0];
                value = _values[0];

                _count--;
                _keys[0] = _keys[_count];
                _values[0] = _values[_count];

                int i = 0;
                while (true)
                {
                    int left = i * 2 + 1;
                    int right = left + 1;
                    int smallest = i;
                    if (left < _count && _keys[left] < _keys[smallest]) smallest = left;
                    if (right < _count && _keys[right] < _keys[smallest]) smallest = right;
                    if (smallest == i) break;
                    Swap(smallest, i);
                    i = smallest;
                }

                return true;
            }

            void Swap(int a, int b)
            {
                (_keys[a], _keys[b]) = (_keys[b], _keys[a]);
                (_values[a], _values[b]) = (_values[b], _values[a]);
            }
        }
    }
}
