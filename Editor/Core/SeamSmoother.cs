using System.Collections.Generic;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Takes the crease out of the join.
    ///
    /// Mirroring is exact, but if the kept half arrives at the plane at an angle then its
    /// reflection leaves with the opposite angle, and the two meet in a ridge running down the
    /// middle — the "small hill" you would otherwise go and flatten by hand. This runs a
    /// Laplacian relax over a band either side of the seam, strongest at the seam and fading to
    /// nothing at the edge of the band, so the ridge settles while the rest of the mesh is left
    /// exactly as the mirror produced it.
    ///
    /// The mesh is symmetric and so is its connectivity, which is why relaxing it keeps it
    /// symmetric rather than drifting off to one side.
    /// </summary>
    public static class SeamSmoother
    {
        /// <summary>
        /// Relaxes the band around the plane in place. Returns the per-vertex falloff, for
        /// <see cref="BlendNormals"/> to reuse, or null when smoothing was a no-op.
        /// </summary>
        public static float[] Apply(Vector3[] positions, List<int>[] submeshes,
            Vector3 planeNormal, Vector3 planePoint, float width, float strength, int iterations,
            SeamFalloff falloffCurve, bool[] frozen, bool[] onPlane, float weldTolerance)
        {
            if (width <= 0f || strength <= 0f || iterations <= 0) return null;

            var normal = planeNormal.normalized;
            var groupOf = PositionWelder.Weld(positions, weldTolerance, out int groupCount);
            var members = PositionWelder.MembersOf(groupOf, groupCount);

            var groupPosition = new Vector3[groupCount];
            for (int g = 0; g < groupCount; g++) groupPosition[g] = positions[members[g][0]];

            var neighbours = BuildNeighbours(submeshes, groupOf, groupCount);

            // Falloff is measured once, on the shape the mirror produced, so the band does not
            // creep outwards as vertices move.
            var falloff = new float[groupCount];
            var pinned = new bool[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                bool isFrozen = false;
                bool isSeam = false;
                foreach (int vertex in members[g])
                {
                    if (frozen != null && frozen[vertex]) isFrozen = true;
                    if (onPlane != null && onPlane[vertex]) isSeam = true;
                }
                pinned[g] = isSeam;

                if (isFrozen) continue; // a region the user asked to keep as-is stays put
                float distance = Mathf.Abs(Vector3.Dot(groupPosition[g] - planePoint, normal));
                falloff[g] = Weight(falloffCurve, Mathf.Clamp01(distance / width));
            }

            var next = new Vector3[groupCount];
            for (int pass = 0; pass < iterations; pass++)
            {
                for (int g = 0; g < groupCount; g++)
                {
                    float weight = falloff[g] * strength;
                    if (weight <= 0f || neighbours[g].Count == 0)
                    {
                        next[g] = groupPosition[g];
                        continue;
                    }

                    var average = Vector3.zero;
                    foreach (int other in neighbours[g]) average += groupPosition[other];
                    average /= neighbours[g].Count;

                    next[g] = Vector3.LerpUnclamped(groupPosition[g], average, weight);
                }

                // Seam vertices are shared by both halves, so letting them drift off the plane
                // would tear the join open.
                for (int g = 0; g < groupCount; g++)
                {
                    if (pinned[g]) next[g] -= Vector3.Dot(next[g] - planePoint, normal) * normal;
                    groupPosition[g] = next[g];
                }
            }

            var perVertex = new float[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                int g = groupOf[i];
                positions[i] = groupPosition[g];
                perVertex[i] = falloff[g];
            }
            return perVertex;
        }

        /// <summary>
        /// Blends recalculated normals into the smoothed band, leaving the mesh's own normals
        /// untouched outside it — hand-authored shading elsewhere on the model survives.
        /// </summary>
        public static void BlendNormals(Vector3[] positions, Vector3[] normals, List<int>[] submeshes,
            float[] falloff, float weldTolerance)
        {
            if (normals == null || falloff == null) return;

            var groupOf = PositionWelder.Weld(positions, weldTolerance, out int groupCount);
            var accumulated = new Vector3[groupCount];

            foreach (var submesh in submeshes)
            {
                for (int t = 0; t < submesh.Count; t += 3)
                {
                    int a = submesh[t], b = submesh[t + 1], c = submesh[t + 2];
                    // Un-normalised cross product is twice the area, which weights big triangles
                    // more heavily — the usual way to get a stable vertex normal.
                    var face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    accumulated[groupOf[a]] += face;
                    accumulated[groupOf[b]] += face;
                    accumulated[groupOf[c]] += face;
                }
            }

            for (int g = 0; g < groupCount; g++)
                if (accumulated[g].sqrMagnitude > 1e-16f) accumulated[g].Normalize();

            for (int i = 0; i < normals.Length; i++)
            {
                float weight = falloff[i];
                if (weight <= 0f) continue;
                var recalculated = accumulated[groupOf[i]];
                if (recalculated.sqrMagnitude < 0.5f) continue;
                normals[i] = Vector3.Slerp(normals[i], recalculated, weight).normalized;
            }
        }

        /// <summary>Influence at <paramref name="t"/> of the way from the plane to the edge of the band.</summary>
        public static float Weight(SeamFalloff falloff, float t)
        {
            float s = 1f - Mathf.Clamp01(t);
            switch (falloff)
            {
                case SeamFalloff.Sphere: return Mathf.Sqrt(s * (2f - s));
                case SeamFalloff.Root: return Mathf.Sqrt(s);
                case SeamFalloff.Linear: return s;
                case SeamFalloff.Sharp: return s * s;
                case SeamFalloff.Constant: return t >= 1f ? 0f : 1f;
                default: return s * s * (3f - 2f * s); // Smooth
            }
        }

        static List<int>[] BuildNeighbours(List<int>[] submeshes, int[] groupOf, int groupCount)
        {
            var sets = new HashSet<int>[groupCount];
            for (int g = 0; g < groupCount; g++) sets[g] = new HashSet<int>();

            void Link(int a, int b)
            {
                if (a == b) return;
                sets[a].Add(b);
                sets[b].Add(a);
            }

            foreach (var submesh in submeshes)
            {
                for (int t = 0; t < submesh.Count; t += 3)
                {
                    int a = groupOf[submesh[t]], b = groupOf[submesh[t + 1]], c = groupOf[submesh[t + 2]];
                    Link(a, b);
                    Link(b, c);
                    Link(c, a);
                }
            }

            var neighbours = new List<int>[groupCount];
            for (int g = 0; g < groupCount; g++) neighbours[g] = new List<int>(sets[g]);
            return neighbours;
        }
    }
}
