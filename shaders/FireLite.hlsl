// LXIUM FireLite: 법정 불(SH_Vefects_VFX_URP_Fire_Flames_01)의 가벼운 판. docs/ai/mahosaba-render.md C장
// build_firelite.py가 게임 셰이더의 속성·바인딩 틀에 이 코드만 넣어 번들을 만든다. 그래서 상수 버퍼 위치와
// 텍스처·샘플러 레지스터는 원본과 같아야 한다 (게임이 원본 메타데이터대로 값을 채움).
// 원본 대비: 텍스처 읽기 8회(깊이 포함) → 4회, 마스크가 빈 픽셀은 2회 뒤 중단. 깊이 페이드는 뺌.

// $Globals (b0). 원본 메타데이터: unity_MatrixVP 1248, _TimeParameters 304
cbuffer Globals : register(b0)
{
    float4 _g0[19];
    float4 _TimeParameters;
    float4 _g1[58];
    float4x4 unity_MatrixVP;
};

// UnityPerDraw (b1, 정점 셰이더)
cbuffer UnityPerDraw : register(b1)
{
    float4x4 unity_ObjectToWorld;
};

// UnityPerMaterial (b1, 픽셀 셰이더). 원본과 같은 176바이트 배치
cbuffer UnityPerMaterial : register(b1)
{
    float4 _ColorBottom;
    float4 _ColorTop;
    float4 _ColorGradientMask_ST;
    float2 _Mask_Scale;
    float2 _Noise_02_Scale;
    float2 _NoiseDistortion_Speed;
    float2 _NoiseDistortion_Scale;
    float2 _Noise_02_Speed;
    float2 _Noise_01_Scale;
    float2 _Noise_01_Speed;
    float2 _Mask_Offset;
    float _EmissiveIntensity;
    float _NoisesOpacityBoost;
    float _Noises_Multiply;
    float _Noises_Power;
    float _Mask_Multiply;
    float _pad132;
    float _Dissolve;
    float _DistortionMaskIntensity;
    float _DistortionAmount;
    float _WindSpeed;
    float2 _pad152;
    float2 _pad160;
    float _Mask_Power;
    float _DepthFade;
};

Texture2D _NoiseDistortion_Texture : register(t1);
Texture2D _Noise_01_Texture : register(t2);
Texture2D _Noise_02_Texture : register(t3);
Texture2D _Mask_Texture : register(t5);
SamplerState sampler_Mask : register(s2);
SamplerState sampler_NoiseDistortion : register(s4);
SamplerState sampler_Noise_01 : register(s5);
SamplerState sampler_Noise_02 : register(s6);

// 입력은 원본 정점 셰이더와 같은 순서·레지스터 (파티클 정점 스트림 바인딩을 그대로 씀)
struct VSIn
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 color : COLOR;
    float4 uv : TEXCOORD0;
};

struct V2F
{
    float4 pos : SV_POSITION;
    float4 color : COLOR;
    float2 uv : TEXCOORD0;
};

V2F vert(VSIn v)
{
    V2F o;
    float3 world = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1)).xyz;
    o.pos = mul(unity_MatrixVP, float4(world, 1));
    o.color = v.color;
    o.uv = v.uv.xy;
    return o;
}

// 원본의 _TextureSample2(T_VFX_Gradient_Up_Down_01, 클램프)는 v에만 따르는 S자 곡선이라 식으로 대신함 (오차 0.01 안팎)
float Gradient(float v)
{
    v = saturate(v);
    return lerp(v, v * v * (3 - 2 * v), 0.6);
}

float4 frag(V2F i) : SV_Target
{
    float t = _TimeParameters.x * _WindSpeed;
    float d = _NoiseDistortion_Texture.Sample(sampler_NoiseDistortion, i.uv * _NoiseDistortion_Scale + t * _NoiseDistortion_Speed).r
        * _DistortionAmount * 0.1;
    // 원본의 _DistortionMaskTexture는 비어 있어(흰색 = 1) 곱하지 않음
    float mask = saturate(pow(_Mask_Texture.Sample(sampler_Mask, i.uv * _Mask_Scale + _Mask_Offset + d * _DistortionMaskIntensity).r, _Mask_Power)
        * _Mask_Multiply);
    // 마스크가 0이면 알파·색이 모두 0이라 나머지를 건너뜀 (마스크 텍스처의 54%가 0)
    clip(mask - 1e-4);
    float n1 = _Noise_01_Texture.Sample(sampler_Noise_01, i.uv * _Noise_01_Scale + t * _Noise_01_Speed + d).r;
    float n2 = _Noise_02_Texture.Sample(sampler_Noise_02, i.uv * _Noise_02_Scale + t * _Noise_02_Speed + d).r;
    float noise = saturate(pow(n1 * n2, _Noises_Power) * _Noises_Multiply) * _NoisesOpacityBoost;
    float a = mask - noise * Gradient(i.uv.y + 0.4);
    // 원본의 _ColorGradientMask도 비어 있어(흰색) lerp(_ColorBottom, _ColorTop, 1) = _ColorTop
    float4 c;
    c.a = saturate(saturate(a - _Dissolve) * i.color.a);
    c.rgb = _ColorTop.rgb * i.color.rgb * a * _EmissiveIntensity;
    return c;
}
