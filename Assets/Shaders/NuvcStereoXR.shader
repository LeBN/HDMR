Shader "Hdmr/NuvcStereoXR"
{
    // Per-eye screen-space overlay for the immersive build. The vertex stage
    // outputs clip space directly (ignores model/view) so the image paints
    // onto EACH eye's viewport centred on that eye's optical axis — no world
    // quad, so the Quest IPD adds no parallax. The Switch already baked the
    // correct stereo disparity + its Labo-lens pre-warp into the SBS frame;
    // here we only undo the pre-warp and place the result at the Switch VR
    // kit's field of view.  research/distortion/xr-stereo-scheme.md
    Properties
    {
        _MainTex ("Capture RGB", 2D) = "black" {}
        _SplitX ("SBS split X", Float) = 0.5
        _Scale ("Scale", Float) = 0.89
        _K1 ("Rectify k1", Float) = -0.78
        _K2 ("Rectify k2", Float) = 0.43
        _Aspect ("Aspect (half w/h)", Float) = 0.889
        _CenXL ("Centre X L", Float) = 0.5
        _CenYL ("Centre Y L", Float) = 0.5
        _CenXR ("Centre X R", Float) = 0.5
        _CenYR ("Centre Y R", Float) = 0.5
        _FillX ("Fill X", Float) = 1.0
        _FillY ("Fill Y", Float) = 1.78
        _FovFrac ("FOV frac (theta_S / theta_Quest)", Float) = 0.68
        _EyeShiftX ("Eye shift X (symmetric)", Float) = 0.0
        _OffXL ("Off X L", Float) = 0
        _OffXR ("Off X R", Float) = 0
        _OffY ("Off Y", Float) = 0
        _BgGrid ("Bg grid", Float) = 0
        _Bright ("Brightness", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _SplitX, _Scale, _K1, _K2, _Aspect;
            float _CenXL, _CenYL, _CenXR, _CenYR, _FillX, _FillY;
            float _FovFrac, _EyeShiftX, _OffXL, _OffXR, _OffY, _BgGrid, _Bright;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                // clip space straight from the quad uv -> fills the whole eye viewport
                float2 p = v.uv * 2.0 - 1.0;
                o.pos = float4(p.x, p.y, UNITY_NEAR_CLIP_VALUE, 1.0);
                o.uv = v.uv;
                return o;
            }

            // rectified-image uv (0..1, ~16:9) -> distorted SBS half -> sample.
            // Same undistort math as Hdmr/NuvcStereo::sampleEye.
            float4 sampleEye(float2 uv, float eye)
            {
                float offx = eye < 0 ? _OffXL : _OffXR;
                float2 cen = eye < 0 ? float2(_CenXL, _CenYL) : float2(_CenXR, _CenYR);

                float2 o = float2((uv.x - 0.5) * _Aspect / max(_FillX, 0.05),
                                  (uv.y - 0.5)           / max(_FillY, 0.05));
                float2 c = float2((cen.x - 0.5) * _Aspect, cen.y - 0.5);
                float2 d = (o - c) / max(_Scale, 0.01);
                float  rr = dot(d, d);
                d *= max(1.0 + _K1 * rr + _K2 * rr * rr, 0.03);
                float2 s = c + d;

                float2 ph = float2(s.x / _Aspect + 0.5, s.y + 0.5);
                ph += float2(offx, _OffY);

                if (eye < 0) ph.x = ph.x * _SplitX;
                else         ph.x = _SplitX + ph.x * (1.0 - _SplitX);

                if (any(ph < 0.0) || any(ph > 1.0)) return float4(0, 0, 0, 1);
                return float4(tex2D(_MainTex, ph).rgb, 1.0);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float eye = unity_StereoEyeIndex < 0.5 ? -1.0 : 1.0;

                // central _FovFrac of the eye viewport maps to the rectified
                // image; a symmetric per-eye X shift for IPD / convergence trim
                float2 uv = (i.uv - 0.5 - float2(eye * _EyeShiftX, _OffY)) / max(_FovFrac, 0.05) + 0.5;
                if (any(uv < 0.0) || any(uv > 1.0)) return fixed4(0, 0, 0, 1);

                fixed4 c = sampleEye(uv, eye);
                c.rgb *= _Bright;

                if (_BgGrid > 0.5)
                {
                    float px = 0.0016;
                    if (abs(uv.x - 0.5) < px || abs(uv.y - 0.5) < px) c.rgb = lerp(c.rgb, float3(0,1,0), 0.7);
                    float2 g = abs(frac(uv * 16.0) - 0.5);
                    if (min(g.x, g.y) < 0.02) c.rgb = lerp(c.rgb, float3(0,0.7,0), 0.3);
                }
                return c;
            }
            ENDCG
        }
    }
}
