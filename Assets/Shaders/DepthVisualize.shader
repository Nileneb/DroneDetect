Shader "DroneDetect/DepthVisualize"
{
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

            fixed4 frag(v2f_img i) : SV_Target
            {
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                float linearDepth = Linear01Depth(rawDepth);
                return fixed4(linearDepth, linearDepth, linearDepth, 1.0);
            }
            ENDCG
        }
    }
}
