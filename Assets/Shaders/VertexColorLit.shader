// Flat low-poly lighting for OSM world meshes: vertex colour * (main light + ambient), with shadows and fog.
Shader "LeGamez/VertexColorLit"
{
    Properties
    {
        _Tint ("Tint", Color) = (1,1,1,1)
        _ShadowStrength ("Shadow Strength", Range(0,1)) = 0.68
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
            // Trees/bushes/lamps are drawn with Graphics.RenderMeshInstanced (World/InstancedProps).
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                half _ShadowStrength;
                half4 _WindowColor;
                float _FloorHeight;
            CBUFFER_END

            #if defined(UNITY_INSTANCING_ENABLED)
            // Per-instance colour: instance colour / prototype reference colour (InstancePrototypes.TintOf).
            UNITY_INSTANCING_BUFFER_START(PerInstance)
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstanceTint)
            UNITY_INSTANCING_BUFFER_END(PerInstance)
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;
                float4 facade     : TEXCOORD0; // (u along wall, height, wall top, window span); y < 0 = none
                UNITY_VERTEX_INPUT_INSTANCE_ID
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
                UNITY_SETUP_INSTANCE_ID(v);
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.color = v.color * _Tint;
                #if defined(UNITY_INSTANCING_ENABLED)
                    o.color.rgb *= (half3)UNITY_ACCESS_INSTANCED_PROP(PerInstance, _InstanceTint).rgb;
                #endif
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

            float Hash21(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

            // Procedural window grid. Returns (window mask, frame mask, glass gradient, mullion mask);
            // reveal = shadowed inner edge of the opening (fake depth), id = per-window random.
            float4 FacadeMask(float4 f, out float reveal, out float id, out float fyOut)
            {
                reveal = 0; id = 0; fyOut = 0;
                if (f.y < 0) return 0;
                const float spacing = 2.6;
                float u = f.x, h = f.y, top = f.z, span = f.w;
                float floorIdx = floor(h / _FloorHeight);
                float fy = h - floorIdx * _FloorHeight;          // metres within the floor
                float fx = u - floor(u / spacing) * spacing;      // metres within the window bay
                // Ground floor of tall buildings: taller shop-style windows.
                bool shop = floorIdx < 0.5 && top > 7.0;
                float sill = shop ? 0.5 : 0.9;
                float head = shop ? 2.6 : 2.25;
                float halfW = shop ? 0.95 : 0.52;
                float c = spacing * 0.5;
                float inSpan = AABox(u, 0.0, span);
                float notTop = step(floorIdx * _FloorHeight + head + 0.6, top); // top floor: lintel ≥ 0.6 m under the eaves
                float win = AABox(fx, c - halfW, c + halfW) * AABox(fy, sill, head);
                float frame = AABox(fx, c - halfW - 0.08, c + halfW + 0.08) * AABox(fy, sill - 0.1, head + 0.06) - win;
                // Sprossen: white cross bars in residential windows (not in shop fronts).
                float mullion = shop ? 0 : saturate(AABox(fx, c - 0.03, c + 0.03) + AABox(fy, sill + (head - sill) * 0.62 - 0.03, sill + (head - sill) * 0.62 + 0.03)) * win;
                float k = inSpan * notTop;
                // The opening sits ~12 cm deep: lintel and one jamb throw a shadow band onto the glass.
                reveal = saturate(AABox(fy, head - 0.13, head) + AABox(fx, c - halfW, c - halfW + 0.1)) * win * k * (1 - mullion);
                id = Hash21(float2(floor(u / spacing), floorIdx + top * 7.0));
                fyOut = (fy - sill) / (head - sill);
                return float4(win * k, saturate(frame) * k, saturate((fy - sill) / (head - sill)), mullion * k);
            }

            // Smooth value noise for large-scale colour variation on the ground.
            float ValueNoise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3 - 2 * f);
                float a = Hash21(i), b = Hash21(i + float2(1, 0)), c = Hash21(i + float2(0, 1)), d = Hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // Running-bond Klinker. Returns albedo multiplier (mortar lighter, per-brick variation).
            half3 Brick(half3 albedo, float u, float h)
            {
                const float bw = 0.25, bh = 0.075, joint = 0.012;
                float row = floor(h / bh);
                float bx = u / bw + (fmod(row, 2) * 0.5);
                float id = Hash21(float2(floor(bx), row));
                float jx = (frac(bx) * bw), jy = h - row * bh;
                float mortar = 1 - AABox(jx, joint, bw) * AABox(jy, joint, bh);
                half3 brick = albedo * (0.9h + 0.2h * id);
                half3 mortarCol = lerp(albedo, half3(0.78h, 0.76h, 0.72h), 0.75h);
                half3 c = lerp(brick, mortarCol, mortar);
                // Fade to the average colour once bricks get smaller than a few pixels.
                float fade = saturate(1 - fwidth(h) / 0.025);
                return lerp(albedo * 1.02h, c, fade);
            }

            // Paving stones (plazas, pedestrian streets): stretcher bond in world XZ, per-stone tint.
            half3 Paving(half3 albedo, float2 xz)
            {
                const float sw = 0.6, sh = 0.3, joint = 0.02;
                float row = floor(xz.y / sh);
                float sx = xz.x / sw + fmod(row, 3) * 0.33;
                float id = Hash21(float2(floor(sx), row));
                float jx = frac(sx) * sw, jy = xz.y - row * sh;
                float stone = AABox(jx, joint, sw) * AABox(jy, joint, sh);
                half3 c = lerp(albedo * 0.72h, albedo * (0.9h + 0.2h * id), stone);
                float fade = saturate(1 - max(fwidth(xz.x), fwidth(xz.y)) / 0.05);
                return lerp(albedo * 0.95h, c, fade);
            }

            // Pantile roof: rows with shaded lower lip, wavy tiles, per-tile variation.
            half3 RoofTiles(half3 albedo, float u, float v)
            {
                const float rowH = 0.32, tileW = 0.28;
                float row = floor(v / rowH);
                float rf = v / rowH - row;
                float col = u / tileW + fmod(row, 2) * 0.5;
                float id = Hash21(float2(floor(col), row));
                half lip = lerp(0.72h, 1.05h, smoothstep(0.0, 0.3, rf));
                half wave = 0.94h + 0.08h * cos(frac(col) * 6.2831);
                half3 c = albedo * lip * wave * (0.92h + 0.16h * id);
                float fade = saturate(1 - fwidth(v) / 0.08);
                return lerp(albedo * 0.95h, c, fade);
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
                    // Surface material code in vertex alpha (see Lod2Builder): 230 brick, 200 plaster, 120 roof tiles.
                    half code = i.color.a * 255.0h;
                    if (abs(n.y) < 0.3)
                    {
                        if (i.facade.y >= 0)
                        {
                            if (code > 215.0h && code < 245.0h) albedo = Brick(albedo, i.facade.x, i.facade.y);
                            else if (code > 185.0h && code < 215.0h) albedo *= lerp(0.88h, 1.0h, saturate(i.facade.y * 0.6)); // plaster, weathered base
                            if (code > 185.0h && code < 245.0h)
                            {
                                // Sockel: darker plinth band; plaster houses get a grey one.
                                float plinth = AABox(i.facade.y, -1.0, 0.45);
                                half3 plinthCol = code < 215.0h ? half3(0.52h, 0.52h, 0.5h) : albedo * 0.68h;
                                albedo = lerp(albedo, plinthCol, plinth);
                                // Traufgesims: shadowed band right under the eave.
                                albedo *= 1.0h - 0.18h * AABox(i.facade.y, i.facade.z - 0.28, i.facade.z + 0.5) * step(2.5, i.facade.z);
                            }
                        }
                        float reveal, wid, wfy;
                        float4 m = FacadeMask(i.facade, reveal, wid, wfy);
                        // Floor band: thin ledge line at every storey (plaster facades only read well with it).
                        float ledge = (i.facade.y >= 0 && code < 215.0h) ? AABox(frac(i.facade.y / _FloorHeight) * _FloorHeight, 0.0, 0.12) * step(_FloorHeight * 0.9, i.facade.y) : 0;
                        albedo = lerp(albedo, albedo * 0.85h, ledge);
                        half3 frameCol = half3(0.93h, 0.92h, 0.88h); // white window frames
                        albedo = lerp(albedo, frameCol, m.y);
                        half3 glass = lerp(_WindowColor.rgb * 0.6h, _WindowColor.rgb * 1.5h + half3(0.05h, 0.07h, 0.1h), m.z);
                        glass *= 0.85h + 0.3h * wid;
                        // Some windows have net curtains / blinds in the upper part.
                        half curtain = step(0.72, wid) * AABox(wfy, 0.55 + (wid - 0.72) * 1.2, 1.1);
                        glass = lerp(glass, half3(0.78h, 0.75h, 0.68h) * 0.85h, curtain);
                        glass = lerp(glass, glass * 0.4h, reveal);
                        albedo = lerp(albedo, glass, m.x);
                        albedo = lerp(albedo, frameCol, m.w);
                        // Glass reflects a bit of sky regardless of light direction.
                        extra += (m.x - m.w) * (1 - curtain) * (1 - reveal) * ambient * 0.25h;
                    }
                    else if (code > 100.0h && code < 140.0h)
                    {
                        albedo = RoofTiles(albedo, i.facade.x, i.facade.y);
                    }
                    else if (code > 75.0h && code < 105.0h && n.y > 0.9)
                    {
                        albedo = Paving(albedo, i.positionWS.xz);
                    }
                    if (n.y > 0.5 && !(code > 100.0h && code < 140.0h))
                    {
                        // Ground/flat roofs: patchy large-scale variation plus fine mottling (faded out with distance).
                        float2 xz = i.positionWS.xz;
                        half large = ValueNoise(xz * 0.07) * 0.6h + ValueNoise(xz * 0.21 + 17.0) * 0.4h;
                        half fine = ValueNoise(xz * 1.7 + 5.0);
                        half fineFade = saturate(1 - max(fwidth(xz.x), fwidth(xz.y)) / 0.35);
                        albedo *= 0.86h + 0.28h * large + (fine - 0.5h) * 0.12h * fineFade;
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
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
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
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert (Attributes v) { Varyings o; UNITY_SETUP_INSTANCE_ID(v); o.positionCS = TransformObjectToHClip(v.positionOS.xyz); return o; }
            half4 frag (Varyings i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
