using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace LumiMeshTools.Editor.Tests
{
    public class NameSymmetryTests
    {
        [TestCase("Left arm", "Right arm")]
        [TestCase("Right arm", "Left arm")]
        [TestCase("Hand_L", "Hand_R")]
        [TestCase("Hand_R", "Hand_L")]
        [TestCase("Upper_leg.L", "Upper_leg.R")]
        [TestCase("L_Shoulder", "R_Shoulder")]
        [TestCase("thumb l", "thumb r")]
        public void FlipsSideMarkers(string input, string expected)
        {
            Assert.IsTrue(NameSymmetry.TryFlip(input, out string flipped));
            Assert.AreEqual(expected, flipped);
        }

        [TestCase("Hips")]
        [TestCase("Clothes")]
        [TestCase("Skirt_01")]
        [TestCase("Spine")]
        public void LeavesSidelessNamesAlone(string input)
        {
            Assert.IsFalse(NameSymmetry.TryFlip(input, out _));
        }

        [Test]
        public void FlippingTwiceIsIdentity()
        {
            Assert.IsTrue(NameSymmetry.TryFlip("Hand_L", out string once));
            Assert.IsTrue(NameSymmetry.TryFlip(once, out string twice));
            Assert.AreEqual("Hand_L", twice);
        }
    }

    public class MeshSymmetrizerTests
    {
        const float Tolerance = 1e-4f;

        [Test]
        public void MakesAnAsymmetricMeshSymmetric()
        {
            var mesh = BuildGrid(new[] { -1f, -0.5f, 0f, 0.5f, 1f }, new[] { 0f, 0.5f, 1f });
            PushSideOutOfPlane(mesh, x => x > 0f, 0.3f);

            var result = Symmetrize(mesh, out var report);

            AssertMirrorSymmetric(result, axis: 0, offset: 0f);
            // The +x half was displaced, the -x half was not, so a symmetric result means the
            // displacement is gone.
            foreach (var v in result.vertices) Assert.AreEqual(0f, v.z, Tolerance);
            Assert.AreEqual(0, report.warnings.Count);
        }

        [Test]
        public void CutsTrianglesWhenNoVertexSitsOnThePlane()
        {
            // Columns straddle x = 0 without landing on it, so the plane runs through the middle
            // of a quad and the triangles have to be split.
            var mesh = BuildGrid(new[] { -1f, -0.4f, 0.6f, 1.2f }, new[] { 0f, 1f });

            var result = Symmetrize(mesh, out _);

            int onPlane = 0;
            foreach (var v in result.vertices) if (Mathf.Abs(v.x) < Tolerance) onPlane++;
            Assert.Greater(onPlane, 0, "expected the cut to introduce vertices on the mirror plane");
            AssertMirrorSymmetric(result, axis: 0, offset: 0f);
        }

        [Test]
        public void ReusesAnExistingCentreRowInsteadOfDuplicatingIt()
        {
            // The grid already has a column of vertices sitting on x = 0. Cutting there must land
            // on those vertices rather than stack a coincident copy beside each one.
            var mesh = BuildGrid(new[] { -1f, -0.5f, 0f, 0.5f, 1f }, new[] { 0f, 0.5f, 1f });

            var result = Symmetrize(mesh, out var report);

            Assert.AreEqual(3, report.seamVertexCount, "one seam vertex per row, not two");
            Assert.AreEqual(mesh.vertexCount, result.vertexCount,
                "a symmetric grid should rebuild to the same vertex count it started with");
        }

        [Test]
        public void KeepsTheChosenHalfUntouched()
        {
            var mesh = BuildGrid(new[] { -1f, -0.5f, 0f, 0.5f, 1f }, new[] { 0f, 1f });
            PushSideOutOfPlane(mesh, x => x > 0f, 0.3f);

            var result = Symmetrize(mesh, out _, keepPositiveSide: true);

            // Keeping +x this time means the displacement is what survives.
            foreach (var v in result.vertices)
                if (Mathf.Abs(v.x) > Tolerance) Assert.AreEqual(0.3f, v.z, Tolerance);
        }

        [Test]
        public void PreservesUvsAndSubmeshes()
        {
            var mesh = BuildGrid(new[] { -1f, 0f, 1f }, new[] { 0f, 1f });
            var result = Symmetrize(mesh, out _);

            Assert.AreEqual(mesh.subMeshCount, result.subMeshCount);
            Assert.AreEqual(result.vertexCount, result.uv.Length);
            Assert.AreEqual(result.vertexCount, result.normals.Length);
        }

        [Test]
        public void RebuildsBlendShapes()
        {
            var mesh = BuildGrid(new[] { -1f, -0.5f, 0f, 0.5f, 1f }, new[] { 0f, 1f });
            AddBlendShape(mesh, "Shrink", v => new Vector3(0f, -0.2f, 0f));

            var result = Symmetrize(mesh, out _);

            Assert.AreEqual(1, result.blendShapeCount);
            Assert.AreEqual("Shrink", result.GetBlendShapeName(0));

            var deltas = new Vector3[result.vertexCount];
            result.GetBlendShapeFrameVertices(0, 0, deltas, null, null);
            foreach (var d in deltas) Assert.AreEqual(-0.2f, d.y, Tolerance);
        }

        [Test]
        public void KeepsOneSidedBlendShapesOneSided()
        {
            var mesh = BuildGrid(new[] { -1f, -0.5f, 0f, 0.5f, 1f }, new[] { 0f, 1f });
            // Only moves the kept (-x) half.
            AddBlendShape(mesh, "Wing_L", v => v.x < -Tolerance ? new Vector3(0f, 0.5f, 0f) : Vector3.zero);
            AddBlendShape(mesh, "Wing_R", v => Vector3.zero);

            var result = Symmetrize(mesh, out _);

            var positions = result.vertices;
            var left = new Vector3[result.vertexCount];
            var right = new Vector3[result.vertexCount];
            result.GetBlendShapeFrameVertices(0, 0, left, null, null);
            result.GetBlendShapeFrameVertices(1, 0, right, null, null);

            for (int i = 0; i < result.vertexCount; i++)
            {
                if (positions[i].x < -Tolerance)
                {
                    Assert.AreEqual(0.5f, left[i].y, Tolerance, "Wing_L should move the left half");
                    Assert.AreEqual(0f, right[i].y, Tolerance, "Wing_R should leave the left half alone");
                }
                else if (positions[i].x > Tolerance)
                {
                    Assert.AreEqual(0f, left[i].y, Tolerance, "Wing_L should leave the right half alone");
                    Assert.AreEqual(0.5f, right[i].y, Tolerance, "Wing_R should move the right half");
                }
            }
        }

        // ---- helpers ----------------------------------------------------------------------

        static Mesh Symmetrize(Mesh source, out SymmetrizeReport report, bool keepPositiveSide = false)
        {
            var snapshot = MeshSnapshot.Read(source, out string error);
            Assert.IsNull(error, error);

            report = new SymmetrizeReport();
            var options = new SymmetrizeOptions
            {
                axis = 0,
                planeOffset = 0f,
                keepPositiveSide = keepPositiveSide,
                seamTolerance = Tolerance,
            };
            var result = MeshSymmetrizer.Build(snapshot, options, null, report);
            Assert.IsNotNull(result);
            return result;
        }

        static void AssertMirrorSymmetric(Mesh mesh, int axis, float offset)
        {
            var positions = mesh.vertices;
            foreach (var v in positions)
            {
                var mirrored = v;
                mirrored[axis] = 2f * offset - mirrored[axis];

                bool found = false;
                foreach (var other in positions)
                {
                    if ((other - mirrored).sqrMagnitude > Tolerance * Tolerance) continue;
                    found = true;
                    break;
                }
                Assert.IsTrue(found, $"no mirror partner for {v}");
            }
        }

        static Mesh BuildGrid(float[] xs, float[] ys)
        {
            var mesh = new Mesh { name = "Grid" };
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();

            for (int j = 0; j < ys.Length; j++)
            for (int i = 0; i < xs.Length; i++)
            {
                positions.Add(new Vector3(xs[i], ys[j], 0f));
                normals.Add(new Vector3(0f, 0f, -1f));
                uvs.Add(new Vector2((float)i / (xs.Length - 1), (float)j / (ys.Length - 1)));
            }

            var triangles = new List<int>();
            for (int j = 0; j < ys.Length - 1; j++)
            for (int i = 0; i < xs.Length - 1; i++)
            {
                int a = j * xs.Length + i;
                int b = a + 1;
                int c = a + xs.Length;
                int d = c + 1;
                triangles.AddRange(new[] { a, c, b, b, c, d });
            }

            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static void PushSideOutOfPlane(Mesh mesh, System.Func<float, bool> selector, float z)
        {
            var positions = mesh.vertices;
            for (int i = 0; i < positions.Length; i++)
                if (selector(positions[i].x)) positions[i].z = z;
            mesh.vertices = positions;
            mesh.RecalculateBounds();
        }

        static void AddBlendShape(Mesh mesh, string name, System.Func<Vector3, Vector3> delta)
        {
            var positions = mesh.vertices;
            var deltas = new Vector3[positions.Length];
            for (int i = 0; i < positions.Length; i++) deltas[i] = delta(positions[i]);
            mesh.AddBlendShapeFrame(name, 100f, deltas, null, null);
        }
    }
}
