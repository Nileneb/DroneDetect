Shader "DroneDetect/SentisModelInput"
{
    Properties
    {
        _CropScale ("Crop Scale", Vector) = (1, 1, 0, 0)
        _CropOffset ("Crop Offset", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float2 _CropScale;
            float2 _CropOffset;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = _CropOffset + i.uv * _CropScale;
                float3 rgb = tex2D(_MainTex, uv).rgb;
                return float4(rgb, 1.0);
            }
            ENDCG
        }
    }
}
