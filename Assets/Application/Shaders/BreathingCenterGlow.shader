Shader "Custom/BreathingCenterGlow"
{
    Properties
    {
        [HDR] _GlowColor ("Glow Color", Color) = (0, 1, 1, 1) // 빛의 색상 (HDR 추천)
        _MinFocus ("Max Size (Min Power)", Range(0.5, 5.0)) = 1.0   // 빛이 가장 커졌을 때의 퍼짐 정도
        _MaxFocus ("Min Size (Max Power)", Range(1.0, 10.0)) = 6.0  // 빛이 가장 작아졌을 때의 뭉침 정도
        _BreathSpeed ("Breathing Speed", Range(0.1, 5.0)) = 2.0     // 깜빡이는 속도
    }
    SubShader
    {
        // 투명한 물체로 설정 (가장자리를 옅게 하기 위함)
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 200

        CGPROGRAM
        // alpha:fade 옵션을 추가하여 투명도 조절이 가능하게 함
        #pragma surface surf Standard alpha:fade

        struct Input
        {
            float3 viewDir;
        };

        fixed4 _GlowColor;
        half _MinFocus;
        half _MaxFocus;
        half _BreathSpeed;

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // 1. 시간(Time)에 따른 사인파 계산 (Breathing 효과의 핵심)
            // _Time.y는 시간을 의미, sin 함수는 -1 ~ 1을 오가므로
            // 이를 0 ~ 1 사이의 값으로 변환하여 부드럽게 반복되게 함
            float breath = (sin(_Time.y * _BreathSpeed) + 1.0) * 0.5;

            // 2. 빛의 집중도(Power)를 시간에 따라 변화시킴
            // lerp 함수를 통해 Min값과 Max값 사이를 계속 오르내림
            float currentPower = lerp(_MinFocus, _MaxFocus, breath);

            // 3. 중앙 발광 계산 (N dot V)
            // 중앙일수록 1, 가장자리일수록 0
            float NdotV = saturate(dot(normalize(IN.viewDir), o.Normal));

            // 4. 계산된 Power 적용
            // Power 값이 변하면서 빛의 영역이 커졌다 작아졌다 함
            float glow = pow(NdotV, currentPower);

            // 5. 색상 및 투명도 적용
            o.Emission = _GlowColor.rgb * glow; // 발광
            o.Albedo = fixed3(0,0,0);           // 기본 색은 검정(빛만 보이게)
            o.Alpha = glow * _GlowColor.a;      // 가장자리로 갈수록 투명해짐
        }
        ENDCG
    }
    FallBack "Diffuse"
}