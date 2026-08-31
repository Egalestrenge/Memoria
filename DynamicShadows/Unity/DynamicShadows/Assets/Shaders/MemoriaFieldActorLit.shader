// Personaje del field dibujado en el pase 3D, con el MISMO color que el juego y una pizca de luz.
//
// #pragma target 3.0 no es opcional: el juego corre en Direct3D 9, donde Unity compila por defecto
// a shader model 2.0 y este pase no cabe en sus 64 instrucciones. Si falta, el shader viaja en el
// bundle pero llega con isSupported=false y el personaje deja hasta de proyectar sombra.
//
// La aritmetica de color esta copiada de PSX/FieldMapActor (StreamingAssets/Shaders/PSX), leida
// del ensamblador d3d9 que trae el juego:
//     vertex:    oD0 = color_vertice * _Color
//     fragment:  r0 = tex2D(_MainTex, uv) * oD0
//                texkill(r0.a - 0.5)          <- recorte alfa
//                rgb = 2 * r0.a * r0.rgb      <- modulate2x tipico de PSX, premultiplicado
//                Blend One OneMinusSrcAlpha
// Con _LightInfluence = 0 la salida es identica a la del juego. Subiendolo, el personaje empieza a
// responder a la direccional, a la ambiental y a las luces puntuales, sin dejar de ser el mismo
// dibujo plano de siempre.
//
// Diferencias deliberadas con el original: ZWrite On y cola Geometry, porque aqui el personaje
// comparte z-buffer real con la geometria proxy (modo PLAYER3D only) en vez de la profundidad
// falsa tipo OT del pase PSX.
Shader "Memoria/FieldActorLit"
{
    Properties
    {
        _MainTex ("Textura", 2D) = "white" {}
        _Color ("Tinte", Color) = (1,1,1,1)
        _LightInfluence ("Influencia de la luz", Range(0,1)) = 0.35
        _Wrap ("Suavizado del difuso", Range(0,1)) = 0.6
        // 15 = escribe RGBA, 0 = no escribe color pero si profundidad. Lo pone el mod: con 0 este
        // material se convierte en una mascara de profundidad con la silueta exacta del personaje,
        // recorte alfa incluido, y eso es lo que impide que el catcher le pinte sombras encima
        // cuando al personaje lo sigue dibujando el juego.
        _ColorMask ("Mascara de color", Float) = 15
        // Marca de stencil. En reposo es inerte (Ref 0, Comp Always, Pass Keep). El mod la pone en
        // Ref 1 / Replace cuando el personaje actua de mascara, y el shadow catcher se salta esos
        // pixeles. Hace falta stencil y no basta la profundidad: una mesa entre la camara y el
        // personaje esta legitimamente delante, gana el test de profundidad, y le pintaria su
        // sombra encima.
        _StencilRef ("Stencil ref", Float) = 0
        _StencilComp ("Stencil comp", Float) = 8
        _StencilOp ("Stencil op", Float) = 0

        // Modo modulacion. Con 1 el shader no dibuja al personaje: emite el FACTOR de iluminacion
        // y se mezcla multiplicando sobre lo que el juego ya pinto. Asi el personaje conserva su
        // color exacto y su oclusion contra el fondo -lo sigue dibujando el juego- y aun asi se
        // oscurece al entrar en sombra. Donde no pasa nada el factor vale 1 y el pixel no cambia.
        _Modulate ("Modular en vez de dibujar", Float) = 0
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
                // Difuso envolvente: un NdotL crudo deja la espalda a negro, que no pega con el
                // sombreado plano de los modelos del field.
                float ndl = saturate((dot(n, _WorldSpaceLightPos0.xyz) + _Wrap) / (1.0 + _Wrap));
                fixed atten = LIGHT_ATTENUATION(i);
                fixed3 lit = ShadeSH9(float4(n, 1)) + _LightColor0.rgb * ndl * atten;

                // 1 = como en el juego. Ajusta AMBIENT y la intensidad de la direccional para que
                // "lit" valga aproximadamente 1 en una zona bien iluminada; asi la influencia de la
                // luz solo se nota al entrar en sombra o al acercarse a una luz.
                fixed3 factor = lerp(fixed3(1,1,1), lit, _LightInfluence);

                // Modulando, la salida es el factor y la mezcla lo multiplica por lo que el juego
                // pinto. Con factor 1 el pixel queda intacto, que es la garantia de no degradar
                // el color: no depende de afinar ningun valor.
                if (_Modulate > 0.5)
                    return fixed4(factor, 1.0);
                return fixed4(psx * factor, c.a);
            }
            ENDCG
        }

        // Luces puntuales: lo que tine al personaje cuando pasa junto a una antorcha. Solo suma,
        // asi que con _LightInfluence a 0 no aporta nada.
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

        // La silueta que se proyecta. Repite el recorte alfa: sin el, las capas, el pelo y las
        // cintas -que son quads con textura calada- proyectarian rectangulos.
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

    // Plan B, por si el SubShader de arriba no compila en esta plataforma. Reproduce el color del
    // juego y nada mas: sin direccional, sin ambiental y sin luces puntuales, pero con el recorte
    // alfa y su pase de sombra, que es lo que hace falta para que el personaje se vea correcto y
    // proyecte una silueta correcta. Unity se queda con el primer SubShader que soporte, asi que
    // si el de arriba se cae no se pierde el modo "only" entero: solo se pierde la luz.
    //
    // Cabe de sobra en shader model 2.0: cinco instrucciones y una textura.
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
                // Este SubShader no calcula luz, asi que modular es no tocar nada: factor 1. Mas
                // vale eso que dibujar el personaje encima del que ya pinto el juego.
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
