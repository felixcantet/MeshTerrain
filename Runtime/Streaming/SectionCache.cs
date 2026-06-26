using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// Two-tier section store keyed by <see cref="SectionKey"/> (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §7</c>):
    /// a bounded RAM LRU over recently-used <see cref="CookedSection"/>s, backed by a disk tier of blittable
    /// blobs. A hit avoids the (heavy) cook entirely. Disk entries persist across runs and across evictions;
    /// shipping a prepopulated disk folder makes the player do zero cooks (UE-parity frozen world).
    /// </summary>
    public interface ISectionCache
    {
        /// <summary>RAM then disk. On a hit returns a freshly-owned <see cref="CookedSection"/> (caller must
        /// not dispose — the cache owns RAM entries; see <see cref="OnEvicted"/>). False = miss (cook needed).</summary>
        bool TryGet(in SectionKey key, out CookedSection cooked);

        /// <summary>Inserts a freshly-cooked section into RAM (LRU) and writes its blob to disk. The cache
        /// takes ownership of <paramref name="cooked"/>'s native memory.</summary>
        void Put(in SectionKey key, CookedSection cooked);

        /// <summary>Drops the RAM entry for <paramref name="coord"/> (disposing its native memory); the disk
        /// blob is kept. Called by the streamer on unload (<c>§9</c>).</summary>
        void OnEvicted(int3 coord);

        /// <summary>Disposes all RAM entries (does not touch disk).</summary>
        void Clear();

        /// <summary>Deletes the disk tier (forces a rebuild). RAM is cleared too.</summary>
        void Purge();
    }

    /// <summary>
    /// Default <see cref="ISectionCache"/>: a capacity-bounded RAM LRU + a file-per-section disk tier under
    /// <c>persistentDataPath/MeshTerrain/&lt;id&gt;/</c> at runtime (or a supplied directory, e.g.
    /// <c>Library/MeshTerrain</c> in the editor). RAM eviction disposes native memory; disk blobs persist.
    /// </summary>
    public sealed class SectionCache : ISectionCache, IDisposable
    {
        readonly string _diskDir;
        readonly int _ramCapacity;
        readonly Allocator _allocator;

        // coord -> entry. The LRU order is tracked by _lru (most-recent at the end).
        readonly Dictionary<int3, Entry> _ram = new();
        readonly LinkedList<int3> _lru = new();

        sealed class Entry
        {
            public SectionKey Key;
            public CookedSection Cooked;
            public LinkedListNode<int3> LruNode;
        }

        /// <summary>Cook counter for diagnostics/tests: incremented by callers (the cooker) so a test can
        /// prove a 2nd access did not cook. Lives here so the streamer + tests share one counter.</summary>
        public int CookCount;

        public SectionCache(string megaMeshId, int ramCapacity = 64, string overrideDir = null, Allocator allocator = Allocator.Persistent)
        {
            _ramCapacity = math.max(1, ramCapacity);
            _allocator = allocator;
            _diskDir = overrideDir ?? Path.Combine(Application.persistentDataPath, "MeshTerrain", string.IsNullOrEmpty(megaMeshId) ? "default" : megaMeshId);
            Directory.CreateDirectory(_diskDir);
        }

        string PathFor(in SectionKey key) => Path.Combine(_diskDir, key.FileStem + ".mtsc");

        public bool TryGet(in SectionKey key, out CookedSection cooked)
        {
            // RAM tier — only a hit if the full key matches (params/variant/class all unchanged).
            if (_ram.TryGetValue(key.Coord, out var entry) && entry.Key.Equals(key))
            {
                Touch(entry);
                cooked = entry.Cooked;
                return true;
            }

            // Disk tier.
            string path = PathFor(key);
            if (File.Exists(path))
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                    if (SectionBlob.TryRead(fs, _allocator, out var fromDisk))
                    {
                        // Promote into RAM (takes ownership).
                        InsertRam(key, fromDisk);
                        cooked = fromDisk;
                        return true;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"SectionCache: failed to read blob '{path}' ({e.Message}); treating as miss.");
                }
            }

            cooked = null;
            return false;
        }

        public void Put(in SectionKey key, CookedSection cooked)
        {
            // Disk first (durable), then RAM.
            try
            {
                string path = PathFor(key);
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
                SectionBlob.Write(fs, cooked);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"SectionCache: failed to write blob for {key.Coord} ({e.Message}).");
            }

            InsertRam(key, cooked);
        }

        public void OnEvicted(int3 coord)
        {
            if (_ram.TryGetValue(coord, out var entry))
            {
                _ram.Remove(coord);
                _lru.Remove(entry.LruNode);
                entry.Cooked.Dispose();
            }
        }

        public void Clear()
        {
            foreach (var entry in _ram.Values)
                entry.Cooked.Dispose();
            _ram.Clear();
            _lru.Clear();
        }

        public void Purge()
        {
            Clear();
            try
            {
                if (Directory.Exists(_diskDir))
                    foreach (var f in Directory.GetFiles(_diskDir, "*.mtsc"))
                        File.Delete(f);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"SectionCache: purge failed ({e.Message}).");
            }
        }

        public void Dispose() => Clear();

        // ---- RAM/LRU plumbing ----

        void InsertRam(in SectionKey key, CookedSection cooked)
        {
            // Replace any stale entry at this coord (different key — e.g. after an edit).
            if (_ram.TryGetValue(key.Coord, out var existing))
            {
                _lru.Remove(existing.LruNode);
                _ram.Remove(key.Coord);
                if (!ReferenceEquals(existing.Cooked, cooked))
                    existing.Cooked.Dispose();
            }

            var node = _lru.AddLast(key.Coord);
            _ram[key.Coord] = new Entry { Key = key, Cooked = cooked, LruNode = node };

            EvictToCapacity();
        }

        void Touch(Entry entry)
        {
            _lru.Remove(entry.LruNode);
            entry.LruNode = _lru.AddLast(entry.Key.Coord);
        }

        void EvictToCapacity()
        {
            while (_ram.Count > _ramCapacity && _lru.First != null)
            {
                int3 oldest = _lru.First.Value;
                OnEvicted(oldest); // disposes RAM; disk blob kept
            }
        }
    }
}
