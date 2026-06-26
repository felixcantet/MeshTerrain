using Unity.Jobs;
using Unity.Mathematics;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>Lifecycle state of a streamed section (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §5.4</c>).</summary>
    public enum SectionState
    {
        Queued,      // selected for load, not started
        Cooking,     // heavy build in flight (async path)
        Presenting,  // cooked, instantiate in progress
        Ready,       // presented (or empty cell)
    }

    /// <summary>
    /// One resident (or in-flight) section in the streamer's resident set. <see cref="Handle"/> is non-null
    /// once presented; an empty cell is recorded <see cref="SectionState.Ready"/> with a null handle so it
    /// isn't re-cooked on every refocus. <see cref="Generation"/> lets the streamer cancel a stale load that
    /// drifted out of range while cooking (async path).
    /// </summary>
    public sealed class ResidentSection
    {
        public int3 Coord;
        public SectionState State;
        public ISectionHandle Handle;
        public JobHandle? PendingJobs;
        public int Generation;
    }
}
