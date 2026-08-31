// Proxy geometry for the scenery: invisible except for the shadow it receives.
//
// The idea is to never redraw the background. The 3D camera clears only the z-buffer, so by the
// time this geometry is drawn the framebuffer ALREADY holds the game's prerendered plate.
// Multiplying by the shadow attenuation, a lit pixel is multiplied by 1 -> identical to vanilla,
// bit for bit, without projecting any texture or matching colour spaces. Only what the shadow
// touches gets darker.
//
// The ForwardAdd pass below does NOT add light: it subtracts. No lamp may brighten the scenery
// -that would spoil the prerendered plate, which already has its lighting painted in- but it may
// darken where something blocks its light. That is what makes a spotlight cast the character's
// shadow.
//
// The ShadowCaster pass below is NOT there to cast a shadow: it is there to be able to RECEIVE
// one. Directional shadows in forward rendering are screen-space shadows, and Unity resolves them
// by reading _CameraDepthTexture, which it builds from each object's ShadowCaster pass. A shader
// without that pass never enters the depth texture, so its pixel queries the shadow at the
// background's depth and always comes out lit. To stop the geometry casting, the place to do it is
// the MeshRenderer's "Cast Shadows > Off" dropdown, not removing the pass.
//
// Queue Geometry-1 on purpose: the depth pass has to be written BEFORE the character (queue
// Geometry) so the character is correctly occluded when walking behind.
Shader "Memoria/ShadowCatcher"
{
    Properties
    {
        _ShadowColor ("Shadow colour", Color) = (0.35, 0.36, 0.45, 1)
        _Strength ("Strength", Range(0,1)) = 1.0
        // Additive-light pass diagnostics, set by the mod from the configuration file.
        // At rest it is 0 and does nothing. The pass darkens by "reach * (1 - shadow)", and when
        // nothing shows you need to know WHICH of the two terms is dead, because they are different
        // faults with different fixes. Each mode paints one of them alone, in black and white so it
        // reads over any background:
        //   1  flat red         -> whether the pass runs at all
        //   2  shadow only      -> black where the light is blocked. All white means the shader is
        //                          not reading that lamp's shadow map.
        //   3  reach only       -> black where the lamp does not arrive. All black means the
        //                          attenuation is zero, and then the product can never darken.
        //   4  the final factor -> black where the pass darkens fully, white where it touches
        //                          nothing. This is the shadow that will be seen, without the
        //                          material's colour and strength on top, so it separates "the
        //                          shader does not compute" from "the material is reducing it to
        //                          nothing".
        _AddDebug ("Additive pass diagnostics", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry-1" "IgnoreProjector"="True" }

        // 1) Depth only. This is what makes this geometry occlude the character.
        // It carries its own program even though it paints nothing: Unity 5.2 rejects a pass with
        // no vertex shader ("Pass '' has no vertex shader") and marks the whole shader unsupported.
        Pass
        {
            ZWrite On
            ColorMask 0

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 vert(float4 vertex : POSITION) : SV_POSITION
            {
                return mul(UNITY_MATRIX_MVP, vertex);
            }

            fixed4 frag() : SV_Target
            {
                return 0;
            }
            ENDCG
        }

        // 2) The shadow, multiplied over what is already painted.
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            ZWrite Off
            ZTest LEqual
            Blend DstColor Zero
            // There used to be a stencil discard here using the character silhouette. It was
            // removed: it cut unconditionally, without looking at depth, so it bit into the
            // character's own shadow exactly where their silhouette touched it on screen. The depth
            // mask does the same job and does it properly: it discards only the surfaces BEHIND
            // them, the only ones that should not paint over them. A surface that is genuinely in
            // front has every right to darken, and there the game is painting the background, not
            // the character.

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "AutoLight.cginc"

            fixed4 _ShadowColor;
            fixed _Strength;

            struct v2f
            {
                float4 pos : SV_POSITION;
                LIGHTING_COORDS(0,1)
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                o.pos = mul(UNITY_MATRIX_MVP, v.vertex);
                TRANSFER_VERTEX_TO_FRAGMENT(o);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // atten = 1 iluminado, 0 en sombra.
                fixed atten = LIGHT_ATTENUATION(i);
                fixed3 tint = lerp(_ShadowColor.rgb, fixed3(1,1,1), atten);
                return fixed4(lerp(fixed3(1,1,1), tint, _Strength), 1);
            }
            ENDCG
        }

        // 3) The shadow of each additional lamp: spot and point lights.
        //
        // Runs once per light. It multiplies just like the base pass, but the factor cannot be the
        // bare attenuation: outside the lamp's range the attenuation is 0, and darkening there would
        // black out half the map just for putting a lantern in a corner. What darkens is "how much
        // light from THIS lamp would arrive here" multiplied by "how much of it is blocked", so the
        // two are needed separately and LIGHT_ATTENUATION hands them back already multiplied
        // together. Hence the distance part is computed separately, the way AutoLight.cginc does.
        //
        // And that part has to carry the lamp's INTENSITY, not just its falloff. Unity's falloff is
        // brutal -at half the range it is already down to 13%- so a spotlight of intensity 3.5 that
        // lights a Standard plane perfectly well gave a factor of 8% here: an 8% shadow over a
        // prerendered background is invisible. Exactly the kind of fault that took a while to find,
        // because Unity was casting perfectly and the shader was reading the shadow map correctly;
        // the only problem was that the result came out multiplied by almost nothing. _LightColor0
        // already carries colour times intensity, which is precisely "how much light arrives".
        Pass
        {
            Tags { "LightMode"="ForwardAdd" }
            ZWrite Off
            ZTest LEqual
            Blend DstColor Zero

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fwdadd_fullshadows
            // Drop the point-light shadow variants, which are cube maps and the most expensive of
            // the set. The character shader already lost fullshadows for not fitting in Direct3D 9;
            // here the shadows cannot be given up -they are the whole point of the pass- but the
            // point-light ones can, and they cost considerably more than the spot ones. A spotlight
            // still casts; a point light lights but does not cast.
            #pragma skip_variants SHADOWS_CUBE POINT_COOKIE
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            fixed4 _ShadowColor;
            fixed _Strength;
            fixed _AddDebug;

            struct v2f
            {
                float4 pos : SV_POSITION;
                LIGHTING_COORDS(0,1)
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                o.pos = mul(UNITY_MATRIX_MVP, v.vertex);
                TRANSFER_VERTEX_TO_FRAGMENT(o);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed shadow = SHADOW_ATTENUATION(i);

                // This lamp's falloff at this pixel, without looking at whether anything blocks it.
                #if defined(POINT) || defined(POINT_COOKIE)
                    fixed falloff = tex2D(_LightTexture0, dot(i._LightCoord, i._LightCoord).rr).UNITY_ATTEN_CHANNEL;
                #elif defined(SPOT)
                    fixed falloff = (i._LightCoord.z > 0) * UnitySpotCookie(i._LightCoord)
                                  * UnitySpotAttenuate(i._LightCoord.xyz);
                #else
                    fixed falloff = 1.0;
                #endif

                // How much light from this lamp would arrive here: the falloff TIMES the intensity.
                // See the pass note; without the intensity the factor is near zero and nothing
                // darkens.
                fixed3 arriving = _LightColor0.rgb * falloff;
                fixed reach = saturate(max(arriving.r, max(arriving.g, arriving.b)));

                // Diagnostics. See the property note: each mode isolates one term. The pass
                // multiplies, so a 1 leaves the pixel as it was and a 0 turns it black.
                fixed blocked = saturate(reach * (1.0 - shadow)) * _Strength;

                if (_AddDebug > 3.5)
                    return fixed4(1.0 - blocked, 1.0 - blocked, 1.0 - blocked, 1.0);
                if (_AddDebug > 2.5)
                    return fixed4(reach, reach, reach, 1.0);
                if (_AddDebug > 1.5)
                    return fixed4(shadow, shadow, shadow, 1.0);
                if (_AddDebug > 0.5)
                    return fixed4(1.0, 0.3, 0.3, 1.0);

                return fixed4(lerp(fixed3(1, 1, 1), _ShadowColor.rgb, blocked), 1.0);
            }
            ENDCG
        }

        // 4) Depth for the shadow system. See the header note: without this pass the catcher
        // receives nothing. Whether it also casts is decided per object on the MeshRenderer; the
        // scenery's shadows are already painted into the background, so the normal setting is "Off"
        // unless the directional points the same way as the painted light.
        Pass
        {
            Tags { "LightMode"="ShadowCaster" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"

            struct v2f
            {
                V2F_SHADOW_CASTER;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                TRANSFER_SHADOW_CASTER(o)
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }
    }

    // Fallback in case the ForwardAdd pass does not compile on this platform: no spot or point
    // light shadows, but everything else intact. Unity keeps the first SubShader it supports, so
    // one lamp too many cannot bring the whole scenery down.
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry-1" "IgnoreProjector"="True" }

        Pass
        {
            ZWrite On
            ColorMask 0

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 vert(float4 vertex : POSITION) : SV_POSITION
            {
                return mul(UNITY_MATRIX_MVP, vertex);
            }

            fixed4 frag() : SV_Target
            {
                return 0;
            }
            ENDCG
        }

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            ZWrite Off
            ZTest LEqual
            Blend DstColor Zero

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "AutoLight.cginc"

            fixed4 _ShadowColor;
            fixed _Strength;

            struct v2f
            {
                float4 pos : SV_POSITION;
                LIGHTING_COORDS(0,1)
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                o.pos = mul(UNITY_MATRIX_MVP, v.vertex);
                TRANSFER_VERTEX_TO_FRAGMENT(o);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed atten = LIGHT_ATTENUATION(i);
                fixed3 tint = lerp(_ShadowColor.rgb, fixed3(1, 1, 1), atten);
                return fixed4(lerp(fixed3(1, 1, 1), tint, _Strength), 1);
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

            struct v2f
            {
                V2F_SHADOW_CASTER;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                TRANSFER_SHADOW_CASTER(o)
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }
    }

    Fallback Off
}
