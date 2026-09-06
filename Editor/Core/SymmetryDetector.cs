using System.Collections.Generic;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Finds the plane a mesh is most nearly mirrored about.
    ///
    /// The candidates come from the shape itself rather than from a blind search over angles: if a
    /// point set is symmetric about a plane, that plane's normal is necessarily one of the
    /// principal axes of the set's covariance — reflection commutes with the covariance matrix, so
    /// the normal has to be an eigenvector of it. Three eigenvectors plus the three coordinate
    /// axes gives six candidates, and a short local refinement takes it the rest of the way.
    ///
    /// This is what makes a piece that was modelled at an angle usable: mirroring such a piece
    /// about a straight axis folds it into a ridge down the middle, and the cure is to mirror
    /// about the plane it was actually built around.
    /// </summary>
    public static class SymmetryDetector
    {
        public struct PlaneFit
        {
            public Vector3 normal;
            public Vector3 point;
            public float score;       // fraction of sampled vertices that found a mirror partner
            public float tolerance;   // how close a partner has to be, derived from the mesh size
            public float tiltDegrees; // how far the fitted plane leans off the nearest axis

            public int DominantAxis
            {
                get
                {
                    float x = Mathf.Abs(normal.x), y = Mathf.Abs(normal.y), z = Mathf.Abs(normal.z);
                    return x >= y && x >= z ? 0 : y >= z ? 1 : 2;
                }
            }

            public string AxisName => DominantAxis == 0 ? "X" : DominantAxis == 1 ? "Y" : "Z";
        }

        /// <summary>A candidate plane before refinement: a normal and the offset that suits it.</summary>
        struct PlaneSeed
        {
            public Vector3 normal;
            public float offset;
            public float score;
        }

        const int SeedSamples = 800;
        const int CoarseSamples = 1200;
        const int FineSamples = 2400;
        const int RefineSteps = 12;

        /// <summary>Fits a mirror plane to the given points, in their own space.</summary>
        public static PlaneFit Fit(IReadOnlyList<Vector3> positions, out float[] axisScores)
        {
            axisScores = new float[3];
            var fit = new PlaneFit { normal = Vector3.right, point = Vector3.zero, tolerance = 1e-4f };
            if (positions == null || positions.Count == 0) return fit;

            var min = positions[0];
            var max = positions[0];
            for (int i = 1; i < positions.Count; i++)
            {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }
            var size = max - min;
            var boundsCenter = (min + max) * 0.5f;

            float tolerance = Mathf.Max(size.magnitude * 0.002f, 1e-5f);

            // The search steers on a loose match radius and tightens as it closes in. The grid is
            // built at the loosest radius, because a cell has to be at least as wide as any radius
            // queried against it.
            float searchTolerance = tolerance * 8f;
            var grid = new VertexGrid(positions, searchTolerance);

            var seedSamples = Sample(positions, SeedSamples);
            var coarse = Sample(positions, CoarseSamples);
            var fine = Sample(positions, FineSamples);

            // --- candidates ------------------------------------------------------------------
            var centroid = Centroid(positions);
            var candidates = new List<Vector3>(PrincipalAxes(positions, centroid));
            candidates.Add(Vector3.right);
            candidates.Add(Vector3.up);
            candidates.Add(Vector3.forward);

            for (int axis = 0; axis < 3; axis++)
            {
                if (size[axis] <= tolerance) continue;
                var normal = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
                axisScores[axis] = Mathf.Max(
                    Score(grid, coarse, normal, 0f, tolerance),
                    Score(grid, coarse, normal, boundsCenter[axis], tolerance));
            }

            // Pick the offset that suits each candidate normal, then keep the shortlist.
            var seeds = new List<PlaneSeed>();
            foreach (var candidate in candidates)
            {
                if (candidate.sqrMagnitude < 0.5f) continue;
                var normal = candidate.normalized;

                bool duplicate = false;
                foreach (var seen in seeds)
                    if (Mathf.Abs(Vector3.Dot(seen.normal, normal)) > 0.9994f) { duplicate = true; break; }
                if (duplicate) continue;

                var seed = new PlaneSeed { normal = normal, score = -1f };

                // A symmetric set is unchanged by the reflection, so its centroid lies on the
                // plane. Zero and the bounds centre cover the meshes that are not symmetric
                // enough for that to hold exactly.
                foreach (float d in new[] { Vector3.Dot(centroid, normal), 0f, Vector3.Dot(boundsCenter, normal) })
                {
                    float score = SoftScore(grid, seedSamples, normal, d, searchTolerance);
                    if (score <= seed.score) continue;
                    seed.score = score;
                    seed.offset = d;
                }
                seeds.Add(seed);
            }

            if (seeds.Count == 0) return fit;

            // A garment that is already square and symmetric needs no search at all, and skipping
            // it keeps the common case instant on a dense mesh.
            foreach (var seed in seeds)
                if (Score(grid, coarse, seed.normal, seed.offset, tolerance) >= 0.995f)
                    return Finish(seed.normal, seed.offset);

            // --- local refinement --------------------------------------------------------------
            // Every candidate is refined, not only the highest scoring seed. A large one-sided
            // piece drags the centroid and tilts the covariance along with it, so the seed that
            // looks best up front can be the wrong one while the real answer sits a few degrees
            // off a candidate that scored lower.
            var bestNormal = seeds[0].normal;
            float bestD = seeds[0].offset;
            float bestScore = -1f;

            foreach (var seed in seeds)
            {
                var refined = RefineFrom(seed.normal, seed.offset, out float refinedOffset);
                float score = Score(grid, fine, refined, refinedOffset, tolerance);
                if (score <= bestScore) continue;
                bestScore = score;
                bestNormal = refined;
                bestD = refinedOffset;
            }

            return Finish(bestNormal, bestD);

            // ---- local helpers ----------------------------------------------------------------

            Vector3 RefineFrom(Vector3 seedNormal, float seedOffset, out float settledOffset)
            {
                var u = Vector3.Cross(seedNormal, Mathf.Abs(seedNormal.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;
                var v = Vector3.Cross(seedNormal, u).normalized;
                float tiltU = 0f, tiltV = 0f;
                float offset = seedOffset;

                float ScoreAt(List<Vector3> samples, float radius, float a, float b, float d)
                    => SoftScore(grid, samples, Tilt(seedNormal, u, v, a, b), d, radius);

                float offsetRange = Mathf.Max(size.magnitude * 0.02f, tolerance * 20f);

                // Wide enough to cover a seed that a one-sided piece has dragged off the answer.
                float tiltRange = 30f;

                for (int round = 0; round < 5; round++)
                {
                    var samples = round < 2 ? coarse : fine;
                    float radius = searchTolerance * Mathf.Pow(0.5f, Mathf.Min(round, 3)); // 8x, 4x, 2x, 1x, 1x

                    // Scores from different sample counts and radii are not comparable, so the bar
                    // is re-measured each round, or real improvements get rejected.
                    float best = ScoreAt(samples, radius, tiltU, tiltV, offset);

                    float heldTilt = tiltU, heldTiltV = tiltV, heldOffset = offset;
                    offset = Refine(d => ScoreAt(samples, radius, heldTilt, heldTiltV, d), heldOffset, offsetRange, ref best);

                    float settled = offset;
                    tiltU = Refine(a => ScoreAt(samples, radius, a, heldTiltV, settled), tiltU, tiltRange, ref best);

                    float settledU = tiltU;
                    tiltV = Refine(b => ScoreAt(samples, radius, settledU, b, settled), tiltV, tiltRange, ref best);

                    offsetRange *= 0.5f;
                    tiltRange *= 0.5f;
                }

                settledOffset = offset;
                return Tilt(seedNormal, u, v, tiltU, tiltV);
            }

            PlaneFit Finish(Vector3 normal, float d)
            {
                fit.normal = normal;
                fit.point = normal * d;
                fit.score = Score(grid, fine, normal, d, tolerance);
                fit.tolerance = tolerance;

                var nearestAxis = fit.DominantAxis == 0 ? Vector3.right
                    : fit.DominantAxis == 1 ? Vector3.up : Vector3.forward;
                float angle = Vector3.Angle(normal, nearestAxis);
                fit.tiltDegrees = Mathf.Min(angle, 180f - angle);
                return fit;
            }
        }

        /// <summary>
        /// Scans one parameter across a range and returns the middle of the best run it finds.
        ///
        /// Taking the middle rather than the first winner matters: at a loose match radius a whole
        /// span of values scores identically, and stopping at the near edge of that span leaves an
        /// error the next, tighter round is too narrow to walk back.
        /// </summary>
        static float Refine(System.Func<float, float> score, float start, float range, ref float best)
        {
            float sum = 0f;
            int count = 0;
            int lastIndex = int.MinValue;

            for (int i = -RefineSteps; i <= RefineSteps; i++)
            {
                float candidate = start + range * i / RefineSteps;
                float s = score(candidate);

                if (s > best)
                {
                    best = s;
                    sum = candidate;
                    count = 1;
                }
                else if (count > 0 && i == lastIndex + 1 && Mathf.Approximately(s, best))
                {
                    // Only a run touching the current winner counts, so two separate peaks of
                    // equal height never average into the valley between them.
                    sum += candidate;
                    count++;
                }
                else continue;

                lastIndex = i;
            }

            return count > 0 ? sum / count : start;
        }

        static Vector3 Tilt(Vector3 normal, Vector3 u, Vector3 v, float degreesU, float degreesV)
        {
            if (degreesU == 0f && degreesV == 0f) return normal;
            return (Quaternion.AngleAxis(degreesU, u) * Quaternion.AngleAxis(degreesV, v) * normal).normalized;
        }

        // ---- scoring --------------------------------------------------------------------------

        /// <summary>Fraction of the points that have a mirror partner across the plane.</summary>
        public static float Score(IReadOnlyList<Vector3> positions, Vector3 normal, Vector3 point, float tolerance)
        {
            if (positions == null || positions.Count == 0) return 0f;
            var unit = normal.normalized;
            var grid = new VertexGrid(positions, tolerance);
            return Score(grid, Sample(positions, FineSamples), unit, Vector3.Dot(point, unit), tolerance);
        }

        static float Score(VertexGrid grid, List<Vector3> samples, Vector3 normal, float planeD, float tolerance)
        {
            if (samples.Count == 0) return 0f;
            int matched = 0;
            foreach (var q in samples)
            {
                var mirrored = q - 2f * (Vector3.Dot(q, normal) - planeD) * normal;
                if (grid.HasVertexNear(mirrored, tolerance)) matched++;
            }
            return (float)matched / samples.Count;
        }

        /// <summary>
        /// How well a plane matches, measured by how far each mirrored point misses rather than by
        /// whether it lands inside a threshold. A yes/no test gives the search nothing to follow:
        /// narrow the threshold and a plane two degrees off scores zero, exactly like one ninety
        /// degrees off. Measuring the miss distance gives every candidate a slope to walk down.
        /// </summary>
        static float SoftScore(VertexGrid grid, List<Vector3> samples, Vector3 normal, float planeD, float radius)
        {
            if (samples.Count == 0) return 0f;
            float total = 0f;
            foreach (var q in samples)
            {
                var mirrored = q - 2f * (Vector3.Dot(q, normal) - planeD) * normal;
                total += 1f - grid.NearestDistance(mirrored, radius) / radius;
            }
            return total / samples.Count;
        }

        static List<Vector3> Sample(IReadOnlyList<Vector3> positions, int budget)
        {
            int stride = Mathf.Max(1, positions.Count / budget);
            var samples = new List<Vector3>(positions.Count / stride + 1);
            for (int i = 0; i < positions.Count; i += stride) samples.Add(positions[i]);
            return samples;
        }

        // ---- principal axes ---------------------------------------------------------------------

        public static Vector3 Centroid(IReadOnlyList<Vector3> positions)
        {
            var sum = Vector3.zero;
            foreach (var p in positions) sum += p;
            return sum / positions.Count;
        }

        /// <summary>Eigenvectors of the point set's covariance, largest spread first.</summary>
        public static Vector3[] PrincipalAxes(IReadOnlyList<Vector3> positions, Vector3 centroid)
        {
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p in positions)
            {
                var d = p - centroid;
                xx += (double)d.x * d.x;
                xy += (double)d.x * d.y;
                xz += (double)d.x * d.z;
                yy += (double)d.y * d.y;
                yz += (double)d.y * d.z;
                zz += (double)d.z * d.z;
            }

            var matrix = new double[3, 3]
            {
                { xx, xy, xz },
                { xy, yy, yz },
                { xz, yz, zz },
            };

            return JacobiEigenvectors(matrix);
        }

        /// <summary>Cyclic Jacobi rotation, which is plenty for a symmetric 3x3.</summary>
        static Vector3[] JacobiEigenvectors(double[,] a)
        {
            var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

            for (int sweep = 0; sweep < 32; sweep++)
            {
                double off = System.Math.Abs(a[0, 1]) + System.Math.Abs(a[0, 2]) + System.Math.Abs(a[1, 2]);
                if (off < 1e-14) break;

                for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (System.Math.Abs(a[p, q]) < 1e-18) continue;

                    double theta = (a[q, q] - a[p, p]) / (2.0 * a[p, q]);
                    double t = System.Math.Sign(theta) / (System.Math.Abs(theta) + System.Math.Sqrt(theta * theta + 1.0));
                    if (theta == 0.0) t = 1.0;
                    double c = 1.0 / System.Math.Sqrt(t * t + 1.0);
                    double s = t * c;

                    double apq = a[p, q];
                    a[p, p] -= t * apq;
                    a[q, q] += t * apq;
                    a[p, q] = a[q, p] = 0.0;

                    for (int r = 0; r < 3; r++)
                    {
                        if (r != p && r != q)
                        {
                            double arp = a[r, p], arq = a[r, q];
                            a[r, p] = a[p, r] = c * arp - s * arq;
                            a[r, q] = a[q, r] = c * arq + s * arp;
                        }

                        double vrp = v[r, p], vrq = v[r, q];
                        v[r, p] = c * vrp - s * vrq;
                        v[r, q] = c * vrq + s * vrp;
                    }
                }
            }

            var order = new[] { 0, 1, 2 };
            System.Array.Sort(order, (i, j) => a[j, j].CompareTo(a[i, i]));

            var axes = new Vector3[3];
            for (int i = 0; i < 3; i++)
            {
                int column = order[i];
                axes[i] = new Vector3((float)v[0, column], (float)v[1, column], (float)v[2, column]).normalized;
            }
            return axes;
        }

        // ---- spatial lookup ---------------------------------------------------------------------

        /// <summary>Uniform hash grid, used to answer "how near is the closest vertex".</summary>
        sealed class VertexGrid
        {
            readonly Dictionary<(int, int, int), List<int>> _cells = new Dictionary<(int, int, int), List<int>>();
            readonly IReadOnlyList<Vector3> _positions;
            readonly float _cell;

            public VertexGrid(IReadOnlyList<Vector3> positions, float cellSize)
            {
                _positions = positions;
                _cell = Mathf.Max(cellSize, 1e-6f);
                for (int i = 0; i < positions.Count; i++)
                {
                    var key = Key(positions[i]);
                    if (!_cells.TryGetValue(key, out var bucket)) _cells[key] = bucket = new List<int>(4);
                    bucket.Add(i);
                }
            }

            (int, int, int) Key(Vector3 p) => (
                Mathf.FloorToInt(p.x / _cell),
                Mathf.FloorToInt(p.y / _cell),
                Mathf.FloorToInt(p.z / _cell));

            public bool HasVertexNear(Vector3 point, float radius) => NearestDistance(point, radius) < radius;

            /// <summary>Distance to the closest vertex, or <paramref name="radius"/> if none is nearer.</summary>
            public float NearestDistance(Vector3 point, float radius)
            {
                var key = Key(point);
                float nearest = radius * radius;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!_cells.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket))
                        continue;
                    foreach (int i in bucket)
                    {
                        float sqr = (_positions[i] - point).sqrMagnitude;
                        if (sqr < nearest) nearest = sqr;
                    }
                }
                return Mathf.Sqrt(nearest);
            }
        }
    }
}
