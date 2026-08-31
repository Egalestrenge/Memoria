// The field character drawn in the 3D pass, with the SAME colour as the game plus a touch of light.
//
// #pragma target 3.0 is not optional: the game runs on Direct3D 9, where Unity compiles to shader
// model 2.0 by default and this pass does not fit in its 64 instructions. Without it the shader
// still travels in the bundle but arrives with isSupported=false, and the character stops even
// casting a shadow.
//
// The colour arithmetic is copied from PSX/FieldMapActor (StreamingAssets/Shaders/PSX), read off
// the d3d9 assembly shipped with the game:
//     vertex:    oD0 = color_vertice * _Color
//     fragment:  r0 = tex2D(_MainTex, uv) * oD0
//                texkill(r0.a - 0.5)          <- alpha cutout
//                rgb = 2 * r0.a * r0.rgb      <- the usual PSX modulate2x, premultiplied
//                Blend One OneMinusSrcAlpha
// With _LightInfluence = 0 the output is identical to the game's. Raising it, the character starts
// responding to the directional, to ambient and to point lights, while still being the same flat
// drawing as always.
//
// Deliberate differences from the original: ZWrite On and queue Geometry, because here the
// character shares a real z-buffer with the proxy geometry (PLAYER3D only mode) instead of the
// PSX pass's fake OT-style depth.
Shader "Memoria/FieldActorLit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _LightInfluence ("Light influence", Range(0,1)) = 0.35
        _Wrap ("Diffuse wrap", Range(0,1)) = 0.6
        // 15 = writes RGBA, 0 = writes no colour but does write depth. Set by the mod: with 0 this
        // material becomes a depth mask with the character's exact silhouette, alpha cutout
        // included, and that is what stops the catcher painting shadows over them while the
        // character is still being drawn by the game.
        _ColorMask ("Colour mask", Float) = 15
        // Stencil mark. At rest it is inert (Ref 0, Comp Always, Pass Keep). The mod sets it to
        // Ref 1 / Replace when the character acts as a mask, and the shadow catcher then skips those
        // pixels. Stencil is needed and depth is not enough: a table between the camera and the
        // character is legitimately in front, wins the depth test, and would paint its shadow over
        // them.
        _StencilRef ("Stencil ref", Float) = 0
        _StencilComp ("Stencil comp", Float) = 8
        _StencilOp ("Stencil op", Float) = 0

        // Modulation mode. With 1 the shader does not draw the character: it emits the lighting
        // FACTOR and blends by multiplying over what the game already painted. That way the
        // character keeps their exact colour and their occlusion against the background -the game
        // still draws them- and yet darkens on entering shadow. Where nothing happens the factor is
        // 1 and the pixel is unchanged.
        _Modulate ("Modulate instead of drawing", Float) = 0
        _SrcBlend ("Src blend", Float) = 1
        _DstBlend ("Dst blend", Float) = 10
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "IgnoreProjector"="True" }

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            ZWrite On
            ColorMask [_ColorMask]
            Blend [_SrcBlend] [_DstBlend]
            Stencil { Ref [_StencilRef] Comp [_StencilComp] Pass [_StencilOp] }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed _LightInfluence;
            fixed _Wrap;
            fixed _Modulate;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                float3 normal : TEXCOORD1;
                LIGHTING_COORDS(2,3)
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                o.pos = mul(UNITY_MATRIX_MVP, v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.color = v.color * _Color;
                o.normal = UnityObjectToWorldNormal(v.normal);
                TRANSFER_VERTEX_TO_FRAGMENT(o);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                clip(c.a - 0.5);
                fixed3 psx = 2.0 * c.a * c.rgb;

                float3 n = normalize(i.normal);
                // Wrapped diffuse: a raw NdotL leaves the back pitch black, which does not suit the
                // flat shading of the field models.
                float ndl = saturate((dot(n, _WorldSpaceLightPos0.xyz) + _Wrap) / (1.0 + _Wrap));
                fixed atten = LIGHT_ATTENUATION(i);
                fixed3 lit = ShadeSH9(float4(n, 1)) + _LightColor0.rgb * ndl * atten;

                // 1 = as in the game. Tune AMBIENT and the directional intensity so that "lit" is
                // roughly 1 in a well-lit area; then the light influence is only noticeable on
                // entering shadow or walking up to a light.
                fixed3 factor = lerp(fixed3(1,1,1), lit, _LightInfluence);

                // When modulating, the output is the factor and the blend multiplies it by what
                // the game painted. With a factor of 1 the pixel is untouched, which is the
                // guarantee that colour is never degraded: it does not depend on tuning anything.
                if (_Modulate > 0.5)
                    return fixed4(factor, 1.0);
                return fixed4(psx * factor, c.a);
            }
            ENDCG
        }

        // Point lights: what tints the character when they walk past a torch. It only adds, so
        // with _LightInfluence at 0 it contributes nothing.
        Pass
        {
            Tags { "LightMode"="ForwardAdd" }
            ZWrite Off
            ZTest LEqual
            ColorMask [_ColorMask]
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fwdadd
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed _LightInfluence;
            fixed _Wrap;
            fixed _Modulate;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                float3 normal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                LIGHTING_COORDS(3,4)
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                o.pos = mul(UNITY_MATRIX_MVP, v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.color = v.color * _Color;
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(_Object2World, v.vertex).xyz;
                TRANSFER_VERTEX_TO_FRAGMENT(o);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                clip(c.a - 0.5);
                fixed3 psx = 2.0 * c.a * c.rgb;

                float3 n = normalize(i.normal);
                float3 l = normalize(_WorldSpaceLightPos0.xyz - i.worldPos * _WorldSpaceLightPos0.w);
                float ndl = saturate((dot(n, l) + _Wrap) / (1.0 + _Wrap));
                fixed atten = LIGHT_ATTENUATION(i);
                return fixed4(psx * _LightColor0.rgb * ndl * atten * _LightInfluence, 0);
            }
            ENDCG
        }

        // The silhouette that gets cast. It repeats the alpha cutout: without it capes, hair and
        // ribbons -which are quads with a cut-out texture- would cast rectangles.
        Pass
        {
            Tags { "LightMode"="ShadowCaster" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            struct v2f
            {
                V2F_SHADOW_CASTER;
                float2 uv : TEXCOORD1;
                fixed4 color : COLOR;
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                TRANSFER_SHADOW_CASTER(o)
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.color = v.color * _Color;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                clip(c.a - 0.5);
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }
    }

    // Fallback in case the SubShader above does not compile on this platform. It reproduces the
    // game's colour and nothing else: no directional, no ambient and no point lights, but with the
    // alpha cutout and its shadow pass, which is what is needed for the character to look right and
    // cast a correct silhouette. Unity keeps the first SubShader it supports, so if the one above
    // falls over the whole "only" mode is not lost: only the lighting is.
    //
    // It fits comfortably in shader model 2.0: five instructions and one texture.
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "IgnoreProjector"="True" }

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            ZWrite On
            ColorMask [_ColorMask]
            Blend [_SrcBlend] [_DstBlend]
            Stencil { Ref [_StencilRef] Comp [_StencilComp] Pass [_StencilOp] }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed _Modulate;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                o.pos = mul(UNITY_MATRIX_MVP, v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                clip(c.a - 0.5);
                // This SubShader computes no lighting, so modulating means touching nothing:
                // factor 1. Better that than drawing the character over the one the game painted.
                if (_Modulate > 0.5)
                    return fixed4(1.0, 1.0, 1.0, 1.0);
                return fixed4(2.0 * c.a * c.rgb, c.a);
            }
            ENDCG
        }

        Pass
        {
            Tags { "LightMode"="ShadowCaster" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            struct v2f
            {
                V2F_SHADOW_CASTER;
                float2 uv : TEXCOORD1;
                fixed4 color : COLOR;
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                TRANSFER_SHADOW_CASTER(o)
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.color = v.color * _Color;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                clip(c.a - 0.5);
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }
    }

    Fallback Off
}
