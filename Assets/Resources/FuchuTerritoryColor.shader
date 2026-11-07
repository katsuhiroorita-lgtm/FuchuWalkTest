Shader "Fuchu/Walking Territory"
{
    Properties
    {
        [MainColor] _Color ("Color", Color) = (0, 0.4, 1, 1)
        _ZWrite ("Write depth", Float) = 0
        _Shade ("Shade buildings", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Off
            ZWrite [_ZWrite]
            Blend SrcAlpha OneMinusSrcAlpha
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Shade;
                float _ZWrite;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half lighting : TEXCOORD0;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                float3 normal = TransformObjectToWorldNormal(input.normalOS, false);
                half light = saturate(dot(normal, half3(0.36, 0.8, 0.48)));
                output.lighting = lerp(1.0, 0.68 + light * 0.32, _Shade);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                return half4(_Color.rgb * input.lighting, _Color.a);
            }
            ENDHLSL
        }
    }
}
