using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Atlas-UV parameters for a section. Mirrors the relevant fields of UE
    /// <c>FSectionDomainMapping</c> (<c>doc/source/.../MeshPartitionChannelCollection_GenerateChannelUV.cpp</c>).
    /// </summary>
    [Serializable]
    public struct ChannelUVSettings
    {
        /// <summary>World size (uu) of one ideal texel. UE <c>TexelSize3D</c> / Definition <c>ChannelTexelSize</c>.</summary>
        public float TexelSize3D;
        /// <summary>Upper bound on the per-section texture resolution.</summary>
        public int MaxImageResolution;
        /// <summary>Lower bound on the per-section texture resolution.</summary>
        public int MinImageResolution;
        /// <summary>Target gutter width in output texels (drives the inter-island margin).</summary>
        public float GutterTexelCount;

        public static ChannelUVSettings Default => new ChannelUVSettings
        {
            TexelSize3D = 100f,
            MaxImageResolution = 4096,
            MinImageResolution = 16,
            GutterTexelCount = 4f,
        };
    }

    /// <summary>
    /// Per-section atlas metrics produced alongside the generated UVs: the chosen texture
    /// resolution and the world-space size encoded by one UV unit (<c>Size3D</c>), used by the
    /// material to relate texels to world space. Port of the resolution-bearing fields of UE
    /// <c>FSectionDomainMapping</c>.
    /// </summary>
    public struct SectionDomainMapping
    {
        public int ImageResolution;
        public float Size3D;
        public float Area3D;
        public float AreaUV;

        public float2 TexcoordMetrics => new float2(Size3D, Size3D);
    }

    /// <summary>
    /// Generates atlas <see cref="MeshData.ChannelUVs"/> for a section by box/triangle-normal
    /// projection (simplified port of UE <c>ReferenceBoxProject</c>): each triangle is assigned to
    /// the box face matching the dominant axis of its geometric normal, projected onto that face,
    /// and the 6 face-islands are shelf-packed into the unit square with a gutter margin.
    ///
    /// Because a vertex may belong to triangles on different box faces, the UV must split at those
    /// seams. This generates a <b>fresh, de-duplicated</b> <see cref="MeshData"/> where boundary
    /// vertices are duplicated per face — callers should use the returned mesh for rendering. The
    /// per-vertex weight side-car is remapped to the new vertex set in parallel.
    /// </summary>
    public static class ChannelUVUnwrap
    {
        // Box faces: +X,-X,+Y,-Y,+Z,-Z. For each, the two minor axes used as island-local (u,v).
        static readonly int[] AxisU = { 2, 2, 0, 0, 0, 0 }; // u axis index per face
        static readonly int[] AxisV = { 1, 1, 2, 2, 1, 1 }; // v axis index per face

        /// <summary>
        /// Produces a new section mesh carrying generated <see cref="MeshData.ChannelUVs"/>, with
        /// vertices duplicated where box faces meet. The source weight side-car (if any) is remapped
        /// onto the new vertices via <paramref name="resultWeights"/>. The caller owns and must
        /// dispose the returned <see cref="MeshData"/> and <paramref name="resultWeights"/>.
        /// </summary>
        public static MeshData Generate(
            in MeshData source,
            WeightLayerSet sourceWeights,
            in ChannelUVSettings settings,
            Allocator allocator,
            out WeightLayerSet resultWeights,
            out SectionDomainMapping mapping)
        {
            int triCount = source.TriangleCount;

            // 1. Assign each triangle to a box face and emit per-(face,vertex) UV elements.
            //    A vertex used by triangles on multiple faces is duplicated, one copy per face.
            //    Managed scratch (not Allocator.Temp) so this runs on the async cook's worker thread —
            //    Allocator.Temp is not valid on a thread-pool thread (doc/08 §8).
            var faceOfTri = new int[triCount];
            for (int t = 0; t < triCount; t++)
                faceOfTri[t] = DominantFace(TriangleNormal(source, t));

            // Map (sourceVertex, face) -> new vertex id, plus the inverse for attribute transfer.
            var remap = new System.Collections.Generic.Dictionary<(int, int), int>();
            var newToSource = new System.Collections.Generic.List<int>();
            var newToFace = new System.Collections.Generic.List<int>();
            var newTriangles = new int3[triCount];

            for (int t = 0; t < triCount; t++)
            {
                int face = faceOfTri[t];
                int3 tri = source.Triangles[t];
                newTriangles[t] = new int3(
                    GetOrAdd(remap, newToSource, newToFace, tri.x, face),
                    GetOrAdd(remap, newToSource, newToFace, tri.y, face),
                    GetOrAdd(remap, newToSource, newToFace, tri.z, face));
            }

            int newVertCount = newToSource.Count;

            // 2. Project each new vertex into island-local UV (the two minor axes of its face).
            var islandUV = new float2[newVertCount];
            var faceMin = new float2[6];
            var faceMax = new float2[6];
            for (int f = 0; f < 6; f++) { faceMin[f] = new float2(float.MaxValue); faceMax[f] = new float2(float.MinValue); }

            for (int v = 0; v < newVertCount; v++)
            {
                int face = newToFace[v];
                float3 p = source.Vertices[newToSource[v]];
                float2 uv = new float2(p[AxisU[face]], p[AxisV[face]]);
                islandUV[v] = uv;
                faceMin[face] = math.min(faceMin[face], uv);
                faceMax[face] = math.max(faceMax[face], uv);
            }

            // 3. Shelf-pack the (up to 6) non-empty face islands into the unit square, gutter margin.
            //    Resolution isn't known yet, so use a provisional gutter fraction and refine below.
            float gutter = ProvisionalGutterUV(settings);
            PackIslands(faceMin, faceMax, gutter, out var faceOffset, out var faceScale, out float packScale);

            // 4. Allocate the de-duplicated mesh and write attributes + final UVs.
            var dst = MeshData.Allocate(newVertCount, triCount, allocator,
                source.HasNormals, withChannelUVs: true, source.HasSourceUV0, source.HasBaseIDs);

            resultWeights = (sourceWeights != null && sourceWeights.LayerCount > 0)
                ? new WeightLayerSet(allocator)
                : null;
            if (resultWeights != null)
                for (int i = 0; i < sourceWeights.LayerCount; i++)
                    resultWeights.InitializeLayer(sourceWeights.LayerNames[i], newVertCount);

            for (int v = 0; v < newVertCount; v++)
            {
                int sv = newToSource[v];
                int face = newToFace[v];
                dst.Vertices[v] = source.Vertices[sv];
                if (source.HasNormals) dst.Normals[v] = source.Normals[sv];
                if (source.HasSourceUV0) dst.SourceUV0[v] = source.SourceUV0[sv];

                float2 local = (islandUV[v] - faceMin[face]) * faceScale[face] + faceOffset[face];
                dst.ChannelUVs[v] = local;
            }

            // Remap weight layers onto the new (seam-split) vertex set. NativeArray is returned by
            // value, so the destination/source layers are hoisted into locals before indexing.
            if (resultWeights != null)
            {
                for (int i = 0; i < sourceWeights.LayerCount; i++)
                {
                    var srcLayer = sourceWeights.GetLayerByIndex(i);
                    var dstLayer = resultWeights.GetLayerByIndex(i);
                    for (int v = 0; v < newVertCount; v++)
                        dstLayer[v] = srcLayer[newToSource[v]];
                }
            }

            for (int t = 0; t < triCount; t++)
            {
                dst.Triangles[t] = newTriangles[t];
                if (source.HasBaseIDs) dst.BaseIDLayer[t] = source.BaseIDLayer[t];
            }

            // 5. Compute the domain mapping (resolution + Size3D) from 3D vs UV area.
            mapping = ComputeDomainMapping(dst, settings, packScale);

            return dst;
        }

        static int GetOrAdd(
            System.Collections.Generic.Dictionary<(int, int), int> remap,
            System.Collections.Generic.List<int> newToSource,
            System.Collections.Generic.List<int> newToFace,
            int sourceVertex, int face)
        {
            var key = (sourceVertex, face);
            if (remap.TryGetValue(key, out int id))
                return id;
            id = newToSource.Count;
            remap.Add(key, id);
            newToSource.Add(sourceVertex);
            newToFace.Add(face);
            return id;
        }

        static float3 TriangleNormal(in MeshData mesh, int t)
        {
            int3 tri = mesh.Triangles[t];
            float3 a = mesh.Vertices[tri.x];
            float3 b = mesh.Vertices[tri.y];
            float3 c = mesh.Vertices[tri.z];
            return math.cross(b - a, c - a);
        }

        /// <summary>Box face index for the dominant axis of a normal: +X,-X,+Y,-Y,+Z,-Z = 0..5.</summary>
        static int DominantFace(float3 normal)
        {
            float3 a = math.abs(normal);
            if (a.x >= a.y && a.x >= a.z) return normal.x >= 0 ? 0 : 1;
            if (a.y >= a.x && a.y >= a.z) return normal.y >= 0 ? 2 : 3;
            return normal.z >= 0 ? 4 : 5;
        }

        static float ProvisionalGutterUV(in ChannelUVSettings s)
        {
            // Margin as a fraction of the unit square; refined against final resolution downstream.
            int res = math.max(s.MinImageResolution, 64);
            return math.clamp(s.GutterTexelCount / res, 0f, 0.05f);
        }

        /// <summary>
        /// Shelf-packs the non-empty face islands (each sized by its 2D bbox) into [0,1]² with a
        /// uniform gutter. Returns per-face placement (offset/scale) and the uniform world→UV scale
        /// applied (so the domain mapping can recover Size3D).
        /// </summary>
        static void PackIslands(
            float2[] faceMin, float2[] faceMax, float gutter,
            out float2[] faceOffset, out float2[] faceScale, out float packScale)
        {
            faceOffset = new float2[6];
            faceScale = new float2[6];

            // Find the largest island extent so all islands share a single world→UV scale
            // (keeps texel density uniform across faces).
            float maxExtent = 1e-6f;
            var extents = new float2[6];
            for (int f = 0; f < 6; f++)
            {
                if (faceMin[f].x > faceMax[f].x) { extents[f] = float2.zero; continue; }
                extents[f] = math.max(faceMax[f] - faceMin[f], new float2(1e-6f));
                maxExtent = math.max(maxExtent, math.cmax(extents[f]));
            }

            // Shelf layout: place islands left-to-right, wrapping to a new row, in a unit square
            // partitioned into 3 columns × 2 rows (6 faces). Each cell holds one island, scaled to
            // fit the cell minus gutter, preserving aspect via the shared scale.
            const int cols = 3, rows = 2;
            float cellW = 1f / cols;
            float cellH = 1f / rows;
            float innerW = cellW - gutter;
            float innerH = cellH - gutter;

            // Shared world→UV scale: largest island must fit the smaller inner cell dimension.
            float minInner = math.min(innerW, innerH);
            packScale = minInner / maxExtent;

            for (int f = 0; f < 6; f++)
            {
                int col = f % cols;
                int row = f / cols;
                float2 cellOrigin = new float2(col * cellW + gutter * 0.5f, row * cellH + gutter * 0.5f);

                if (extents[f].x <= 1e-6f && faceMin[f].x > faceMax[f].x)
                {
                    // Empty face: collapse to the cell origin (no triangles reference it).
                    faceOffset[f] = cellOrigin;
                    faceScale[f] = float2.zero;
                    continue;
                }

                faceScale[f] = new float2(packScale);
                faceOffset[f] = cellOrigin;
            }
        }

        static SectionDomainMapping ComputeDomainMapping(in MeshData mesh, in ChannelUVSettings s, float packScale)
        {
            double area3D = 0, areaUV = 0;
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                int3 tri = mesh.Triangles[t];
                float3 p0 = mesh.Vertices[tri.x], p1 = mesh.Vertices[tri.y], p2 = mesh.Vertices[tri.z];
                area3D += 0.5 * math.length(math.cross(p1 - p0, p2 - p0));

                float2 u0 = mesh.ChannelUVs[tri.x], u1 = mesh.ChannelUVs[tri.y], u2 = mesh.ChannelUVs[tri.z];
                areaUV += 0.5 * math.abs(Cross2(u1 - u0, u2 - u0));
            }

            // SectionSize = world-space side length of a unit UV square = sqrt(Area3D / AreaUV).
            double projected = areaUV <= 1e-12 ? 0.0 : area3D / areaUV;
            double sectionSize = math.sqrt(projected);
            double idealRes = s.TexelSize3D <= 0f ? s.MinImageResolution : sectionSize / s.TexelSize3D;

            int minRes = math.max(1, s.MinImageResolution);
            idealRes = math.clamp(idealRes, minRes, s.MaxImageResolution);
            int resolution = (int)(math.ceil(idealRes / 4.0) * 4); // round up to a multiple of 4
            resolution = math.min(resolution, s.MaxImageResolution);

            return new SectionDomainMapping
            {
                ImageResolution = math.max(4, resolution),
                Size3D = (float)sectionSize,
                Area3D = (float)area3D,
                AreaUV = (float)areaUV,
            };
        }

        static float Cross2(float2 a, float2 b) => a.x * b.y - a.y * b.x;
    }
}
