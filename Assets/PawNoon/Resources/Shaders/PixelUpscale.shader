// Paw Noon — final upscale of the virtual-resolution pixel target to the screen.
// Sharp-bilinear: every source texel becomes a flat block; only the 1-screen-pixel seam between
// texels is blended, so non-integer scale factors (e.g. 270 -> 1179 = 4.37x) stay crisp without
// shimmering. The source RTs are point-filtered, so the bilinear blend is done manually from 4
// texel-centre taps (tex2Dlod: no implicit derivatives at block edges).
//
// World image: sub-pixel shake offset, focus zoom, DRAW tear (alternating 4-px bands) + 1-frame
// chromatic split, warm grade + black crush, "last light" tension grade, stepped radial pop,
// dithered pixel-grid vignette (+ red wince), stepped full-screen flash.
// Overlay image (HUD / cues, premultiplied alpha): composited last, unzoomed, unshaken, unflashed.
// All "soft" effects are quantized on the virtual pixel grid with a 4x4 ordered dither.
Shader "PawNoon/PixelUpscale"
{
    Properties
    {
        _MainTex ("Virtual RT", 2D) = "black" {}
        _OverlayTex ("Overlay RT (premultiplied)", 2D) = "black" {}
        _OverlayOn ("Overlay on", Float) = 0
        _OverlayOffset ("Overlay offset px (xy)", Vector) = (0, 0, 0, 0)
        _TexSize ("Tex size (w,h,1/w,1/h)", Vector) = (270, 480, 0.0037, 0.0021)
        _Zoom ("Zoom", Float) = 1
        _ZoomCenter ("Zoom centre (uv xy)", Vector) = (0.5, 0.5, 0, 0)
        _Offset ("Offset px (xy)", Vector) = (0, 0, 0, 0)
        _Flash ("Flash", Range(0, 1)) = 0
        _FlashColor ("Flash colour", Color) = (1, 1, 1, 1)
        _Vignette ("Vignette", Range(0, 1)) = 0.24
        _VigInner ("Vignette inner radius", Float) = 0.32
        _Warm ("Warm grade", Range(0, 1)) = 0.6
        _Crush ("Black crush", Range(0, 0.2)) = 0.025
        _Tension ("Last-light tension", Range(0, 1)) = 0
        _Tear ("Tear px (signed)", Float) = 0
        _Chroma ("Chroma split px", Float) = 0
        _Wince ("Wince", Range(0, 1)) = 0
        _Pop ("Pop (texel x, y, radius px, amount)", Vector) = (0, 0, 1, 0)
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Opaque" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _OverlayTex;
            float _OverlayOn;
            float4 _OverlayOffset;
            float4 _TexSize;
            float _Zoom;
            float4 _ZoomCenter;
            float4 _Offset;
            float _Flash;
            fixed4 _FlashColor;
            float _Vignette;
            float _VigInner;
            float _Warm;
            float _Crush;
            float _Tension;
            float _Tear;
            float _Chroma;
            float _Wince;
            float4 _Pop;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 tapMain (float2 texelCentre)
            {
                float2 c = clamp(texelCentre, float2(0.5, 0.5), _TexSize.xy - 0.5);
                return tex2Dlod(_MainTex, float4(c * _TexSize.zw, 0, 0));
            }

            float4 tapOverlay (float2 texelCentre)
            {
                float2 c = clamp(texelCentre, float2(0.5, 0.5), _TexSize.xy - 0.5);
                return tex2Dlod(_OverlayTex, float4(c * _TexSize.zw, 0, 0));
            }

            // sharp-bilinear: returns the bilinear weights + base texel centre for a texel coordinate
            void sharpCoords (float2 texel, float2 scale, out float2 c00, out float2 w)
            {
                float2 fl = floor(texel);
                float2 s = texel - fl;
                float2 range = 0.5 - 0.5 / scale;
                float2 cd = s - 0.5;
                float2 f = (cd - clamp(cd, -range, range)) * scale + 0.5;
                float2 p = fl + f - 0.5;          // texel centres sit at k + 0.5
                float2 p0 = floor(p);
                w = p - p0;
                c00 = p0 + 0.5;
            }

            float4 sharpMain (float2 texel, float2 scale)
            {
                float2 c00, w;
                sharpCoords(texel, scale, c00, w);
                float4 a = tapMain(c00);
                float4 b = tapMain(c00 + float2(1, 0));
                float4 c = tapMain(c00 + float2(0, 1));
                float4 d = tapMain(c00 + float2(1, 1));
                return lerp(lerp(a, b, w.x), lerp(c, d, w.x), w.y);
            }

            float4 sharpOverlay (float2 texel, float2 scale)
            {
                float2 c00, w;
                sharpCoords(texel, scale, c00, w);
                float4 a = tapOverlay(c00);
                float4 b = tapOverlay(c00 + float2(1, 0));
                float4 c = tapOverlay(c00 + float2(0, 1));
                float4 d = tapOverlay(c00 + float2(1, 1));
                return lerp(lerp(a, b, w.x), lerp(c, d, w.x), w.y);
            }

            // 4x4 ordered dither threshold in [0, 1) for an integer pixel coordinate
            float bayer2 (float2 a)
            {
                a = floor(a);
                return frac(a.x * 0.5 + a.y * a.y * 0.75);
            }
            float bayer4 (float2 a)
            {
                return bayer2(0.5 * a) * 0.25 + bayer2(a);
            }

            // quantize an amount 0..1 into `steps` levels, dithered on the pixel grid
            float stepped (float amount, float steps, float threshold)
            {
                return saturate(floor(amount * steps + threshold) / steps);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // screen-anchored virtual pixel (vignette, dither, tear bands): stable under zoom/shake
                float2 vpix = floor(i.uv * _TexSize.xy);
                float dither = bayer4(vpix);

                // ---------------- world
                float zoom = max(_Zoom, 1.0);
                float2 uv = (i.uv - _ZoomCenter.xy) / zoom + _ZoomCenter.xy;
                float2 texel = uv * _TexSize.xy + _Offset.xy;
                // DRAW tear: alternating 4-px bands shifted by whole pixels
                float band = fmod(floor(vpix.y * 0.25), 2.0);
                texel.x += (band * 2.0 - 1.0) * _Tear;

                float2 scale = max(_ScreenParams.xy * _TexSize.zw * zoom, 1.0);
                float4 col = sharpMain(texel, scale);
                if (_Chroma > 0.001)
                {
                    col.r = sharpMain(texel + float2(_Chroma, 0), scale).r;
                    col.b = sharpMain(texel - float2(_Chroma, 0), scale).b;
                }

                // warm high-noon grade: lift reds, trim blues in the highlights
                float lum = dot(col.rgb, float3(0.299, 0.587, 0.114));
                float3 warm = col.rgb * float3(1.045, 1.0, 0.93) + float3(0.012, 0.006, 0.0) * lum;
                col.rgb = lerp(col.rgb, saturate(warm), _Warm);
                // slight black crush: chunky ink outlines read deeper
                col.rgb = saturate((col.rgb - _Crush) / (1.0 - _Crush));

                // "last light": desaturated, warmer, darker as the match nears its end
                if (_Tension > 0.001)
                {
                    float l2 = dot(col.rgb, float3(0.299, 0.587, 0.114));
                    float3 last = lerp(col.rgb, l2.xxx, 0.3) * float3(1.05, 0.95, 0.82) * 0.92;
                    col.rgb = lerp(col.rgb, saturate(last), _Tension);
                }

                // stepped radial brightness pop, locked to content pixels (hit on Blackpaw)
                if (_Pop.w > 0.001)
                {
                    float2 cp = floor(texel) + 0.5;
                    float r = length(cp - _Pop.xy) / max(_Pop.z, 1.0);
                    float amt = stepped(saturate(1.0 - r) * 1.6 * _Pop.w, 4.0, bayer4(floor(texel)));
                    col.rgb = lerp(col.rgb, float3(1.0, 0.97, 0.86), amt * 0.6);
                }

                // dithered oval vignette toward ink-purple, quantized on the virtual pixel grid
                float2 vuv = (vpix + 0.5) * _TexSize.zw;
                float2 q = (vuv - 0.5) * float2(1.0, 0.85);
                float d = length(q) * 1.15;
                float vShape = smoothstep(_VigInner, 0.78, d);
                float vig = stepped(vShape * (_Vignette + _Tension * 0.16), 6.0, dither);
                col.rgb = lerp(col.rgb, col.rgb * float3(0.62, 0.52, 0.66), vig);
                if (_Wince > 0.001)
                {
                    float wShape = smoothstep(0.22, 0.7, d);
                    float wv = stepped(wShape * _Wince, 4.0, dither);
                    col.rgb = lerp(col.rgb, col.rgb * float3(0.78, 0.16, 0.2), wv * 0.85);
                }

                col.rgb = lerp(col.rgb, _FlashColor.rgb, saturate(_Flash));

                // ---------------- overlay (premultiplied): crisp, unzoomed
                if (_OverlayOn > 0.5)
                {
                    float2 otexel = i.uv * _TexSize.xy + _OverlayOffset.xy;
                    float2 oscale = max(_ScreenParams.xy * _TexSize.zw, 1.0);
                    float4 o = sharpOverlay(otexel, oscale);
                    col.rgb = col.rgb * (1.0 - o.a) + o.rgb;
                }

                col.a = 1;
                return col;
            }
            ENDCG
        }
    }
    Fallback Off
}
