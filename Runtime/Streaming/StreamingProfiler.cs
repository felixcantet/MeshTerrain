using System.Collections.Generic;
using System.Text;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// In-depth timing instrumentation for the streamer + section compile, so the bottleneck of a slow
    /// stream-in can be located without an external profiler (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §8</c>).
    ///
    /// <para>Two layers of data:</para>
    /// <list type="bullet">
    ///   <item><b>Phase accumulators</b> — total ms + call count per named phase (cook, present, and the
    ///   present sub-phases: channels, skirt, LOD simplify, mesh upload, collider bake). Aggregated across
    ///   the whole run; read the worst average to find the hot phase.</item>
    ///   <item><b>Per-section samples</b> — the cook-thread wall time and the main-thread present time of the
    ///   N most recent sections, so you can see whether one section is pathologically expensive.</item>
    /// </list>
    /// Enable with <see cref="Enabled"/> (the demo turns it on). Zero overhead when disabled.
    /// </summary>
    public static class StreamingProfiler
    {
        public static bool Enabled;

        public struct Phase
        {
            public double TotalMs;
            public int Count;
            public double MaxMs;
            public double AvgMs => Count > 0 ? TotalMs / Count : 0.0;
        }

        public struct SectionSample
        {
            public int CoordX, CoordY, CoordZ;
            public int Vertices, Triangles, Lods;
            public bool WasCacheHit;
            public double CookThreadMs;   // worker wall time (cook task)
            public double PresentMs;      // main-thread present time
        }

        static readonly Dictionary<string, Phase> _phases = new();
        static readonly Queue<SectionSample> _samples = new();
        const int MaxSamples = 32;

        /// <summary>Adds <paramref name="ms"/> to the named phase accumulator (main-thread phases).</summary>
        public static void AddPhase(string name, double ms)
        {
            if (!Enabled) return;
            _phases.TryGetValue(name, out var p);
            p.TotalMs += ms;
            p.Count++;
            if (ms > p.MaxMs) p.MaxMs = ms;
            _phases[name] = p;
        }

        public static void AddSample(in SectionSample s)
        {
            if (!Enabled) return;
            _samples.Enqueue(s);
            while (_samples.Count > MaxSamples) _samples.Dequeue();
        }

        public static void Reset()
        {
            _phases.Clear();
            _samples.Clear();
        }

        public static IReadOnlyDictionary<string, Phase> Phases => _phases;
        public static IEnumerable<SectionSample> Samples => _samples;

        /// <summary>Human-readable dump (phase table sorted by total time + the slowest recent sections).</summary>
        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== StreamingProfiler ===");
            sb.AppendLine($"Live: {StreamingDiagnostics.LiveSections}  Cooks: {StreamingDiagnostics.Cooks}  " +
                          $"Hits: {StreamingDiagnostics.CacheHits}  Misses: {StreamingDiagnostics.CacheMisses}");

            sb.AppendLine("-- Phases (name: total ms / calls = avg ms, max ms) --");
            var ordered = new List<KeyValuePair<string, Phase>>(_phases);
            ordered.Sort((a, b) => b.Value.TotalMs.CompareTo(a.Value.TotalMs));
            foreach (var kvp in ordered)
                sb.AppendLine($"  {kvp.Key,-18}: {kvp.Value.TotalMs,8:F1} / {kvp.Value.Count,-4} = {kvp.Value.AvgMs,6:F2}  (max {kvp.Value.MaxMs,6:F2})");

            sb.AppendLine("-- Slowest recent sections (present ms) --");
            var samples = new List<SectionSample>(_samples);
            samples.Sort((a, b) => b.PresentMs.CompareTo(a.PresentMs));
            int n = 0;
            foreach (var s in samples)
            {
                if (n++ >= 8) break;
                sb.AppendLine($"  ({s.CoordX},{s.CoordY},{s.CoordZ}) v={s.Vertices} t={s.Triangles} lods={s.Lods} " +
                              $"{(s.WasCacheHit ? "HIT " : "cook")} cookThread={s.CookThreadMs:F1}ms present={s.PresentMs:F1}ms");
            }
            return sb.ToString();
        }
    }
}
