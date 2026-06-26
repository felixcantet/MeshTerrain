using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Extension helpers for folding common scalar/math types into a <see cref="Hash128"/>. The built-in
    /// <see cref="Hash128.Append(int)"/>/<see cref="Hash128.Append(float)"/>/<see cref="Hash128.Append(string)"/>
    /// overloads don't cover <c>double</c> or the <c>Unity.Mathematics</c> vector types, so the streaming
    /// param-hashing (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §7</c>) routes through these to stay
    /// deterministic and allocation-free.
    /// </summary>
    public static class HashUtil
    {
        public static void AppendValue(this ref Hash128 h, double v)
        {
            // Hash the exact bit pattern so equal doubles hash identically across runs.
            long bits = System.BitConverter.DoubleToInt64Bits(v);
            h.Append((int)(bits & 0xFFFFFFFF));
            h.Append((int)(bits >> 32));
        }

        public static void AppendValue(this ref Hash128 h, int2 v) { h.Append(v.x); h.Append(v.y); }
        public static void AppendValue(this ref Hash128 h, int3 v) { h.Append(v.x); h.Append(v.y); h.Append(v.z); }

        public static void AppendValue(this ref Hash128 h, float2 v) { h.Append(v.x); h.Append(v.y); }
        public static void AppendValue(this ref Hash128 h, float3 v) { h.Append(v.x); h.Append(v.y); h.Append(v.z); }
        public static void AppendValue(this ref Hash128 h, float4 v) { h.Append(v.x); h.Append(v.y); h.Append(v.z); h.Append(v.w); }

        public static void AppendValue(this ref Hash128 h, double2 v) { h.AppendValue(v.x); h.AppendValue(v.y); }

        public static void AppendValue(this ref Hash128 h, float4x4 m)
        {
            h.AppendValue(m.c0); h.AppendValue(m.c1); h.AppendValue(m.c2); h.AppendValue(m.c3);
        }
    }
}
