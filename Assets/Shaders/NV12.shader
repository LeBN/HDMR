Shader "Hdmr/NV12"
{
    // NV12: full-res single-channel Y (R8) + half-res interleaved CbCr (RG).
    // BT.709 limited-range by default; _FullRange=1 for 0-255 sources.
    Properties
    {
        _YTex  ("Y (R8)",  2D) = "black" {}
        _UVTex ("CbCr (RG)", 2D) = "gray" {}
        _FullRange ("Full range", Float) = 0
        _FlipX ("Flip X", Float) = 0
        _FlipY ("Flip Y", Float) = 0
        _Gain ("Gain (exposure)", Float) = 1
        _Gamma ("Gamma", Float) = 1
        _Lift ("Black lift", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _YTex;
            sampler2D _UVTex;
            float _FullRange, _FlipX, _FlipY, _Gain, _Gamma, _Lift;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float2 uv = v.texcoord;
                if (_FlipX > 0.5) uv.x = 1.0 - uv.x;
                if (_FlipY > 0.5) uv.y = 1.0 - uv.y;
                o.uv = uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float y  = tex2D(_YTex,  i.uv).r;
                float2 uv = tex2D(_UVTex, i.uv).rg;
                float cb = uv.x - 0.5;
                float cr = uv.y - 0.5;

                if (_FullRange < 0.5) y = (y - 0.0625) * 1.16438356;   // 16..235 -> 0..1

                // BT.709
                float r = y + 1.79274107 * cr;
                float g = y - 0.21324861 * cb - 0.53290933 * cr;
                float b = y + 2.11240179 * cb;
                float3 rgb = saturate(float3(r, g, b));

                // grade: black-lift, exposure gain, gamma
                rgb = rgb * (1.0 - _Lift) + _Lift;
                rgb = saturate(rgb * _Gain);
                if (abs(_Gamma - 1.0) > 0.001) rgb = pow(rgb, 1.0 / max(_Gamma, 0.01));
                return fixed4(rgb, 1.0);
            }
            ENDCG
        }
    }
}
