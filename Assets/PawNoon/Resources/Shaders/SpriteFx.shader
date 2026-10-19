// Paw Noon — sprite shader: vertex colour tint, white flash amount, optional heat shimmer
// (whole-pixel horizontal row offsets animated over _HeatTime = PawTime.AmbientT, so the shimmer
// holds its breath with the street during the duel hold, for far environment layers).
// Alpha blend writes coverage as One/OneMinusSrcAlpha so an RT cleared to (0,0,0,0) ends up
// premultiplied (the HUD overlay composite in PixelUpscale); opaque targets are unaffected.
// Used by SpriteRenderers and by BitmapText meshes (white glyphs tinted by vertex colour).
Shader "PawNoon/SpriteFx"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _Flash ("Flash", Range(0, 1)) = 0
        _FlashColor ("Flash colour", Color) = (1, 1, 1, 1)
        _HeatAmp ("Heat amplitude (px)", Float) = 0
        _HeatSpeed ("Heat speed", Float) = 2.2
        _HeatFreq ("Heat row frequency", Float) = 0.45
        _HeatTop ("Heat band top (uv y)", Float) = 1
        _HeatBottom ("Heat band bottom (uv y)", Float) = 0
        _HeatTime ("Heat clock (s)", Float) = 0
    }
    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _Color;
            float _Flash;
            fixed4 _FlashColor;
            float _HeatAmp;
            float _HeatSpeed;
            float _HeatFreq;
            float _HeatTop;
            float _HeatBottom;
            float _HeatTime;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
                if (_HeatAmp > 0.001)
                {
                    float row = floor(uv.y * _MainTex_TexelSize.w);
                    float t = _HeatTime * _HeatSpeed;
                    float wave = sin(row * _HeatFreq + t) * sin(row * 0.11 - t * 0.37);
                    float band = step(_HeatBottom, uv.y) * step(uv.y, _HeatTop);
                    float shift = round(wave * _HeatAmp * band);
                    uv.x += shift * _MainTex_TexelSize.x;
                }
                fixed4 c = tex2D(_MainTex, uv) * i.color;
                c.rgb = lerp(c.rgb, _FlashColor.rgb, saturate(_Flash));
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
