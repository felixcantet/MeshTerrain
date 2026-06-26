using System;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Packed slot→texture-slice table for a section's channels. Unity port of UE
    /// <c>FChannelPacking</c> (<c>doc/source/.../MeshPartitionChannel.h</c>).
    ///
    /// Up to 24 channels are packed into 4 × 32-bit words, 6 slots per word, 5 bits per slot.
    /// Slot value <see cref="SlotInvalid"/> (31) means "this channel is absent from this section".
    /// The material reads the four words (as floats, bit-reinterpreted) plus a texcoord metric via
    /// a <see cref="MaterialPropertyBlock"/> — the Unity equivalent of UE's Custom Primitive Data.
    /// </summary>
    public readonly struct ChannelTable
    {
        // Layout constants, ported 1:1 from UE FChannelPacking.
        public const int SlotNumBits = 5;
        public const int SlotInvalid = (1 << SlotNumBits) - 1;      // 31
        public const int WordNumSlots = 32 / SlotNumBits;            // 6
        public const int TableNumWords = 4;
        public const int MaxNumberPackedChannels = TableNumWords * WordNumSlots; // 24

        /// <summary>The four packed words (each holds 6 × 5-bit slots).</summary>
        public readonly uint4 Words;

        /// <summary>Number of populated channel slots (global channel count).</summary>
        public readonly int SlotCount;

        ChannelTable(uint4 words, int slotCount)
        {
            Words = words;
            SlotCount = slotCount;
        }

        /// <summary>
        /// Builds a table from per-channel texture slice indices. <paramref name="sliceForChannel"/>[c]
        /// is the texture slice the c-th global channel was rasterized into, or a negative value (or
        /// <see cref="SlotInvalid"/>) when the channel is absent from this section.
        /// </summary>
        public static ChannelTable Build(int[] sliceForChannel)
        {
            if (sliceForChannel == null)
                return new ChannelTable(new uint4(0xFFFFFFFFu), 0);
            if (sliceForChannel.Length > MaxNumberPackedChannels)
                throw new InvalidOperationException(
                    $"Channel packing supports up to {MaxNumberPackedChannels} channels (got {sliceForChannel.Length}).");

            // All-bits-set means every slot defaults to SlotInvalid (31).
            var words = new uint4(0xFFFFFFFFu);
            const uint mask = (1u << SlotNumBits) - 1u;

            for (int c = 0; c < sliceForChannel.Length; c++)
            {
                int wordId = c / WordNumSlots;
                int slotId = c % WordNumSlots;
                int slice = sliceForChannel[c];
                uint value = slice < 0 ? (uint)SlotInvalid : (uint)slice & mask;

                int shift = slotId * SlotNumBits;
                uint word = words[wordId];
                word &= ~(mask << shift); // clear the slot
                word |= value << shift;   // write the slice index
                words[wordId] = word;
            }

            return new ChannelTable(words, sliceForChannel.Length);
        }

        /// <summary>Reads back the texture slice for global channel <paramref name="channel"/>.</summary>
        public int GetSlice(int channel)
        {
            if (channel < 0 || channel >= MaxNumberPackedChannels)
                return SlotInvalid;
            int wordId = channel / WordNumSlots;
            int slotId = channel % WordNumSlots;
            const uint mask = (1u << SlotNumBits) - 1u;
            return (int)((Words[wordId] >> (slotId * SlotNumBits)) & mask);
        }
    }

    /// <summary>
    /// Helpers to push a section's channel atlas + packing table onto a renderer. Bridges the
    /// <see cref="ChannelTable"/> and channel texture into shader-readable per-renderer state via a
    /// <see cref="MaterialPropertyBlock"/> (the Unity analogue of UE Custom Primitive Data).
    /// </summary>
    public static class ChannelPacking
    {
        public static readonly int ChannelTexId = Shader.PropertyToID("_ChannelTex");
        public static readonly int ChannelSlicesId = Shader.PropertyToID("_ChannelSlices");
        public static readonly int ChannelTexcoordId = Shader.PropertyToID("_ChannelTexcoord");
        public static readonly int ChannelCountId = Shader.PropertyToID("_ChannelCount");

        /// <summary>
        /// Writes the channel texture, per-channel texture-slice indices, the texcoord metric
        /// (section world size encoded in UV), and the channel count into a
        /// <see cref="MaterialPropertyBlock"/> applied to <paramref name="renderer"/>.
        ///
        /// The slices are sent as a plain float array (one entry per channel) rather than the
        /// bit-packed <see cref="ChannelTable.Words"/>: reinterpreting a packed <c>uint</c> as a
        /// float can yield NaN/Inf bit patterns that Unity's material/GPU float pipeline does not
        /// preserve. The <see cref="ChannelTable"/> bit layout is still the canonical storage form
        /// (UE Custom-Primitive-Data parity); this is only the per-renderer transport.
        /// </summary>
        public static void ApplyToRenderer(
            Renderer renderer,
            Texture channelTexture,
            in ChannelTable table,
            float2 texcoordMetrics)
        {
            if (renderer == null) return;

            var mpb = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(mpb);

            if (channelTexture != null)
                mpb.SetTexture(ChannelTexId, channelTexture);

            // One slice index per global channel (SlotInvalid where absent). Fixed length keeps the
            // shader array binding stable across sections.
            var slices = new float[ChannelTable.MaxNumberPackedChannels];
            for (int c = 0; c < slices.Length; c++)
                slices[c] = table.GetSlice(c);
            mpb.SetFloatArray(ChannelSlicesId, slices);

            mpb.SetVector(ChannelTexcoordId, new Vector4(texcoordMetrics.x, texcoordMetrics.y, 0f, 0f));
            mpb.SetFloat(ChannelCountId, table.SlotCount);

            renderer.SetPropertyBlock(mpb);
        }
    }
}
