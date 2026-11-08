Shader "Fuchu/GPU Ground Fill"
{
    Properties
    {
        [NoScaleOffset] _GroundMask ("Claimed ground", 2D) = "black" {}
        _FillColor ("Fill color", Color) = (0.04, 0.27, 0.9, 0.68)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _MaskRect;
            float4x4 _WorldToGround;
            half4 _FillColor;
        CBUFFER_END

        float2 GroundUV(float2 position)
        {
            return (position - _MaskRect.xy) * _MaskRect.zw;
        }
        ENDHLSL

        Pass
        {
            Name "Overlay"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            ZWrite Off
            // Match the visible road or terrain surface without blending onto the one underneath.
            ZTest Equal
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex OverlayVertex
            #pragma fragment OverlayFragment
            TEXTURE2D(_GroundMask);
            SAMPLER(sampler_GroundMask);

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings OverlayVertex(float4 positionOS : POSITION)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(positionOS.xyz);
                float3 positionWS = TransformObjectToWorld(positionOS.xyz);
                output.uv = GroundUV(mul(_WorldToGround, float4(positionWS, 1)).xz);
                return output;
            }

            half4 OverlayFragment(Varyings input) : SV_Target
            {
                half coverage = SAMPLE_TEXTURE2D(_GroundMask, sampler_GroundMask, input.uv).r;
                clip(coverage - 0.001);
                return half4(_FillColor.rgb, _FillColor.a * coverage);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Stamp"
            Tags { "LightMode" = "FuchuMaskStamp" }
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask R

            HLSLPROGRAM
            #pragma vertex StampVertex
            #pragma fragment StampFragment

            float4 StampVertex(float4 positionOS : POSITION) : SV_POSITION
            {
                float2 position = GroundUV(positionOS.xz) * 2.0 - 1.0;
                #if UNITY_UV_STARTS_AT_TOP
                    position.y = -position.y;
                #endif
                return float4(position, 0.5, 1.0);
            }

            half4 StampFragment() : SV_Target
            {
                return half4(1, 0, 0, 1);
            }
            ENDHLSL
        }
    }
}
