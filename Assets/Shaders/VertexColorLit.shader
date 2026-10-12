// Flat low-poly lighting for OSM world meshes: vertex colour * (main light + ambient), with shadows and fog.
Shader "LeGamez/VertexColorLit"
{
    Properties
    {
        _Tint ("Tint", Color) = (1,1,1,1)
        _ShadowStrength ("Shadow Strength", Range(0,1)) = 0.55
        [Toggle(_WATER_ANIM)] _WaterAnim ("Water Shimmer", Float) = 0
        [Toggle(_FACADE)] _Facade ("Procedural Facade Windows", Float) = 0
        _WindowColor ("Window Color", Color) = (0.20,0.26,0.33,1)
        _FloorHeight ("Floor Height", Float) = 3.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma shader_feature_local _WATER_ANIM
            #pragma shader_feature_local _FACADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                half _ShadowStrength;
                half4 _WindowColor;
                float _FloorHeight;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;
                float4 facade     : TEXCOORD0; // (u along wall, height, wall top, window span); y < 0 = none
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                half4  color      : COLOR;
                half   fogFactor  : TEXCOORD2;
                float4 facade     : TEXCOORD3;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.color = v.color * _Tint;
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                o.facade = v.facade;
                return o;
            }

            // Anti-aliased box: 1 inside [a, b], 0 outside, softened over one pixel.
            float AABox(float x, float a, float b)
            {
                float w = max(fwidth(x), 1e-4);
                return saturate((x - a) / w + 0.5) * saturate((b - x) / w + 0.5);
            }

            // Procedural window grid. Returns (window mask, frame mask, glass vertical gradient).
            float3 FacadeMask(float4 f)
            {
                if (f.y < 0) return 0;
                const float spacing = 2.6;
                float u = f.x, h = f.y, top = f.z, span = f.w;
                float floorIdx = floor(h / _FloorHeight);
                float fy = h - floorIdx * _FloorHeight;          // metres within the floor
                float fx = u - floor(u / spacing) * spacing;      // metres within the window bay
                // Ground floor of tall buildings: taller shop-style windows.
                bool shop = floorIdx < 0.5 && top > 7.0;
                float sill = shop ? 0.5 : 0.9;
                float head = shop ? 2.6 : 2.3;
                float halfW = shop ? 0.95 : 0.55;
                float inSpan = AABox(u, 0.0, span);
                float notTop = step(floorIdx * _FloorHeight + head + 0.3, top); // skip partial top floor
                float win = AABox(fx, spacing * 0.5 - halfW, spacing * 0.5 + halfW) * AABox(fy, sill, head);
                float frame = AABox(fx, spacing * 0.5 - halfW - 0.09, spacing * 0.5 + halfW + 0.09)
                            * AABox(fy, sill - 0.12, head + 0.06) - win;
                float k = inSpan * notTop;
                return float3(win * k, saturate(frame) * k, saturate((fy - sill) / (head - sill)));
            }

            half4 frag (Varyings i) : SV_Target
            {
                half3 n = normalize(i.normalWS);
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half shadow = lerp(1.0h, mainLight.shadowAttenuation, _ShadowStrength);
                half ndl = saturate(dot(n, mainLight.direction));
                // Wrapped diffuse keeps low-poly faces readable on the dark side.
                half diffuse = ndl * 0.75h + 0.25h;
                half3 ambient = SampleSH(n);
                half3 albedo = i.color.rgb;
                half3 extra = 0;
                #if defined(_FACADE)
                    if (abs(n.y) < 0.3)
                    {
                        float3 m = FacadeMask(i.facade);
                        // Floor band: thin ledge line at every storey.
                        float ledge = i.facade.y >= 0 ? AABox(frac(i.facade.y / _FloorHeight) * _FloorHeight, 0.0, 0.12) * step(_FloorHeight * 0.9, i.facade.y) : 0;
                        albedo = lerp(albedo, albedo * 0.82h, ledge);
                        albedo = lerp(albedo, min(albedo * 1.35h + 0.08h, 1.0h), m.y);
                        half3 glass = lerp(_WindowColor.rgb * 0.7h, _WindowColor.rgb * 1.6h + half3(0.05h, 0.07h, 0.1h), m.z);
                        albedo = lerp(albedo, glass, m.x);
                        // Glass reflects a bit of sky regardless of light direction.
                        extra += m.x * ambient * 0.25h;
                    }
                #endif
                #if defined(_WATER_ANIM)
                    half wave = sin(i.positionWS.x * 0.35 + _Time.y * 1.3) * sin(i.positionWS.z * 0.27 + _Time.y * 0.9);
                    albedo *= 1.0h + wave * 0.06h;
                    half3 viewDir = normalize(GetWorldSpaceViewDir(i.positionWS));
                    half3 waveN = normalize(half3(wave * 0.08h, 1.0h, cos(i.positionWS.x * 0.21 + _Time.y) * 0.06h));
                    half3 h = normalize(mainLight.direction + viewDir);
                    half spec = pow(saturate(dot(waveN, h)), 180.0h) * 1.2h * shadow;
                    half fresnel = pow(1.0h - saturate(dot(waveN, viewDir)), 4.0h);
                    extra += mainLight.color * spec + ambient * fresnel * 0.5h;
                #endif
                half3 color = albedo * (mainLight.color * diffuse * shadow + ambient * 0.6h) + extra;
                color = MixFog(color, i.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(v.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDir));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = positionCS;
                return o;
            }

            half4 frag (Varyings i) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert (Attributes v) { Varyings o; o.positionCS = TransformObjectToHClip(v.positionOS.xyz); return o; }
            half4 frag (Varyings i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
