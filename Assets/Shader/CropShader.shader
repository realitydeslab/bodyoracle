Shader "Custom/CropShader"
{
    Properties
    {
        _MainTex ("Main Texture", 2D) = "black" {}
        _AspectRatio ("Aspect Ratio", Float) = 1.0
        _Rotate ("Rotate", Float) = 0.0
    }
    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "Queue"="Geometry"
        }
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
            float _AspectRatio;
            float _Rotate;
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
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
                
                if (_Rotate > 0.5) {
                    squareUV = float2(1.0 - squareUV.y, squareUV.x);
                }
                
                return tex2D(_MainTex, squareUV);
            }
            ENDCG
        }
    }
}