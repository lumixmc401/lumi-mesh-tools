using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Rebuilds a mesh so that one half is an exact mirror of the other.
    ///
    /// The pass runs in three stages: cut the mesh against the mirror plane (splitting the
    /// triangles that straddle it, so the source half needs no pre-existing centre seam),
    /// weld what landed on the plane, then duplicate the survivors reflected across it.
    /// Every per-vertex channel is carried through both stages, which is why blend shapes,
    /// skin weights and UVs come out the far end intact.
    /// </summary>
    public static class MeshSymmetrizer
    {
        /// <summary>
        /// An output vertex, expressed as a point on the source mesh: either a plain copy of
        /// vertex <see cref="a"/>, or the point <see cref="t"/> of the way along the edge from
        /// <see cref="a"/> to <see cref="b"/> where that edge crosses the mirror plane.
        /// </summary>
        public struct VertexRef
        {
            public int a;
            public int b;   // -1 for a plain copy
            public float t;
        }

        public static Mesh Build(MeshSnapshot src, SymmetrizeOptions options, int[] boneMirror, SymmetrizeReport report)
        {
            report = report ?? new SymmetrizeReport();
            int axis = Mathf.Clamp(options.axis, 0, 2);
            float side = options.keepPositiveSide ? 1f : -1f;
            float tol = Mathf.Max(options.seamTolerance, 1e-7f);
            int n = src.vertexCount;

            report.sourceVertexCount = n;

            // Signed distance to the plane, flipped so that positive always means "keep".
            var distance = new float[n];
            var inside = new bool[n];
            for (int i = 0; i < n; i++)
            {
                distance[i] = (src.positions[i][axis] - options.planeOffset) * side;
                inside[i] = distance[i] >= -tol;
            }

            // ---- Stage 1: cut ------------------------------------------------------------
            var verts = new List<VertexRef>(n);
            var copyOf = new int[n];
            for (int i = 0; i < n; i++) copyOf[i] = -1;
            var cutOfEdge = new Dictionary<long, int>();

            int CopyVertex(int i)
            {
                if (copyOf[i] < 0)
                {
                    copyOf[i] = verts.Count;
                    verts.Add(new VertexRef { a = i, b = -1, t = 0f });
                }
                return copyOf[i];
            }

            int CutVertex(int keep, int drop)
            {
                long key = keep < drop
                    ? ((long)keep << 32) | (uint)drop
                    : ((long)drop << 32) | (uint)keep;
                if (cutOfEdge.TryGetValue(key, out int existing)) return existing;

                float t = distance[keep] / (distance[keep] - distance[drop]);
                var vr = keep < drop
                    ? new VertexRef { a = keep, b = drop, t = t }
                    : new VertexRef { a = drop, b = keep, t = 1f - t };

                int index = verts.Count;
                verts.Add(vr);
                cutOfEdge[key] = index;
                return index;
            }

            var keptTriangles = new List<int>[src.submeshes.Length];
            for (int sm = 0; sm < src.submeshes.Length; sm++)
            {
                var source = src.submeshes[sm];
                var output = keptTriangles[sm] = new List<int>(source.Length);
                report.sourceTriangleCount += source.Length / 3;

                for (int t = 0; t < source.Length; t += 3)
                {
                    int a = source[t], b = source[t + 1], c = source[t + 2];
                    int insideCount = (inside[a] ? 1 : 0) + (inside[b] ? 1 : 0) + (inside[c] ? 1 : 0);
                    if (insideCount == 0) continue;

                    if (insideCount == 3)
                    {
                        output.Add(CopyVertex(a));
                        output.Add(CopyVertex(b));
                        output.Add(CopyVertex(c));
                        continue;
                    }

                    // Rotating the triangle keeps the winding order, so we can normalise which
                    // corner is the odd one out and handle a single arrangement of each case.
                    if (insideCount == 1)
                    {
                        while (!inside[a]) { int tmp = a; a = b; b = c; c = tmp; }
                        output.Add(CopyVertex(a));
                        output.Add(CutVertex(a, b));
                        output.Add(CutVertex(a, c));
                    }
                    else
                    {
                        while (inside[c]) { int tmp = a; a = b; b = c; c = tmp; }
                        int bc = CutVertex(b, c);
                        int ca = CutVertex(a, c);
                        output.Add(CopyVertex(a));
                        output.Add(CopyVertex(b));
                        output.Add(bc);
                        output.Add(CopyVertex(a));
                        output.Add(bc);
                        output.Add(ca);
                    }
                }
            }

            int kept = verts.Count;
            report.keptVertexCount = kept;
            if (kept == 0)
            {
                report.warnings.Add("Nothing is on the side you chose to keep. Try the other side, or a different axis.");
                return null;
            }

            // ---- Stage 2: classify the seam and lay out the mirrored half ------------------
            var onPlane = new bool[kept];
            var basePositions = new Vector3[kept];
            for (int k = 0; k < kept; k++)
            {
                var p = Sample(src.positions, verts[k]);
                if (options.weldSeam && Mathf.Abs(p[axis] - options.planeOffset) <= tol)
                {
                    onPlane[k] = true;
                    p[axis] = options.planeOffset;
                    report.seamVertexCount++;
                }
                basePositions[k] = p;
            }

            var mirrorOf = new int[kept];
            int total = kept;
            for (int k = 0; k < kept; k++) mirrorOf[k] = onPlane[k] ? k : total++;
            report.resultVertexCount = total;

            // ---- Stage 3: fill every channel ---------------------------------------------
            var positions = new Vector3[total];
            for (int k = 0; k < kept; k++)
            {
                positions[k] = basePositions[k];
                if (mirrorOf[k] != k) positions[mirrorOf[k]] = ReflectPoint(basePositions[k], axis, options.planeOffset);
            }

            Vector3[] normals = null;
            if (src.normals != null)
            {
                normals = new Vector3[total];
                for (int k = 0; k < kept; k++)
                {
                    var nrm = Sample(src.normals, verts[k]).normalized;
                    if (onPlane[k])
                    {
                        // Flattening the seam normal onto the plane is what makes the two halves
                        // shade continuously across the join.
                        var flat = nrm;
                        flat[axis] = 0f;
                        if (flat.sqrMagnitude > 1e-8f) nrm = flat.normalized;
                    }
                    normals[k] = nrm;
                    if (mirrorOf[k] != k) normals[mirrorOf[k]] = ReflectVector(nrm, axis);
                }
            }

            Vector4[] tangents = null;
            if (src.tangents != null)
            {
                tangents = new Vector4[total];
                for (int k = 0; k < kept; k++)
                {
                    var tan = SampleTangent(src.tangents, verts[k]);
                    tangents[k] = tan;
                    if (mirrorOf[k] != k)
                    {
                        var mirrored = ReflectVector(new Vector3(tan.x, tan.y, tan.z), axis);
                        // Reflection flips handedness, so the bitangent sign flips with it.
                        tangents[mirrorOf[k]] = new Vector4(mirrored.x, mirrored.y, mirrored.z, -tan.w);
                    }
                }
            }

            Color[] colors = null;
            if (src.colors != null)
            {
                colors = new Color[total];
                for (int k = 0; k < kept; k++)
                {
                    var col = Sample(src.colors, verts[k]);
                    colors[k] = col;
                    if (mirrorOf[k] != k) colors[mirrorOf[k]] = col;
                }
            }

            var uvs = new Vector4[MeshSnapshot.UvChannels][];
            for (int ch = 0; ch < MeshSnapshot.UvChannels; ch++)
            {
                var source = src.uvs[ch];
                if (source == null) continue;
                var channel = uvs[ch] = new Vector4[total];
                for (int k = 0; k < kept; k++)
                {
                    var uv = Sample(source, verts[k]);
                    channel[k] = uv;
                    // The mirrored half shares the source half's UV island, which is what makes a
                    // symmetric texture line up on both sides.
                    if (mirrorOf[k] != k) channel[mirrorOf[k]] = uv;
                }
            }

            BoneWeight[] boneWeights = null;
            if (src.boneWeights != null)
            {
                boneWeights = new BoneWeight[total];
                bool remap = options.mirrorBoneWeights && boneMirror != null;
                for (int k = 0; k < kept; k++)
                {
                    var bw = SampleBoneWeight(src.boneWeights, verts[k]);
                    boneWeights[k] = bw;
                    if (mirrorOf[k] != k)
                        boneWeights[mirrorOf[k]] = remap ? MirrorBoneWeight(bw, boneMirror) : bw;
                }
            }

            // ---- Triangles ----------------------------------------------------------------
            var finalTriangles = new List<int>[src.submeshes.Length];
            for (int sm = 0; sm < keptTriangles.Length; sm++)
            {
                var source = keptTriangles[sm];
                var output = finalTriangles[sm] = new List<int>(source.Count * 2);
                output.AddRange(source);

                for (int t = 0; t < source.Count; t += 3)
                {
                    int a = source[t], b = source[t + 1], c = source[t + 2];
                    // A triangle lying entirely in the plane would mirror onto itself, back to
                    // front, so skip it rather than emit a coincident double-sided face.
                    if (onPlane[a] && onPlane[b] && onPlane[c]) continue;
                    // Reflection reverses winding, so swap two corners to keep the face outward.
                    output.Add(mirrorOf[a]);
                    output.Add(mirrorOf[c]);
                    output.Add(mirrorOf[b]);
                }
                report.resultTriangleCount += output.Count / 3;
            }

            // ---- Assemble -----------------------------------------------------------------
            var mesh = new Mesh { name = src.name + "_Symmetric" };
            mesh.indexFormat = total > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(positions);
            if (normals != null) mesh.SetNormals(normals);
            if (tangents != null) mesh.SetTangents(tangents);
            if (colors != null) mesh.SetColors(colors);

            for (int ch = 0; ch < MeshSnapshot.UvChannels; ch++)
            {
                if (uvs[ch] == null) continue;
                SetUvChannel(mesh, ch, uvs[ch], src.uvDimensions[ch]);
            }

            if (boneWeights != null)
            {
                mesh.boneWeights = boneWeights;
                if (src.bindposes != null) mesh.bindposes = src.bindposes;
            }

            mesh.subMeshCount = finalTriangles.Length;
            for (int sm = 0; sm < finalTriangles.Length; sm++)
                mesh.SetTriangles(finalTriangles[sm], sm, false);

            mesh.RecalculateBounds();

            BuildBlendShapes(mesh, src, options, verts, mirrorOf, onPlane, kept, total, axis, report);

            return mesh;
        }

        static void BuildBlendShapes(Mesh mesh, MeshSnapshot src, SymmetrizeOptions options,
            List<VertexRef> verts, int[] mirrorOf, bool[] onPlane, int kept, int total, int axis,
            SymmetrizeReport report)
        {
            if (src.blendShapes.Count == 0) return;

            var byName = new Dictionary<string, int>(src.blendShapes.Count);
            for (int i = 0; i < src.blendShapes.Count; i++) byName[src.blendShapes[i].name] = i;

            for (int s = 0; s < src.blendShapes.Count; s++)
            {
                var shape = src.blendShapes[s];
                var partner = shape;

                if (options.pairBlendShapes &&
                    NameSymmetry.TryFlip(shape.name, out string opposite) &&
                    byName.TryGetValue(opposite, out int partnerIndex))
                {
                    var candidate = src.blendShapes[partnerIndex];
                    if (candidate.frames.Count == shape.frames.Count) partner = candidate;
                    else report.warnings.Add(
                        "Blend shape '" + shape.name + "' and its pair '" + opposite +
                        "' have different frame counts; mirrored from its own deltas instead.");
                }

                for (int f = 0; f < shape.frames.Count; f++)
                {
                    var own = shape.frames[f];
                    var other = partner.frames[f];

                    var dv = MirrorDeltas(own.deltaVertices, other.deltaVertices, verts, mirrorOf, onPlane, kept, total, axis, options.weldSeam);
                    var dn = own.deltaNormals != null || other.deltaNormals != null
                        ? MirrorDeltas(own.deltaNormals, other.deltaNormals, verts, mirrorOf, onPlane, kept, total, axis, options.weldSeam)
                        : null;
                    var dt = own.deltaTangents != null || other.deltaTangents != null
                        ? MirrorDeltas(own.deltaTangents, other.deltaTangents, verts, mirrorOf, onPlane, kept, total, axis, options.weldSeam)
                        : null;

                    mesh.AddBlendShapeFrame(shape.name, own.weight, dv, dn, dt);
                }
            }
        }

        static Vector3[] MirrorDeltas(Vector3[] own, Vector3[] partner, List<VertexRef> verts,
            int[] mirrorOf, bool[] onPlane, int kept, int total, int axis, bool weldSeam)
        {
            var result = new Vector3[total];
            for (int k = 0; k < kept; k++)
            {
                var delta = Sample(own, verts[k]);
                if (weldSeam && onPlane[k]) delta[axis] = 0f; // keep the seam on the plane while the shape plays
                result[k] = delta;

                if (mirrorOf[k] != k)
                    result[mirrorOf[k]] = ReflectVector(Sample(partner, verts[k]), axis);
            }
            return result;
        }

        // ---- Reflection ---------------------------------------------------------------------

        static Vector3 ReflectPoint(Vector3 p, int axis, float offset)
        {
            p[axis] = 2f * offset - p[axis];
            return p;
        }

        static Vector3 ReflectVector(Vector3 v, int axis)
        {
            v[axis] = -v[axis];
            return v;
        }

        // ---- Sampling -----------------------------------------------------------------------

        static Vector3 Sample(Vector3[] values, VertexRef v)
        {
            if (values == null) return Vector3.zero;
            return v.b < 0 ? values[v.a] : Vector3.LerpUnclamped(values[v.a], values[v.b], v.t);
        }

        static Color Sample(Color[] values, VertexRef v)
        {
            return v.b < 0 ? values[v.a] : Color.LerpUnclamped(values[v.a], values[v.b], v.t);
        }

        static Vector4 Sample(List<Vector4> values, VertexRef v)
        {
            return v.b < 0 ? values[v.a] : Vector4.LerpUnclamped(values[v.a], values[v.b], v.t);
        }

        static Vector4 SampleTangent(Vector4[] values, VertexRef v)
        {
            if (v.b < 0) return values[v.a];
            var a = values[v.a];
            var b = values[v.b];
            var dir = Vector3.LerpUnclamped(new Vector3(a.x, a.y, a.z), new Vector3(b.x, b.y, b.z), v.t);
            if (dir.sqrMagnitude > 1e-12f) dir.Normalize();
            // w is a handedness flag, not a value to average.
            float w = v.t < 0.5f ? a.w : b.w;
            return new Vector4(dir.x, dir.y, dir.z, w);
        }

        // ---- Bone weights -------------------------------------------------------------------

        const int MaxInfluences = 8;
        static readonly int[] ScratchBones = new int[MaxInfluences];
        static readonly float[] ScratchWeights = new float[MaxInfluences];

        static BoneWeight SampleBoneWeight(BoneWeight[] values, VertexRef v)
        {
            if (v.b < 0) return values[v.a];
            int count = 0;
            AddInfluences(ref count, values[v.a], 1f - v.t);
            AddInfluences(ref count, values[v.b], v.t);
            return Compose(count);
        }

        static BoneWeight MirrorBoneWeight(BoneWeight bw, int[] boneMirror)
        {
            int count = 0;
            AddInfluence(ref count, Remap(bw.boneIndex0, boneMirror), bw.weight0);
            AddInfluence(ref count, Remap(bw.boneIndex1, boneMirror), bw.weight1);
            AddInfluence(ref count, Remap(bw.boneIndex2, boneMirror), bw.weight2);
            AddInfluence(ref count, Remap(bw.boneIndex3, boneMirror), bw.weight3);
            return Compose(count);
        }

        static int Remap(int bone, int[] boneMirror)
        {
            return bone >= 0 && bone < boneMirror.Length ? boneMirror[bone] : bone;
        }

        static void AddInfluences(ref int count, BoneWeight bw, float scale)
        {
            AddInfluence(ref count, bw.boneIndex0, bw.weight0 * scale);
            AddInfluence(ref count, bw.boneIndex1, bw.weight1 * scale);
            AddInfluence(ref count, bw.boneIndex2, bw.weight2 * scale);
            AddInfluence(ref count, bw.boneIndex3, bw.weight3 * scale);
        }

        static void AddInfluence(ref int count, int bone, float weight)
        {
            if (weight <= 0f) return;
            for (int i = 0; i < count; i++)
            {
                if (ScratchBones[i] != bone) continue;
                ScratchWeights[i] += weight;
                return;
            }
            if (count < MaxInfluences)
            {
                ScratchBones[count] = bone;
                ScratchWeights[count] = weight;
                count++;
                return;
            }
            int smallest = 0;
            for (int i = 1; i < count; i++) if (ScratchWeights[i] < ScratchWeights[smallest]) smallest = i;
            if (weight > ScratchWeights[smallest])
            {
                ScratchBones[smallest] = bone;
                ScratchWeights[smallest] = weight;
            }
        }

        static BoneWeight Compose(int count)
        {
            var result = new BoneWeight();
            if (count == 0) { result.weight0 = 1f; return result; }

            int take = Mathf.Min(4, count);
            for (int i = 0; i < take; i++)
            {
                int best = i;
                for (int j = i + 1; j < count; j++) if (ScratchWeights[j] > ScratchWeights[best]) best = j;
                if (best == i) continue;

                float weight = ScratchWeights[i];
                ScratchWeights[i] = ScratchWeights[best];
                ScratchWeights[best] = weight;

                int bone = ScratchBones[i];
                ScratchBones[i] = ScratchBones[best];
                ScratchBones[best] = bone;
            }

            float sum = 0f;
            for (int i = 0; i < take; i++) sum += ScratchWeights[i];
            if (sum <= 0f) { result.boneIndex0 = ScratchBones[0]; result.weight0 = 1f; return result; }

            result.boneIndex0 = ScratchBones[0];
            result.weight0 = ScratchWeights[0] / sum;
            if (take > 1) { result.boneIndex1 = ScratchBones[1]; result.weight1 = ScratchWeights[1] / sum; }
            if (take > 2) { result.boneIndex2 = ScratchBones[2]; result.weight2 = ScratchWeights[2] / sum; }
            if (take > 3) { result.boneIndex3 = ScratchBones[3]; result.weight3 = ScratchWeights[3] / sum; }
            return result;
        }

        // ---- Misc ---------------------------------------------------------------------------

        static void SetUvChannel(Mesh mesh, int channel, Vector4[] values, int dimension)
        {
            switch (dimension)
            {
                case 2:
                {
                    var list = new List<Vector2>(values.Length);
                    foreach (var v in values) list.Add(new Vector2(v.x, v.y));
                    mesh.SetUVs(channel, list);
                    break;
                }
                case 3:
                {
                    var list = new List<Vector3>(values.Length);
                    foreach (var v in values) list.Add(new Vector3(v.x, v.y, v.z));
                    mesh.SetUVs(channel, list);
                    break;
                }
                default:
                    mesh.SetUVs(channel, new List<Vector4>(values));
                    break;
            }
        }

        /// <summary>
        /// Pairs up each bone with its opposite-side counterpart by name, so weights on the
        /// mirrored half drive the mirrored bones. Bones with no counterpart map to themselves.
        /// </summary>
        public static int[] BuildBoneMirrorMap(Transform[] bones, out int unmapped)
        {
            unmapped = 0;
            if (bones == null) return null;

            var indexByName = new Dictionary<string, int>(bones.Length);
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] != null) indexByName[bones[i].name] = i;

            var map = new int[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                map[i] = i;
                if (bones[i] == null) continue;
                if (!NameSymmetry.TryFlip(bones[i].name, out string opposite)) continue;
                if (indexByName.TryGetValue(opposite, out int j)) map[i] = j;
                else unmapped++;
            }
            return map;
        }
    }
}
