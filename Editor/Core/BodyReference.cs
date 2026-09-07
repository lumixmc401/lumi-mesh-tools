using System.Collections.Generic;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// The avatar's body, expressed in the garment's own mesh space, together with the mirror
    /// plane taken from it.
    ///
    /// A garment is rarely symmetric enough to say where its own centre is — one measured here
    /// was 22mm out at the median and 43mm at worst — so asking it to supply its own reference
    /// gives a plane that is already wrong before any editing starts. The body underneath is
    /// symmetric to a fraction of a millimetre, and it is also the surface the garment has to
    /// keep sitting on, so it supplies both the plane and the fit constraint.
    /// </summary>
    public sealed class BodyReference
    {
        /// <summary>Body vertices, in the garment's mesh space.</summary>
        public readonly Vector3[] positions;
        public readonly Vector3[] normals;

        public Vector3 planeNormal = Vector3.right;
        public Vector3 planePoint = Vector3.zero;

        /// <summary>
        /// Median distance from a body point to the nearest mirrored body point. This is the
        /// reference's own error: if it is not small, nothing measured against it can be trusted.
        /// </summary>
        public float symmetryResidual = float.NaN;

        public readonly List<string> warnings = new List<string>();

        readonly PointGrid _grid;

        BodyReference(Vector3[] positions, Vector3[] normals, float cellSize)
        {
            this.positions = positions;
            this.normals = normals;
            _grid = new PointGrid(positions, cellSize);
        }

        public bool IsUsable => positions.Length > 0;

        /// <summary>
        /// A reference built straight from points already in the garment's space. The plane is
        /// left at its default until <see cref="FitPlane"/> is called.
        /// </summary>
        public static BodyReference FromPoints(IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals)
        {
            var p = new Vector3[positions.Count];
            var n = new Vector3[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                p[i] = positions[i];
                n[i] = normals != null && i < normals.Count ? normals[i] : Vector3.up;
            }
            float extent = p.Length > 0 ? BoundsOf(p).size.magnitude : 1f;
            return new BodyReference(p, n, Mathf.Max(extent * 0.01f, 1e-4f));
        }

        /// <summary>
        /// Gathers the given body renderers into <paramref name="garment"/>'s mesh space and fits
        /// the mirror plane.
        /// </summary>
        /// <param name="planeSource">
        /// Transform whose right axis seeds the plane normal — the avatar root or its hips. The
        /// offset along that normal is then fitted to the body itself, because an armature can sit
        /// a few millimetres off the mesh it drives.
        /// </param>
        public static BodyReference Build(Renderer garment, IList<Renderer> bodies, Transform planeSource)
        {
            var points = new List<Vector3>();
            var norms = new List<Vector3>();

            if (garment == null || bodies == null)
                return new BodyReference(points.ToArray(), norms.ToArray(), 0.02f);

            var toGarment = garment.transform.worldToLocalMatrix;

            foreach (var body in bodies)
            {
                var mesh = MeshOf(body);
                if (mesh == null) continue;

                // The bind-pose mesh, not a posed bake: a posed body carries the pose's own
                // asymmetry, which on a test avatar was 5.1mm against 0.15mm in bind pose.
                var toMesh = toGarment * body.transform.localToWorldMatrix;
                var v = mesh.vertices;
                var n = mesh.normals;
                bool hasNormals = n != null && n.Length == v.Length;

                for (int i = 0; i < v.Length; i++)
                {
                    points.Add(toMesh.MultiplyPoint3x4(v[i]));
                    norms.Add(hasNormals ? toMesh.MultiplyVector(n[i]).normalized : Vector3.up);
                }
            }

            var array = points.ToArray();
            var extent = array.Length > 0 ? BoundsOf(array).size.magnitude : 1f;
            var reference = new BodyReference(array, norms.ToArray(), Mathf.Max(extent * 0.01f, 1e-4f));

            if (array.Length == 0)
            {
                reference.warnings.Add("No body mesh was given, so there is nothing to measure against.");
                return reference;
            }

            reference.FitPlane(SeedNormal(garment, planeSource));
            reference.CheckOverlaps(garment);
            return reference;
        }

        static Mesh MeshOf(Renderer renderer)
        {
            switch (renderer)
            {
                case null: return null;
                case SkinnedMeshRenderer skinned: return skinned.sharedMesh;
                default:
                    var filter = renderer.GetComponent<MeshFilter>();
                    return filter != null ? filter.sharedMesh : null;
            }
        }

        static Bounds BoundsOf(IReadOnlyList<Vector3> points)
        {
            var bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Count; i++) bounds.Encapsulate(points[i]);
            return bounds;
        }

        static Vector3 SeedNormal(Renderer garment, Transform planeSource)
        {
            if (planeSource == null) return Vector3.right;
            var candidate = garment.transform.worldToLocalMatrix.MultiplyVector(planeSource.right);
            return candidate.sqrMagnitude > 1e-8f ? candidate.normalized : Vector3.right;
        }

        /// <summary>
        /// Points the mirror plane along <paramref name="seedNormal"/> and searches for the offset
        /// where the body best mirrors onto itself. The armature gives the direction; the mesh
        /// gives the exact position, because a rig can sit a few millimetres off the skin it drives.
        /// </summary>
        public void FitPlane(Vector3 seedNormal)
        {
            var normal = seedNormal.sqrMagnitude > 1e-8f ? seedNormal.normalized : Vector3.right;
            planeNormal = normal;
            if (positions.Length == 0) return;

            // Sub-sample: the offset search runs the residual many times and a body mesh is dense.
            var sample = Subsample(positions, 2000);
            var bounds = BoundsOf(positions);

            float seed = Vector3.Dot(bounds.center, normal);
            float span = Mathf.Max(bounds.size.magnitude * 0.05f, 0.01f);

            float best = seed;
            float bestScore = Residual(sample, normal, seed);
            for (int pass = 0; pass < 4; pass++)
            {
                float step = span / 8f;
                for (int i = -8; i <= 8; i++)
                {
                    float offset = best + i * step;
                    float score = Residual(sample, normal, offset);
                    if (score < bestScore) { bestScore = score; best = offset; }
                }
                span *= 0.25f;
            }

            planePoint = normal * best;
            symmetryResidual = bestScore;

            if (symmetryResidual > 0.001f)
                warnings.Add(
                    $"The body is only symmetric to {symmetryResidual * 1000f:0.0}mm about the fitted plane. " +
                    "Check that the renderers listed are the body and not clothing, and that the mesh " +
                    "given is the bind pose rather than something already edited.");
        }

        /// <summary>Median distance from a sampled point to the nearest point of the mirrored set.</summary>
        float Residual(Vector3[] sample, Vector3 normal, float offset)
        {
            var origin = normal * offset;
            var distances = new List<float>(sample.Length);
            foreach (var p in sample)
            {
                var mirrored = p - 2f * Vector3.Dot(p - origin, normal) * normal;
                distances.Add(_grid.NearestDistance(mirrored));
            }
            distances.Sort();
            return distances.Count == 0 ? float.PositiveInfinity : distances[distances.Count / 2];
        }

        static Vector3[] Subsample(Vector3[] source, int count)
        {
            if (source.Length <= count) return source;
            var result = new Vector3[count];
            // A fixed stride rather than a random pick, so the fit is repeatable run to run.
            float stride = (float)source.Length / count;
            for (int i = 0; i < count; i++) result[i] = source[Mathf.Min(source.Length - 1, (int)(i * stride))];
            return result;
        }

        void CheckOverlaps(Renderer garment)
        {
            var mesh = MeshOf(garment);
            if (mesh == null) return;
            var garmentBounds = mesh.bounds;
            var bodyBounds = BoundsOf(positions);
            if (bodyBounds.Intersects(garmentBounds)) return;

            warnings.Add(
                "The body meshes given do not overlap the garment at all, so every measurement " +
                "against them is meaningless. On avatars split into several skins this usually " +
                "means the wrong one was picked — a chest-and-up mesh listed for a garment worn " +
                "at the hips, for instance.");
        }

        public Vector3 Mirror(Vector3 point) =>
            point - 2f * Vector3.Dot(point - planePoint, planeNormal) * planeNormal;

        /// <summary>
        /// Signed distance from the body surface: positive outside, negative sunk into it.
        /// Approximated from the nearest body vertex and its normal, which is accurate enough on
        /// a dense avatar skin and far cheaper than a true closest-point-on-triangle.
        /// </summary>
        public float Clearance(Vector3 point)
        {
            int i = _grid.Nearest(point, out _);
            if (i < 0) return float.NaN;
            return Vector3.Dot(point - positions[i], normals[i]);
        }

        public int Nearest(Vector3 point, out float distance) => _grid.Nearest(point, out distance);
    }
}
