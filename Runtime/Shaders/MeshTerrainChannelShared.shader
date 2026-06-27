Shader "Mesh Terrain/Channel Blend Shared Atlas (URP, BRG)"
{
    // Shared-atlas instanced channel shader (the scaling path). ALL sections share ONE _ChannelTex
    // Texture2DArray and ONE material; each instance carries a per-instance _ChannelParams = (sliceBase,
    // channelCount, texcoordMetric, _) read via DOTS instancing. Section channel c is at array slice
    // (sliceBase + c). One material -> all sections batch into instanced draws (vs one SetPass per section).
    //
    // Proven by BrgPerInstanceSpike that per-instance DOTS float4 works under BRG.
    Properties
    {
        _BaseColor    ("Base Color", Color) = (0.5, 0.5, 0.5, 1)
        _ChannelTex   ("Shared Channel Atlas (Array)", 2DArray) = "" {}
        _ChannelParams("Channel Params (sliceBase, count, metric, _)", Vector) = (0, 0, 1, 0)

        _ChannelColor0 ("Channel 0 Color", Color) = (0.30, 0.70, 0.25, 1)
        _ChannelColor1 ("Channel 1 Color", Color) = (0.55, 0.52, 0.48, 1)
        _ChannelColor2 ("Channel 2 Color", Color) = (0.95, 0.95, 0.98, 1)
        _ChannelColor3 ("Channel 3 Color", Color) = (0.65, 0.50, 0.30, 1)
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
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _TERRAIN_LAYERS   // on = blend material layers; off = flat debug colors

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D_ARRAY(_ChannelTex);
            SAMPLER(sampler_ChannelTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _ChannelColor0; float4 _ChannelColor1; float4 _ChannelColor2; float4 _ChannelColor3;
                float4 _ChannelColor4; float4 _ChannelColor5; float4 _ChannelColor6; float4 _ChannelColor7;
                float  _Smoothness;
                float  _Metallic;
                float4 _ChannelParams;   // (sliceBase, channelCount, texcoordMetric, _) — per-instance override
            CBUFFER_END

            // Terrain layer params are GLOBAL (shared by all sections), set via Shader.SetGlobal*. Keeping them
            // out of UnityPerMaterial avoids the BRG "var not declared in shader property section" error that
            // cbuffer arrays trigger (arrays can't be BRG material properties). Globals are not per-material.
            float4 _LayerParams[24]; // per-channel (tiling, normalStrength, heightContrast, _)
            float  _LayerCount;

            // Per-instance _ChannelParams from the BRG instance buffer.
            #ifdef UNITY_DOTS_INSTANCING_ENABLED
                UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                    UNITY_DOTS_INSTANCED_PROP(float4, _ChannelParams)
                UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
                #define _ChannelParams UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _ChannelParams)
            #endif

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

            // Per-fragment slice context the layer-blend include reads via SAMPLE_CHANNEL_WEIGHT.
            static int   _slBase;
            static float2 _slChannelUV;
            #define SAMPLE_CHANNEL_WEIGHT(c) \
                saturate(SAMPLE_TEXTURE2D_ARRAY(_ChannelTex, sampler_ChannelTex, _slChannelUV, _slBase + (c)).r)
            #include "MeshTerrainChannelBlend.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 channelUV  : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 channelUV  : TEXCOORD2;
                nointerpolation float2 sliceInfo : TEXCOORD3; // (sliceBase, channelCount)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs nrm = GetVertexNormalInputs(IN.normalOS);
                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.normalWS = nrm.normalWS;
                OUT.channelUV = IN.channelUV;
                float4 cp = _ChannelParams;            // read per-instance in VS
                OUT.sliceInfo = float2(cp.x, cp.y);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                int sliceBase = (int)round(IN.sliceInfo.x);
                int count = (int)round(IN.sliceInfo.y);

                _slBase = sliceBase;
                _slChannelUV = IN.channelUV;

                float3 worldNormal = normalize(IN.normalWS);
                float3 albedo = _BaseColor.rgb;
                float roughness = 1.0 - _Smoothness;
                float metallic = _Metallic;
                float ao = 1.0;
                float3 normalWS = worldNormal;

            #ifdef _TERRAIN_LAYERS
                // Blend per-channel material layers (albedo/normal/mask) by height-biased channel weight.
                TerrainSurface ts = BlendTerrainLayers(IN.positionWS.xz, _BaseColor.rgb);
                albedo = ts.albedo;
                roughness = ts.roughness;
                metallic = ts.metallic;
                ao = ts.ao;

                // Tangent basis for the terrain (mostly up-facing): tangent along world +X projected onto the
                // surface, bitangent = N x T. Good enough for terrain; triplanar is a later upgrade.
                float3 t = normalize(float3(1, 0, 0) - worldNormal * worldNormal.x);
                float3 b = cross(worldNormal, t);
                normalWS = normalize(t * ts.normalTS.x + b * ts.normalTS.y + worldNormal * ts.normalTS.z);
            #else
                // Flat debug-color blend (visualizer).
                float totalWeight = 0;
                float3 blended = 0;
                [loop]
                for (int c = 0; c < count && c < 24; c++)
                {
                    float w = saturate(SAMPLE_TEXTURE2D_ARRAY(_ChannelTex, sampler_ChannelTex, IN.channelUV, sliceBase + c).r);
                    blended += ChannelColor(c).rgb * w;
                    totalWeight += w;
                }
                totalWeight = saturate(totalWeight);
                if (totalWeight > 1e-4)
                    albedo = lerp(_BaseColor.rgb, blended / max(totalWeight, 1e-4), totalWeight);
            #endif

                InputData lightingInput = (InputData)0;
                lightingInput.positionWS = IN.positionWS;
                lightingInput.normalWS = normalWS;
                lightingInput.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                lightingInput.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                lightingInput.bakedGI = max(SampleSH(normalWS), 0.15);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.metallic = metallic;
                surface.smoothness = 1.0 - roughness;
                surface.occlusion = ao;
                surface.alpha = 1.0;

                return UniversalFragmentPBR(lightingInput, surface);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadowFrag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct SAttributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct SVaryings { float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };

            SVaryings shadowVert(SAttributes IN)
            {
                SVaryings OUT = (SVaryings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
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
