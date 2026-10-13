// Stylised sky: zenith-to-horizon gradient, soft sun with halo and a slowly drifting layer of puffy clouds
// (thresholded value noise on a virtual cloud plane) – flat-shaded look to match the low-poly world.
Shader "LeGamez/Sky"
{
    Properties
    {
        _ZenithColor ("Zenith", Color) = (0.33, 0.52, 0.80, 1)
        _HorizonColor ("Horizon", Color) = (0.74, 0.82, 0.90, 1)
        _GroundColor ("Below Horizon", Color) = (0.74, 0.82, 0.90, 1)
        _SunColor ("Sun", Color) = (1, 0.95, 0.82, 1)
        _SunSize ("Sun Size", Range(0.001, 0.1)) = 0.03
        _CloudColor ("Cloud", Color) = (1, 1, 1, 1)
        _CloudShade ("Cloud Shadow", Color) = (0.72, 0.77, 0.86, 1)
        _CloudCover ("Cloud Cover", Range(0, 1)) = 0.45
        _CloudScale ("Cloud Scale", Float) = 1.6
        _CloudSpeed ("Cloud Speed", Float) = 0.004
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ZenithColor, _HorizonColor, _GroundColor, _SunColor, _CloudColor, _CloudShade;
                half _SunSize, _CloudCover;
                float _CloudScale, _CloudSpeed;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.dir = v.positionOS.xyz;
                return o;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x), lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), f.x), f.y);
            }

            float Fbm(float2 p)
            {
                float v = 0, a = 0.55;
                for (int k = 0; k < 4; k++) { v += Noise(p) * a; p = p * 2.07 + 13.1; a *= 0.48; }
                return v;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float up = d.y;
                // Gradient: quick fade from the hazy horizon into a deeper blue zenith.
                half t = saturate(pow(saturate(up), 0.45));
                half3 col = up >= 0 ? lerp(_HorizonColor.rgb, _ZenithColor.rgb, t) : _GroundColor.rgb;

                Light sun = GetMainLight();
                float sd = dot(d, sun.direction);
                // Halo and disc.
                col += _SunColor.rgb * pow(saturate(sd), 64) * 0.35h;
                col = lerp(col, _SunColor.rgb * 1.6h, smoothstep(1 - _SunSize * 0.02, 1 - _SunSize * 0.012, sd));

                if (up > 0.02)
                {
                    // Project onto a cloud plane; clouds thin out towards the horizon.
                    float2 uv = d.xz / (up + 0.12) * _CloudScale + _Time.y * _CloudSpeed * float2(1, 0.35);
                    float n = Fbm(uv);
                    float cover = 1 - _CloudCover;
                    float edge = max(fwidth(n), 0.01);
                    float mask = smoothstep(cover - edge, cover + edge * 2, n);
                    // Two-tone flat shading: brighter where the noise climbs towards the sun side.
                    float lit = smoothstep(-0.02, 0.06, Fbm(uv + sun.direction.xz * 0.09) - n + 0.03);
                    half3 cloud = lerp(_CloudShade.rgb, _CloudColor.rgb, lit);
                    cloud += _SunColor.rgb * pow(saturate(sd), 8) * 0.15h;
                    mask *= saturate((up - 0.02) * 6);
                    col = lerp(col, cloud, mask * 0.95);
                }
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
