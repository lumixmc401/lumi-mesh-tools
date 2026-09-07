using System.Collections.Generic;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Finds the rigid move that puts a garment back where it belongs on the body.
    ///
    /// Two things are balanced. The first is placement: how far the garment is from being
    /// symmetric about the body's mirror plane. The second is fit: how evenly it sits off the
    /// body surface. Placement alone would happily lift the garment off the hips to win a
    /// millimetre of symmetry; fit alone is blind, because a band can slide right around the body
    /// and still hug it everywhere.
    ///
    /// Nothing here replaces geometry. The answer is a single rotation and offset, fed through
    /// the ordinary falloff, so trim that is asymmetric on purpose keeps its shape and is simply
    /// carried along.
    /// </summary>
    public static class BodyFit
    {
        public sealed class Settings
        {
            /// <summary>How much staying on the body matters against getting symmetric.</summary>
            public float clearanceWeight = 1f;

            public bool allowRotation = true;
            public bool allowTranslation = true;

            /// <summary>Halvings of the search step. Each one roughly doubles the precision.</summary>
            public int refinements = 7;

            public float startAngle = 4f;      // degrees
            public float startOffset = 0.004f; // metres

            public int maxEvaluations = 900;
        }

        public struct Report
        {
            public Vector3 position;        // where the handle ends up
            public Quaternion rotation;
            public float mirrorBefore, mirrorAfter;
            public float clearanceBefore, clearanceAfter;
            public int evaluations;
            public bool moved;
        }

        // Distances past this stop counting. Trim that is asymmetric by design never matches its
        // mirror at any pose, and without a cap those few large residuals steer the whole fit.
        const float ResidualCap = 0.02f;
        const int MaxSamples = 800;

        public static Report Solve(Vector3[] basePositions, float[] weights, Vector3 pivot,
            BodyReference body, Settings settings)
        {
            var report = new Report { position = pivot, rotation = Quaternion.identity };
            if (basePositions == null || body == null || !body.IsUsable) return report;

            settings = settings ?? new Settings();
            var scratch = new Vector3[basePositions.Length];
            var samples = SampleIndices(basePositions.Length);
            float cell = Cell(basePositions);

            report.mirrorBefore = MirrorResidual(basePositions, body, samples, cell);
            report.clearanceBefore = ClearanceSpread(basePositions, body, samples);

            var p = new float[6];
            var step = new[]
            {
                settings.startAngle, settings.startAngle, settings.startAngle,
                settings.startOffset, settings.startOffset, settings.startOffset,
            };

            int evaluations = 0;
            float best = Evaluate(p, basePositions, weights, pivot, body, settings, scratch, samples, cell);
            evaluations++;

            var trial = new float[6];
            for (int pass = 0; pass <= settings.refinements; pass++)
            {
                bool improved = true;
                while (improved && evaluations < settings.maxEvaluations)
                {
                    improved = false;
                    for (int k = 0; k < 6; k++)
                    {
                        if (k < 3 && !settings.allowRotation) continue;
                        if (k >= 3 && !settings.allowTranslation) continue;

                        for (int sign = -1; sign <= 1; sign += 2)
                        {
                            if (evaluations >= settings.maxEvaluations) break;
                            System.Array.Copy(p, trial, 6);
                            trial[k] += sign * step[k];

                            float score = Evaluate(trial, basePositions, weights, pivot, body,
                                settings, scratch, samples, cell);
                            evaluations++;

                            if (score >= best - 1e-7f) continue;
                            best = score;
                            System.Array.Copy(trial, p, 6);
                            improved = true;
                            break;
                        }
                    }
                }
                for (int k = 0; k < 6; k++) step[k] *= 0.5f;
            }

            report.rotation = Quaternion.Euler(p[0], p[1], p[2]);
            report.position = pivot + new Vector3(p[3], p[4], p[5]);
            report.evaluations = evaluations;
            report.moved = report.rotation != Quaternion.identity || report.position != pivot;

            var final = ProportionalEdit.HandleTransform(pivot, report.position, report.rotation, Vector3.one);
            ProportionalEdit.Apply(scratch, basePositions, weights, final);
            report.mirrorAfter = MirrorResidual(scratch, body, samples, cell);
            report.clearanceAfter = ClearanceSpread(scratch, body, samples);
            return report;
        }

        static float Evaluate(float[] p, Vector3[] basePositions, float[] weights, Vector3 pivot,
            BodyReference body, Settings settings, Vector3[] scratch, int[] samples, float cell)
        {
            var rotation = Quaternion.Euler(p[0], p[1], p[2]);
            var position = pivot + new Vector3(p[3], p[4], p[5]);
            var transform = ProportionalEdit.HandleTransform(pivot, position, rotation, Vector3.one);
            ProportionalEdit.Apply(scratch, basePositions, weights, transform);

            return MirrorResidual(scratch, body, samples, cell)
                 + settings.clearanceWeight * ClearanceSpread(scratch, body, samples);
        }

        /// <summary>
        /// Mean capped distance from a point to the nearest point of the garment's own mirror
        /// image. The plane comes from the body, which is the whole reason this is measurable —
        /// the garment's own centre is not trustworthy enough to mirror about.
        /// </summary>
        public static float MirrorResidual(Vector3[] positions, BodyReference body, int[] samples, float cell)
        {
            var grid = new PointGrid(positions, cell);
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

        public static int[] SampleIndices(int count)
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
