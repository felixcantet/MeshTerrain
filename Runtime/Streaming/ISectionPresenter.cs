using UnityEngine;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// Opaque handle returned by an <see cref="ISectionPresenter"/>; the streamer keeps it to release the
    /// section later. Backends wrap whatever they created (a <c>CompiledSection</c>, an ECS entity set, …).
    /// </summary>
    public interface ISectionHandle
    {
        Unity.Mathematics.int3 Coord { get; }
    }

    /// <summary>
    /// Turns a backend-agnostic <see cref="CookedSection"/> into something visible/collidable, and releases
    /// it. The streaming <b>core</b> (controller/queue/cache/cooker) talks only to this interface and
    /// <see cref="CookedSection"/>, never to <c>GameObject</c>/<c>MeshRenderer</c>/Entities — so an ECS
    /// presenter can be added later without touching the core (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §4</c>).
    /// </summary>
    public interface ISectionPresenter
    {
        /// <summary>
        /// Presents the cooked section under <paramref name="root"/>. Synchronous and light (instantiate,
        /// not cook): upload the mesh, add components, bind the atlas. Returns a handle for
        /// <see cref="Release"/>.
        /// </summary>
        ISectionHandle Present(CookedSection cooked, Transform root);

        /// <summary>Releases <b>all</b> resources of a presented section (GO, meshes, textures). See
        /// <c>§9</c> memory guarantees — every owned UnityEngine.Object must be destroyed here.</summary>
        void Release(ISectionHandle handle);
    }
}
