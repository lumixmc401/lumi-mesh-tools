using System.Collections.Generic;

namespace LumiMeshTools.Editor
{
    public sealed class SymmetrizeOptions
    {
        /// <summary>0 = X, 1 = Y, 2 = Z, in the mesh's own space.</summary>
        public int axis = 0;

        /// <summary>Where the mirror plane sits along <see cref="axis"/>.</summary>
        public float planeOffset = 0f;

        /// <summary>Which half survives and gets mirrored onto the other.</summary>
        public bool keepPositiveSide = false;

        /// <summary>How close to the plane a vertex has to be to count as sitting on it.</summary>
        public float seamTolerance = 1e-4f;

        /// <summary>
        /// Share the vertices that land on the plane between both halves, so the join is
        /// watertight. Turning this off leaves two coincident vertex rows down the middle.
        /// </summary>
        public bool weldSeam = true;

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
        public int unmappedBoneCount;
    }
}
