// LXIUM CourtLite: 법정 재질(URP Lit, ForwardLit 패스)의 가벼운 판. docs/ai/mahosaba-render.md C장
// build_shaders.py가 게임의 URP Lit 셰이더 틀에 이 코드만 넣는다. 엔진이 원본 메타데이터대로 값을 채우므로
// 상수 버퍼 오프셋과 텍스처·샘플러 레지스터는 원본 변형과 같아야 한다.
//   CL_VS                정점 셰이더 (원본 _NORMALMAP 변형의 바인딩)
//   CL_PS + CL_MAPS      바닥·계단: 노멀·메탈릭·오클루전 맵 (원본 _NORMALMAP _METALLICSPECGLOSSMAP _OCCLUSIONMAP 변형)
//   CL_PS + CL_CUTOUT    벽: 알파 테스트 (원본 _ALPHATEST_ON 변형)
//   CL_PS + CL_PREMUL    단상: 투명 premultiply (원본 _SURFACE_TYPE_TRANSPARENT _ALPHAPREMULTIPLY_ON 변형)
// 원본에서 뺀 것: 메인 라이트·추가 라이트 그림자, SSAO, 반사 프로브 블렌딩·박스 투영, 라이트 쿠키·레이어, 메인 라이트.
// 법정에는 방향광·그림자·쿠키·반사 프로브가 없어(mahosaba.md 6.2) 빠진 항은 원본에서도 0이거나 1이다.
// 이 게임은 감마 색공간이라 URP처럼 SH만 sRGB로 바꾼다.

struct VSIn
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 tangent : TANGENT;
    float2 uv0 : TEXCOORD0;
    float2 uv1 : TEXCOORD1;
    float2 uv2 : TEXCOORD2;
};

struct V2F
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 positionWS : TEXCOORD1;
    float3 normalWS : TEXCOORD2;
    float4 tangentWS : TEXCOORD3;
};

#if defined(CL_VS)

cbuffer Globals : register(b0)
{
    float4 _vg0[78];
    float4x4 unity_MatrixVP;            // 1248
};

cbuffer UnityPerDraw : register(b1)
{
    float4x4 unity_ObjectToWorld;       // 0
    float4x4 unity_WorldToObject;       // 64
    float4 _vd0;
    float4 unity_WorldTransformParams;  // 144
};

cbuffer UnityPerMaterial : register(b2)
{
    float4 _BaseMap_ST;                 // 0
};

V2F vert(VSIn v)
{
    V2F o;
    float3 positionWS = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1)).xyz;
    o.pos = mul(unity_MatrixVP, float4(positionWS, 1));
    o.uv = v.uv0 * _BaseMap_ST.xy + _BaseMap_ST.zw;
    o.positionWS = positionWS;
    o.normalWS = normalize(mul(v.normal, (float3x3)unity_WorldToObject));
    o.tangentWS = float4(normalize(mul((float3x3)unity_ObjectToWorld, v.tangent.xyz)), v.tangent.w * unity_WorldTransformParams.w);
    return o;
}

#elif defined(CL_PS)

cbuffer Globals : register(b0)
{
    float4 _pg0[2];
    float4 _GlossyEnvironmentCubeMap_HDR;   // 32
    float4 _pg1;
    float4 _GlobalMipBias;                  // 64 (.x)
    float4 _pg2[5];
    float4 _AdditionalLightsCount;          // 160 (.x)
    float4 _pg3[10];
    float3 _WorldSpaceCameraPos;            // 336
};

#define MAX_LIGHTS 256
cbuffer AdditionalLights : register(b1)
{
    float4 _AdditionalLightsPosition[MAX_LIGHTS];      // 0
    float4 _AdditionalLightsColor[MAX_LIGHTS];         // 4096
    float4 _AdditionalLightsAttenuation[MAX_LIGHTS];   // 8192
    float4 _AdditionalLightsSpotDir[MAX_LIGHTS];       // 12288
};

cbuffer UnityPerDraw : register(b2)
{
    float4 _pd0[11];
    float4 unity_LightData;             // 176 (.y = 이 물체에 닿는 추가 라이트 수)
    float4 unity_LightIndices[2];       // 192
    float4 _pd1[11];
    float4 unity_SHAr;                  // 400
    float4 unity_SHAg;
    float4 unity_SHAb;
    float4 unity_SHBr;
    float4 unity_SHBg;
    float4 unity_SHBb;
    float4 unity_SHC;                   // 496
};

cbuffer UnityPerMaterial : register(b3)
{
    float4 _pm0[3];
    float4 _BaseColor;                  // 48
    float4 _pm1[2];
    float _Cutoff;                      // 96
    float _Smoothness;                  // 100
    float _Metallic;                    // 104
    float _BumpScale;                   // 108
    float _Parallax;                    // 112
    float _OcclusionStrength;           // 116
    float2 _pm2;
    float2 _pm3;
    float _Surface;                     // 136
};

// 레지스터는 원본 변형마다 다름 (mahosaba-render.md C장 표)
#if defined(CL_PREMUL)
TextureCube _GlossyEnvironmentCubeMap : register(t0);
SamplerState sampler_GlossyEnvironmentCubeMap : register(s0);
Texture2D _BaseMap : register(t3);
SamplerState sampler_BaseMap : register(s3);
#else
TextureCube _GlossyEnvironmentCubeMap : register(t0);
SamplerState sampler_GlossyEnvironmentCubeMap : register(s1);
Texture2D _BaseMap : register(t3);
SamplerState sampler_BaseMap : register(s4);
#endif
#if defined(CL_MAPS)
Texture2D _BumpMap : register(t4);
SamplerState sampler_BumpMap : register(s5);
Texture2D _OcclusionMap : register(t5);
SamplerState sampler_OcclusionMap : register(s6);
Texture2D _MetallicGlossMap : register(t6);
SamplerState sampler_MetallicGlossMap : register(s7);
#endif

float3 LinearToSRGB(float3 c)
{
    float3 lo = c * 12.92321;
    float3 hi = 1.055 * pow(abs(c), 0.416667) - 0.055;
    return c <= 0.0031308 ? lo : hi;
}

// URP SampleSH (L2). 감마 색공간에서는 sRGB로 바꿔 씀
float3 SampleSH(float3 n)
{
    float4 a = float4(n, 1);
    float3 l1 = float3(dot(unity_SHAr, a), dot(unity_SHAg, a), dot(unity_SHAb, a));
    float4 b = n.xyzz * n.yzzx;
    float3 l2 = float3(dot(unity_SHBr, b), dot(unity_SHBg, b), dot(unity_SHBb, b)) + unity_SHC.rgb * (n.x * n.x - n.y * n.y);
    return LinearToSRGB(l1 + l2);
}

float3 DecodeHDR(float4 c, float4 instr)
{
    float alpha = max(instr.w * (c.a - 1) + 1, 0);
    return c.rgb * (instr.x * pow(alpha, instr.y));
}

float4 frag(V2F i) : SV_Target
{
    float4 albedo = _BaseMap.SampleBias(sampler_BaseMap, i.uv, _GlobalMipBias.x) * _BaseColor;
#if defined(CL_CUTOUT)
    clip(albedo.a - _Cutoff);
#endif

#if defined(CL_MAPS)
    float4 mg = _MetallicGlossMap.SampleBias(sampler_MetallicGlossMap, i.uv, _GlobalMipBias.x);
    float metallic = mg.r;
    float smoothness = mg.a * _Smoothness;
    // URP UnpackNormalScale (DXT5nm·BC5 겸용)
    float4 packed = _BumpMap.SampleBias(sampler_BumpMap, i.uv, _GlobalMipBias.x);
    packed.a *= packed.r;
    float3 nTS;
    nTS.xy = (packed.ag * 2 - 1) * _BumpScale;
    nTS.z = max(1.0e-16, sqrt(1 - saturate(dot(nTS.xy, nTS.xy))));
    float3 bitangent = cross(i.normalWS, i.tangentWS.xyz) * i.tangentWS.w;
    float3 N = normalize(nTS.x * i.tangentWS.xyz + nTS.y * bitangent + nTS.z * i.normalWS);
    float occlusion = lerp(1, _OcclusionMap.SampleBias(sampler_OcclusionMap, i.uv, _GlobalMipBias.x).g, _OcclusionStrength);
#else
    float metallic = _Metallic;
    float smoothness = _Smoothness;
    float3 N = normalize(i.normalWS);
    float occlusion = 1;
#endif

    float3 V = normalize(_WorldSpaceCameraPos - i.positionWS);

    // URP InitializeBRDFData
    float oneMinusReflectivity = 0.96 - 0.96 * metallic;
    float3 diffuse = albedo.rgb * oneMinusReflectivity;
    float3 specular = lerp(0.04, albedo.rgb, metallic);
    float perceptualRoughness = 1 - smoothness;
    float roughness = max(perceptualRoughness * perceptualRoughness, 0.0078125);
    float roughness2 = max(roughness * roughness, 6.103515625e-5);
    float grazing = saturate(smoothness + 1 - oneMinusReflectivity);
    float normalization = roughness * 4 + 2;
#if defined(CL_PREMUL)
    diffuse *= albedo.a;
#endif

    // 간접광: SH + 전역 반사 큐브맵 한 장 (법정에는 반사 프로브가 없어 원본도 이 맵만 씀)
    float NoV = saturate(dot(N, V));
    float fresnel = 1 - NoV;
    fresnel *= fresnel;
    fresnel *= fresnel;
    float3 R = reflect(-V, N);
    float mip = perceptualRoughness * (1.7 - 0.7 * perceptualRoughness) * 6;
    float3 env = DecodeHDR(_GlossyEnvironmentCubeMap.SampleLevel(sampler_GlossyEnvironmentCubeMap, R, mip), _GlossyEnvironmentCubeMap_HDR);
    float3 color = (SampleSH(N) * diffuse + env * (lerp(specular, grazing, fresnel) / (roughness2 + 1))) * occlusion;

    // 추가 라이트 (법정 스포트라이트 2개). URP Forward 경로와 같은 감쇠·GGX 식
    uint count = min((uint)_AdditionalLightsCount.x, (uint)unity_LightData.y);
    [loop] for (uint li = 0; li < count; li++)
    {
        uint index = (uint)unity_LightIndices[li >> 2][li & 3];
        float4 lp = _AdditionalLightsPosition[index];
        float3 L = lp.xyz - i.positionWS * lp.w;
        float d2 = max(dot(L, L), 6.103515625e-5);
        L *= rsqrt(d2);
        float4 att = _AdditionalLightsAttenuation[index];
        float f = d2 * att.x;
        float smooth = max(1 - f * f, 0);
        float spot = saturate(dot(_AdditionalLightsSpotDir[index].xyz, L) * att.z + att.w);
        float3 radiance = _AdditionalLightsColor[index].rgb * (smooth * smooth / d2 * spot * spot * saturate(dot(N, L)));

        float3 H = normalize(L + V);
        float NoH = saturate(dot(N, H));
        float LoH = saturate(dot(L, H));
        float d = NoH * NoH * (roughness2 - 1) + 1.00001;
        float specTerm = roughness2 / (d * d * max(0.1, LoH * LoH) * normalization);
        color += (diffuse + specular * specTerm) * radiance;
    }

    return float4(color, _Surface == 1 ? albedo.a : 1);
}

#endif
