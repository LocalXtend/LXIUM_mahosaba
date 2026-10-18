# 마법소녀의 마녀재판 렌더링 최적화 설계 (AI용)

- 사람용 문서: `../human/mahosaba-render.md`
- 기반 사실: `mahosaba.md` (구조·근거). 이 문서는 그 위에서 LXIUM 고유 렌더링을 설계함
- 0.7.0 대사·UI 연구: `mahosaba-ui.md` (별도 경로의 실측·실패한 실험·인계). `[Text] RevealReuse`는 기본 끔인 실험 기능.
- 목표: 내장 GPU(Intel UHD 620 ~ Iris Xe)에서 체감 프레임 향상. 대사·UI는 원래 해상도 유지
- 표기: [관찰] [추론] [미확인]. 효과 수치는 측정 전까지 모두 [미확인]

## 1. 출발점

| 비용 | 위치 | 근거 |
|---|---|---|
| 레이어 캐릭터마다 URP 카메라 전체 렌더 (1500x3000) | `LayeredDrawer.DrawWithCamera` | mahosaba.md 5.3-3 |
| 모든 액터(캐릭터·배경)의 전환 셰이더 RT 복사, 매 프레임 | `TransitionalSpriteRenderer.Update` | 5.3-5 |
| 3D 템플릿 설정(MSAA 4x, SSAO, 중간 텍스처 Always)이 모든 카메라에 | URP 에셋 | 4장 |
| 무대 카메라 2대가 화면 분할과 무관하게 함께 렌더 | `StageCameraController` | 6.2 |
| 프레임 상한 60/30, VSync는 품질 단계 | `CameraManagerExtended` | 8장 |

화면에 실제로 보이는 것(배경 1장 + 캐릭터 몇 명 + UI)은 가볍고, 비용 대부분은 **결과가 같은 화면 밖 렌더를 매 프레임 반복**하는 데서 나온다 [추론].

## 2. 기법

### A. 설정 교정 (화면 변화 없음, 구현됨)

| ID | 이름 | 방법 | 화면 변화 | 설정 |
|---|---|---|---|---|
| A1 | 2D 카메라 MSAA 끄기 | `beginCameraRendering`에서 직교 카메라만 `allowMSAA=false`. 스프라이트·UI는 가장자리가 투명이라 MSAA 효과 없음. 무대 카메라는 RT의 2x 유지 | 없음 [추론] | `Urp.MsaaOff` |
| A2 | SSAO 무대 전용 | 같은 콜백에서 SSAO 기능을 원근(무대) 카메라일 때만 `SetActive(true)`. 2D 카메라는 불투명 물체가 없어 AO가 나오지 않음 | 없음 [추론] | `Urp.Ssao` |
| A3 | 중간 텍스처 Auto | `UniversalRendererData.m_IntermediateTextureMode = Auto` + `SetDirty()`. 캐릭터 합성 카메라처럼 후처리 없는 카메라는 RT에 바로 그림 | 없음 [추론] | `Urp.IntermediateAuto` |
| A4 | 프레임 제한 해제 | 매 프레임 `targetFrameRate`·VSync를 비교해 다를 때만 설정. VSync는 icall | 설정에 따름 | `FrameRate.TargetFps` |

### B. LXIUM 자체 렌더 경로

| ID | 이름 | 방법 | 대응 힌트 | 상태 |
|---|---|---|---|---|
| B1 | 변경분만 합성 | `LayeredDrawer.DrawLayers` 앞에서 레이어 상태 해시(켜짐·스프라이트·색·재질·레이어·PPU)를 비교, 같으면 이전 RT 그대로 반환 | Temporal Reprojection을 노벨에 맞게: 생성(GEN) 프레임 대신 **같은 결과를 재사용**하므로 아티팩트 없음 | 구현 `Character.ChangeOnly` |
| B2 | 전환 복사 생략 | `TransitionalRenderer.RenderToTexture` 앞에서 입력 해시(메인·전환 텍스처, 진행도, 색, 블러, 뒤집기, 대상 RT)가 같으면 생략. 메인 텍스처가 B1이 그린 RT면 그 버전 번호를 해시에 넣음. 출처 모르는 RT(다이스·영상)는 항상 그림 | 같음 | 구현 (B1과 같은 설정) |
| B3 | 액터 원래 해상도 | 레이어 캐릭터 메타데이터 PPU를 `화면 높이 / 1440`배로(1080p: 0.75 → 1125x2250, 픽셀 56%). 합성 RT와 화면 쿼드가 같이 줄어 화면상 크기 그대로 | Spatial Resolution을 "월드 = 화면 밖 액터 합성"에만 적용. UI·대사는 메인 카메라라 영향 없음 | 구현 `Character.Resolution` |
| B4 | 무대 가시성 컬링 | 게임이 무대를 띄운 동안에도, RawImage의 상속 알파가 0인 화면의 카메라는 끔. 0.2.3부터 그 RawImage도 `CanvasRenderer.cull = true`로 그리기 목록에서 뺌 (UGUI는 알파 0이어도 화면 크기 투명 사각형을 그림) | — | 구현 `Stage.HiddenSkip` (2차: -10.2 ms. RawImage 컬링 7차 1080p: -0.14 ms, 화면 차이 0) |
| B5 | 무대 해상도 | `RenderTexture_1/2`를 화면 높이에 맞춰 줄임(원래 화면과 무관하게 1920x1080). RawImage가 화면 크기로 보여 주므로 화면보다 큰 RT는 낭비. 해상도를 바꾸면 따라감 | Spatial Resolution | 구현 `Stage.Resolution` (기본 0 = 화면 맞춤, 0.1.1. 3차: 768p에서 -5.1 ms, 세밀한 무늬에서 화소 차이 2.6%) |
| B6 | 무대 반 프레임 | 무대 카메라의 위치·회전·FOV가 지난 프레임과 같으면 두 프레임에 한 번만 그림(두 카메라는 번갈아). 그 사이에는 RT의 지난 그림을 RawImage가 그대로 보여 줌. 카메라가 움직이는 프레임은 항상 그려서 팬·줌이 끊기지 않음. 불·캐릭터 변화는 최대 1프레임 늦음 | Temporal Frame Reprojection | 선택 `Stage.HalfRate` (4차: -2.0 ms, 정지 장면 화면 같음. 프레임 시간이 번갈아 짧고 길어 기본은 끔. 0.5.2: 바로 앞 프레임에 그린 카메라만 건너뜀 — 그 전에는 숨어 있다 나타난 카메라도 첫 프레임을 건너뛰어 RT에 남은 오래된 장면이 한 프레임 보일 수 있었음 [추론: 코드]. B16이 켜져 있으면 멈춘 장면은 B16이 이미 건너뛰므로, 반 프레임은 불처럼 매 프레임 바뀌는 장면에서만 차이를 냄) |
| B10 | 무대 MSAA 끄기 | `RenderTexture_1/2.antiAliasing` 2 → 1 (`Release` 후 변경). 가장자리는 무대 카메라의 FXAA가 다듬음 | — | 기본 켬 `Stage.MsaaOff` (0.2.1. 4차: -1.13 ms, 화면 차이 0) |
| B11 | 무대 후처리 끄기 | 무대 카메라 `UniversalAdditionalCameraData.renderPostProcessing = false`. 볼륨 효과가 모두 0이라 후처리는 색 보정 LUT 만들기 + 우버 패스 + FXAA뿐이고, 메인 카메라가 화면 전체에 FXAA를 한 번 더 함. 0.2.2부터 메인 카메라 후처리가 꺼져 있으면(게임 옵션) 무대 후처리를 살려 법정이 무보정으로 남지 않게 함 | — | 기본 켬 `Stage.PostOff` (0.2.1. 4차: -0.66 ms, 평균 1.7/255) |
| B12 | 깊이 프라이밍 | SSAO 프리패스 깊이로 불투명 패스를 `ZTest Equal`. 실제 렌더러(`asset.scriptableRenderer`)의 `depthPrimingMode`를 무대 카메라만 `Forced`로 바꿔 실험 | Render Fast | **제거** (0.2.1. 4차: MSAA 끔 위에서 추가 이득 0) |
| B13 | SSAO 가볍게 | `ScreenSpaceAmbientOcclusion.m_Settings`: `Downsample = true`, `Samples = Low`, `BlurQuality = Low`. 매 프레임 설정을 읽으므로 바로 적용 | Spatial Resolution | `Urp.Ssao = StageLite` (4차 768p: -1.24 ms, 5차 1080p: 원래 품질 대비 -3.3 ms. 법정 화소 차이 0) |
| B14 | SSAO 끄기 + 깊이 | SSAO를 끄면 무대 카메라에 깊이 텍스처가 없어져 원본 불 셰이더의 깊이 페이드가 깨질 수 있으므로 무대 카메라 `requiresDepthOption = On` (MSAA 깊이 복사만 함) | — | **기본** `Urp.Ssao = Off` (0.2.2). FireLite를 쓰는 중이면 깊이를 요청하지 않음 (0.2.1부터). 4차: 깊이 요청 포함 -0.73 ms로 Lite보다 느렸음 → 5차 1080p(깊이 요청 없음): Lite 대비 **-1.80 ms**, 법정 화소 차이 0 |
| B15 | 법정 건물 캐시 | 무대 카메라 한 대일 때 바닥·벽·계단의 조명 결과를 같은 시점의 RT에 그려 두고, 무대 카메라는 그 색을 픽셀 위치로 `Load`만 하는 `LXIUM/CourtCached` 사본 재질로 그림 (레이어 31 + 전용 카메라) | Temporal Frame Reprojection (정확한 재사용) | **지움 (0.5.0)**. 10차: 화면 화소 차이 0, 11차: -0.11 ms로 같은 설정끼리의 흔들림(0.10 ms)과 같음. CourtLite가 이미 조명을 가볍게 해 남은 몫이 작고, 깊이·래스터·후처리는 그대로라서 [추론] |
| B16 | 법정 바뀔 때만 그리기 | 무대 카메라가 보는 것이 그대로인 프레임은 카메라를 끄고 RT의 지난 그림을 RawImage가 그대로 보여 줌 (B6·`02_noStage`와 같은 방식). 장면 버전을 두고, 바뀐 프레임마다 1 증가. 카메라마다 마지막으로 그린 버전이 지금과 같고 1초가 안 지났으면 건너뜀. 바뀜 판단: ① 카메라 위치·회전·FOV(안 그리는 동안 움직였어도 다시 그릴 때까지 무효) ② 캐릭터 그림 내용: `RenderToTexture`가 실제로 새 그림을 만들 때(입력 해시가 바뀜, 추적 못 하는 RT, `Character.ChangeOnly` 꺼짐) `Dirty`. 1초 안전 갱신은 알리지 않음 ③ 캐릭터 위치·크기·회전·불투명도: `RenderToTexture` 앞에서 액터마다 (포인터, `Opacity`, `localToWorldMatrix`) 해시를 순서 무관하게 더해 지난 프레임 합과 비교 (사라진 액터도 잡힘) ④ 법정: `CourtShaders`가 1초마다 모을 때 보이는 법정의 렌더러(+`_fireVfx`)·조명 목록을 받고, 매 프레임 렌더러 켜짐·`localToWorldMatrix`, 조명 켜짐·색·세기·범위·각도·변환을 해시. `MeshRenderer`가 아니거나(파티클 등) Lit·CourtLite가 아닌 셰이더(불 등)를 쓰는 렌더러가 켜져 있으면 매 프레임 그림 ⑤ RT 크기·MSAA, 무대 후처리·깊이, 법정 셰이더 교체, 실험실 설정 변경 → `Dirty`. 파괴된 렌더러를 만나면 다음 수집까지 매 프레임 그림. 로그 `stage court: n renderers, n lights, moving: 종류=수` | Temporal Frame Reprojection (카메라 단위 정확한 재사용) | **기본 `Stage.CourtChangeOnly`** (0.5.1. 0.5.0은 키 `ChangeOnly`·기본 끔이었으나 `[Character] ChangeOnly`와 이름이 같아 바꿈). 12차: **-2.14 ms (-32%, 151 → 224 fps)**, `stageSkip` 1.95(카메라 2대 중 보이는 1대도 95% 프레임에서 건너뜀), 화면 같음 — `02_noStage` 상한과 같은 수준. 사용자 플레이(0.5.1)에서도 문제 없음. 알려진 빈틈: 법정 밖에서 무대 카메라에 보이는 연출(스폰 효과 등)은 감지하지 않아 최대 1초 늦을 수 있음 |
| B7 | 제로 카피 액터 | `TransitionalSpriteRenderer`의 RT 단계를 없애고 `DrawMesh`에서 전환 셰이더로 화면에 바로 그림 (전환·색·블러를 한 패스로) | Render Fast | 후보 (C1 필요할 수 있음) |
| B8 | 절차적 레이어 합성 | 캐릭터의 `renderCamera`를 비워 Naninovel 절차적 경로(CommandBuffer)로. URP 카메라 1회가 DrawMesh 몇 개로 바뀜. 스텐실 마스크 재질 호환 확인 필요 | Render Fast | 후보 |
| B9 | 숨은 UI 정지 | 캔버스만 꺼진 화면의 TrueShadow·애니메이션 컴포넌트를 끔 | — | 후보 (CPU 측정 후) |

B1 해시는 매 프레임 렌더러마다 interop 호출을 하므로 CPU 비용이 있다. 0.2.3부터 렌더러별 GameObject 래퍼를 처음 한 번만 꺼내 두고, 실험실이 모드 전체의 메인 스레드 시간(`modCpu`)을 기록한다. 7차(재판, 캐릭터 11명): 프레임당 0.51~0.62 ms. 지금은 GPU가 병목이라 fps에는 거의 영향 없음 (mahosaba.md 10장 7차).

**빠른 변화 감지** (0.4.0, `Character.FastDetect`, 기본 켬. 10차: 모드 CPU 1.23 → 0.83 ms, 11차: 1.29 → 0.68 ms, 화면 같음): 드로어마다 0~3 순번을 주고, 최근 1초 안에 해시가 바뀐(hot) 드로어와 전환 입력(메인·전환 텍스처, 진행도, 블러, 색, 뒤집기 — 내용 버전 제외)이 바뀐 직후 1초 동안은 모든 드로어를 매 프레임 해시. 나머지는 `(frameCount + 순번) % 4 == 0`인 프레임에만 해시하고 그 밖에는 해시 없이 지난 RT를 씀 → 식은 캐릭터의 해시 비용 1/4, 놓친 변화는 최대 3프레임. RT가 바뀌었거나 1초 안전 갱신 차례면 항상 해시. 1초 안전 갱신의 내용 버전 증가는 전환 입력에서 빼서 hot을 만들지 않음

B1·B2가 켜지면 대사만 넘기는 장면에서 화면 밖 렌더는 거의 0이 되고, 남는 것은 메인 카메라의 화면 그리기뿐이다 (2차: 재판에서 합성 11회/프레임 → 1초 안전 갱신만, -8.9 ms) [관찰: mahosaba.md 10장]. 표정이 바뀌는 프레임에만 B1 1회 + B2 1회가 생긴다. 안전장치로 입력이 같아도 1초마다 한 번은 다시 그리되, 여러 액터의 안전 갱신이 한 프레임에 몰리지 않게 프레임당 B1·B2 각 1개로 제한한다 (0.1.1).

### C. 자체 셰이더

셰이더는 실행 중 컴파일이 안 된다. 이 PC에는 Unity 에디터 본체가 없어서(6000.5.7f1은 Hub 메타데이터만 있음 [관찰]) 에디터 없이 만든다.

**빌드 방법** (`optimizeMods/LXIUM_mahosaba/shaders/build_shaders.py`, UnityPy + `d3dcompiler_47.dll`. 0.3.0부터 FireLite·CourtLite를 한 번들로)

1. 게임 번들의 원본 셰이더 객체를 틀로 씀. 직렬화 형식이 게임과 같아서(6000.0.48f1) 버전 호환 문제가 없음
2. 플랫폼 블롭(LZ4) 구조: `개수 + (오프셋, 길이, 세그먼트)×N + 항목`. 항목은 서브프로그램 또는 파라미터 블롭(상수 버퍼 이름·크기)
3. 서브프로그램 구조: `버전 202012090, GPU 프로그램 종류, 통계 4개, 키워드 목록, 코드(길이 + Unity 헤더 38바이트 + DXBC, 4바이트 정렬), 나머지(정점 채널 바인딩 등)`. DXBC만 `D3DCompile`(원본과 같은 셰이더 모델, O3) 결과로 바꿈. Unity 헤더의 바이트 1~3은 텍스처·상수 버퍼·샘플러 슬롯 수로 보이며 원본 것을 그대로 둠
   - 파라미터 블롭(변형별): `버전, 6, 0×4, 상수 버퍼마다 (이름, 크기, 벡터 수, (이름, 0, 행, 열, 종류, 배열, 오프셋)×n, 행렬 수 …), 텍스처 수, (이름, 0, 레지스터, 샘플러 레지스터 또는 -1, 차원 4=2D·8=큐브)×n, …, 인라인 샘플러(상태, 레지스터)`. 텍스처 레지스터는 변형마다 다르고, 상수 버퍼 배치·바인딩은 패스 공통(`m_CommonParameters`)
4. 텍스처·샘플러·상수의 위치는 `m_ParsedForm`의 메타데이터대로 엔진이 채우므로, HLSL에서 레지스터(`t1~t5`, `s2~s6`)와 상수 버퍼 오프셋(`$Globals`의 `_TimeParameters` 304, `unity_MatrixVP` 1248, `UnityPerMaterial` 176바이트 배치)을 원본과 같게 맞춤
5. 첫 패스(화면 그리기)와 D3D11만 남기고 나머지 패스·Vulkan·OpenGL 코드는 뺌 → 번들에 원본 코드가 없음. 타입 트리도 남은 두 객체(Shader, AssetBundle) 것만
6. 이름 `LXIUM/FireLite`·`LXIUM/CourtLite`, 번들 이름 `lxium_mahosaba`, 내부 파일 `CAB-<md5("LXIUM_mahosaba")>` (게임 번들과 같은 CAB 이름이면 Unity가 이미 로드된 번들로 보고 거부)
7. 결과 `res/lxium_mahosaba.bundle`(29 KB, 셰이더 2개)를 DLL에 포함. 0.4.x의 CourtCached(같은 원본 복제·키워드 바꾸기)는 0.5.0에서 B15와 함께 지움 → 0.3.2 번들과 바이트까지 같음. 실행 중 `AssetBundle.LoadFromMemory` → `LoadAllAssetsAsync(Shader)` → `hideFlags = DontUnloadUnusedAsset`

**셰이더**

| ID | 셰이더 | 내용 | 게임 연동 | 상태 |
|---|---|---|---|---|
| C1 | `LXIUM/ActorDirect` | 전환(Naninovel `TransitionalTexture`의 전환 키워드 중 실제 쓰는 것만) + 색조 + 불투명도 + 선명화를 한 패스로 화면에 | `TransitionalMaterial` 프로퍼티(메인·전환 텍스처, 진행도, 색), 깊이 패스 컷오프 | 후보 (같은 방법으로 빌드 가능) |
| C2 | `LXIUM/Sharpen` | B3·B5로 줄인 RT를 화면에 그릴 때 RCAS(적응형 선명화) | B3·B5 배율을 보고 강도 조절 | 후보 |
| C3 | `LXIUM/FireLite` | 원본 불 식(mahosaba.md 6.2)에서 늘 1인 빈 텍스처 2개를 상수로, `_TextureSample2`(세로 S자 곡선)를 식으로 바꾸고 깊이 페이드를 뺌 → 텍스처 8회 → 4회. 마스크가 0인 픽셀은 `clip`으로 노이즈 2회를 건너뜀(마스크 텍스처의 54%) | 불 재질의 `shader`만 바꿈. 속성 이름이 원본과 같아 색·속도·마스크 값과 `ChangeFireFxActivity`의 켜고 끄기를 그대로 따름. 불빛 색은 스포트라이트라 영향 없음 | 기본 켬 `Stage.FireLite` (0.2.1, D3D11 전용. 4차: 불 비용 0.93 → 0.21 ms) |
| C4 | `LXIUM/CourtLite` | URP Lit ForwardLit과 같은 BRDF(GGX)·스포트라이트 감쇠·SH(감마 변환)를 쓰고, 그림자·SSAO·반사 프로브 블렌딩·박스 투영·쿠키·메인 라이트를 뺌. 반사는 전역 큐브맵 한 장(`_GlossyEnvironmentCubeMap`) | 재질의 `shader`만 바꿈. 키워드·값이 그대로라 변형 선택도 원본과 같음. 스포트라이트 색은 매 프레임 URP가 넣는 값을 그대로 읽음 | 기본 켬 `Stage.CourtLite` (0.3.2. 8·9차 1080p: 법정 건물 2.1 → 1.4 ms, -0.70·-0.74 ms, 165 → 185 fps, 바닥·벽·계단·단상 모두 원본과 같음) |

- FireLite 화면 차이: 불 쿼드가 벽·바닥과 만나는 1 유닛 안에서 원래는 흐려지던 경계가 또렷해짐. 그라데이션 근사 오차 0.01 안팎 [추론]
- 렌더 큐: `Material.shader`를 바꾸면 Unity가 큐를 새 셰이더 기본값으로 되돌림(Lit·CourtLite는 2000). 투명 단상(3000)이 불투명 순서로 밀려 사라졌던 문제(0.3.0)를 0.3.1에서 고침: 원본 셰이더일 때의 큐를 재질마다 기억해 바꿀 때마다 다시 넣음
- 적용 범위: `Resources.FindObjectsOfTypeAll<Court>()` 아래(그리고 `_fireVfx` 아래) 렌더러 재질 중 셰이더 이름이 원본인 것. 법정 프리팹이 다시 로드될 수 있어 켜져 있는 동안 1초마다 다시 모음 (`CourtShaders`)

**CourtLite 변형** (원본 URP Lit ForwardLit의 D3D11 변형 80개 가운데 법정 재질이 고르는 것과 같은 키워드의 3개 + 정점 1개만 남김)

| 정의 | 원본 변형 키워드 (공통: `_MAIN_LIGHT_SHADOWS _ADDITIONAL_LIGHTS _ADDITIONAL_LIGHT_SHADOWS _REFLECTION_PROBE_BLENDING _REFLECTION_PROBE_BOX_PROJECTION _SCREEN_SPACE_OCCLUSION`) | 재질 | 텍스처(레지스터/샘플러) | 명령어·텍스처 읽기 (원본 → LXIUM) |
|---|---|---|---|---|
| `CL_VS` | `_MAIN_LIGHT_SHADOWS _ADDITIONAL_LIGHTS _NORMALMAP` (vs_4_0) | 전부 | — | 원본 출력에서 그림자 좌표·SH 정점값을 뺌 |
| `CL_MAPS` | 공통 + `_NORMALMAP _METALLICSPECGLOSSMAP _OCCLUSIONMAP` | Court_Floor, Court_Step | 반사 큐브 t0/s1, `_BaseMap` t3/s4, `_BumpMap` t4/s5, `_OcclusionMap` t5/s6, `_MetallicGlossMap` t6/s7 | 약 300·9회 → 137·5회 |
| `CL_CUTOUT` | 공통 + `_ALPHATEST_ON` | Court_Wall (3종) | t0/s1, t3/s4 | 약 300·7회 → 119·2회 |
| `CL_PREMUL` | 공통 + `_SURFACE_TYPE_TRANSPARENT _ALPHAPREMULTIPLY_ON` | Court_Stand | t0/s0, t3/s3 | 약 270·6회 → 117·2회 |

- 원본 변형이 모두 그림자·SSAO 키워드를 가진 것만 빌드에 남아 있어, 그림자·SSAO가 없는 지금도 원본은 그림자 맵(메인 1 + 라이트마다 1)·SSAO 텍스처를 읽고 반사 프로브 블렌딩·박스 투영(큐브 3회)을 계산함 [관찰: 디스어셈블]
- 상수 배치 (픽셀): `$Globals` b0(`_GlossyEnvironmentCubeMap_HDR` 32, `_GlobalMipBias` 64, `_AdditionalLightsCount` 160, `_WorldSpaceCameraPos` 336), `AdditionalLights` b1(위치 0, 색 4096, 감쇠 8192, 스포트 방향 12288, 각 256개), `UnityPerDraw` b2(`unity_LightData` 176, `unity_LightIndices` 192, `unity_SHAr~SHC` 400~496), `UnityPerMaterial` b3(`_BaseColor` 48, `_Cutoff` 96, `_Smoothness` 100, `_Metallic` 104, `_BumpScale` 108, `_OcclusionStrength` 116, `_Surface` 136). 정점: `$Globals` b0(`unity_MatrixVP` 1248), `UnityPerDraw` b1(`unity_ObjectToWorld` 0, `unity_WorldToObject` 64, `unity_WorldTransformParams` 144), `UnityPerMaterial` b2(`_BaseMap_ST` 0)
- 빠진 패스(그림자·깊이·DepthNormals·모션 벡터): 법정에 그림자 라이트가 없고 SSAO 기본 끔이라 쓰이지 않음. SSAO를 켜면 법정이 DepthNormals에 안 그려져 AO가 법정에 생기지 않음(지금도 화면 영향 0)
- 화면 차이 예상: 반사 프로브가 없어(mahosaba.md 6.2) 빠진 항은 원본에서도 0 또는 1 → 같아야 함. 확인은 실험실 스크린샷으로 [추론]

UHD 620에서 120fps: A+B1+B2 뒤 대화 장면의 매 프레임 일은 1080p 화면에 쿼드 몇 장 + UI라서 가능성이 높다 [추론]. 재판 장면은 Iris Xe 768p 창 모드에서 A+B1+B2+B4+B5로 115fps (mahosaba.md 10장 3차), 4차에서 B10·B11·B13·C3 조합으로 768p 154 fps. 0.3.2(C4 추가) 1080p 약 185 fps. **1080p 전체 화면**(5차): 0.2.1 기본 124 fps, SSAO 끔(0.2.2 기본) 160 fps. 남은 6.2 ms ≈ 무대 카메라 2.4 + 메인 카메라·UI·CPU 3.8 [관찰·추론: 10장 5차].

### D. 멈춤(로딩 랙) 줄이기 (0.6.0)

13차 ETW 추적(10장 13차)으로 찾은 긴 프레임의 원인별 대응. 모두 결과는 원래와 같고 순서·방법만 바꿈.

| # | 원인 [관찰: 추적 호출 트리] | 대응 | 상태 |
|---|---|---|---|
| D1 | TMP 동적 폰트에 새 글자를 더할 때마다 `TMP_FontAsset.UpdateGSUB/GPOSFontFeaturesForNewGlyphIndex` → `FontEngine.GetLigatureSubstitutionRecords`가 폰트의 OpenType 표 전체를 다시 읽고 버림(`OTL_Table::Clear_Deallocate`가 66%). 불러오기 화면 한 프레임 430 ms 중 79%, 재판 시작 레이아웃 재구성 한 프레임의 38% | `TMP_Settings.m_GetFontFeaturesAtRuntime = false`. `TryAddCharacterInternal`이 이 값을 보고 두 단계를 건너뜀 [관찰: ISIL `get_getFontFeaturesAtRuntime` 비교 후 분기]. 이미 굽혀 있던 글자의 합자·커닝은 그대로. 한글 음절은 합자가 없음 | 기본 `Text.FastGlyphs` |
| D2 | Naninovel `ResourceProvider.IsLocationCached<T>(path)` = `LocationsCache.Any(r => r.Path == path && AreTypesCompatible(r.Type, typeof(T)))`. 위치 목록이 2.8만~5.5만 개라 한 번에 약 1 ms. 대본을 불러올 때 명령마다 불려 재판 시작에 300·500 ms 프레임 | 제네릭 공유 구현(GameAssembly+0x754AF0, 앞 24바이트 확인)을 `INativeDetour`로 가로채 경로 → 형식 목록 색인으로 찾음. 형식 판정은 원래와 같은 `AreTypesCompatible`(쌍마다 캐시). 목록은 뒤에 더해지기만 해서 늘어난 만큼만 이어서 색인, 줄면 다시 만듦. 색인은 IL2CPP 메모리 직접 읽기(List `_items` +0x10, 배열 +0x20, 항목 16바이트, 문자열 +0x10 길이·+0x14 글자) — 처음 항목을 interop 값과 비교해 맞을 때만 | 기본 (설정 없음). 검증: 진단 모드에서 처음 200번은 원래 함수와 결과 비교, 690번 중 다른 것 0. 351번 조회 2.0 ms (전 250~470 ms). 색인을 늘리는 첫 조회는 2.7만 개에 약 50 ms |
| D3 | 모드 코드가 처음 불릴 때 CoreCLR가 메인 스레드에서 JIT (`clrjit!CILJit::compileMethod`, 프레임 73·50 ms) | `Plugin.Load`에서 모드 어셈블리의 모든 메서드를 `RuntimeHelpers.PrepareMethod` (113개) | 기본 |
| D4 | 화면 패널(IMGUI) 첫 표시: IMGUI 글꼴의 커닝 표 읽기(`FontEngine.GetPairAdjustmentRecords`) 350·170 ms. 또 `OnGUI`가 있는 컴포넌트는 그리지 않아도 매 프레임 IMGUI가 돎 | `OnGUI`를 별도 컴포넌트(`GuiHost`)로 옮겨 패널·실험실 안내가 있을 때만 켬. 첫 표시 멈춤은 남음 (TMP로 바꾸면 D1 효과를 받음) | 일부 |
| D5 | 새 글자 SDF 그리기(`TryAddGlyphToTexture` → `Calculate3x3AAEDT`) 글자당 수 ms. 대사 한 줄에 새 글자가 많으면 100~180 ms | 남는 프레임에 앞으로 나올 글자를 미리 더하기 (대본의 문장 또는 자주 쓰는 한글 2,350자) | 후보 |
| D6 | 상태 저장(`CharacterManagerExtended.SaveServiceState` → `ActorState.OverwriteFromActor` → `String.Join` + LINQ) — Gosub 때 121 ms | 외형이 그대로면 지난 문자열 재사용 | 후보 |
| D7 | 모드의 관리 힙 GC(`coreclr!gc_heap::garbage_collect`, 167 ms 프레임의 13%) | 매 프레임 할당 줄이기, `GCSettings.LatencyMode` | 후보 |
| D8 | 캐릭터 첫 합성: 8K 아틀라스 업로드·RT 만들기(`CreateTexture2D` 0.8 s/로딩), 번들 LZ4·crunch 풀기 | 비동기 업로드 버퍼(16 MB → 아틀라스 크기)·시간 조각 조정 실험 | 후보 (로그: `priority=BelowNormal uploadSlice=2ms uploadBuffer=16MB`) |
| D9 | `SaveLoadUi.OnShow` 페이지 칸 `Instantiate` | — | 게임 UI 구조 |

### E. 모드 자체 비용 줄이기 (0.6.1)

진단 설정 `Debug.LayerDiag`의 부분별 시간(`ModProf`, 1080p 전체 화면 재판 대화·심문, 프레임당):

| 부분 | 0.6.0 | 0.6.1 |
|---|---|---|
| 캐릭터 변화 확인 (`DrawLayers` 앞, 11번) | 0.22~0.34 ms | **0.035 ms** |
| 전환 복사 확인 (`RenderToTexture` 앞, 12번) | 0.10 ms | 0.10 ms |
| 무대 (`LateTick`) | 0.085 ms | 0.09 ms |
| `Update` | 0.055 ms | 0.055 ms |
| 안전 다시 합성 (1.5~2 ms짜리) | 초당 11번 | 초당 약 1번 |

- E1 **외형 이벤트 감지** (`Character.FastDetect`의 뜻을 바꿈): 레이어 해시를 4프레임마다 돌려 가며 계산하던 것을, Naninovel이 외형을 바꿀 때만 계산. 이벤트 = `LayeredActorBehaviour.ApplyComposition`·`RebuildLayers`·`NotifyPerceivedVisibilityChanged` 뒤, `LayeredRendererLayer.Enabled` 설정 뒤에 올리는 번호 + 캐릭터 루트의 `activeInHierarchy`(루트가 켜질 때는 이벤트가 없어 1번 놓쳤음 → 추가) + 합성 RT·PPU. 번호가 그대로면 해시 없이 지난 그림. 놓친 변화 대비로 캐릭터마다 2초에 한 번 해시 확인(프레임당 최대 하나), 같은 그림 다시 합성(안전 갱신)은 10초로 늘림 (끄면 1초)
- 검증: 진단 로그의 `missedByEvents`(확인 검사에서야 발견된 변화) 불러오기·재판·심문 내내 0 [관찰]

### F. 메인 카메라 GPU 줄이기 (0.8.0)

15차 GPU 타이머로 메인 카메라(1080p 재판 대화, 3.2 ms)를 실험실 진단 프리셋으로 나눔 [관찰]: 후처리(FXAA + LUT) 1.3~1.7 ms, UI 1.9~2.2 ms, 그중 법정 화면 캔버스(`ScreenCanvas`) 1.45~1.6 ms, 월드(배경) 0.15 ms.

- F1 **메인 후처리 끄기** (`Urp.MainPostOff`): 볼륨 효과가 모두 0이라 메인 카메라 후처리가 하는 일은 화면 전체 FXAA뿐 (mahosaba.md 4장). 끄면 B의 규칙대로 법정 카메라가 자기 FXAA를 켜서 법정 가장자리는 그대로, UI 글자는 FXAA가 빠져 약간 선명. 200 → 262 fps [관찰: 15차]
- F2 **법정 화면 마스크 생략** (`Stage.ScreenMaskSkip`): `ScreenCanvas`의 화면마다(`StageScreenContainer`) `Quadrilateral`(GigaCreation.Essentials.Ui, `MaskableGraphic`, 꼭짓점 `_x`·`_y`·`_z`·`_w` = 사각형 로컬 좌표) + `Mask`(`showMaskGraphic=false`) + RawImage(무대 RT, B5G6R5) [관찰: 캔버스 목록·interop]. 화면 분할이 아닐 때 꼭짓점은 사각형의 네 모서리(±960, ±540)라 마스크가 잘라내는 것이 없는데, 스텐실 쓰기·되돌리기 2번이 Iris Xe 1080p에서 약 1 ms [관찰: 마스크를 끈 진단 −0.96 ms, 화면 평균 차이 0.08/255]
  - 꼭짓점이 모두 사각형 안이고 이루는 넓이가 사각형 넓이와 같으며(= 네 모서리), 사각형이 화면에 축 정렬로 화면 전체를 덮으면 `Mask`를 끄고 사각형 그래픽도 `cull` (마스크를 끄면 사각형이 흰 그래픽으로 그려져, 법정 화면이 서서히 나타날 때 흰빛이 섞임)
  - 판단 시점: `Canvas.preWillRenderCanvases` (게임 스크립트가 꼭짓점을 바꾼 뒤, UI 그래픽을 다시 만들기 전) → 분할 연출이 시작되는 프레임부터 마스크가 다시 켜짐. 다시 켤 때 `OnCullingChanged`로 cull 동안 건너뛴 사각형 메시 갱신을 등록
  - 판단 입력(꼭짓점·`rect` 필드·`localToWorldMatrix`·화면 크기)이 그대로면 지난 판단을 씀. 매 프레임 계산하면 interop 래퍼 생성으로 모드 CPU +0.14 ms였음. 게임 시작 직후 캔버스의 UI 카메라가 비어 있는 동안은 판단을 저장하지 않음 (첫 판에서 틀린 판단이 굳어 마스크가 한 번도 꺼지지 않았음)
  - 결과 (16차, 다른 프로그램의 GPU 사용 없이): 메인 카메라 GPU 2.10 → 0.88 ms, 288 → 317 fps, 스크린샷 평균 차이 0.018/255 [관찰]
- F3 **버그 수정**: B의 숨은 화면 RawImage `cull`을 풀 때 `Graphic.OnCullingChanged()`를 부름 (`MaskableGraphic.UpdateCull`과 같음). 없으면 cull 동안 건너뛴 다시 만들기가 등록되지 않아, 그 사이 메시가 지워진 경우(이미지 끔·켬 등) 법정이 검게 남음 [관찰: 실험실 진단에서 재현]

### G. 메인 스레드 CPU 줄이기 (0.8.0)

17차 ETW(F 뒤, 1080p 재판 대화, 다른 프로그램의 GPU 사용 없이): 프레임 2.9~3.2 ms 중 GPU 0.8~0.9 ms → **CPU가 걸림돌**. 메인 스레드 프레임당 약 3.2 ms [관찰: 호출 트리]:

| 부분 | 비중 |
|---|---|
| 렌더 (URP: 렌더 그래프 기록 4.5%, 컬링 4.4%, 렌더 그래프 실행 3.3%, 그리기 제출 4.8%) | 25% |
| Update (`EventSystem` 레이캐스트 5.9%, Naninovel `TransitionalSpriteRenderer.Update` 3.1%, Steam 콜백 2.7%) | 15% |
| 캔버스 (`CanvasUpdateRegistry.PerformUpdate` 5%: `RectMask2D` 잘라내기 3.3%, TrueShadow `OnWillRenderCanvas` 4%: `Graphic.canvas` → `GetComponentsInParent`) | 12% |
| LateUpdate (TrueShadow 변화 추적 4.3%) | 7% |
| 모드 코드·스택을 못 푼 부분 | 약 20% |

- G1 **Steam 콜백 빈도 제한** (`Cpu.SteamThrottle`): `Steamworks.CallbackDispatcher.RunFrame` 프리픽스로 초당 60번까지만 (매번 Steam 클라이언트 파이프를 `PeekNamedPipe`로 확인). Steam은 콜백을 쌓아 두었다가 다음 호출에 주므로 최대 16 ms 늦을 뿐. 341.6 → 345.5 fps [관찰: 17차]
- G2 **UI 마우스 검사 재사용** (`Cpu.RaycastReuse`): `InputSystemUIInputModule.PerformRaycast`(1A573B0, ICF 검사 통과) 프리픽스. 같은 포인터·같은 위치·마우스 버튼과 휠 변화 없음·16 ms 안이면 모듈이 `eventData.pointerCurrentRaycast`에 보관한 지난 결과를 그대로 돌려줌 (모듈은 이 값을 다시 `pointerCurrentRaycast`에 넣으므로 들어가기·나가기 이벤트 없음). 맞힌 오브젝트가 파괴됐으면(래퍼는 있는데 Unity 객체 없음) 새로 검사. 360.7 → 372~374 fps [관찰: 18차]. 대사 클릭 진행·Auto 버튼·불러오기 메뉴 동작 확인 [관찰]
  - 첫 구현(`EventSystem.RaycastAll`에서 결과 목록을 모드의 IL2CPP List에 복사해 두고 다시 채움)은 시작하자마자 `GameObject.get_transform` 접근 위반으로 꺼졌음 → 결과를 직접 보관하지 않는 방식으로 바꿈
- G3 **법정 재질 수집을 이벤트로** (21차): `CourtShaders`가 1초마다 `Resources.FindObjectsOfTypeAll<Court>`로 법정을 다시 찾았는데, 이 검색이 **3~4 ms**라 1초마다 한 프레임이 튐 [관찰: 단계별 시간 find 2.9~3.7 ms, 재질 모으기 0.24 ms, 법정 목록 0.27~0.31 ms]. `Court.Awake`(3859F0, ICF 검사 통과) 뒤에 그 법정을 목록에 넣고, 1초마다는 알려진 법정이 살아 있는지·어느 쪽이 켜졌는지만 확인. 전체 검색은 게임 시작 직후 한 번(훅보다 먼저 생긴 법정 대비). 설정이 바뀌거나 법정이 생기고 없어지거나 켜진 법정이 바뀔 때만 다시 모음. 법정 렌더러가 파괴돼 변화 검사가 실패하면 다시 모으기를 요청 [관찰: 재판 진입 때 한 번 모음(번들 첫 로드 포함 19 ms, 로딩 화면), 그 뒤 1초마다의 수집 없음, 모드 Update 0.032 → 0.019 ms]
- G4 **모드 자체 interop 호출 줄이기** (21차): interop의 메서드·속성 호출은 `il2cpp_runtime_invoke`를 거치고, Unity 객체의 `==`·구조체 연산자도 il2cpp 호출이라 비쌈. 필드 읽기는 메모리를 바로 읽음
  - 전환 복사 확인(B2): `FlipX`·`FlipY`·`BlurIntensity`는 자동 속성 backing field로 읽음, 액터 Transform 래퍼를 상태에 캐시, `TryCast<RenderTexture>`(래퍼 생성) 대신 `il2cpp_class_is_assignable_from`, 프레임 시각은 Host.Update가 한 번 읽음 → 프레임당 0.13 → 0.071~0.077 ms(12번). DrawLayers 0.045~0.053 → 0.028 ms(11번)
  - 무대 LateTick: RT 크기·후처리·깊이 재확인(`Resize`·`ApplyCameraData`, interop 약 20번)을 16프레임마다(장면이 바뀐 프레임에는 바로), 카메라 Transform·화면 이미지 CanvasRenderer 래퍼 캐시, 참조 비교, 위치·회전은 필드 비교 → 0.105 → 0.086 ms
  - 모드 합계(실험실 `modCpu`) 0.31 → 0.23 ms [관찰: 21차]
  - 법정 렌더러를 최상위 묶음별로 나눠 꺼진 묶음을 건너뛰는 시도는 이 장면에서 차이가 없어(Court 0.051~0.069 ms) 지움
- **경계 비용** (20차): 진단용 TrueShadow 패치 2개를 상시 설치하면 그림자 61개 × 2 = 프레임당 122번 네이티브↔관리 경계를 지나 +0.06 ms(빈 프리픽스만 붙인 `18_shadowHooked` 338.7 fps vs 기준 344~347 fps, 호출 1번에 약 0.5 µs). 0.8.0 개발본에 한동안 상시 설치돼 있었음 → 실험실 프리셋이 측정하는 동안만 `Harmony.Patch`로 붙이고 `UnpatchSelf`로 뗌. 그림자 갱신을 멈춘 `19_shadowFreeze` 368 fps → TrueShadow 원래 비용 약 0.17 ms [관찰]
- 메인 카메라 그림자 준비 끄기(`renderShadows=false` → 그림자 거리 0 → 컬링에서 ShadowCasters 빠짐, 19차 다른 작업의 후보 1): 354.4 vs 기준 약 354 fps → 효과 없음. 장면의 켜진 조명은 법정 SpotLight 하나이고 원래 `shadows=None` [관찰: 진단 덤프 20차]. 채택 안 함
- 진단 상한 (18차, 실험실 `18_shadowFreeze`·`19_noEventSystem`, 기준 약 340 fps) [관찰]:
  - UI 그림자 TrueShadow 갱신을 모두 건너뜀: −0.23~0.29 ms (372~375 fps). 켜진 그림자 61개(에셋 포함 805개)
  - EventSystem 끔: −0.12~0.29 ms (358~375 fps)
- TrueShadow 매 프레임 일 [관찰: ISIL `Prof isil`]: `LateUpdate`(D76620)는 그림자 오브젝트를 `SetActive(원본 그래픽 켜짐)`으로 매번 맞추고 `ShouldPerformWorks`(D76440)면 `CheckTransformDirtied`(D78620, Transform·색 추적기를 제네릭 대리자로 비교). `OnWillRenderCanvas`(D767B0)는 켜짐·`ShouldPerformWorks` 확인 뒤 텍스처가 더러울 때만 `ShadowFactory.Get`으로 다시 만들고, 그림자의 부모·형제 순서를 매번 확인(`Transform.GetSiblingIndex`·`SetParent`). 그림자 하나에 네이티브 호출 15~20번. 이 검사들이 곧 변화 감지라서 같은 결과를 지키며 건너뛸 조건이 없음 → 보류
- UI 레이캐스터 [관찰: 진단 덤프]: 켜진 레이캐스터 25개, 검사 대상 그래픽 합계 약 60개. 그려지지 않는(depth −1) 것은 옵션 메뉴 하위 캔버스의 약 25개뿐 → 꺼진 캔버스 레이캐스트 생략은 이득이 작음
- Naninovel `TransitionalSpriteRenderer.Update`(5F7970): 액터마다 매 프레임 머티리얼 값 쓰기 + `PrepareRenderTexture` + `Graphics.DrawMesh` 1~2번 (액터 12명에 0.09 ms)
- 모드 부분별 시간에 `Court`(법정 변화 검사, LateTick 안)·`Masks`(F2 판단) 칸을 더함. 재판 대화 프레임당: 전환 복사 확인 0.13~0.15 ms(12번), LateTick 0.13~0.15 ms(그중 Court 0.07), Update 0.06, DrawLayers 0.05(11번), Masks 0.02 [관찰: Claude 앱 GPU 사용 중이라 실제보다 큼]

### H. 별도 CPU 연구 인계 (2026-10-19, 19차)

**런타임 기능 추가 없음, 개발 버전 0.8.0 유지.** `b6b4768` 기준의 Canvas 탐색·클리핑·렌더 제출 연구는 [mahosaba-ui.md 6장](mahosaba-ui.md#6-2026-10-19-별도-cpu-연구-19차-미채택)에 전체 결과를 남겼다.

- Canvas null 결과 캐시는 프레임당 약 70번의 탐색을 제거했지만, 새 패치 없는 기준과 전체 성능이 겹쳐 실험 코드·설정을 제거했다. `CanvasLookupReuse`는 현 기능이 아니다.
- 활성 RectMask2D 16개 중 빈 대상은 1개. 정지 마스크·TMP·렌더 그래프는 엔진에 기존 변화 검사/캐시가 있어 일반적인 중복 캐시는 구현하지 않았다.
- 후속 후보: 그림자 없는 카메라의 ShadowCasters 컬링 준비, TSR의 일회성 DrawMesh 제출을 상주 렌더러로 대체. **둘 다 미구현·미측정**, 일반 게임 화면까지 그림자/정렬/전환 보존 조건부터 확인해야 한다.
- 19차는 수동 저장 11번의 두 캐릭터 대화라 18차의 372 fps와 직접 비교하지 않는다. 다른 작업의 모드 자체 이벤트 전환이나 TrueShadow 반속도는 건드리지 않았다.
- 실험용 DLL을 원래 0.8.0으로 복원, 사용자 값 보존 후 `GpuTimer/LayerDiag/RenderLab=false`, 게임 종료. 원본 GameAssembly·metadata 해시 유지. 릴리스·catalog 변경 없음.

### 채택하지 않는 것

| 힌트 | 이유 |
|---|---|
| Async (병렬 처리) | 노벨 진행·Unity API는 메인 스레드 전용. 렌더는 이미 gfx-jobs(`gfx-threading-mode=6`) [관찰] |
| Opaque Ice (투명 → 불투명) | B1·B2 뒤에는 투명 쿼드 몇 장만 남아 이득이 작음 [추론] |
| 메인 카메라 Render Scale + FSR | 메인 카메라에 UI·대사가 같이 그려져 글자가 흐려짐 (mahosaba.md 7장). B3이 같은 효과를 월드에만 줌 |
| 투명 UI 그리기 생략 (`cullTransparentMesh`, CanvasGroup 알파 0 그래픽 `cull`) | 모든 그래픽이 이미 `cullTransparentMesh=true`. 알파 0인 화면 크기 그래픽 46개를 `cull`해도 GPU 차이 0 → Unity가 이미 건너뜀 [관찰: 15·16차] |
| URP 렌더 그래프 끄기 (호환 모드, `RenderGraphSettings.m_EnableRenderCompatibilityMode`를 Load에서 켬. 속성 setter는 플레이어 빌드에서 예외) | 더 느림: 345 → 280 fps, 메인 카메라 GPU 0.82 → 1.85 ms (호환 경로가 패스를 더 씀) [관찰: 17차] |
| 하이브리드 CPU 스레드 배치 (CPU 세트로 메인·렌더 스레드를 P 코어에, 나머지를 E 코어에) | i5-1235U에서 효과 없음: 윈도우에 맡김 303, P 코어 고정 289, 나머지 E 코어 306 fps, E 코어 쪽은 p99가 나빠짐 [관찰: 16차] |

## 3. 검증 절차 (렌더 실험실)

1. 설정 `Debug.RenderLab = true`
2. 캐릭터가 나오는 장면(대화) 또는 재판 장면에서 **F10**
3. 프리셋 24개를 8초씩(안정 2초 + 측정 6초, 약 3분 25초). 측정하는 6초 동안은 안내 상자(IMGUI, 프레임당 약 0.13 ms)를 숨김 (0.8.0부터, 그 전 기록의 fps는 그만큼 낮음: 17차 기준 317 → 345). 매번 설정값으로 되돌린 뒤 하나만 바꿈 (0.8.0. 0.7.0은 9개, 0.5.x·0.6.x는 6개, 0.4.x·0.3.x는 7개, 0.2.3은 10개, 0.2.2는 12개, 0.2.1은 11개, 0.2.0은 14개, 0.1.x는 누적 7단계). 결론이 난 실험(SSAO 품질, 원본 불, 반 프레임, 해상도 75%, 화면 컬링, CourtLite, 건물 캐시, 빠른 감지)은 뺌

   | 프리셋 | 내용 |
   |---|---|
   | `00_original` | 모든 최적화 끔, 무대 RT 1920x1080 |
   | `01_current` | 지금 설정 (기준) |
   | `02_noStage` | 진단: 무대 카메라를 모두 멈춤(RT는 마지막 그림). 남는 시간 = 메인 카메라·UI·캐릭터·CPU |
   | `03_stageChangeOnly` / `04_stageChangeOnlyOff` | B16 켬 / 끔 (설정과 같은 쪽은 기준과 같음). 둘의 차이 = 효과, `02`가 상한. `stageSkip`이 2에 가까울수록 많이 건너뜀(카메라 2대) |
   | `05_current` | 기준을 다시 잼. `01`과 차이 = 발열·잡음 → 두 기준을 시간에 따라 보간해 Δ를 냄 |
   | `06_revealOn` / `07_revealOff` | 대사 갱신 재사용 켬 / 끔. 같은 대사를 완전히 표시한 상태로 유지 (`mahosaba-ui.md`) |
   | `08_current` | UI 비교 뒤 원래 설정을 다시 잼 |
   | `09_mainPostOn` | 메인 카메라 후처리 켬 (게임 옵션 값). 기준과 차이 = F1 효과 |
   | `10_noUi` / `11_noStageScreen` / `12_noWorld` | 진단: 메인 카메라에서 UI 레이어 / 법정 화면 캔버스 / 월드 레이어를 뺌. 화면이 잠깐 이상해짐. GPU 타이머로 그 몫을 잼 |
   | `13_current` | 진단 뒤 기준을 다시 잼 |
   | `14_screenMaskOn` / `15_current` | 법정 화면 마스크 생략 끔 / 기준. 차이 = F2 효과 |
   | `16_steamEveryFrame` / `17_current` | Steam 콜백 매 프레임 / 기준. 차이 = G1 효과 |
   | `18_shadowHooked` / `19_shadowFreeze` / `20_noEventSystem` / `21_current` | 진단: UI 그림자 두 메서드에 빈 패치만(경계 비용) / 그림자 갱신 전부 건너뛰기(그림자가 멈춤) / EventSystem 끔(입력 없음). 진단 패치는 이 동안만 붙임 |
   | `22_raycastEveryFrame` / `23_current` | UI 마우스 검사 재사용 끔 / 기준. 차이 = G2 효과 |

4. 측정 중에는 프레임 제한·VSync를 끔. 결과는 `BepInEx/LXIUM_mahosaba/<시각>/lab.txt`와 프리셋별 스크린샷(JPG)
5. 기록 값: fps, 프레임 시간 avg·p50·p95·p99·max, 프레임당 카메라 렌더 수, `layerDraw`/`layerSkip`(B1), `copyDraw`/`copySkip`(B2), `stageSkip`(B4·B6), `modCpu`(모드가 프레임당 쓰는 메인 스레드 시간: 패치·Host·카메라 콜백·화면 패널, 0.2.3)
6. 스크린샷을 `01_current`와 비교해 화면 변화를 확인. UI 장식·캐릭터가 움직이므로 법정 가운데(캐릭터·글자 없는 곳)만 잘라 비교
7. `FrameTimingManager`는 이 빌드에서 켜져 있다고 나오지만 D3D11에서 값이 오지 않아(4·5차) 0.2.2에서 기록을 뺌
8. 측정 중 화면 패널(PgUp·PgDn)은 켜 둘 수 있음: 플래그 패널에 실험실이 바꾸는 값이 그대로 보이고, 스크린샷을 찍는 프레임에는 숨김
9. `Character.Resolution`은 액터 생성 때 적용되므로 실험실 안에서 바뀌지 않음. 값을 바꾸고 다시 실행해 비교
10. `Debug.GpuTimer`가 켜져 있으면 `gpu`(프레임당 GPU 합)·`mainCam`(메인 카메라)도 적음. 각 줄의 `at=`는 측정 시작 시각 (ETW 추적·성능 카운터와 맞출 때)
11. `Debug.LayerDiag`가 켜져 있으면 시작할 때 켜진 루트 캔버스와 화면의 4분의 1보다 큰 그래픽(화면 안 넓이, 상속 알파, 색 알파, `cullTransparentMesh`, 클릭 판정 여부, 마스크), 법정 화면 캔버스 상세(셰이더·마스크·RT 형식), 메인 카메라 HDR·MSAA를 로그에 남김
12. **측정 환경**: 다른 프로그램이 GPU를 쓰면 결과가 크게 흔들림. 16차에 이 PC의 Claude 앱이 도구 실행 중 GPU 3D 엔진의 약 50%를 써서, 마스크 생략이 GPU를 1 ms 줄이고도 fps가 떨어지는 결과가 나왔음. 앱이 쉬는 동안 실험실을 돌리자 반대로 +10~20%. `typeperf "\GPU Engine(*engtype_3D)\Utilization Percentage"`로 프로세스별 사용률을 함께 기록할 것. 또 이 노트북(i5-1235U, 15 W급)은 CPU·GPU가 전력 한도를 나눠 써서 GPU 일을 줄이면 CPU 클럭이 오름 (UI를 뺀 진단에서 P 코어 2.1 → 2.56 GHz) → 어느 쪽을 줄여도 프레임이 빨라질 수 있음
13. **자동 시작** (0.8.0): `BepInEx/LXIUM_mahosaba/lab.start` 파일이 생기면 F10 없이 시작 (1초마다 확인, 게임을 켜기 전에 만든 파일은 지우기만 함). 자동 입력 도구의 F10은 창 포커스·Windows 입력 도우미 창(TextInputHost)에 막혀 자주 실패했음. 세이브 불러오기는 Win32 `SetCursorPos`+`mouse_event`(DPI 인식)로 클릭. 타이틀은 BepInEx 로그에 `stage cameras=`가 찍힌 뒤에도 약 15초 지나야 입력을 받음. 불러오기 완료는 로그의 `stage court:`로 판단
14. **자동화 범위**: 13번의 Win32 입력은 이전 회차의 외부 작업 기록이며 모드에 세이브 로더/회귀 테스트가 들어 있다는 뜻이 아니다. 19차는 창 화면을 확인하며 외부 클릭으로 수동 저장 11번을 골랐다. `lab.start`는 측정 시작만 담당한다. 대사·장면 고정과 새 패치가 아예 없는 기준을 확인하고, 예외 시 설정 복원까지 보장된 것으로 취급하지 않는다. [19차 검사·제외 기록](mahosaba-ui.md#6-2026-10-19-별도-cpu-연구-19차-미채택) 참조.

## 4. 설정 (`BepInEx/config/LXIUM_mahosaba.cfg`)

| 키 | 기본값 | 화면 변화 |
|---|---|---|
| `FrameRate.TargetFps` | 0 (모니터 주사율 + VSync) | -1 = 제한 없음(VSync 끔), N = N fps(VSync 끔) |
| `Urp.MsaaOff` | true | 없음 |
| `Urp.Ssao` | Off (0.2.2) | 768p·1080p 측정 모두 StageOnly·StageLite·Off의 법정 화소가 같음 |
| `Urp.IntermediateAuto` | true | 없음 |
| `Character.ChangeOnly` | true | 없음 |
| `Character.Resolution` | 0 (화면 맞춤) | 1440p 미만에서 캐릭터를 확대하는 연출은 약간 부드러워짐 |
| `Stage.Resolution` | 0 (화면 맞춤) | 1080p 이상에서는 원래와 같음. 그보다 낮은 화면에서는 원래 RT를 줄여 보이던 것을 화면 크기로 바로 그림. 0보다 큰 값은 고정 배율 |
| `Stage.HiddenSkip` | true | 없음 |
| `Stage.MsaaOff` | true (0.2.1) | 측정 장면에서 없음 (FXAA가 다듬음) |
| `Stage.PostOff` | true (0.2.1) | 거의 없음 (법정에 FXAA가 두 번 → 한 번, 평균 1.7/255). 게임 옵션에서 후처리를 끄면 무대 후처리는 켜진 채로 둠 |
| `Stage.HalfRate` | false | 카메라가 멈춰 있을 때 불·캐릭터 변화가 반 박자 늦음. `CourtChangeOnly`와 함께면 불처럼 매 프레임 바뀌는 장면에서만 차이 |
| `Stage.CourtLite` | true (0.3.2) | 원본과 같아야 함 (그림자·SSAO·반사 프로브가 원래 없는 법정). D3D11 전용 |
| `Stage.FireLite` | true (0.2.1) | 불 쿼드가 벽·바닥과 닿는 경계가 또렷해질 수 있음. 실제 불 연출에서 자세히 보면 원본과 조금 다름 (사용자 확인). D3D11 전용 |
| `Character.FastDetect` | true (0.4.0, 0.6.1부터 외형 이벤트 감지 E1) | 이벤트로 못 잡은 변화는 최대 2초 늦음 (검증 중 0번) |
| `Stage.CourtChangeOnly` | true (0.5.1) | 감지한 변화는 같은 프레임에 반영. 감지하지 못한 변화(법정 밖 연출 등)는 최대 1초 늦음. 불이 켜져 있으면 매 프레임 그림 |
| `Text.FastGlyphs` | true (0.6.0) | 동적으로 더해지는 글자에 합자·커닝이 적용되지 않음. 한글 음절은 합자가 없음 |
| `Urp.MainPostOff` | true (0.8.0) | 법정 가장자리는 같음 (법정 카메라 FXAA). UI 글자는 FXAA가 빠져 약간 선명 |
| `Stage.ScreenMaskSkip` | true (0.8.0) | 없음. 화면 분할 연출이 시작되는 프레임부터 마스크를 다시 씀 |
| `Cpu.SteamThrottle` | true (0.8.0) | 없음. Steam 알림이 최대 16 ms 늦음 |
| `Cpu.RaycastReuse` | true (0.8.0) | 없음. 멈춘 마우스 아래로 나타난 UI의 강조 표시가 최대 16 ms 늦음 |
| `Text.RevealReuse` | false (0.7.0) | 실험. 표시 완료 대사의 알파 갱신 생략, 화질 차이 없이 약 0.03 ms CPU 절감(한 장면). 전체 연출 검증 전 기본 끔 |
| `Debug.LayerDiag` | false (0.6.0) | — (분석용 로그) |
| `Debug.GpuTimer` | false (0.6.1) | — (분석용. 카메라별 GPU 시간) |
| `Debug.RenderLab` | false | — |

0.2.1 기본값은 4차 측정으로 정함. 이전 버전의 설정 파일에는 옛 기본값(false/StageOnly)이 적혀 있어 그대로 남으므로, 업데이트한 사용자는 직접 바꿔야 함 (BepInEx는 파일에 있는 값을 우선)

게임 옵션의 "포스트 프로세싱"을 끄면 메인 카메라에서 빠지는 것은 FXAA뿐이다 (볼륨 효과가 모두 0, mahosaba.md 4장). 글자가 약간 선명해지고 후처리 패스가 줄어든다 [추론].

## 5. 화면 패널 (0.4.0, `Hud`)

- 화면 오른쪽 위. IMGUI. 실험실 스크린샷 프레임에는 숨김. 그리기 이벤트(Repaint)에서만 그림. 0.6.0부터 패널·실험실 안내가 있을 때만 `OnGUI` 컴포넌트를 켬 (D4)
- 글자: 줄바꿈 없는 전용 `GUIStyle`(흰 글자), 줄 높이는 `CalcHeight("가Ag|")`로 실제 글꼴에서 잼 (0.4.1. 0.4.0은 글자 크기×1.45로 잡아 화면 비율에 따라 아래가 잘렸음). 설명 상자만 줄바꿈되는 기본 label 스타일
- **PgUp: 프레임 통계** — 최근 1초 창. FPS, 하위 1%(가장 느린 1% 프레임 시간의 fps), 상위 1%(가장 빠른 1%), 프레임 시간 평균, **바뀐 시간**(플래그가 바뀌면 그 직전 창의 평균을 기준으로 지금 평균의 차이), 모드 CPU(프레임당, 패널 비용 포함)
- **PgDn: 플래그 목록** — 지금 실제로 적용된 값(실험실이 바꾸는 값 포함)을 초록(켜짐)·빨강(꺼짐)으로. 마우스를 올리면 패널 왼쪽에 설명(기능·측정값·개선 가능성). 진단 플래그는 실험실이 도는 동안만 보임
- 이 게임은 새 Input System만 써서 IMGUI `Event.mousePosition`이 갱신되지 않음 → `Mouse.current.position`을 IMGUI 좌표로 바꿔 씀 [관찰: 0.4.0 실행 확인]
- PgUp·PgDn은 게임 입력 설정(Input System 바인딩)에 없음 [관찰: 번들 문자열]

## 7. 프로파일링 (ETW, 0.6.0)

개발 빌드가 아닌 게임이라 Unity 프로파일러를 쓸 수 없어서 Windows 이벤트 추적(ETW)으로 잰다.

1. 추적: `wpr -start tools\Prof\lite.wprp!Lite -filemode` → 게임 조작 → `wpr -stop out.etl` (관리자). `lite.wprp` = CPU 샘플(스택) + DXGI Present 이벤트만. 기본 `CPU` 프로필은 문맥 전환 스택 때문에 Unity 잡 스레드가 많으면 추적 자체가 게임을 크게 느리게 함(7.7 GB, 스택 걷기 19%)
2. 기호: UnityPlayer.dll은 Unity 심볼 서버(`https://symbolserver.unity3d.com`)에 릴리스 플레이어 PDB가 있음 (`UnityPlayer_Win64_player_il2cpp_x64.pdb`, 22.8 MB). GameAssembly.dll은 PDB가 없어 Cpp2IL로 메서드 주소표(9.5만 개)를 만듦 (`Prof map`)
3. 분석 (`tools/Prof`, TraceProcessor): `frames` = Present 간격으로 프레임 시간 통계·긴 프레임 목록, `tree` = 구간의 메인 스레드 호출 트리, `cpu` = 구간의 함수별 합계, `timeline` = 스레드별 CPU 사용
4. 진단 설정 `Debug.LayerDiag`: 5초마다 캐릭터 합성 횟수·원인(바뀐 레이어·항목)·원래 합성 시간·이벤트로 못 잡은 변화 수, D2 색인 조회 수·시간, 모드 부분별 프레임당 시간(`ModProf`)을 로그에 남김
5. **GPU 타이머** `Debug.GpuTimer` (0.6.1): D3D11 타임스탬프 쿼리를 카메라 렌더 앞뒤에 넣어 카메라별 GPU 시간을 5초마다 로그·화면 패널에. 쿼리는 Unity 렌더 스레드에서 써야 해서 `CommandBuffer.IssuePluginEvent` + `Graphics.ExecuteCommandBuffer`로 렌더 순서에 끼움 (URP 콜백이 넘겨준 `ScriptableRenderContext`로 `ExecuteCommandBuffer`를 부르면 interop 접근 위반으로 게임이 시작하자마자 꺼졌음). 결과는 4프레임 뒤에 읽어 GPU를 기다리지 않음. 0.8.0부터 프레임 시작·끝(`WaitForEndOfFrame`)에도 표시를 넣어 프레임 전체 GPU 시간과 카메라 밖 시간(`outsideCameras`)을 함께 적고, 실험실이 프리셋별 합을 읽음
6. **RenderDoc 메모**: Steam 게임이라 exe를 직접 켜면 Steam이 다시 실행해 주입이 풀림 → 잴 때만 게임 폴더에 `steam_appid.txt`(3101040)를 두고 끝나면 지움. 이렇게 켜도 **BepInEx가 로드되지 않음**(로그가 갱신되지 않음, RenderDoc과 doorstop이 둘 다 시작 단계를 가로채는 것으로 보임) → 캡처는 모드 없는 원래 게임의 프레임. F12는 Steam 스크린샷 키와 겹치고, PrintScreen은 Windows 캡처 도구를 엶. 분석 스크립트 `tools/Prof/rdc_frame.py`(qrenderdoc `--python`, 첫 실행 안내창을 끄는 설정 필요)
7. **CPU 코어별 표본** (`Prof cpu`, 0.8.0): 구간마다 스레드별로 어느 논리 프로세서에서 돌았는지(`== thread cpus`)와 추적 시작 시각을 출력. 하이브리드 CPU에서 메인 스레드가 E 코어에 머문 비율을 봄 (16차: 13~25%). 코어별 실제 클럭은 `typeperf "\Processor Information(0,N)\Actual Frequency"`
8. interop 형식 확인: `ilspycmd -t <형식> BepInEx/interop/<어셈블리>.dll` (`dotnet tool install -g ilspycmd`). 필드·속성 형식만 보임, 메서드 본문은 GameAssembly에 있음
9. 충돌 주소 풀기 (`Prof sym <dll> <pdb 폴더> <16진 오프셋>`, dbghelp): Windows 이벤트 로그의 `UnityPlayer.dll` 오프셋을 Unity 심볼 서버 PDB로 함수 이름으로. 17차 레이캐스트 재사용 충돌 = `GameObject_Get_Custom_PropTransform+0xA`
10. 게임 메서드 읽기 (`Prof isil <게임 폴더> <형식::메서드>...`): Cpp2IL로 메서드를 ISIL(호출 대상 이름이 풀린 명령 목록)로 출력. 분기 조건까지 보임


## 8. 미확인

- B2의 해시가 놓치는 입력이 있는지 (전환 중 시간 기반 효과 등). 놓쳐도 1초 안에 다시 그림
  - 확인한 것 [관찰: ISIL]: `TransitionalRenderer`의 `MainTexture`·`TransitionTexture`·`TransitionProgress`·`TintColor` 등은 필드가 아니라 `TextureMaterial`(`TransitionalMaterial`) 값을 읽고 씀. `TransitionTo`는 시작 때 `TransitionalMaterial.UpdateRandomSeed`를 부름 → 해시에 없는 입력은 전환 종류(`Transition`)·난수 씨앗·디졸브 텍스처. 셋 다 전환 시작 때만 바뀌고 그때 `TransitionTexture`·진행도도 함께 바뀌므로 놓칠 가능성은 낮음 [추론]
  - 사용자 보고 (0.5.1): 일반 대화에서 배경이 바뀔 때 조금 느리고 이전 장면이 잠깐 비치는 느낌 → 디스코드 화면 공유 때문이었음 (사용자 확인). 모드와 무관
- B3 적용 시 확대 연출의 화질
- C3을 실제 불 연출 장면에서 본 화질: 사용자 확인 — 자세히 보면 원본과 차이가 있으나 설정(`Stage.FireLite`)으로 끌 수 있어 기본 켬 유지
