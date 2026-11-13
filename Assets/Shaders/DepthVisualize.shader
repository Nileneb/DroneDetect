Shader "DroneDetect/DepthVisualize"
{
    Properties
    {
        _MaxDepth ("Maximum Visualization Distance", Float) = 20
        _CropScale ("Centered Crop Scale", Vector) = (1, 1, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _CameraDepthTexture;
            float _MaxDepth;
            float2 _CropScale;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 depthUV = (i.uv - 0.5) * _CropScale + 0.5;
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, depthUV);
                float eyeDepth = LinearEyeDepth(rawDepth);
                float normalizedDepth = saturate(eyeDepth / max(_MaxDepth, 0.1));
                return fixed4(normalizedDepth, normalizedDepth, normalizedDepth, 1.0);
            }
            ENDCG
        }
    }
}
