// Geometria proxy del escenario: invisible salvo por la sombra que recibe.
//
// La idea es no redibujar el fondo. La camara 3D limpia solo el z-buffer, asi que cuando esta
// geometria se dibuja el framebuffer YA contiene la placa prerenderizada del juego. Multiplicando
// por la atenuacion de sombra, un pixel iluminado queda multiplicado por 1 -> identico al vanilla,
// bit a bit, sin proyectar ninguna textura ni casar espacios de color. Solo se oscurece lo que la
// sombra toca.
//
// El pase ForwardAdd de abajo NO suma luz: resta. Ninguna lampara puede aclarar el escenario -eso
// estropearia la placa prerenderizada, que ya trae su iluminacion pintada-, pero si puede oscurecer
// donde algo bloquea su luz. Es lo que hace que un foco proyecte la sombra del personaje.
//
// El pase ShadowCaster de abajo NO esta para proyectar sombra: esta para poder RECIBIRLA. La
// sombra direccional en forward es una sombra en espacio de pantalla, y Unity la resuelve leyendo
// _CameraDepthTexture, que construye con el pase ShadowCaster de cada objeto. Un shader sin ese
// pase no entra en la textura de profundidad, y entonces su pixel consulta la sombra a la
// profundidad del fondo: sale iluminado siempre. Para que la geometria no proyecte, el sitio es
// el desplegable "Cast Shadows > Off" del MeshRenderer, no quitar el pase.
//
// Cola Geometry-1 a proposito: el pase de profundidad tiene que escribirse ANTES que el personaje
// (cola Geometry) para que el personaje quede correctamente ocluido al pasar por detras.
Shader "Memoria/ShadowCatcher"
{
    Properties
    {
        _ShadowColor ("Color de sombra", Color) = (0.35, 0.36, 0.45, 1)
        _Strength ("Intensidad", Range(0,1)) = 1.0
        // Diagnostico del pase de luces adicionales, puesto por el mod desde la configuracion.
        // En reposo vale 0 y no hace nada. El pase oscurece por "reach * (1 - shadow)", y cuando no
        // se ve nada hay que saber CUAL de los dos terminos esta muerto, porque son fallos
        // distintos con arreglos distintos. Cada modo pinta uno a solas, en blanco y negro para que
        // se lea sobre cualquier fondo:
        //   1  rojo plano       -> el pase se ejecuta o no
        //   2  solo la sombra   -> negro donde la luz esta bloqueada. Todo blanco significa que el
        //                          shader no esta leyendo el mapa de sombras de esa lampara.
        //   3  solo el alcance  -> negro donde la lampara no llega. Todo negro significa que la
        //                          atenuacion es cero, y entonces el producto nunca puede oscurecer.
        //   4  el factor final -> negro donde el pase oscurece del todo, blanco donde no toca nada.
        //                          Es la sombra que se va a ver, sin el color ni la intensidad del
        //                          material encima, asi que separa "el shader no calcula" de "el
        //                          material la esta dejando en nada".
        _AddDebug ("Diagnostico del pase aditivo", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry-1" "IgnoreProjector"="True" }

        // 1) Solo profundidad. Es lo que hace que esta geometria ocluya al personaje.
        // Lleva programa propio aunque no pinte nada: Unity 5.2 rechaza un pase sin vertex shader
        // ("Pass '' has no vertex shader") y deja el shader entero como no soportado.
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

        // 2) La sombra, multiplicada sobre lo que ya hay pintado.
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            ZWrite Off
            ZTest LEqual
            Blend DstColor Zero
            // Aqui hubo un descarte por stencil con la silueta del personaje. Se quito: recortaba
            // incondicionalmente, sin mirar profundidad, asi que mordia la sombra del propio
            // personaje justo donde su silueta la tocaba en pantalla. La mascara de profundidad
            // hace el mismo trabajo y ademas bien: descarta solo las superficies que estan DETRAS
            // de el, que son las unicas que no deberian pintarle encima. Una superficie que este
            // de verdad delante tiene todo el derecho a oscurecer, y ahi el juego esta pintando
            // el fondo, no al personaje.

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

        // 3) La sombra de cada lampara adicional: focos y luces puntuales.
        //
        // Se ejecuta una vez por luz. Multiplica igual que el pase base, pero el factor no puede
        // ser la atenuacion a secas: fuera del alcance de la lampara la atenuacion es 0, y
        // oscurecer ahi apagaria medio mapa por poner un farol en una esquina. Lo que oscurece es
        // "cuanta luz de ESTA lampara llegaria aqui" multiplicado por "cuanta esta bloqueada", asi
        // que hacen falta las dos por separado y LIGHT_ATTENUATION las devuelve ya multiplicadas.
        // De ahi que la parte de distancia se calcule aparte, igual que hace AutoLight.cginc.
        //
        // Y esa parte tiene que llevar la INTENSIDAD de la lampara, no solo su caida. La caida de
        // Unity es brutal -a media distancia del alcance ya va por el 13%-, asi que un foco de
        // intensidad 3.5 que ilumina de sobra un plano Standard daba aqui un factor del 8%: una
        // sombra del 8% sobre un fondo prerenderizado no se ve. Justo el fallo que costo encontrar,
        // porque Unity proyectaba perfectamente y el shader leia bien el mapa de sombras; lo unico
        // que pasaba es que el resultado salia multiplicado por casi nada. _LightColor0 ya trae
        // color por intensidad, que es exactamente "cuanta luz llega".
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
            // Fuera las variantes de sombra de luz puntual, que son mapas de cubo y las mas caras
            // del conjunto. El shader del personaje ya se quedo sin fullshadows por no caber en
            // Direct3D 9; aqui no se puede renunciar a las sombras -son el objetivo del pase-, pero
            // si a las de point light, que valen bastante mas que las de foco. Un foco sigue
            // proyectando; una luz puntual ilumina pero no proyecta.
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

                // La caida de esta lampara en este pixel, sin mirar si algo la bloquea.
                #if defined(POINT) || defined(POINT_COOKIE)
                    fixed falloff = tex2D(_LightTexture0, dot(i._LightCoord, i._LightCoord).rr).UNITY_ATTEN_CHANNEL;
                #elif defined(SPOT)
                    fixed falloff = (i._LightCoord.z > 0) * UnitySpotCookie(i._LightCoord)
                                  * UnitySpotAttenuate(i._LightCoord.xyz);
                #else
                    fixed falloff = 1.0;
                #endif

                // Cuanta luz de esta lampara llegaria aqui: la caida POR la intensidad. Ver la nota
                // del pase; sin la intensidad el factor sale casi cero y no se oscurece nada.
                fixed3 arriving = _LightColor0.rgb * falloff;
                fixed reach = saturate(max(arriving.r, max(arriving.g, arriving.b)));

                // Diagnostico. Ver la nota de las propiedades: cada modo saca un termino a solas.
                // El pase multiplica, asi que un 1 deja el pixel como estaba y un 0 lo pone negro.
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

        // 4) Profundidad para el sistema de sombras. Ver la nota de la cabecera: sin este pase
        // el catcher no recibe nada. Que ademas proyecte o no se decide por objeto en el
        // MeshRenderer; las sombras del escenario ya estan pintadas en el fondo, asi que lo
        // normal es dejarlo en "Off" salvo que la direccional apunte igual que la luz pintada.
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

    // Plan B si el pase ForwardAdd no compila en esta plataforma: sin sombras de focos ni de
    // luces puntuales, pero con todo lo demas intacto. Unity se queda con el primer SubShader que
    // soporte, asi que una lampara de mas no puede tumbar el escenario entero.
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
