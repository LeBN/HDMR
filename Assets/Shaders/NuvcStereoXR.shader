Shader "Hdmr/NuvcStereoXR"
{
    // True per-eye version of Hdmr/NuvcStereo for an immersive (XR) build.
    // Draw this on a world-space quad in front of the camera. Under Quest's
    // single-pass-instanced stereo the frag runs once per eye; we read
    // unity_StereoEyeIndex (0 = left, 1 = right) and sample the matching
    // half of the SBS capture with that eye's offsets / converge / warp.
    //
    // _MainTex is the plain 2D capture RGB (NuvcCapture.Output) — NOT a
    // stereo texture array — so no UNITY_DECLARE_SCREENSPACE_TEXTURE here.
    Properties
    {
        _MainTex  ("Capture RGB", 2D) = "black" {}
        _SplitX   ("SBS split X", Float) = 0.5
        _Scale    ("Scale", Float) = 1.0
        _OffXL ("Off X L", Float) = 0
        _OffYL ("Off Y L", Float) = 0
        _OffXR ("Off X R", Float) = 0
        _OffYR ("Off Y R", Float) = 0
        _Converge ("Converge", Float) = 0
        _K1 ("Distortion k1", Float) = -1.0
        _K2 ("Distortion k2", Float) = 0
        _Aspect ("Aspect (half w/h)", Float) = 0.889
        _CenXL ("Distortion centre X L", Float) = 0.5
        _CenYL ("Distortion centre Y L", Float) = 0.5
        _CenXR ("Distortion centre X R", Float) = 0.5
        _CenYR ("Distortion centre Y R", Float) = 0.5
        _FillX ("Fill X", Float) = 1.0
        _FillY ("Fill Y", Float) = 1.8
        _BgGrid ("Bg grid", Float) = 0
        _Opacity ("Opacity", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Cull Off ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _SplitX, _Scale, _OffXL, _OffYL, _OffXR, _OffYR, _Converge, _K1, _K2, _Aspect, _BgGrid, _Opacity;
            float _CenXL, _CenYL, _CenXR, _CenYR, _FillX, _FillY;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // see Hdmr/NuvcStereo — un-distort a half of the SBS source into a
            // flat rectangle. eye = -1 (L) / +1 (R), uv = eye-local output [0,1].
            float4 sampleEye(float2 uv, float eye)
            {
                float offx = eye < 0 ? _OffXL : _OffXR;
                float offy = eye < 0 ? _OffYL : _OffYR;
                float2 cen = eye < 0 ? float2(_CenXL, _CenYL) : float2(_CenXR, _CenYR);

                float2 o = float2((uv.x - 0.5) * _Aspect / max(_FillX, 0.05),
                                  (uv.y - 0.5)           / max(_FillY, 0.05));
                float2 cc = float2((cen.x - 0.5) * _Aspect, cen.y - 0.5);
                float2 d = (o - cc) / max(_Scale, 0.01);
                float  rr = dot(d, d);
                d *= max(1.0 + _K1 * rr + _K2 * rr * rr, 0.03);
                float2 s = cc + d;

                float2 p = float2(s.x / _Aspect + 0.5, s.y + 0.5);
                p += float2(offx + eye * _Converge * 0.5, offy);

                float sx = _SplitX;
                if (eye < 0) p.x = p.x * sx;
                else         p.x = sx + p.x * (1.0 - sx);

                if (any(p < 0.0) || any(p > 1.0)) return float4(0, 0, 0, 0);
                return float4(tex2D(_MainTex, p).rgb, 1.0);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float eye = unity_StereoEyeIndex < 0.5 ? -1.0 : 1.0;
                float4 c = sampleEye(i.uv, eye);

                if (_BgGrid > 0.5)
                {
                    float px = 0.0016;
                    if (abs(i.uv.x - 0.5) < px || abs(i.uv.y - 0.5) < px)
                        c = float4(lerp(c.rgb, float3(0, 1, 0), 0.7), max(c.a, 0.7));
                    float2 g = abs(frac(i.uv * 20.0) - 0.5);
                    if (min(g.x, g.y) < 0.015)
                        c = float4(lerp(c.rgb, float3(0, 0.7, 0), 0.3), max(c.a, 0.3));
                }

                c.a *= _Opacity;
                return c;
            }
            ENDCG
        }
    }
}
