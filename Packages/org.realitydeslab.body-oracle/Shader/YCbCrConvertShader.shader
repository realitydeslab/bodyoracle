Shader "Custom/YCbCrConvertShader"
{
    Properties
    {
        _textureY ("TextureY", 2D) = "white" {}
        _textureCbCr ("TextureCbCr", 2D) = "black" {}
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Opaque"
        }

        Pass
        {
            Cull Off
            ZTest Always
            ZWrite Off
            Lighting Off
            LOD 100
            
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _textureY;
            sampler2D _textureCbCr;
            
            // YCbCr to RGB conversion matrix
            static const half4x4 YCbCrToRGB = half4x4(
                half4(1.0h,  0.0000h,  1.4020h, -0.7010h),
                half4(1.0h, -0.3441h, -0.7141h,  0.5291h),
                half4(1.0h,  1.7720h,  0.0000h, -0.8860h),
                half4(0.0h,  0.0000h,  0.0000h,  1.0000h)
            );
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            
            fixed4 frag (v2f i) : SV_Target
            {
                float2 transformedUV = i.uv;
                transformedUV.y = 1.0 - i.uv.y;

                // Sample the video textures (in YCbCr)
                half4 ycbcr = half4(
                    tex2D(_textureY, transformedUV).r,
                    tex2D(_textureCbCr, transformedUV).rg,
                    1.0h
                );

                // Convert from YCbCr to RGB
                half4 videoColor = mul(YCbCrToRGB, ycbcr);
                
                #if !UNITY_COLORSPACE_GAMMA
                // If rendering in linear color space, convert from sRGB to RGB
                videoColor.rgb = GammaToLinearSpace(videoColor.rgb);
                #endif
                
                return videoColor;
            }
            ENDCG
        }
    }
}
