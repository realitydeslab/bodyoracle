Shader "BodyOracle/GPU Sprites"
{
    Properties { _MainTex ("Sprite", 2D) = "white" {} }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+100" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:Setup
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Detection { float4 box; float4 info; float4 extra; };
            StructuredBuffer<Detection> _Detections;
            StructuredBuffer<uint> _Metadata;
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4x4 _PoseToWorld;
            float _PlaneDistance;
            int _ClassId;
            int _OnlyCenter;
            void Setup() { }
            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                uint i = 0;
                #if UNITY_ANY_INSTANCING_ENABLED
                    i = unity_InstanceID;
                #endif
                Detection d = _Detections[i];
                bool visible = i < _Metadata[0] && (int)d.info.y == _ClassId &&
                    (_OnlyCenter == 0 || _Metadata[1] == i + 1);
                float3 local = float3(d.info.zw, _PlaneDistance) + input.positionOS * d.extra.x;
                float3 world = mul(_PoseToWorld, float4(local, 1)).xyz;
                output.positionCS = visible ? TransformWorldToHClip(world) : float4(2, 2, 2, 1);
                output.uv = input.uv;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
            }
            ENDHLSL
        }
    }
}
