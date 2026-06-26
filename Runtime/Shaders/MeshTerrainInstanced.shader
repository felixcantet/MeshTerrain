Shader "Mesh Terrain/Instanced Flat (URP, BRG)"
{
    // Flat, DOTS-instancing-aware terrain shader for the BatchRendererGroup presenter prototype
    // (milestone 5.6a). No channel atlas yet — just a lit base color — so the goal is to prove the
    // GPU-instanced draw path (per-instance transform from the BRG instance buffer) lifts the
    // GameObject-path scaling ceiling. Channels land in 5.6c via a shared atlas + per-instance slice.
    Properties
    {
        _BaseColor  ("Base Color", Color) = (0.45, 0.50, 0.40, 1)
        _Smoothness ("Smoothness", Range(0,1)) = 0.1
        _Metallic   ("Metallic", Range(0,1)) = 0.0
        // Per-instance LOD debug tint (written by the BRG presenter). Declared so the property exists on
        // the material for DOTS instancing; default disables the tint (w=0).
        _LodColor   ("LOD Debug Color", Vector) = (1, 1, 1, 0)
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

            // GPU instancing + DOTS/BRG instancing. DOTS_INSTANCING_ON makes unity_ObjectToWorld /
            // unity_WorldToObject come from the BRG instance GraphicsBuffer.
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float  _Smoothness;
                float  _Metallic;
                float4 _LodColor;     // debug tint (per-material; the presenter uses a material per LOD band)
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
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
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                InputData lightingInput = (InputData)0;
                lightingInput.positionWS = IN.positionWS;
                lightingInput.normalWS = normalize(IN.normalWS);
                lightingInput.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                lightingInput.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                lightingInput.bakedGI = max(SampleSH(lightingInput.normalWS), 0.15);

                // LOD debug tint: lerp the base toward the per-instance LOD color when enabled (w>0).
                float3 albedo = lerp(_BaseColor.rgb, _LodColor.rgb, saturate(_LodColor.w) * 0.7);

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

            struct SAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct SVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

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
