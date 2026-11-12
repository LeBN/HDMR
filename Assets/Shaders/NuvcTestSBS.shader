Shader "Hdmr/NuvcTestSBS"
{
    // Procedural side-by-side calibration frame — use it to dial in the
    // stereo split / convergence / per-eye offset without needing a live
    // Switch feed. Each half carries: a labelled corner marker (L brighter,
    // R cooler), concentric rings + crosshair on the half-centre, a ruled
    // grid, and an edge frame. The right half's rings sit a few % inward so
    // "rings concentric in both eyes" == convergence correct.
    Properties { _Split ("Split X", Float) = 0.5 }
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

            float _Split;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            float ring(float2 p, float2 c, float r, float w)
            {
                return 1.0 - smoothstep(w, w * 2.0, abs(length(p - c) - r));
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
                bool left = uv.x < _Split;

                // local coords inside this half, 0..1, plus a small inward
                // bias for the right half so a converged view lines up
                float2 h = left ? float2(uv.x / _Split, uv.y)
                                : float2((uv.x - _Split) / (1.0 - _Split), uv.y);
                float2 c = left ? float2(0.5, 0.5) : float2(0.47, 0.5);

                float3 col = left ? float3(0.05, 0.06, 0.08) : float3(0.06, 0.07, 0.10);

                // ruled grid
                float2 g = abs(frac(h * 10.0) - 0.5);
                if (min(g.x, g.y) < 0.02) col += 0.10;
                float2 g2 = abs(frac(h * 2.0) - 0.5);
                if (min(g2.x, g2.y) < 0.012) col += 0.18;

                // concentric rings on the half-centre
                float3 rc = left ? float3(0.3, 1.0, 0.4) : float3(0.4, 0.8, 1.0);
                col += rc * (ring(h, c, 0.12, 0.004) + ring(h, c, 0.24, 0.004) + ring(h, c, 0.36, 0.004));

                // crosshair
                if (abs(h.x - c.x) < 0.0025 || abs(h.y - c.y) < 0.0025) col = rc;

                // edge frame
                if (h.x < 0.012 || h.x > 0.988 || h.y < 0.012 || h.y > 0.988)
                    col = left ? float3(1, 1, 1) : float3(1, 0.85, 0.4);

                // corner block (top-left of each half) — L bright white, R amber
                if (h.x < 0.09 && h.y > 0.91) col = left ? float3(1, 1, 1) : float3(1, 0.6, 0.15);

                // the source seam itself
                if (abs(uv.x - _Split) < 0.001) col = float3(1, 0, 1);

                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
