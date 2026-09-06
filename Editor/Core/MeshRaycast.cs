using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>Ray against a mesh's triangles, for clicking on a model in the scene view.</summary>
    public static class MeshRaycast
    {
        /// <summary>
        /// Finds the nearest triangle the ray hits, in the mesh's own space. Returns the index
        /// into the triangle list of its first corner, so the caller can look up whatever it
        /// tracks per vertex.
        /// </summary>
        public static bool Raycast(MeshSnapshot src, Ray ray, out int vertexIndex, out Vector3 point)
        {
            vertexIndex = -1;
            point = Vector3.zero;

            float nearest = float.MaxValue;
            var positions = src.positions;

            foreach (var submesh in src.submeshes)
            {
                for (int t = 0; t < submesh.Length; t += 3)
                {
                    if (!HitsTriangle(ray, positions[submesh[t]], positions[submesh[t + 1]], positions[submesh[t + 2]],
                        out float distance)) continue;
                    if (distance >= nearest) continue;
                    nearest = distance;
                    vertexIndex = submesh[t];
                }
            }

            if (vertexIndex < 0) return false;
            point = ray.origin + ray.direction * nearest;
            return true;
        }

        /// <summary>Möller–Trumbore, hitting either face so back-facing pieces can still be picked.</summary>
        public static bool HitsTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0f;
            var ab = b - a;
            var ac = c - a;
            var p = Vector3.Cross(ray.direction, ac);
            float determinant = Vector3.Dot(ab, p);
            if (Mathf.Abs(determinant) < 1e-12f) return false;

            float inverse = 1f / determinant;
            var toStart = ray.origin - a;
            float u = Vector3.Dot(toStart, p) * inverse;
            if (u < 0f || u > 1f) return false;

            var q = Vector3.Cross(toStart, ab);
            float v = Vector3.Dot(ray.direction, q) * inverse;
            if (v < 0f || u + v > 1f) return false;

            distance = Vector3.Dot(ac, q) * inverse;
            return distance > 0f;
        }
    }
}
