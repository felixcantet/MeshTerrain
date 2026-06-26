using System;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// Blittable disk (de)serialization for a <see cref="CookedSection"/>
    /// (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §7.3</c>). <see cref="MeshData"/> is backed by
    /// <see cref="NativeArray{T}"/>, so the geometry buffers are written/read as raw bytes (no Unity
    /// serialization). A version + the key's <see cref="SectionKey.ClassHash"/> guard the format: a bump
    /// makes a stale blob read as a miss.
    /// </summary>
    public static class SectionBlob
    {
        // "MTSC" little-endian.
        const uint Magic = 0x4353544D;
        const uint Version = 2; // v2: carries the baked LOD chain (doc/08 §8)

        [Flags]
        enum Flags : uint
        {
            None = 0,
            HasNormals = 1 << 0,
            HasChannelUVs = 1 << 1,
            HasSourceUV0 = 1 << 2,
            HasBaseIDs = 1 << 3,
            HasWeights = 1 << 4,
            HasAtlas = 1 << 5,
            HasLods = 1 << 6,
            HasCollision = 1 << 7,
        }

        public static void Write(Stream stream, CookedSection cooked)
        {
            using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            w.Write(Magic);
            w.Write(Version);

            WriteKey(w, cooked.Key);
            WriteDims(w, cooked.Dims);
            WriteInt3(w, cooked.Coord);

            Flags flags = MeshFlags(cooked.Mesh, cooked.Weights != null && cooked.Weights.LayerCount > 0);
            if (cooked.HasAtlas) flags |= Flags.HasAtlas;
            if (cooked.HasBakedLods) flags |= Flags.HasLods;
            if (cooked.HasBakedCollision) flags |= Flags.HasCollision;
            w.Write((uint)flags);

            WriteMesh(w, cooked.Mesh);
            WriteWeights(w, cooked.Weights);

            if (cooked.HasAtlas)
            {
                w.Write(cooked.ChannelAtlasResolution);
                w.Write(cooked.ChannelAtlasSlices);
                WriteUInt4(w, cooked.ChannelTable.Words);
                w.Write(cooked.ChannelTable.SlotCount);
                w.Write(cooked.ChannelTexcoordMetrics.x);
                w.Write(cooked.ChannelTexcoordMetrics.y);
                w.Write(cooked.ChannelAtlasBlob.Length);
                w.Write(cooked.ChannelAtlasBlob);
            }

            if (cooked.HasBakedLods)
            {
                w.Write(cooked.Lods.Length);
                foreach (var lod in cooked.Lods)
                {
                    w.Write((uint)MeshFlags(lod.Mesh, lod.Weights != null && lod.Weights.LayerCount > 0));
                    WriteMesh(w, lod.Mesh);
                    WriteWeights(w, lod.Weights);
                }
            }

            if (cooked.HasBakedCollision)
            {
                w.Write((uint)MeshFlags(cooked.CollisionMesh, false));
                WriteMesh(w, cooked.CollisionMesh);
            }
        }

        static Flags MeshFlags(in MeshData mesh, bool hasWeights)
        {
            Flags flags = Flags.None;
            if (mesh.HasNormals) flags |= Flags.HasNormals;
            if (mesh.HasChannelUVs) flags |= Flags.HasChannelUVs;
            if (mesh.HasSourceUV0) flags |= Flags.HasSourceUV0;
            if (mesh.HasBaseIDs) flags |= Flags.HasBaseIDs;
            if (hasWeights) flags |= Flags.HasWeights;
            return flags;
        }

        static void WriteMesh(BinaryWriter w, in MeshData mesh)
        {
            w.Write(mesh.VertexCount);
            w.Write(mesh.TriangleCount);
            WriteArray(w, mesh.Vertices);
            WriteArray(w, mesh.Triangles);
            if (mesh.HasNormals) WriteArray(w, mesh.Normals);
            if (mesh.HasChannelUVs) WriteArray(w, mesh.ChannelUVs);
            if (mesh.HasSourceUV0) WriteArray(w, mesh.SourceUV0);
            if (mesh.HasBaseIDs) WriteArray(w, mesh.BaseIDLayer);
        }

        static MeshData ReadMesh(BinaryReader r, Flags flags, Allocator allocator, out int vertexCount)
        {
            vertexCount = r.ReadInt32();
            int triangleCount = r.ReadInt32();
            var mesh = MeshData.Allocate(
                vertexCount, triangleCount, allocator,
                withNormals: (flags & Flags.HasNormals) != 0,
                withChannelUVs: (flags & Flags.HasChannelUVs) != 0,
                withSourceUV0: (flags & Flags.HasSourceUV0) != 0,
                withBaseIDs: (flags & Flags.HasBaseIDs) != 0);
            ReadArray(r, mesh.Vertices);
            ReadArray(r, mesh.Triangles);
            if ((flags & Flags.HasNormals) != 0) ReadArray(r, mesh.Normals);
            if ((flags & Flags.HasChannelUVs) != 0) ReadArray(r, mesh.ChannelUVs);
            if ((flags & Flags.HasSourceUV0) != 0) ReadArray(r, mesh.SourceUV0);
            if ((flags & Flags.HasBaseIDs) != 0) ReadArray(r, mesh.BaseIDLayer);
            return mesh;
        }

        static void WriteWeights(BinaryWriter w, WeightLayerSet weights)
        {
            bool hasWeights = weights != null && weights.LayerCount > 0;
            if (!hasWeights) { w.Write(0); return; }
            var names = weights.LayerNames;
            w.Write(names.Count);
            for (int n = 0; n < names.Count; n++)
            {
                w.Write(names[n]);
                weights.TryGetLayer(names[n], out var layer);
                WriteArray(w, layer);
            }
        }

        static WeightLayerSet ReadWeights(BinaryReader r, int vertexCount, Allocator allocator)
        {
            int count = r.ReadInt32();
            if (count == 0) return null;
            var weights = new WeightLayerSet(allocator);
            for (int n = 0; n < count; n++)
            {
                string name = r.ReadString();
                var layer = weights.InitializeLayer(name, vertexCount);
                ReadArray(r, layer);
            }
            return weights;
        }

        /// <summary>
        /// Reads a blob into a fresh <see cref="CookedSection"/> (geometry allocated with
        /// <paramref name="allocator"/>; caller owns/disposes). Returns false on a magic/version mismatch
        /// (treat as a cache miss).
        /// </summary>
        public static bool TryRead(Stream stream, Allocator allocator, out CookedSection cooked)
        {
            cooked = null;
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            if (r.ReadUInt32() != Magic) return false;
            if (r.ReadUInt32() != Version) return false;

            var key = ReadKey(r);
            var dims = ReadDims(r);
            var coord = ReadInt3(r);
            var flags = (Flags)r.ReadUInt32();

            var mesh = ReadMesh(r, flags, allocator, out int vertexCount);
            WeightLayerSet weights = ReadWeights(r, vertexCount, allocator);

            cooked = new CookedSection
            {
                Coord = coord,
                Key = key,
                Mesh = mesh,
                Weights = weights,
                Dims = dims,
            };

            if ((flags & Flags.HasAtlas) != 0)
            {
                cooked.ChannelAtlasResolution = r.ReadInt32();
                cooked.ChannelAtlasSlices = r.ReadInt32();
                uint4 words = ReadUInt4(r);
                int slotCount = r.ReadInt32();
                cooked.ChannelTable = ChannelTable.FromWords(words, slotCount);
                float mx = r.ReadSingle();
                float my = r.ReadSingle();
                cooked.ChannelTexcoordMetrics = new float2(mx, my);
                int blobLen = r.ReadInt32();
                cooked.ChannelAtlasBlob = r.ReadBytes(blobLen);
            }

            if ((flags & Flags.HasLods) != 0)
            {
                int lodCount = r.ReadInt32();
                var lods = new LodMesh[lodCount];
                for (int i = 0; i < lodCount; i++)
                {
                    var lodFlags = (Flags)r.ReadUInt32();
                    var lodMesh = ReadMesh(r, lodFlags, allocator, out int lodVerts);
                    var lodWeights = ReadWeights(r, lodVerts, allocator);
                    lods[i] = new LodMesh { Mesh = lodMesh, Weights = lodWeights };
                }
                cooked.Lods = lods;
            }

            if ((flags & Flags.HasCollision) != 0)
            {
                var colFlags = (Flags)r.ReadUInt32();
                cooked.CollisionMesh = ReadMesh(r, colFlags, allocator, out _);
            }

            return true;
        }

        // ---- raw NativeArray <-> bytes ----

        static void WriteArray<T>(BinaryWriter w, NativeArray<T> array) where T : struct
        {
            int elemSize = UnsafeUtility.SizeOf<T>();
            int byteCount = array.Length * elemSize;
            var bytes = array.Reinterpret<byte>(elemSize);
            // BinaryWriter has no Span overload on this runtime; copy out to a managed buffer.
            var managed = bytes.ToArray();
            w.Write(byteCount);
            w.Write(managed, 0, managed.Length);
        }

        static void ReadArray<T>(BinaryReader r, NativeArray<T> dst) where T : struct
        {
            int elemSize = UnsafeUtility.SizeOf<T>();
            int byteCount = r.ReadInt32();
            int expected = dst.Length * elemSize;
            if (byteCount != expected)
                throw new InvalidDataException($"SectionBlob: array byte mismatch (got {byteCount}, expected {expected}).");
            byte[] managed = r.ReadBytes(byteCount);
            var bytes = dst.Reinterpret<byte>(elemSize);
            bytes.CopyFrom(managed);
        }

        // ---- scalar helpers ----

        static void WriteInt3(BinaryWriter w, int3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static int3 ReadInt3(BinaryReader r) => new int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

        static void WriteFloat3(BinaryWriter w, float3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static float3 ReadFloat3(BinaryReader r) => new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        static void WriteUInt4(BinaryWriter w, uint4 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }
        static uint4 ReadUInt4(BinaryReader r) => new uint4(r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());

        static void WriteHash(BinaryWriter w, Hash128 h) => w.Write(h.ToString());
        static Hash128 ReadHash(BinaryReader r) => Hash128.Parse(r.ReadString());

        static void WriteKey(BinaryWriter w, in SectionKey key)
        {
            WriteInt3(w, key.Coord);
            WriteHash(w, key.ModifiersHash);
            WriteHash(w, key.ModifierSetHash);
            WriteHash(w, key.VariantHash);
            WriteHash(w, key.ClassHash);
        }

        static SectionKey ReadKey(BinaryReader r)
        {
            int3 coord = ReadInt3(r);
            Hash128 mh = ReadHash(r);
            Hash128 ms = ReadHash(r);
            Hash128 vh = ReadHash(r);
            Hash128 ch = ReadHash(r);
            return new SectionKey(coord, mh, ms, vh, ch);
        }

        static void WriteDims(BinaryWriter w, in GridDimensions dims)
        {
            WriteFloat3(w, dims.SnappedMin);
            WriteInt3(w, dims.OriginCoord);
            WriteInt3(w, dims.CellNumber);
            WriteFloat3(w, dims.CellExtent);
        }

        static GridDimensions ReadDims(BinaryReader r) => new GridDimensions
        {
            SnappedMin = ReadFloat3(r),
            OriginCoord = ReadInt3(r),
            CellNumber = ReadInt3(r),
            CellExtent = ReadFloat3(r),
        };
    }
}
