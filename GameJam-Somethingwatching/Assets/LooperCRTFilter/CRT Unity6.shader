Shader "Looper/CRT Unity6"
{
    Properties
    {
        _Blur_Offset("Blur Offset", Float) = 0.002
        _Scan_Line_Number("Scan Line Number", Float) = 200
        _Scan_Line_Speed("Scan Line Speed", Float) = 0.01
        [HDR] _Color("Color", Color) = (1,1,1,1)
        _Brightness("Brightness", Range(0,4)) = 1
        _ScanlineStrength("Scanline Strength", Range(0,1)) = 0.4
        _BlackLineWidth("Black Line Width", Range(0,1)) = 0.35
        _Alpha("Alpha", Range(0,1)) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "CRT Fullscreen"
            ZWrite Off ZTest Always Cull Off Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            CBUFFER_START(UnityPerMaterial)
            float _Blur_Offset, _Scan_Line_Number, _Scan_Line_Speed, _Alpha;
            float _Brightness, _ScanlineStrength, _BlackLineWidth;
            float4 _Color;
            CBUFFER_END
            float3 ReadColor(float2 uv)
            {
                // Clamp the source coordinates at the border instead of loading outside the texture.
                return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, saturate(uv), 0).rgb;
            }
            float Scanlines(float2 uv)
            {
                // Evaluate horizontal scanlines directly. A checkerboard with zero X
                // tiling lies exactly on a cell boundary and loses contrast when its
                // zero derivative is protected against division by zero.
                float p = (uv.y + _Time.y * _Scan_Line_Speed + .5) * _Scan_Line_Number;
                float width = saturate(_BlackLineWidth);
                float coverage;
                if (width <= 0) coverage = 0;
                else if (width >= 1) coverage = 1;
                else
                {
                    float edgeDistance = abs(frac(p) - .5);
                    float aa = max(fwidth(p) * .5, 1e-5);
                    coverage = 1 - smoothstep(width * .5 - aa, width * .5 + aa, edgeDistance);
                    // Unresolvable stripes converge to their average coverage.
                    coverage = lerp(coverage, width, saturate(fwidth(p) - .5));
                }
                return 1 - saturate(_ScanlineStrength) * coverage;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float d = _Blur_Offset;
                // Reproduce the four offsets, channel tints and average in the source graph.
                float3 color = ReadColor(uv + float2(d,0)) * float3(1,0,0);
                color += ReadColor(uv + float2(0,d));
                color += ReadColor(uv + float2(-d,0)) * float3(0,.020782470703125,1);
                color += ReadColor(uv + float2(0,-d));
                return half4(color * .25 * _Color.rgb * _Brightness * Scanlines(uv), _Alpha);
            }
            ENDHLSL
        }
    }
}


