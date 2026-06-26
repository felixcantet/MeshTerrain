namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// Lightweight counters for the streaming memory guarantee (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §9</c>):
    /// the streamer increments on present and decrements on release, so a test (or the demo HUD) can assert
    /// that after a load→unload cycle the live counts return to baseline. This is a correctness guard-rail
    /// to keep active as the eviction path evolves, not a profiler substitute.
    /// </summary>
    public static class StreamingDiagnostics
    {
        /// <summary>Sections currently presented (handles held).</summary>
        public static int LiveSections;

        /// <summary>Cache hits / misses since the last <see cref="Reset"/> (a miss == a cook).</summary>
        public static int CacheHits;
        public static int CacheMisses;

        /// <summary>Total cooks performed (== misses; kept separate for clarity in HUD/tests).</summary>
        public static int Cooks;

        /// <summary>Cooks that finished but were cancelled before present (drifted out of range). High =
        /// the focus/hysteresis is thrashing the load boundary; the result is cached, not wasted.</summary>
        public static int CooksCancelled;

        public static void Reset()
        {
            LiveSections = 0;
            CacheHits = 0;
            CacheMisses = 0;
            Cooks = 0;
            CooksCancelled = 0;
        }
    }
}
