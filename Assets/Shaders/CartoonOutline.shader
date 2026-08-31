Shader "Custom/CartoonOutline"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (0.08, 0.08, 0.08, 1.0)
        _OutlineThickness ("Outline Thickness", Range(0.5, 1.0)) = 0.5
        _DepthSensitivity ("Depth Sensitivity", Range(0.01, 5.0)) = 1.0
        _NormalsSensitivity ("Normals Sensitivity", Range(0.01, 5.0)) = 1.0
        _DepthThreshold ("Depth Threshold", Range(0.001, 0.5)) = 0.02
        _NormalsThreshold ("Normals Threshold", Range(0.01, 1.0)) = 0.3
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100
        ZWrite Off
        Cull Off
        ZTest Always

        Pass
        {
            Name "CartoonOutlinePass"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _OutlineColor;
                float _OutlineThickness;
                float _DepthSensitivity;
                float _NormalsSensitivity;
                float _DepthThreshold;
                float _NormalsThreshold;
            CBUFFER_END

            // Sample raw depth and convert safely
            float SampleLinearDepthSafe(float2 uv)
            {
                float rawDepth = SampleSceneDepth(uv);
                #if UNITY_REVERSED_Z
                if (rawDepth <= 0.00001) return _ProjectionParams.z;
                #else
                if (rawDepth >= 0.99999) return _ProjectionParams.z;
                #endif
                return LinearEyeDepth(rawDepth, _ZBufferParams);
            }

            // Sample world normal
            float3 SampleNormalSafe(float2 uv)
            {
                return SampleSceneNormals(uv);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half4 sceneColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv);

                float rawDepth = SampleSceneDepth(uv);
                #if UNITY_REVERSED_Z
                bool isSky = (rawDepth <= 0.00001);
                #else
                bool isSky = (rawDepth >= 0.99999);
                #endif

                float2 texelSize = _ScreenParams.zw - 1.0;
                float2 offset = texelSize * _OutlineThickness;

                // 4 diagonal sample points (Roberts cross)
                float2 uv00 = uv + float2(-offset.x, -offset.y);
                float2 uv11 = uv + float2( offset.x,  offset.y);
                float2 uv01 = uv + float2(-offset.x,  offset.y);
                float2 uv10 = uv + float2( offset.x, -offset.y);

                // Sample Depths
                float d00 = SampleLinearDepthSafe(uv00);
                float d11 = SampleLinearDepthSafe(uv11);
                float d01 = SampleLinearDepthSafe(uv01);
                float d10 = SampleLinearDepthSafe(uv10);

                // Depth difference (normalized by depth to keep line thickness consistent at distance)
                float centerDepth = SampleLinearDepthSafe(uv);
                float depthDiff1 = abs(d00 - d11) / max(0.001, centerDepth);
                float depthDiff2 = abs(d01 - d10) / max(0.001, centerDepth);
                float depthEdge = sqrt(depthDiff1 * depthDiff1 + depthDiff2 * depthDiff2);
                
                float isDepthEdge = depthEdge > _DepthThreshold ? 1.0 : 0.0;
                isDepthEdge *= saturate((depthEdge - _DepthThreshold) * _DepthSensitivity * 10.0);

                // Sample Normals
                float3 n00 = SampleNormalSafe(uv00);
                float3 n11 = SampleNormalSafe(uv11);
                float3 n01 = SampleNormalSafe(uv01);
                float3 n10 = SampleNormalSafe(uv10);

                // Normal difference
                float3 normalDiff1 = n00 - n11;
                float3 normalDiff2 = n01 - n10;
                float normalEdge = sqrt(dot(normalDiff1, normalDiff1) + dot(normalDiff2, normalDiff2));
                
                float isNormalEdge = normalEdge > _NormalsThreshold ? 1.0 : 0.0;
                isNormalEdge *= saturate((normalEdge - _NormalsThreshold) * _NormalsSensitivity * 5.0);

                // Combine edges
                float edge = saturate(isDepthEdge + isNormalEdge);

                // Don't draw outline on pure skybox unless bordering geometry
                if (isSky && edge < 0.5) edge = 0.0;

                // Blend outline with scene color
                half4 finalColor = lerp(sceneColor, _OutlineColor, edge * _OutlineColor.a);
                return finalColor;
            }
            ENDHLSL
        }
    }
}

