using System.Collections.Generic;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Finds the rigid move that puts a garment back where it belongs on the body.
    ///
    /// Four things are weighed, and leaving any of them out produces something unusable:
    ///
    /// * <b>Placement</b> — how far the garment is from symmetric about the body's mirror plane.
    ///   On its own it will happily bury the garment in the hips to win a millimetre.
    /// * <b>Penetration</b> — how far it sinks into the skin. This has to be a floor, not a
    ///   spread: an earlier version scored the *evenness* of the gap, and a uniform sink of five
    ///   millimetres scores perfectly on evenness while clipping straight through the body.
    /// * <b>Distortion</b> — a rigid motion pushed through a falloff shears whatever sits in the
    ///   transition band, and the strain is roughly the distance moved over the width of the
    ///   fade. Moving 39mm across a 45mm fade tore edges by 60%. Unless that is priced in, the
    ///   solver buys symmetry with shape.
    /// * <b>Effort</b> — of two answers that score alike, the smaller move is the safer one.
    ///
    /// Nothing here replaces geometry. The result is one rotation and offset, fed through the
    /// ordinary falloff, so trim that is asymmetric on purpose keeps its shape and is carried along.
    /// </summary>
    public static class BodyFit
    {
        /// <summary>Edges and their rest lengths, so the solver can see what it is straining.</summary>
        public sealed class Topology
        {
            public readonly int[] a;
            public readonly int[] b;
            public readonly float[] restLength;

            Topology(int[] a, int[] b, float[] restLength)
            {
                this.a = a; this.b = b; this.restLength = restLength;
            }

            public static Topology Build(MeshSnapshot snapshot)
            {
                var seen = new HashSet<long>();
                var ia = new List<int>();
                var ib = new List<int>();
                var rest = new List<float>();

                foreach (var submesh in snapshot.submeshes)
                    for (int t = 0; t < submesh.Length; t += 3)
                        for (int e = 0; e < 3; e++)
                        {
                            int u = submesh[t + e], v = submesh[t + (e + 1) % 3];
                            long key = (long)Mathf.Min(u, v) * 4294967296L + Mathf.Max(u, v);
                            if (!seen.Add(key)) continue;
                            float length = (snapshot.positions[u] - snapshot.positions[v]).magnitude;
                            if (length < 1e-6f) continue;
                            ia.Add(u); ib.Add(v); rest.Add(length);
                        }

                return new Topology(ia.ToArray(), ib.ToArray(), rest.ToArray());
            }
        }

        public sealed class Settings
        {
            /// <summary>How hard sinking into the body is punished. This is the anti-clipping term.</summary>
            public float penetrationWeight = 60f;

            /// <summary>Gap the garment is expected to keep off the skin.</summary>
            public float margin = 0.0005f;

            /// <summary>How hard stretching the transition band is punished.</summary>
            public float distortionWeight = 1f;

            /// <summary>Strain above this counts as damage rather than give.</summary>
            public float strainAllowance = 0.05f;

            /// <summary>Mild preference for the smaller of two equally good answers.</summary>
            public float effortWeight = 0.05f;

            /// <summary>Residual evenness of the gap, once penetration is already handled.</summary>
            public float clearanceWeight = 0.25f;

            public bool allowRotation = true;
            public bool allowTranslation = true;

            public int refinements = 7;
            public float startAngle = 4f;
            public float startOffset = 0.004f;
            public int maxEvaluations = 2500;

            /// <summary>How far each trial start is explored before the field is narrowed.</summary>
            public int coarseRefinements = 2;
            public int coarseBudget = 60;

            /// <summary>
            /// Points measured while searching. The search runs thousands of times and only has
            /// to rank candidates, so it works on a thinned-out copy; the numbers reported back
            /// are measured on everything.
            /// </summary>
            public int searchQueries = 250;
            public int searchTargets = 700;
        }

        public struct Report
        {
            public Vector3 position;
            public Quaternion rotation;
            public float mirrorBefore, mirrorAfter;
            public float deepestBefore, deepestAfter;   // negative is inside the body
            public float worstStrain;
            public int evaluations;
            public bool moved;
        }

        // Distances past this stop counting. Trim that is asymmetric by design never matches its
        // mirror at any pose, and without a cap those few large residuals steer the whole fit.
        const float ResidualCap = 0.02f;
        const int MaxSamples = 800;

        // Turns dimensionless strain into metres so it can be added to the other terms. A tenth
        // of this is roughly the scale of the placement error being corrected.
        const float StrainScale = 0.05f;

        public static Report Solve(Vector3[] basePositions, float[] weights, Vector3 pivot,
            BodyReference body, Settings settings, Topology topology = null)
        {
            var report = new Report { position = pivot, rotation = Quaternion.identity };
            if (basePositions == null || body == null || !body.IsUsable) return report;

            settings = settings ?? new Settings();
            var scratch = new Vector3[basePositions.Length];
            var samples = SampleIndices(basePositions.Length);
            float cell = Cell(basePositions);

            report.mirrorBefore = MirrorResidual(basePositions, body, samples, cell);
            report.deepestBefore = DeepestPenetration(basePositions, body, samples);

            int evaluations = 0;

            var searchQueries = SampleIndices(basePositions.Length, settings.searchQueries);
            var searchTargets = SampleIndices(basePositions.Length, settings.searchTargets);
            var targetBuffer = new Vector3[searchTargets.Length];

            float Score(float[] q)
            {
                evaluations++;
                return Evaluate(q, basePositions, weights, pivot, body, settings, topology,
                    scratch, searchQueries, cell, searchTargets, targetBuffer);
            }

            // Compass search: try a step each way along each free axis, keep anything that helps,
            // then halve the step and go again.
            float Search(float[] q, int refinements, int budget)
            {
                var step = new[]
                {
                    settings.startAngle, settings.startAngle, settings.startAngle,
                    settings.startOffset, settings.startOffset, settings.startOffset,
                };
                var trial = new float[6];
                float best = Score(q);
                int spent = 1;

                for (int pass = 0; pass <= refinements; pass++)
                {
                    bool improved = true;
                    while (improved && spent < budget && evaluations < settings.maxEvaluations)
                    {
                        improved = false;
                        for (int k = 0; k < 6; k++)
                        {
                            if (k < 3 && !settings.allowRotation) continue;
                            if (k >= 3 && !settings.allowTranslation) continue;

                            for (int sign = -1; sign <= 1; sign += 2)
                            {
                                if (spent >= budget || evaluations >= settings.maxEvaluations) break;
                                System.Array.Copy(q, trial, 6);
                                trial[k] += sign * step[k];

                                float score = Score(trial);
                                spent++;
                                if (score >= best - 1e-7f) continue;
                                best = score;
                                System.Array.Copy(trial, q, 6);
                                improved = true;
                                break;
                            }
                        }
                    }
                    for (int k = 0; k < 6; k++) step[k] *= 0.5f;
                }
                return best;
            }

            // The landscape is rough enough that starting only from "no change" is a coin toss:
            // on one garment the same settings variously found a 1.5mm answer and an 11mm one,
            // which is the search losing the basin rather than a real trade-off. So sniff at
            // several starting tilts cheaply first, then refine only the most promising.
            var seeds = new List<float[]> { new float[6] };
            if (settings.allowRotation)
                foreach (float angle in new[] { -20f, -10f, 10f, 20f })
                    for (int axis = 0; axis < 3; axis++)
                    {
                        var seed = new float[6];
                        seed[axis] = angle;
                        seeds.Add(seed);
                    }

            float[] p = null;
            float best = float.MaxValue;
            foreach (var seed in seeds)
            {
                if (evaluations >= settings.maxEvaluations) break;
                var candidate = (float[])seed.Clone();
                float score = Search(candidate, settings.coarseRefinements, settings.coarseBudget);
                if (score >= best) continue;
                best = score;
                p = candidate;
            }
            p = p ?? new float[6];
            Search(p, settings.refinements, settings.maxEvaluations);

            report.rotation = Quaternion.Euler(p[0], p[1], p[2]);
            report.position = pivot + new Vector3(p[3], p[4], p[5]);
            report.evaluations = evaluations;
            report.moved = report.rotation != Quaternion.identity || report.position != pivot;

            var final = ProportionalEdit.HandleTransform(pivot, report.position, report.rotation, Vector3.one);
            ProportionalEdit.Apply(scratch, basePositions, weights, final);
            report.mirrorAfter = MirrorResidual(scratch, body, samples, cell);
            report.deepestAfter = DeepestPenetration(scratch, body, samples);
            report.worstStrain = WorstStrain(scratch, topology);
            return report;
        }

        static float Evaluate(float[] p, Vector3[] basePositions, float[] weights, Vector3 pivot,
            BodyReference body, Settings settings, Topology topology, Vector3[] scratch,
            int[] samples, float cell, int[] targets, Vector3[] targetBuffer)
        {
            var rotation = Quaternion.Euler(p[0], p[1], p[2]);
            var position = pivot + new Vector3(p[3], p[4], p[5]);
            var transform = ProportionalEdit.HandleTransform(pivot, position, rotation, Vector3.one);
            ProportionalEdit.Apply(scratch, basePositions, weights, transform);

            float score = MirrorResidual(scratch, body, samples, cell, targets, targetBuffer);
            score += settings.penetrationWeight * Penetration(scratch, body, samples, settings.margin);
            score += settings.clearanceWeight * ClearanceSpread(scratch, body, samples);

            if (topology != null && settings.distortionWeight > 0f)
                score += settings.distortionWeight * StrainScale * Strain(scratch, topology, settings);

            // Effort, measured against the size of the thing being moved.
            float effort = new Vector3(p[3], p[4], p[5]).magnitude
                         + Mathf.Deg2Rad * new Vector3(p[0], p[1], p[2]).magnitude * 0.05f;
            score += settings.effortWeight * effort;
            return score;
        }

        /// <summary>
        /// Mean depth below the margin. A floor, not a spread: evenness alone is satisfied by a
        /// uniform sink straight through the skin.
        /// </summary>
        public static float Penetration(Vector3[] positions, BodyReference body, int[] samples, float margin)
        {
            float sum = 0f;
            int n = 0;
            foreach (int i in samples)
            {
                float c = body.Clearance(positions[i]);
                if (float.IsNaN(c)) continue;
                sum += Mathf.Max(0f, margin - c);
                n++;
            }
            return n == 0 ? 0f : sum / n;
        }

        public static float DeepestPenetration(Vector3[] positions, BodyReference body, int[] samples)
        {
            float deepest = 0f;
            foreach (int i in samples)
            {
                float c = body.Clearance(positions[i]);
                if (!float.IsNaN(c)) deepest = Mathf.Min(deepest, c);
            }
            return deepest;
        }

        /// <summary>
        /// Mean strain, plus a much heavier charge for anything past the allowance. The mean stays
        /// tiny even when a handful of edges in the transition band are torn, so the mean alone
        /// does not notice the damage that actually shows.
        /// </summary>
        static float Strain(Vector3[] positions, Topology topology, Settings settings)
        {
            double sum = 0, excess = 0;
            for (int i = 0; i < topology.a.Length; i++)
            {
                float now = (positions[topology.a[i]] - positions[topology.b[i]]).magnitude;
                float strain = Mathf.Abs(now / topology.restLength[i] - 1f);
                sum += strain;
                if (strain > settings.strainAllowance) excess += strain - settings.strainAllowance;
            }
            int n = Mathf.Max(1, topology.a.Length);
            return (float)(sum / n) + 20f * (float)(excess / n);
        }

        public static float WorstStrain(Vector3[] positions, Topology topology)
        {
            if (topology == null) return 0f;
            float worst = 0f;
            for (int i = 0; i < topology.a.Length; i++)
            {
                float now = (positions[topology.a[i]] - positions[topology.b[i]]).magnitude;
                worst = Mathf.Max(worst, Mathf.Abs(now / topology.restLength[i] - 1f));
            }
            return worst;
        }

        /// <summary>
        /// Mean capped distance from a point to the nearest point of the garment's own mirror
        /// image. The plane comes from the body, which is the whole reason this is measurable —
        /// the garment's own centre is not trustworthy enough to mirror about.
        /// </summary>
        public static float MirrorResidual(Vector3[] positions, BodyReference body, int[] samples, float cell)
            => MirrorResidual(positions, body, samples, cell, null, null);

        static float MirrorResidual(Vector3[] positions, BodyReference body, int[] samples, float cell,
            int[] targets, Vector3[] targetBuffer)
        {
            PointGrid grid;
            if (targets == null)
            {
                grid = new PointGrid(positions, cell);
            }
            else
            {
                for (int i = 0; i < targets.Length; i++) targetBuffer[i] = positions[targets[i]];
                grid = new PointGrid(targetBuffer, cell);
            }

            float sum = 0f;
            foreach (int i in samples)
                sum += Mathf.Min(grid.NearestDistance(body.Mirror(positions[i])), ResidualCap);
            return samples.Length == 0 ? 0f : sum / samples.Length;
        }

        /// <summary>Standard deviation of the gap to the body: how unevenly the garment sits.</summary>
        public static float ClearanceSpread(Vector3[] positions, BodyReference body, int[] samples)
        {
            double sum = 0, sumSq = 0;
            int n = 0;
            foreach (int i in samples)
            {
                float c = body.Clearance(positions[i]);
                if (float.IsNaN(c)) continue;
                sum += c; sumSq += (double)c * c; n++;
            }
            if (n == 0) return 0f;
            double mean = sum / n;
            return Mathf.Sqrt(Mathf.Max(0f, (float)(sumSq / n - mean * mean)));
        }

        public static int[] SampleIndices(int count) => SampleIndices(count, MaxSamples);

        public static int[] SampleIndices(int count, int MaxSamples)
        {
            if (count <= MaxSamples)
            {
                var all = new int[count];
                for (int i = 0; i < count; i++) all[i] = i;
                return all;
            }
            var picked = new int[MaxSamples];
            float stride = (float)count / MaxSamples;
            for (int i = 0; i < MaxSamples; i++) picked[i] = Mathf.Min(count - 1, (int)(i * stride));
            return picked;
        }

        public static float Cell(IReadOnlyList<Vector3> positions)
        {
            if (positions.Count == 0) return 0.01f;
            var bounds = new Bounds(positions[0], Vector3.zero);
            for (int i = 1; i < positions.Count; i++) bounds.Encapsulate(positions[i]);
            return Mathf.Max(bounds.size.magnitude * 0.02f, 1e-4f);
        }
    }
}
