Shader "Hidden/Mesh Terrain/Channel Raster"
{
    // UV-domain rasterization pass (Phase 4b GPU backend). Port of UE
    // MeshPartitionMakeSectionChannels.usf (DrawUVDomainVS/PS): the vertex's channel UV becomes its
    // NDC position, so the section's triangles are rasterized into the atlas texture. The fragment
    // writes the per-vertex weight of the channel currently being drawn (_Slice) to MRT0 and a
    // coverage mask to MRT1. One draw per channel slice, target = one array slice of the RT.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off ZTest Always ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            // Per-vertex channel UVs (xy) and the dense weight buffer laid out [vertex*channelCount + channel].
            StructuredBuffer<float2> _ChannelUVs;
            StructuredBuffer<float>  _ChannelWeights;
            int _ChannelCount;
            int _Slice;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float  weight     : TEXCOORD0;
            };

            Varyings vert(uint vid : SV_VertexID)
            {
                Varyings o;
                float2 uv = _ChannelUVs[vid];

                // UV [0,1] -> NDC [-1,1], flip Y to match texture origin.
                float2 ndc = uv * 2.0 - 1.0;
                ndc.y = -ndc.y;
                o.positionCS = float4(ndc, 0.0, 1.0);

                o.weight = _ChannelWeights[vid * _ChannelCount + _Slice];
                return o;
            }

            void frag(Varyings i, out float outWeight : SV_Target0, out float outMask : SV_Target1)
            {
                outWeight = i.weight;
                outMask = 1.0;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
