using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// How influence fades with distance. The same curves Blender offers for proportional
    /// editing, so the shapes behave the way anyone who has used that will expect.
    /// </summary>
    public enum FalloffCurve
    {
        Smooth,
        Sphere,
        Root,
        Linear,
        Sharp,
        Constant,
    }

    public static class Falloff
    {
        /// <summary>Influence at <paramref name="t"/> of the way out to the edge of the range.</summary>
        public static float Weight(FalloffCurve curve, float t)
        {
            float s = 1f - Mathf.Clamp01(t);
            switch (curve)
            {
                case FalloffCurve.Sphere: return Mathf.Sqrt(s * (2f - s));
                case FalloffCurve.Root: return Mathf.Sqrt(s);
                case FalloffCurve.Linear: return s;
                case FalloffCurve.Sharp: return s * s;
                case FalloffCurve.Constant: return t >= 1f ? 0f : 1f;
                default: return s * s * (3f - 2f * s); // Smooth
            }
        }

        /// <summary>
        /// Influence at a distance, with a plateau before the fade starts.
        ///
        /// The plateau is what keeps a correction from pinching. Fading straight from full
        /// strength at the selection means the geometry just outside it barely moves while the
        /// selection moves fully, and the step between them shows up as a crease — a small hill
        /// running along the boundary. Holding full strength for <paramref name="plateau"/> first
        /// carries the whole neighbourhood along and puts the bend somewhere it does not show.
        /// </summary>
        public static float Weight(FalloffCurve curve, float distance, float plateau, float radius)
        {
            if (distance <= plateau) return 1f;
            if (radius <= 0f) return 0f;
            return Weight(curve, (distance - plateau) / radius);
        }
    }
}
