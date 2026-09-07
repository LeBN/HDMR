Shader "Hdmr/NuvcStereo"
{
    // Second stage: takes the RGB capture and lays it out for the Quest
    // lenses — split a side-by-side source, pan/scale each half, converge,
    // and (inverse-)warp radial distortion to cancel the source's baked lens
    // distortion or add some of its own.
    //
    // _Mode: 0 full · 1 left-half · 2 right-half · 3 SBS fit (halves side by
    //        side, letterboxed) · 4 anaglyph (L red / R cyan) · 5 SBS overlay
    //        (both halves centred, 50% each — for convergence tuning)
    Properties
    {
        _MainTex   ("Capture RGB", 2D) = "black" {}
        _Mode      ("Mode", Float) = 0
        _SplitX    ("SBS split X", Float) = 0.5
        _Scale     ("Scale", Float) = 1.0
        _OffXL ("Off X L", Float) = 0
        _OffYL ("Off Y L", Float) = 0
        _OffXR ("Off X R", Float) = 0
        _OffYR ("Off Y R", Float) = 0
        _Converge  ("Converge", Float) = 0
        _K1 ("Distortion k1", Float) = -0.23
        _K2 ("Distortion k2", Float) = 0.05
        _Aspect ("Aspect (half w/h)", Float) = 0.889
        _CenXL ("Distortion centre X L", Float) = 0.5
        _CenYL ("Distortion centre Y L", Float) = 0.5
        _CenXR ("Distortion centre X R", Float) = 0.5
        _CenYR ("Distortion centre Y R", Float) = 0.5
        _FillX ("Fill X (zoom to fill the eye)", Float) = 1.0
        _FillY ("Fill Y", Float) = 1.75
        _BgGrid ("Bg grid", Float) = 0
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

            sampler2D _MainTex;
            float _Mode, _SplitX, _Scale, _OffXL, _OffYL, _OffXR, _OffYR, _Converge, _K1, _K2, _Aspect, _BgGrid;
            float _CenXL, _CenYL, _CenXR, _CenYR, _FillX, _FillY;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            // Sample one eye's half of the SBS source, un-distorting the baked
            // radial warp so the eye view comes out as a flat rectangle.
            //   eye = -1 (L) / +1 (R). `uv` = eye-local output in [0,1].
            // Work in a metric where the source half is `_Aspect` wide, 1 tall.
            // Output pixel o -> sample the source at  c + (o-c)/scale * (1 + k1 r^2 + k2 r^4)
            // (matches the offline fit; measured k1 ~ -0.23 for the Switch-2 VR SBS).
            float4 sampleEye(float2 uv, float eye)
            {
                float offx = eye < 0 ? _OffXL : _OffXR;
                float offy = eye < 0 ? _OffYL : _OffYR;
                float2 cen = eye < 0 ? float2(_CenXL, _CenYL) : float2(_CenXR, _CenYR);

                // _FillX/_FillY zoom the output into the source so the 16:9
                // game frame (a letterboxed band inside the portrait half) fills
                // the eye view.
                float2 o = float2((uv.x - 0.5) * _Aspect / max(_FillX, 0.05),
                                  (uv.y - 0.5)           / max(_FillY, 0.05));
                float2 c = float2((cen.x  - 0.5) * _Aspect, cen.y  - 0.5);
                float2 d = (o - c) / max(_Scale, 0.01);
                float  rr = dot(d, d);
                d *= (1.0 + _K1 * rr + _K2 * rr * rr);
                float2 s = c + d;

                float2 ph = float2(s.x / _Aspect + 0.5, s.y + 0.5);
                ph += float2(offx + eye * _Converge * 0.5, offy);

                if (eye < 0) ph.x = ph.x * _SplitX;                 // left  -> [0, split]
                else         ph.x = _SplitX + ph.x * (1.0 - _SplitX);

                if (any(ph < 0.0) || any(ph > 1.0)) return float4(0, 0, 0, 1);
                return tex2D(_MainTex, ph);
            }

            // thin bright line near coordinate `at` along X (vert) or Y (horiz)
            bool nearLine(float v, float at, float px) { return abs(v - at) < px; }

            // guide overlay for a per-eye view: centre crosshair + edge frame
            fixed4 guides(fixed4 c, float2 uv)
            {
                if (_BgGrid < 0.5) return c;
                float px = 0.0016;
                // centre cross
                if (nearLine(uv.x, 0.5, px) || nearLine(uv.y, 0.5, px))
                    c.rgb = lerp(c.rgb, float3(0, 1, 0), 0.7);
                // 20-cell grid
                float2 g = abs(frac(uv * 20.0) - 0.5);
                if (min(g.x, g.y) < 0.015) c.rgb = lerp(c.rgb, float3(0, 0.7, 0), 0.3);
                return c;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
                int m = (int)round(_Mode);

                if (m == 0)   // corrected full view: each half un-distorted, kept in the _SplitX layout
                {
                    fixed4 c; float2 e;
                    if (uv.x < _SplitX) { e = float2(uv.x / _SplitX, uv.y);                         c = sampleEye(e, -1); }
                    else                { e = float2((uv.x - _SplitX) / (1.0 - _SplitX), uv.y);     c = sampleEye(e, +1); }
                    if (_BgGrid > 0.5)
                    {
                        if (nearLine(uv.x, _SplitX, 0.0015)) c.rgb = float3(1, 0, 1);   // seam
                        float2 g = abs(frac(e * 12.0) - 0.5);
                        if (min(g.x, g.y) < 0.02) c.rgb = lerp(c.rgb, float3(0, 0.8, 0), 0.35);
                        if (nearLine(e.x, 0.5, 0.003) || nearLine(e.y, 0.5, 0.003)) c.rgb = lerp(c.rgb, float3(0,1,0), 0.7);
                    }
                    return c;
                }
                if (m == 1) return guides(sampleEye(uv, -1), uv);
                if (m == 2) return guides(sampleEye(uv, +1), uv);

                if (m == 3)  // SBS fit: left half in left 50%, right half in right 50%
                {
                    fixed4 c; float2 e;
                    if (uv.x < 0.5) { c = sampleEye(float2(uv.x * 2.0, uv.y), -1); e = float2(uv.x * 2.0, uv.y); }
                    else            { c = sampleEye(float2((uv.x - 0.5) * 2.0, uv.y), +1); e = float2((uv.x - 0.5) * 2.0, uv.y); }
                    if (_BgGrid > 0.5 && nearLine(uv.x, 0.5, 0.001)) c.rgb = float3(1, 1, 0);
                    return guides(c, e);
                }
                if (m == 4)  // anaglyph
                {
                    float4 L = sampleEye(uv, -1);
                    float4 R = sampleEye(uv, +1);
                    float lg = dot(L.rgb, float3(0.299,0.587,0.114));
                    float rg = dot(R.rgb, float3(0.299,0.587,0.114));
                    return fixed4(lg, rg, rg, 1.0);
                }
                // m == 5: overlay both halves 50/50 (ghost — line up features)
                float4 a = sampleEye(uv, -1);
                float4 b = sampleEye(uv, +1);
                fixed4 o = (a + b) * 0.5;
                if (_BgGrid > 0.5)
                {
                    float2 g = abs(frac(uv * 20.0) - 0.5);
                    if (min(g.x, g.y) < 0.02) o.rgb = lerp(o.rgb, float3(0,1,0), 0.4);
                }
                return o;
            }
            ENDCG
        }
    }
}
