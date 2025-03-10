Shader "Custom/ARFoundationCameraCropShader"
{
    Properties
    {
        _MainTex ("Main Texture", 2D) = "black" {}
        _YTex ("Y Texture", 2D) = "black" {}
        _CbCrTex ("CbCr Texture", 2D) = "black" {}
        _AspectRatio ("Aspect Ratio", Float) = 1.0
        _UseYCbCr ("Use YCbCr (1) or RGB (0)", Float) = 1.0
        [KeywordEnum(BT601, BT709, FULL_RANGE)] _YCbCrStandard ("YCbCr Standard", Float) = 0
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
            #pragma multi_compile _YCBCRSTANDARD_BT601 _YCBCRSTANDARD_BT709 _YCBCRSTANDARD_FULL_RANGE
            
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
            float4 _YTex_TexelSize;
            float4 _CbCrTex_TexelSize;
            float _AspectRatio;
            float _UseYCbCr;
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            
            // Converts YCbCr to RGB with different standards
            fixed4 YCbCrToRGB(float y, float2 cbcr)
            {
                float cb = cbcr.x - 0.5;
                float cr = cbcr.y - 0.5;
                
                float r, g, b;
                
                // Different conversion matrices based on standard
                #if defined(_YCBCRSTANDARD_BT601)
                    // BT.601 (SD) conversion
                    r = y + 1.402 * cr;
                    g = y - 0.344 * cb - 0.714 * cr;
                    b = y + 1.772 * cb;
                #elif defined(_YCBCRSTANDARD_BT709)
                    // BT.709 (HD) conversion
                    r = y + 1.5748 * cr;
                    g = y - 0.1873 * cb - 0.4681 * cr;
                    b = y + 1.8556 * cb;
                #else // _YCBCRSTANDARD_FULL_RANGE
                    // Full range conversion (often used in mobile cameras)
                    r = y + 1.402 * cr;
                    g = y - 0.344 * cb - 0.714 * cr;
                    b = y + 1.772 * cb;
                    
                    // Adjust for full range
                    r = r * 1.164;
                    g = g * 1.164;
                    b = b * 1.164;
                #endif
                
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
            
            // Adjust UV coordinates for CbCr plane if needed
            float2 adjustCbCrUV(float2 uv)
            {
                // Check if CbCr texture has different dimensions than Y texture
                // This handles the case where CbCr is at half resolution (common in 4:2:0 format)
                float2 cbcrUV = uv;
                
                // If CbCr texture is half the size of Y texture in either dimension
                if (_CbCrTex_TexelSize.z != _YTex_TexelSize.z || 
                    _CbCrTex_TexelSize.w != _YTex_TexelSize.w) {
                    // Adjust UV to account for the resolution difference
                    // This prevents sampling artifacts at texture edges
                    float2 scale = float2(_YTex_TexelSize.z / _CbCrTex_TexelSize.z,
                                         _YTex_TexelSize.w / _CbCrTex_TexelSize.w);
                    
                    // Center the sampling in the CbCr texel
                    cbcrUV = (floor(uv * scale) + 0.5) / scale;
                }
                
                return cbcrUV;
            }
            
            fixed4 frag (v2f i) : SV_Target
            {
                // Extract center square from input texture
                float2 squareUV = extractCenterSquare(i.uv, _AspectRatio);
                
                // Process the texture based on input type
                if (_UseYCbCr > 0.5) {
                    // YCbCr processing path
                    float y = tex2D(_YTex, squareUV).r;
                    
                    // Adjust UV for CbCr texture if needed
                    float2 cbcrUV = adjustCbCrUV(squareUV);
                    float2 cbcr = tex2D(_CbCrTex, cbcrUV).rg;
                    
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