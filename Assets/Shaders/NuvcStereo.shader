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
        _K1 ("Distortion k1", Float) = 0
        _K2 ("Distortion k2", Float) = 0
        _Aspect ("Aspect (w/h)", Float) = 1.777777
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

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            // radial distortion around centre c; k>0 pincushion, k<0 barrel.
            // Applied to the SAMPLING coord so it inverts a baked distortion.
            float2 warp(float2 uv, float2 c)
            {
                float2 d = uv - c;
                d.x *= _Aspect;                 // work in a square metric
                float r2 = dot(d, d);
                float f = 1.0 + _K1 * r2 + _K2 * r2 * r2;
                d *= f;
                d.x /= _Aspect;
                return c + d;
            }

            // sample one eye's half of the source. eye = -1 (L) or +1 (R)
            float4 sampleEye(float2 uv, float eye)
            {
                float offx = eye < 0 ? _OffXL : _OffXR;
                float offy = eye < 0 ? _OffYL : _OffYR;

                // centre, scale, pan
                float2 p = (uv - 0.5) / max(_Scale, 0.01) + 0.5;
                p += float2(offx + eye * _Converge * 0.5, offy);

                // distortion inverse-warp about the eye centre
                p = warp(p, float2(0.5, 0.5));

                // map into the correct half of the SBS source
                float sx = _SplitX;
                if (eye < 0) p.x = p.x * sx;              // left  -> [0, sx]
                else         p.x = sx + p.x * (1.0 - sx); // right -> [sx, 1]

                if (any(p < 0.0) || any(p > 1.0)) return float4(0,0,0,1);
                return tex2D(_MainTex, p);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
                int m = (int)round(_Mode);

                if (m == 0)   // full, still scale/pan/warp with the L params
                {
                    float2 p = (uv - 0.5) / max(_Scale, 0.01) + 0.5 + float2(_OffXL, _OffYL);
                    p = warp(p, float2(0.5, 0.5));
                    if (any(p < 0.0) || any(p > 1.0)) return fixed4(0,0,0,1);
                    return tex2D(_MainTex, p);
                }
                if (m == 1) return sampleEye(uv, -1);
                if (m == 2) return sampleEye(uv, +1);

                if (m == 3)  // SBS fit: left half in left 50%, right half in right 50%
                {
                    if (uv.x < 0.5) return sampleEye(float2(uv.x * 2.0, uv.y), -1);
                    return sampleEye(float2((uv.x - 0.5) * 2.0, uv.y), +1);
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
