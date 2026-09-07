using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace LumiMeshTools.Editor.Tests
{
    public class BodyFitTests
    {
        const float BodyRadius = 0.10f;
        const float RingRadius = 0.105f;
        const float RingHeight = 0.25f;

        /// <summary>A symmetric torso: a cylinder standing on Y, with outward normals.</summary>
        static void Torso(float centreX, out Vector3[] positions, out Vector3[] normals)
        {
            var p = new List<Vector3>();
            var n = new List<Vector3>();
            for (int row = 0; row <= 40; row++)
            for (int i = 0; i < 48; i++)
            {
                float a = 2f * Mathf.PI * i / 48;
                float y = 0.4f * row / 40f;
                var outward = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                p.Add(new Vector3(centreX, y, 0f) + outward * BodyRadius);
                n.Add(outward);
            }
            positions = p.ToArray();
            normals = n.ToArray();
        }

        /// <summary>A waistband sitting on that torso, level and symmetric about the body centre.</summary>
        static Vector3[] Waistband(float centreX)
        {
            var p = new Vector3[64];
            for (int i = 0; i < p.Length; i++)
            {
                float a = 2f * Mathf.PI * i / p.Length;
                p[i] = new Vector3(centreX + RingRadius * Mathf.Cos(a), RingHeight, RingRadius * Mathf.Sin(a));
            }
            return p;
        }

        static BodyReference Reference(Vector3[] positions, Vector3[] normals, Vector3 planeNormal, float offset)
        {
            var reference = BodyReference.FromPoints(positions, normals);
            reference.planeNormal = planeNormal;
            reference.planePoint = planeNormal * offset;
            return reference;
        }

        [Test]
        public void PointGridFindsTheTrueNearestEvenFarOutside()
        {
            var points = new List<Vector3>();
            for (int i = 0; i < 200; i++) points.Add(new Vector3(i * 0.01f, 0f, 0f));
            var grid = new PointGrid(points, 0.005f);

            // A query a long way off must still answer, not silently report "nothing found" —
            // a miss here reads downstream as no data and empties whatever is being measured.
            int index = grid.Nearest(new Vector3(50f, 0f, 0f), out float distance);
            Assert.AreEqual(199, index, "the far query should resolve to the last point");
            Assert.AreEqual(50f - 1.99f, distance, 1e-3f);

            int near = grid.Nearest(new Vector3(0.504f, 0f, 0f), out _);
            Assert.AreEqual(50, near);
        }

        [Test]
        public void FitsThePlaneOfABodyThatIsNotAtTheOrigin()
        {
            const float centre = 0.037f;
            Torso(centre, out var positions, out var normals);

            var reference = BodyReference.FromPoints(positions, normals);
            reference.FitPlane(Vector3.right);

            Assert.AreEqual(centre, Vector3.Dot(reference.planePoint, Vector3.right), 0.002f,
                "the plane must follow the body, not sit at the origin");
            Assert.Less(reference.symmetryResidual, 0.002f,
                "a cylinder is symmetric, so the residual at the fitted plane should be tiny");
        }

        [Test]
        public void RecoversAKnownTilt()
        {
            Torso(0f, out var bodyPositions, out var bodyNormals);
            var reference = Reference(bodyPositions, bodyNormals, Vector3.right, 0f);

            const float tilt = 12f;
            var crooked = Waistband(0f);
            var tiltRotation = Quaternion.AngleAxis(tilt, Vector3.forward);
            for (int i = 0; i < crooked.Length; i++) crooked[i] = tiltRotation * crooked[i];

            var weights = new float[crooked.Length];
            for (int i = 0; i < weights.Length; i++) weights[i] = 1f;

            var pivot = Vector3.zero;
            foreach (var p in crooked) pivot += p;
            pivot /= crooked.Length;

            var report = BodyFit.Solve(crooked, weights, pivot, reference, new BodyFit.Settings());

            Assert.Less(report.mirrorAfter, report.mirrorBefore * 0.35f,
                $"the fit should take out most of the tilt ({report.mirrorBefore * 1000f:0.00}mm " +
                $"-> {report.mirrorAfter * 1000f:0.00}mm)");
            Assert.GreaterOrEqual(report.deepestAfter, report.deepestBefore - 0.0005f,
                "and it must not achieve that by sinking into the body");
        }

        [Test]
        public void WillNotBuySymmetryBySinkingIntoTheBody()
        {
            // Scoring the *evenness* of the gap instead of its floor let an earlier version push
            // the garment clean through the skin: a uniform sink has zero spread, a perfect score.
            Torso(0f, out var bodyPositions, out var bodyNormals);
            var reference = Reference(bodyPositions, bodyNormals, Vector3.right, 0f);

            var crooked = Waistband(0f);
            var tilt = Quaternion.AngleAxis(14f, Vector3.forward);
            for (int i = 0; i < crooked.Length; i++) crooked[i] = tilt * crooked[i];

            var weights = new float[crooked.Length];
            for (int i = 0; i < weights.Length; i++) weights[i] = 1f;

            var pivot = Vector3.zero;
            foreach (var p in crooked) pivot += p;
            pivot /= crooked.Length;

            var report = BodyFit.Solve(crooked, weights, pivot, reference, new BodyFit.Settings());

            var settled = new Vector3[crooked.Length];
            ProportionalEdit.Apply(settled, crooked, weights,
                ProportionalEdit.HandleTransform(pivot, report.position, report.rotation, Vector3.one));

            var samples = BodyFit.SampleIndices(settled.Length);
            float deepest = BodyFit.DeepestPenetration(settled, reference, samples);
            Assert.Greater(deepest, -0.002f,
                $"the band ended up {deepest * 1000f:0.0}mm inside the body");
        }

        [Test]
        public void WillNotBuySymmetryByTearingTheTransitionBand()
        {
            // A rigid motion pushed through a falloff shears whatever is in the fade. Unless the
            // strain is priced in, the solver trades shape for symmetry - 60% on a real garment.
            Torso(0f, out var bodyPositions, out var bodyNormals);
            var reference = Reference(bodyPositions, bodyNormals, Vector3.right, 0f);

            // a strip running down the torso, so part of it is held and part is moved
            var strip = new List<Vector3>();
            var triangles = new List<int>();
            const int Rows = 24;
            for (int row = 0; row < Rows; row++)
            {
                float y = 0.05f + 0.30f * row / (Rows - 1);
                strip.Add(new Vector3(-0.02f, y, RingRadius));
                strip.Add(new Vector3(0.02f, y, RingRadius));
            }
            for (int row = 0; row < Rows - 1; row++)
            {
                int i = row * 2;
                triangles.AddRange(new[] { i, i + 2, i + 1, i + 1, i + 2, i + 3 });
            }

            var snapshot = new MeshSnapshot
            {
                positions = strip.ToArray(),
                vertexCount = strip.Count,
                submeshes = new[] { triangles.ToArray() },
            };
            var topology = BodyFit.Topology.Build(snapshot);

            // full strength at the top, nothing at the bottom: the fade is the whole strip
            var weights = new float[strip.Count];
            for (int i = 0; i < weights.Length; i++)
                weights[i] = Mathf.Clamp01((i / 2) / (float)(Rows - 1));

            var positions = strip.ToArray();
            var pivot = positions[positions.Length - 1];
            var report = BodyFit.Solve(positions, weights, pivot, reference,
                new BodyFit.Settings(), topology);

            Assert.Less(report.worstStrain, 0.25f,
                $"the fade was stretched by {report.worstStrain * 100f:0} %");
        }

        [Test]
        public void LeavesAnAlreadyStraightGarmentAlone()
        {
            Torso(0f, out var bodyPositions, out var bodyNormals);
            var reference = Reference(bodyPositions, bodyNormals, Vector3.right, 0f);

            var straight = Waistband(0f);
            var weights = new float[straight.Length];
            for (int i = 0; i < weights.Length; i++) weights[i] = 1f;

            var report = BodyFit.Solve(straight, weights, new Vector3(0f, RingHeight, 0f),
                reference, new BodyFit.Settings());

            Assert.Less(Quaternion.Angle(Quaternion.identity, report.rotation), 2f,
                "nothing is wrong, so nothing should be turned");
        }

        [Test]
        public void RigidShellsAreCarriedWithoutBeingStretched()
        {
            // Two vertices of a shell, at opposite ends of the falloff so a per-vertex blend
            // would pull them apart. A buckle is not supposed to change length.
            var positions = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0.05f, 0f, 0f),
            };
            var weights = new[] { 1f, 0.1f };
            var islandOfVertex = new[] { 0, 0 };
            var rigid = new HashSet<int> { 0 };

            var pivot = Vector3.zero;
            var rotation = Quaternion.AngleAxis(30f, Vector3.forward);

            var blended = new Vector3[2];
            ProportionalEdit.Apply(blended, positions, weights,
                ProportionalEdit.HandleTransform(pivot, pivot, rotation, Vector3.one));
            float blendedLength = (blended[1] - blended[0]).magnitude;

            var result = (Vector3[])blended.Clone();
            ProportionalEdit.ApplyRigidShells(result, positions, weights, islandOfVertex, rigid,
                pivot, pivot, rotation, Vector3.one);
            float rigidLength = (result[1] - result[0]).magnitude;

            float original = (positions[1] - positions[0]).magnitude;
            Assert.Less(blendedLength, original * 0.99f,
                "a per-vertex blend really does distort the shell, which is why this exists");
            Assert.AreEqual(original, rigidLength, 1e-6f,
                "moved rigidly, the shell keeps its size exactly");
        }

        [Test]
        public void TrimShellsAreEverythingButTheFabric()
        {
            var islandOfVertex = new[] { 0, 0, 0, 0, 0, 1, 1, 2, -1 };
            var trim = ProportionalEdit.TrimShells(islandOfVertex);

            CollectionAssert.AreEquivalent(new[] { 1, 2 }, trim);
            Assert.IsFalse(trim.Contains(0), "the biggest shell is the garment itself");
        }
    }
}
