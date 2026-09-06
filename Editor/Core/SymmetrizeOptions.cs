using System.Collections.Generic;
using UnityEngine;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// How the seam relax fades with distance from the mirror plane. The same curves Blender
    /// offers for proportional editing, and for the same reason: the plane is the centre the
    /// correction radiates from, and which curve you pick decides whether the fix stays tight
    /// against the join or feathers out across the panel.
    /// </summary>
    public enum SeamFalloff
    {
        Smooth,
        Sphere,
        Root,
        Linear,
        Sharp,
        Constant,
    }

    public sealed class SymmetrizeOptions
    {
        // ---- Mirror plane -------------------------------------------------------------------

        /// <summary>Plane normal, in the mesh's own space. Need not be axis aligned.</summary>
        public Vector3 planeNormal = Vector3.right;

        /// <summary>A point the plane passes through, in the mesh's own space.</summary>
        public Vector3 planePoint = Vector3.zero;

        /// <summary>Which half survives and gets mirrored onto the other.</summary>
        public bool keepPositiveSide = false;

        /// <summary>Points the plane at an axis, which is what almost every avatar mesh wants.</summary>
        public void SetAxisPlane(int axis, float offset)
        {
            planeNormal = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
            planePoint = planeNormal * offset;
        }

        /// <summary>The axis the plane normal leans closest to, for labelling the two sides.</summary>
        public int DominantAxis
        {
            get
            {
                float x = Mathf.Abs(planeNormal.x);
                float y = Mathf.Abs(planeNormal.y);
                float z = Mathf.Abs(planeNormal.z);
                return x >= y && x >= z ? 0 : y >= z ? 1 : 2;
            }
        }

        /// <summary>How far the plane sits from the origin along its own normal.</summary>
        public float PlaneOffset => Vector3.Dot(planePoint, planeNormal.normalized);

        // ---- Seam ---------------------------------------------------------------------------

        /// <summary>How close to the plane a vertex has to be to count as sitting on it.</summary>
        public float seamTolerance = 1e-4f;

        /// <summary>
        /// Share the vertices that land on the plane between both halves, so the join is
        /// watertight. Turning this off leaves two coincident vertex rows down the middle.
        /// </summary>
        public bool weldSeam = true;

        /// <summary>
        /// How far from the plane the smoothing pass reaches, in mesh units. Zero disables it.
        /// This is the cure for the ridge that shows up when the kept half arrives at the plane
        /// at an angle: mirroring doubles that slope into a crease, and smoothing takes it out.
        /// </summary>
        public float seamSmoothWidth = 0f;

        /// <summary>How hard the smoothing pulls, per iteration, right at the seam.</summary>
        public float seamSmoothStrength = 0.5f;

        public int seamSmoothIterations = 3;

        /// <summary>Shape of the fade from the plane out to <see cref="seamSmoothWidth"/>.</summary>
        public SeamFalloff seamFalloff = SeamFalloff.Smooth;

        /// <summary>
        /// Blend recalculated normals into the smoothed band. Outside the band the mesh's own
        /// normals are left alone, so hand-authored shading elsewhere survives.
        /// </summary>
        public bool recalculateSeamNormals = true;

        // ---- Regions ------------------------------------------------------------------------

        /// <summary>
        /// Island index per source vertex, from <see cref="MeshIslands"/>. Null treats the whole
        /// mesh as a single region.
        /// </summary>
        public int[] islandOfVertex;

        /// <summary>
        /// Islands to copy through untouched — neither cut nor mirrored. This is how a piece
        /// that is asymmetric on purpose survives the rebuild.
        /// </summary>
        public HashSet<int> keptAsIsIslands;

        public bool IsKeptAsIs(int vertex)
        {
            if (islandOfVertex == null || keptAsIsIslands == null || keptAsIsIslands.Count == 0) return false;
            int island = islandOfVertex[vertex];
            return island >= 0 && keptAsIsIslands.Contains(island);
        }

        // ---- Channels -----------------------------------------------------------------------

        /// <summary>
        /// Drive the mirrored half of a blend shape from its opposite-side twin when the names
        /// form an L/R pair, so a one-sided shape stays one-sided.
        /// </summary>
        public bool pairBlendShapes = true;

        /// <summary>Remap bone indices on the mirrored half through L/R bone name matching.</summary>
        public bool mirrorBoneWeights = true;
    }

    public sealed class SymmetrizeReport
    {
        public readonly List<string> warnings = new List<string>();

        public int sourceVertexCount;
        public int keptVertexCount;
        public int seamVertexCount;
        public int resultVertexCount;
        public int sourceTriangleCount;
        public int resultTriangleCount;
        public int keptAsIsTriangleCount;
        public int smoothedVertexCount;
    }
}
