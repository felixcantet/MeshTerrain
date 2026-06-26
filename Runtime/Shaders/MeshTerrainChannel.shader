Shader "Mesh Terrain/Channel Blend (URP)"
{
    // Per-section terrain shader (Phase 4). Samples the per-section channel atlas
    // (a Texture2DArray, one slice per weight layer) in the channel-UV domain (UV0),
    // unpacks the FChannelPacking table to resolve channel -> texture slice, and blends
    // a per-channel debug/base color weighted by the sampled weights. The channel texture,
    // packing table, texcoord metric and channel count are pushed per renderer via a
    // MaterialPropertyBlock (Unity analogue of UE Custom Primitive Data).
    Properties
    {
        _BaseColor       ("Base Color", Color) = (0.5, 0.5, 0.5, 1)
        _ChannelTex      ("Channel Atlas (Array)", 2DArray) = "" {}
        _ChannelTable    ("Channel Table (packed)", Vector) = (0,0,0,0)
        _ChannelTexcoord ("Channel Texcoord Metric", Vector) = (1,1,0,0)
        _ChannelCount    ("Channel Count", Float) = 0

        // Up to 8 debug channel colors are enough to visualize painting; extend as needed.
        _ChannelColor0 ("Channel 0 Color", Color) = (0.30, 0.70, 0.25, 1) // grass-ish
        _ChannelColor1 ("Channel 1 Color", Color) = (0.55, 0.52, 0.48, 1) // rock-ish
        _ChannelColor2 ("Channel 2 Color", Color) = (0.95, 0.95, 0.98, 1) // snow-ish
        _ChannelColor3 ("Channel 3 Color", Color) = (0.65, 0.50, 0.30, 1) // dirt-ish
        _ChannelColor4 ("Channel 4 Color", Color) = (0.20, 0.40, 0.70, 1)
        _ChannelColor5 ("Channel 5 Color", Color) = (0.80, 0.30, 0.30, 1)
        _ChannelColor6 ("Channel 6 Color", Color) = (0.80, 0.75, 0.20, 1)
        _ChannelColor7 ("Channel 7 Color", Color) = (0.50, 0.20, 0.60, 1)

        _Smoothness ("Smoothness", Range(0,1)) = 0.1
        _Metallic   ("Metallic", Range(0,1)) = 0.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 200

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D_ARRAY(_ChannelTex);
            SAMPLER(sampler_ChannelTex);

            // Per-section data is supplied via MaterialPropertyBlock. Properties overridden by an
            // MPB must NOT live in the UnityPerMaterial CBUFFER, otherwise the SRP Batcher uses the
            // material's constant buffer and ignores the per-renderer override. Declaring them as
            // plain uniforms here makes this shader SRP-Batcher-incompatible (intended) so each
            // section's slices/texcoord/count are read per draw.
            //
            // _ChannelSlices[c] = texture slice for global channel c (>=31 means absent). Sent as a
            // float array (not bit-packed) so values survive the material/GPU float pipeline.
            float  _ChannelSlices[24];
            float4 _ChannelTexcoord;
            float  _ChannelCount;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _ChannelColor0; float4 _ChannelColor1; float4 _ChannelColor2; float4 _ChannelColor3;
                float4 _ChannelColor4; float4 _ChannelColor5; float4 _ChannelColor6; float4 _ChannelColor7;
                float  _Smoothness;
                float  _Metallic;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 channelUV  : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float2 channelUV   : TEXCOORD2;
            };

            // Texture slice for a global channel (>=31 = SlotInvalid = channel absent in section).
            int ChannelSlice(int channel)
            {
                return (int)round(_ChannelSlices[channel]);
            }

            float4 ChannelColor(int channel)
            {
                if (channel == 0) return _ChannelColor0;
                if (channel == 1) return _ChannelColor1;
                if (channel == 2) return _ChannelColor2;
                if (channel == 3) return _ChannelColor3;
                if (channel == 4) return _ChannelColor4;
                if (channel == 5) return _ChannelColor5;
                if (channel == 6) return _ChannelColor6;
                return _ChannelColor7;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs nrm = GetVertexNormalInputs(IN.normalOS);
                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.normalWS = nrm.normalWS;
                OUT.channelUV = IN.channelUV;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                int count = (int)round(_ChannelCount);

                // Start from the base color so unpainted terrain is always visible. Channel weights
                // blend their colors on top of it; the base shows through where weights are low.
                float3 albedo = _BaseColor.rgb;
                float totalWeight = 0;
                float3 blended = 0;

                [loop]
                for (int c = 0; c < count && c < 24; c++)
                {
                    int slice = ChannelSlice(c);
                    if (slice < 0 || slice >= 31) continue; // SlotInvalid / garbage -> channel absent
                    float w = SAMPLE_TEXTURE2D_ARRAY(_ChannelTex, sampler_ChannelTex, IN.channelUV, slice).r;
                    w = saturate(w);
                    blended += ChannelColor(c).rgb * w;
                    totalWeight += w;
                }

                // Lerp the base toward the (weight-normalized) channel blend by how much weight
                // covers this texel. Painted areas show the channel colors; the rest stays base.
                totalWeight = saturate(totalWeight);
                if (totalWeight > 1e-4)
                {
                    float3 channelColor = blended / max(totalWeight, 1e-4);
                    albedo = lerp(_BaseColor.rgb, channelColor, totalWeight);
                }

                // Build lighting input with baked GI so output is never pitch black under GI-only.
                InputData lightingInput = (InputData)0;
                lightingInput.positionWS = IN.positionWS;
                lightingInput.normalWS = normalize(IN.normalWS);
                lightingInput.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                lightingInput.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                // Baked GI with a small ambient floor so the terrain is legible even when the scene
                // has no directional light and a black ambient/skybox (a common empty-scene setup).
                lightingInput.bakedGI = max(SampleSH(lightingInput.normalWS), 0.15);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.metallic = _Metallic;
                surface.smoothness = _Smoothness;
                surface.occlusion = 1.0;
                surface.alpha = 1.0;

                return UniversalFragmentPBR(lightingInput, surface);
            }
            ENDHLSL
        }

        // Shadow caster so the terrain casts/receives shadows correctly.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadowFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct SAttributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct SVaryings { float4 positionCS : SV_POSITION; };

            SVaryings shadowVert(SAttributes IN)
            {
                SVaryings OUT;
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                OUT.positionCS = positionCS;
                return OUT;
            }

            half4 shadowFrag(SVaryings IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
