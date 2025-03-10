Shader "Custom/ARFoundationCameraCropShader"
{
    Properties
    {
        _MainTex ("Main Texture", 2D) = "black" {}
        _YTex ("Y Texture", 2D) = "black" {}
        _CbCrTex ("CbCr Texture", 2D) = "black" {}
        _AspectRatio ("Aspect Ratio", Float) = 1.0
        _UseYCbCr ("Use YCbCr (1) or RGB (0)", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
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

            sampler2D _MainTex;
            sampler2D _YTex;
            sampler2D _CbCrTex;
            float _AspectRatio;
            float _UseYCbCr;
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            
            // Converts YCbCr to RGB
            fixed4 YCbCrToRGB(float y, float2 cbcr)
            {
                float cb = cbcr.x - 0.5;
                float cr = cbcr.y - 0.5;
                
                // Standard YCbCr to RGB conversion matrix
                float r = y + 1.402 * cr;
                float g = y - 0.344 * cb - 0.714 * cr;
                float b = y + 1.772 * cb;
                
                return fixed4(r, g, b, 1.0);
            }
            
            // Extract center square from input texture
            float2 extractCenterSquare(float2 uv, float aspectRatio)
            {
                float2 center = float2(0.5, 0.5);
                float2 squareUV = uv;
                
                if (aspectRatio > 1.0) {
                    // Width > Height (landscape)
                    // Extract center square with height as the limiting factor
                    float squareSize = 1.0 / aspectRatio; // Size of square relative to width
                    float startX = 0.5 - (squareSize / 2.0);
                    float endX = 0.5 + (squareSize / 2.0);
                    
                    // Map input UV.x from [0,1] to [startX,endX]
                    squareUV.x = startX + (uv.x * squareSize);
                }
                else {
                    // Height > Width (portrait)
                    // Extract center square with width as the limiting factor
                    float squareSize = aspectRatio; // Size of square relative to height
                    float startY = 0.5 - (squareSize / 2.0);
                    float endY = 0.5 + (squareSize / 2.0);
                    
                    // Map input UV.y from [0,1] to [startY,endY]
                    squareUV.y = startY + (uv.y * squareSize);
                }
                
                return squareUV;
            }
            
            fixed4 frag (v2f i) : SV_Target
            {
                // Extract center square from input texture
                float2 squareUV = extractCenterSquare(i.uv, _AspectRatio);
                
                // Process the texture based on input type
                if (_UseYCbCr > 0.5) {
                    // YCbCr processing path
                    float y = tex2D(_YTex, squareUV).r;
                    float2 cbcr = tex2D(_CbCrTex, squareUV).rg;
                    return YCbCrToRGB(y, cbcr);
                } else {
                    // Standard RGB processing path
                    return tex2D(_MainTex, squareUV);
                }
            }
            ENDCG
        }
    }
}