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

            AssertMirrorSymmetric(result, Vector3.right, Vector3.zero);
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
            AssertMirrorSymmetric(result, Vector3.right, Vector3.zero);
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

        [Test]
        public void MirrorsAboutATiltedPlane()
        {
            // A piece modelled at an angle: the plane it is symmetric about is not an axis.
            var normal = new Vector3(1f, 1f, 0f).normalized;
            var mesh = BuildGrid(new[] { -1f, -0.5f, 0f, 0.5f, 1f }, new[] { 0f, 1f });

            var options = BaseOptions();
            options.planeNormal = normal;
            options.planePoint = Vector3.zero;

            var result = Build(mesh, options, out _);

            AssertMirrorSymmetric(result, normal, Vector3.zero);
        }

        // ---- regions ------------------------------------------------------------------------

        [Test]
        public void SplitsDisconnectedShellsIntoRegions()
        {
            var mesh = Combine(
                BuildGrid(new[] { -1f, 0f, 1f }, new[] { 0f, 1f }),
                BuildGrid(new[] { 0.5f, 0.8f }, new[] { 2f, 2.2f }));

            var snapshot = MeshSnapshot.Read(mesh, out string error);
            Assert.IsNull(error, error);

            MeshIslands.Build(snapshot, 1e-5f, out var islands);
            Assert.AreEqual(2, islands.Count);
        }

        [Test]
        public void CopiesAKeptAsIsRegionThroughUntouched()
        {
            // A one-sided ribbon parked off to the +x side, as its own shell.
            var mesh = Combine(
                BuildGrid(new[] { -1f, 0f, 1f }, new[] { 0f, 1f }),
                BuildGrid(new[] { 0.5f, 0.8f }, new[] { 2f, 2.2f }));

            var snapshot = MeshSnapshot.Read(mesh, out string error);
            Assert.IsNull(error, error);
            var islandOfVertex = MeshIslands.Build(snapshot, 1e-5f, out var islands);

            // The ribbon is whichever island does not contain the origin-spanning grid.
            int ribbon = islandOfVertex[snapshot.vertexCount - 1];

            var options = BaseOptions();
            options.islandOfVertex = islandOfVertex;
            options.keptAsIsIslands = new HashSet<int> { ribbon };

            var report = new SymmetrizeReport();
            var result = MeshSymmetrizer.Build(snapshot, options, null, report);
            Assert.IsNotNull(result);

            int ribbonVertices = 0;
            foreach (var v in result.vertices)
            {
                if (v.y < 1.5f) continue;
                ribbonVertices++;
                Assert.Greater(v.x, 0f, "the ribbon should not have been mirrored to the other side");
            }
            Assert.AreEqual(4, ribbonVertices, "the ribbon should come through with its own vertices only");
            Assert.AreEqual(2, report.keptAsIsTriangleCount);
        }

        // ---- seam smoothing -------------------------------------------------------------------

        [Test]
        public void SmoothingFlattensTheRidgeTheMirrorLeaves()
        {
            // The kept half rises as it leaves the plane, so its reflection rises the other way
            // and the two meet in a crease down the middle.
            var mesh = BuildGrid(new[] { -1f, -0.75f, -0.5f, -0.25f, 0f }, new[] { 0f, 0.5f, 1f });
            var sloped = mesh.vertices;
            for (int i = 0; i < sloped.Length; i++) sloped[i].z = -sloped[i].x * 0.5f;
            mesh.vertices = sloped;
            mesh.RecalculateBounds();

            var sharp = Symmetrize(mesh, out _);
            float before = RidgeHeight(sharp);

            var options = BaseOptions();
            options.seamSmoothWidth = 0.6f;
            options.seamSmoothStrength = 0.6f;
            options.seamSmoothIterations = 5;
            var smoothed = Build(mesh, options, out var report);
            float after = RidgeHeight(smoothed);

            Assert.Greater(before, 0.05f, "the unsmoothed mirror should have a visible crease");
            Assert.Less(after, before * 0.75f, $"smoothing should flatten the crease ({before:0.####} → {after:0.####})");
            Assert.Greater(report.smoothedVertexCount, 0);
            AssertMirrorSymmetric(smoothed, Vector3.right, Vector3.zero);
        }

        /// <summary>How far the seam sits below the ridge either side of it, on the middle row.</summary>
        static float RidgeHeight(Mesh mesh)
        {
            var positions = mesh.vertices;
            float seamZ = 0f, sideZ = 0f;
            int sideCount = 0;
            foreach (var v in positions)
            {
                if (Mathf.Abs(v.y - 0.5f) > Tolerance) continue;
                if (Mathf.Abs(v.x) < Tolerance) seamZ = v.z;
                else if (Mathf.Abs(Mathf.Abs(v.x) - 0.25f) < Tolerance) { sideZ += v.z; sideCount++; }
            }
            return sideCount == 0 ? 0f : Mathf.Abs(sideZ / sideCount - seamZ);
        }

        // ---- plane fitting ----------------------------------------------------------------

        [Test]
        public void FitsAStraightPlaneWithoutInventingATilt()
        {
            var fit = SymmetryDetector.Fit(TaperedRidge(Quaternion.identity), out _);

            Assert.Greater(fit.score, 0.95f);
            Assert.Less(fit.tiltDegrees, 0.5f, "a square mesh should not be given a tilt");
        }

        [TestCase(5f)]
        [TestCase(14f)]
        [TestCase(27f)]
        [TestCase(40f)]
        public void FindsThePlaneAPieceWasModelledAround(float degrees)
        {
            // Mirroring a piece like this about a straight axis is what folds a ridge down its
            // middle; the fix is to find the plane it is actually symmetric about.
            var rotation = Quaternion.AngleAxis(degrees, Vector3.forward);
            var fit = SymmetryDetector.Fit(TaperedRidge(rotation), out _);

            float error = Vector3.Angle(fit.normal, rotation * Vector3.right);
            error = Mathf.Min(error, 180f - error);

            Assert.Greater(fit.score, 0.9f);
            Assert.Less(error, 2f, $"fitted plane is {error:0.00}° off the true one");
        }

        /// <summary>A ridge that tapers along its length, so it is symmetric about one plane only.</summary>
        static List<Vector3> TaperedRidge(Quaternion rotation)
        {
            var points = new List<Vector3>();
            for (int j = 0; j < 11; j++)
            for (int i = 0; i < 21; i++)
            {
                float x = -1f + 2f * i / 20f;
                float y = j / 10f;
                float taper = 0.35f + 0.65f * y;
                points.Add(rotation * new Vector3(x * taper, y, (0.4f - 0.4f * Mathf.Abs(x)) * taper));
            }
            return points;
        }

        // ---- falloff ------------------------------------------------------------------------

        [Test]
        public void EveryFalloffCurveRunsFromFullAtThePlaneToNothingAtTheRadius()
        {
            foreach (FalloffCurve curve in System.Enum.GetValues(typeof(FalloffCurve)))
            {
                Assert.AreEqual(1f, Falloff.Weight(curve, 0f), Tolerance, $"{curve} at the plane");
                Assert.AreEqual(0f, Falloff.Weight(curve, 1f), Tolerance, $"{curve} at the radius");
            }
        }

        [Test]
        public void SmoothingLeavesAKeptAsIsRegionWhereItWas()
        {
            var mesh = Combine(
                BuildGrid(new[] { -1f, -0.5f, 0f, 0.5f, 1f }, new[] { 0f, 1f }),
                BuildGrid(new[] { 0.5f, 0.8f }, new[] { 2f, 2.2f }));

            var snapshot = MeshSnapshot.Read(mesh, out string error);
            Assert.IsNull(error, error);
            var islandOfVertex = MeshIslands.Build(snapshot, 1e-5f, out _);

            var options = BaseOptions();
            options.islandOfVertex = islandOfVertex;
            options.keptAsIsIslands = new HashSet<int> { islandOfVertex[snapshot.vertexCount - 1] };
            // Wide enough to swallow the ribbon if the relax were allowed to touch it.
            options.seamSmoothWidth = 3f;
            options.seamSmoothStrength = 1f;
            options.seamSmoothIterations = 8;

            var result = MeshSymmetrizer.Build(snapshot, options, null, new SymmetrizeReport());

            foreach (var v in result.vertices)
            {
                if (v.y < 1.5f) continue;
                bool untouched = Mathf.Abs(v.y - 2f) < Tolerance || Mathf.Abs(v.y - 2.2f) < Tolerance;
                Assert.IsTrue(untouched, $"the kept-as-is ribbon was moved to {v}");
            }
        }

        // ---- helpers ----------------------------------------------------------------------

        static SymmetrizeOptions BaseOptions()
        {
            var options = new SymmetrizeOptions { keepPositiveSide = false, seamTolerance = Tolerance };
            options.SetAxisPlane(0, 0f);
            return options;
        }

        static Mesh Symmetrize(Mesh source, out SymmetrizeReport report, bool keepPositiveSide = false)
        {
            var options = BaseOptions();
            options.keepPositiveSide = keepPositiveSide;
            return Build(source, options, out report);
        }

        static Mesh Build(Mesh source, SymmetrizeOptions options, out SymmetrizeReport report)
        {
            var snapshot = MeshSnapshot.Read(source, out string error);
            Assert.IsNull(error, error);

            report = new SymmetrizeReport();
            var result = MeshSymmetrizer.Build(snapshot, options, null, report);
            Assert.IsNotNull(result);
            return result;
        }

        static void AssertMirrorSymmetric(Mesh mesh, Vector3 planeNormal, Vector3 planePoint)
        {
            var normal = planeNormal.normalized;
            var positions = mesh.vertices;
            foreach (var v in positions)
            {
                var mirrored = v - 2f * Vector3.Dot(v - planePoint, normal) * normal;

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
                uvs.Add(new Vector2(xs.Length == 1 ? 0f : (float)i / (xs.Length - 1),
                                    ys.Length == 1 ? 0f : (float)j / (ys.Length - 1)));
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

        /// <summary>Merges two meshes into one buffer, leaving them as separate shells.</summary>
        static Mesh Combine(Mesh first, Mesh second)
        {
            var mesh = new Mesh { name = "Combined" };
            var positions = new List<Vector3>(first.vertices);
            var normals = new List<Vector3>(first.normals);
            var uvs = new List<Vector2>(first.uv);
            var triangles = new List<int>(first.triangles);

            int offset = positions.Count;
            positions.AddRange(second.vertices);
            normals.AddRange(second.normals);
            uvs.AddRange(second.uv);
            foreach (int index in second.triangles) triangles.Add(index + offset);

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
