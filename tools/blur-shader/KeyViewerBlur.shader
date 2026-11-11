// JipperKeyViewer key-background blur (GrabPass screen blur) / JKV 按键背景模糊（GrabPass 屏幕模糊）
// Drawn by a dedicated merged KeyShapeLayer ("blur layer") that carries, per vertex:
//   uv0.z = blur radius in px (interpolates across a slot, so per-node radii work in ONE material)
//   uv0.w = vibrancy (saturation boost, 0..2)
// and uses the standard uGUI vertex colour as the tint (so per-node colours and the existing
// vertical-gradient path keep working unchanged).
// 由专用的合并 KeyShapeLayer（"模糊层"）绘制，每顶点携带：uv0.z = 模糊半径（像素，顶点间插值，
// 故一个材质即可支持逐节点半径）；uv0.w = 浓度（饱和度增益，0..2）；标准 uGUI 顶点色充当染色，
// 因此逐节点颜色与既有的垂直渐变路径原样可用。
Shader "JipperKeyViewer/UIBlur"
{
    Properties
    {
        // uGUI binds the Graphic's mainTexture (= the key background sprite) here; its ALPHA
        // masks the blur so rounded key art keeps rounded corners instead of a square blur box.
        // uGUI 会把 Graphic 的 mainTexture（= 按键背景贴图）绑到这里；它的 alpha 作为模糊的
        // 遮罩，使圆角按键贴图保留圆角而不是露出方形模糊块。
        [PerRendererData] _MainTex("Sprite Texture", 2D) = "white" {}
        _TintColor("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        // Named grab: one capture per frame no matter how many draw calls use it.
        // 命名抓取：无论当帧多少次绘制都只抓一次。
        GrabPass { "_JKVGrabTexture" }

        Pass
        {
            Name "UIBLUR"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _JKVGrabTexture;
            float4 _JKVGrabTexture_TexelSize;
            sampler2D _MainTex;
            fixed4 _TintColor;

            struct appdata_t
            {
                float4 vertex   : POSITION;
                fixed4 color    : COLOR;
                float4 texcoord : TEXCOORD0; // xy = sprite UV (alpha mask); z = radius px, w = vibrancy / xy=贴图UV（alpha 遮罩），z=半径，w=浓度
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float4 uvgrab   : TEXCOORD0;
                float2 blurp    : TEXCOORD1; // x = radius px, y = vibrancy
                float2 texuv    : TEXCOORD2; // sprite UV — alpha mask / 贴图 UV——alpha 遮罩
            };

            v2f vert(appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
            #if UNITY_UV_STARTS_AT_TOP
                float flip = -1.0;
            #else
                float flip = 1.0;
            #endif
                o.uvgrab.xy = (float2(o.vertex.x, o.vertex.y * flip) + o.vertex.w) * 0.5;
                o.uvgrab.zw = o.vertex.zw;
                o.color = v.color * _TintColor;
                o.blurp = v.texcoord.zw;
                o.texuv = v.texcoord.xy;
                return o;
            }

            // 16 taps on two rings (8 @ full radius, 8 @ half, golden-angle rotated) + the centre
            // tap = 0.2. Two rings keep banding acceptable at UI-blur radii while staying cheap
            // enough for a fullscreen-behind-every-key shader.
            // 双环 16 采样（全半径 8 + 半半径 8，黄金角错开）+ 中心 0.2。双环把 UI 模糊半径下的
            // 条带感压到可接受，同时保持足够便宜。
            static const float2 JKV_TAPS[16] =
            {
                float2( 1.00,  0.00), float2(-0.62,  0.79), float2(-0.22, -0.98), float2( 0.85, -0.53),
                float2(-0.98,  0.22), float2( 0.53,  0.85), float2( 0.79, -0.62), float2(-0.16,  0.99),
                float2( 0.50,  0.00), float2(-0.31,  0.39), float2(-0.11, -0.49), float2( 0.42, -0.27),
                float2(-0.49,  0.11), float2( 0.27,  0.42), float2( 0.39, -0.31), float2(-0.08,  0.49)
            };

            fixed4 frag(v2f i) : SV_Target
            {
                float radius = max(i.blurp.x, 1.0);
                float2 px = radius * _JKVGrabTexture_TexelSize.xy;
                float4 g = UNITY_PROJ_COORD(i.uvgrab);
                fixed4 sum = tex2Dproj(_JKVGrabTexture, g) * 0.2;
                [unroll]
                for (int k = 0; k < 16; k++)
                    sum += tex2Dproj(_JKVGrabTexture, float4(i.uvgrab.xy + JKV_TAPS[k] * px, g.z, g.w)) * 0.05;

                // Vibrancy: push saturation away from luminance. 1+v so 0 keeps the untouched
                // screen colour, matching the reference mod's default of ~0.3 meaning "slightly
                // more saturated than the raw frame". / 浓度：把饱和度从亮度推开。1+v 使 0 保持
                // 原屏颜色——对应参考模组默认 0.3 即"比原始画面略浓"。
                float luma = dot(sum.rgb, float3(0.299, 0.587, 0.114));
                sum.rgb = lerp(float3(luma, luma, luma), sum.rgb, 1.0 + max(i.blurp.y, 0.0));
                sum.rgb *= i.color.rgb;
                // Alpha = tint alpha × sprite alpha: the key background's rounded corners (baked
                // into the sprite's alpha) clip the blur window. The grab texture is fully
                // opaque, so without this the blur would be a square box under rounded art.
                // alpha = 染色 alpha × 贴图 alpha：按键背景的圆角（烙在贴图 alpha 里）裁剪出
                // 模糊窗口。抓取纹理完全不透明，没有这一项时圆角贴图下方会露出方形模糊块。
                sum.a = i.color.a * tex2D(_MainTex, i.texuv).a;
                return sum;
            }
            ENDCG
        }
    }
    Fallback "UI/Default"
}
