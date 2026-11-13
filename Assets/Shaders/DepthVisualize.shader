Shader "DroneDetect/DepthVisualize"
{
    Properties
    {
        _MaxDepth ("Maximum Visualization Distance", Float) = 20
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

            fixed4 frag(v2f_img i) : SV_Target
            {
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                float eyeDepth = LinearEyeDepth(rawDepth);
                float normalizedDepth = saturate(eyeDepth / max(_MaxDepth, 0.1));
                return fixed4(normalizedDepth, normalizedDepth, normalizedDepth, 1.0);
            }
            ENDCG
        }
    }
}
