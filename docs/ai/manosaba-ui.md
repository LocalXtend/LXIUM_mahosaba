# 대사·UI 최적화 연구와 인계 (AI용)

- 사람용: `../human/manosaba-ui.md`
- 조사일: 2026-10-18 KST. 출발 커밋 `52ca387` (0.6.1).
- 기존 렌더링·로딩 연구는 `manosaba-render.md`와 `manosaba.md`. 이 문서는 그 작업과 별도로 추가한 대사·UI 연구를 기록한다.
- 제약: 원본 exe·DLL·번들 수정 금지. BepInEx 런타임 모드로만 동작. Windows 10/11 대상으로, 화질과 진행 순서를 유지한다. Windows 10 별도 실기 검증은 하지 않았다.
- 600 fps는 목표이며 달성 결과가 아니다. CPU 절감과 GPU 병목 해소를 혼동하지 않는다. 메인 카메라 3~4.8 ms가 남으면 CPU만 줄여도 1.67 ms 예산에는 도달하지 못한다.

## 1. 현재 추가한 경로

`src/RevealWork.cs`는 **이미 표시가 끝난 대사의 글자 알파 갱신을 쉬게 하는 실험**이다. `[Text] RevealReuse`로 켜고 끈다. 기본값과 검증 결과는 아래 인계 상태에서 확인한다.

원본 `Naninovel.UI.RevealFader.Update`는 매 프레임 다음 일을 한다 [관찰: 네이티브 역어셈블]:

1. `textInfo.characterCount` 전체 순회. 글자 정보 구조체 크기는 0x178 바이트.
2. `EvaluateOpacity(i)`를 부르고 보이는 글자의 정점 4개에 알파를 기록.
3. `UpdateVertexData(Colors32 = 0x10)` 호출. 글자가 이미 완성됐다는 조기 반환은 없음.

지원 조건은 `supportCustomOpacity=false`, `slackOpacity>=1`, 유한한 양의 `length`, 준비된 TMP 데이터다. 마지막 글자의 표시가 끝났는지 먼저 확인하고, **모든 글자에 원래 `GetRevealRatio`를 호출해 1 이상인지 확인**한다. 원본 Update가 최종 알파를 한 번 적용한 다음 프레임부터 생략한다. 커스텀 알파·이전 글자 흐림·표시 중·dirty·초기화 전 상태는 원본으로 보낸다. 페이드 식을 다시 구현하지 않는다.

캐시 키에는 텍스트·textInfo·글자 배열·메시 배열·RevealState 포인터, 글자 수·마지막 표시 인덱스·length·변경 번호가 들어간다. 다음 패치가 캐시를 무효화한다:

- `RevealState.SetLast`, `Reset`, `SetLatAppendIndex`: 원본 상태 변경과 이벤트 전에 변경 번호 증가.
- `RevealFader.HandleTextChanged`: 같은 문장 재배치·TMP 메시 재생성도 번호 증가. 원본 콜백이 내부에서 즉시 Update를 부르므로 Prefix에서 증가해야 한다.
- `RevealFader.OnEnable/OnDisable`: 해당 인스턴스 캐시 제거.
- 설정 변경: 캐시 전체 제거. 예외 발생: 재사용을 끄고 원본 실행.

다른 모드가 위 이벤트 없이 메시의 정점 색상이나 내부 딕셔너리를 직접 쓰는 조합은 미검증이다. 그 조합에서는 `RevealReuse=false`로 원래 갱신 경로를 사용한다.

## 2. 빌드 식별과 패치 근거

입력 게임은 Unity 6000.0.48f1 IL2CPP다. 원본 파일은 읽기만 했다.

| 입력 | SHA-256 |
|---|---|
| GameAssembly.dll | `d19a9d98abe1f5d120588f23f7fe0645b8b94a2777517f90338d640e0e934506` |
| global-metadata.dat | `2ad884295e77d94d40dfe134392ab69fd8b6a94ca6bbd3b27e84cd3aa2f90fe5` |

REA의 interop 메타데이터 근거 `ev_e4ec1c86675dc681e53f3c33371b79aedffe82ae1e11360f8d8493fce4ee84e6`로 공개 메서드·필드를 확인했다. **interop 래퍼의 IL은 원본 게임 구현이 아니다.** 네이티브 본문은 pefile로 현재 PE의 RVA를 파일 오프셋에 매핑하고 Capstone으로 읽었다. 주소 범위 끝은 다음 알려진 메서드의 시작으로 잡은 검사 범위이며, Ghidra가 확정한 본문 범위가 아니다.

| 검사 범위 [RVA 시작, 끝) | 이름 | 해당 바이트 SHA-256 |
|---|---|---|
| `4A56D0–4A5970` | RevealFader.Update | `926f01a00793eb3857fc71d5219a01f7638af0c693928d90fdc96120c83168a8` |
| `4A5C30–4A5F50` | EvaluateOpacity | `bc7b15acc162d485e407f51c22daca9a8cd95a3f79522289b518d8ff52afb137` |
| `4A5FE0–4A6130` | HandleTextChanged | `2be41386cfade2be9a64e93a89e8c3f4df1d0bbdcb7c25d24bcb65c4449a726c` |

실제 패치 RVA: `Update 4A56D0`, `HandleTextChanged 4A5FE0`, `OnEnable 4A5330`, `OnDisable 4A54A0`, `SetLast 4A82D0`, `Reset 4A86A0`, `SetLatAppendIndex 4A84E0`.

**ICF 검사:** `ga_map.tsv`는 주소별로 이름 하나만 남기므로 주소 공유 여부를 증명하지 못한다. `tools/Prof/HookCheck.cs`는 Cpp2IL 전체 127,775개 메서드 선언(94,842개 네이티브 주소)을 중복 제거 전에 묶어 검사했다. 위 7개는 각각 선언 1개로 확인했다. 게임 업데이트 뒤 다시 실행하고, 공유된 주소는 패치하지 않는다.

```powershell
dotnet run --project tools/Prof -c Release -- check-ui-hooks "<게임 폴더>"
```

모드 자체는 게임의 IL2CPP interop 메서드를 Harmony로 패치하며 위 RVA를 코드에 하드코딩하지 않는다.

## 3. 제외한 후보

| 후보 | 관찰·판단 | 결론 |
|---|---|---|
| 꺼진 Canvas의 TrueShadow 검사 생략 | 타이틀에서 120회/프레임. 관리 코드로 두 캔버스를 확인하는 실험의 생략 0회. 검사 구간 CPU는 껐을 때 0.065~0.103 ms, 켰을 때 0.317~0.360 ms/프레임. 이 수치는 원본 전체 TrueShadow 비용이 아니라 같은 진단 훅 안의 검사 시간 | **실험 코드·참조·설정 모두 제거**. 이름 `Ui.HiddenShadowSkip`은 현 기능이 아님 |
| URP VolumeManager 캐시 | 현재 네이티브 `CheckUpdateRequired` RVA `1C08B70`에도 빈 볼륨 + 초기화 완료 시 조기 반환 있음. [Unity 해당 계열 소스](https://raw.githubusercontent.com/Unity-Technologies/Graphics/6000.0/staging/Packages/com.unity.render-pipelines.core/Runtime/Volume/VolumeManager.cs)와 같은 방향 | 별도 캐시 구현 안 함 |
| 모든 ScriptableUI Update 생략 | `Update 547530`는 가상 호출로 넘기고 `HandleNavigationFocus 547750`에 포커스·가시성 조건이 있음 | 입력·포커스 위험 대비 근거 부족, 구현 안 함 |
| 꺼진 Canvas의 GraphicRaycaster 생략 | 새 타이틀 ETW에서 입력 검사 8.5%, GraphicRaycaster 7.7% 메인 스레드 샘플. 그러나 투명 UI도 의도적으로 클릭을 막을 수 있고, 꺼진 캔버스가 차지하는 몫은 미측정 | 후보로 남김. 알파 0만으로 입력 검사 제거 금지 |

원본 TrueShadow `ShouldPerformWorks D76440`는 `isActiveAndEnabled && !(원본 cull && 그림자 cull)`를 사용한다. 원본 또는 그림자 한쪽만 cull된 것을 근거로 생략하지 않는다. `ShadowRenderer.LateUpdate`는 소멸 정리가 있어 무작정 끄지 않는다.

새 타이틀 ETW(0.6.1, 1080p, 5~25초 16,654 메인 스레드 샘플)는 TrueShadow.OnWillRenderCanvas 3.4%, Graphic.canvas/CacheCanvas 포함 1.5%였다. CPU 샘플 비중은 전체 프레임 ms나 GPU 비중이 아니다. 원본 추적과 로그는 개발 작업 공간 옆 `LXIUM-prof/`에 보관하며 배포 ZIP에는 넣지 않는다.

## 4. 재현·검증 방법

- 일반 빌드: `.NET SDK 10`, `dotnet build -c Release`.
- `[Debug] LayerDiag=true`: 5초마다 `text reveal`의 on/calls/skipped/cpu 및 적용을 막는 이유를 기록. 이 값에는 진단·Harmony 경계 비용이 들어가므로 순수 네이티브 함수 시간으로 부르지 않는다.
- F10 실험실은 기존 00~05 뒤에 `06_revealOn`, `07_revealOff`, `08_current`를 추가한다. 같은 대사가 모두 나타난 채 유지되도록 한다. 끝에는 원래 설정 복원.
- 자동 입력에서 기능키가 전달되지 않을 때: `dotnet build -c Debug -p:UiBench=true -p:Deploy=true`. `[Debug] RenderLab=true`인 실행에서 120초 뒤 off/on/off/on 각 8초 비교를 한 번 시작한다. 첫 2초 안정, 다음 6초 측정. **Debug에서만 유효**하고 Release에는 자동 시작 코드가 없다. 120초 안에 원하는 세이브를 불러오고 대사가 완전히 나타난 상태를 유지한다.
- 실험실은 `BepInEx/LXIUM_manosaba/<시각>/lab.txt`와 JPG를 기록. 최초 시험은 세이브 로딩·대사 없음 상태가 섞여 성능 결론에 쓰지 않았다.
- 화면 비교는 대사 영역을 우선하고 시계·다음 아이콘·오버레이 같은 애니메이션을 제외한다. 텍스트가 없는 프레임은 재사용 효과 검증이 아니다.
- 다음 대사 타이핑, 즉시 표시, 불러오기, 비활성화·재활성화, 같은 문장 재배치, 되감기, 루비·커스텀 알파를 검증하고 실제로 한 항목과 하지 않은 항목을 나눠 기록한다.

## 5. 인계 상태

**0.7.0 개발본에 포함, 기본 끔(`RevealReuse=false`).** 공식 README·런처 catalog는 릴리스 전이라 0.5.2 유지. 이번에 공식 릴리스·카탈로그 갱신·원격 push는 하지 않는다.

검증 환경: Windows 11, Iris Xe, D3D11, 1920×1080 FullScreenWindow, quality 0, VSync·프레임 제한 끔. 기존 사용자의 `Stage.HalfRate=true`를 포함한 설정 위에서 이 기능만 비교했다. `LayerDiag=true`, 켬·끔 모두 같은 Harmony 진단 훅이 설치된 상태이며 타이머에 그 비용도 포함된다. 컴파일된 버전 문자열은 실험 당시 0.6.1, 소스는 이 변경의 개발 빌드다.

유효 실험 폴더 `1018_201442`: 자동 저장 A1에서 불러온 재판의 대사를 진행해 메루루의 40글자 대사를 표시한 채 유지. 첫 로딩 시험 `1018_200908`은 대사 없음·전환이 섞여 제외.

| 구간 | fps | 평균 프레임 ms | p99 ms |
|---|---|---|---|
| 00_revealOff | 191.7 | 5.22 | 9.30 |
| 01_revealOn | 193.0 | 5.18 | 8.41 |
| 02_revealOff | 190.0 | 5.26 | 8.70 |
| 03_revealOn | 193.1 | 5.18 | 9.05 |

- 완성된 대사의 해당 갱신 구간 CPU: 끔 약 0.050 ms/프레임, 켬 0.018~0.019 ms/프레임. 약 0.03 ms 절감. 켬의 안정된 5초 창에서는 호출 966회 중 966회, 964회 중 964회 생략 [관찰].
- 전체 프레임 변화는 평균 5.24→5.18 ms, 약 1% 수준. **짧은 한 장면 시험으로 전체 FPS 향상이나 600 fps 달성을 주장하지 않는다.**
- 네 JPG의 대사 영역 `(x=410..1299, y=850..1039)`은 디코딩 RGB 값이 모두 같음(max 차이 0). 세 줄 전체를 포함하고 다음 아이콘은 제외했다. `python tools/check_ui_lab.py "<실험 폴더>"`로 재검증 가능(Pillow 필요). 이미지 영역 같음이 전체 연출 동등성 증명은 아님.
- 실제 확인: 로더 시작 정상, 기존 저장 불러오기, 40글자 대사 완료·재사용, 다음 16글자 대사로 진행·캐시 재생성. 진단에 새 문장의 글자 수·인덱스가 바뀐 뒤 생략으로 복귀했고 화면도 정상 표시됨.
- **미검증:** 독립적인 되감기, 같은 문장의 강제 재배치, 루비·커스텀 알파 연출, 다른 모드의 직접 정점 수정, Windows 10 실기. 오른쪽 클릭은 대사창 숨김을 확인하지 못했으므로 숨김·복귀 성공으로 세지 않는다. 전체 검증 전 기본 켬으로 바꾸지 않는다.
- 최종 배포 DLL은 자동 비교용 `UiBench` 정의 없이 빌드한다. 테스트 후 원래 사용자 설정을 복구하고 디버그 로그·실험실 자동 시작을 끈다. 실험 DLL을 0.6.1이라는 이름으로 공식 배포하지 않는다.
- 최종 확인: 0.7.0 Release 빌드 경고·오류 0, 패키지의 manifest·버전·DLL 일치, 자동 벤치 필드·프리셋이 Release에 없음. 일반 설정에서 로더의 `loaded 0.7.0`과 타이틀 표시 확인. 원래 사용자 설정값 전부 보존, `RevealReuse/GpuTimer/LayerDiag/RenderLab=false`. GameAssembly·metadata 해시 유지. 확인 후 게임 종료.

후속 작업은 위 미검증 경로와 입력 검사 비용의 실제 분해다. 기존 0.6.1 렌더링 기능의 검증을 이 UI 경로의 검증으로 대체하지 않는다.

## 6. 2026-10-19 별도 CPU 연구 (19차, 미채택)

**기준 커밋 `b6b4768`, 개발 버전 0.8.0. 이번 조사로 추가된 런타임 최적화는 없다.** 위 0.7.0의 측정과 구분한다. 다른 작업의 E1·B2·B6 이벤트 전환과 겹치지 않게 Canvas 탐색·클리핑·렌더 제출을 조사했다. TrueShadow 검사 주기를 줄이는 방식은 적용하지 않았다.

앞 절에서 후보였던 UI 입력 검사는 이후 0.8.0의 `Cpu.RaycastReuse`(렌더링 문서 G2)로 일부 구현되었다. 이번 조사는 그 기능을 대체하거나 되돌리지 않는다.

### Canvas가 없다는 결과도 저장하는 실험

현재 `Graphic.get_canvas` RVA `21F76B0`은 `m_Canvas`가 없으면 `CacheCanvas`(`21F7790`)를 부른다. 후자는 `GetComponentsInParent(false, list)`로 첫 활성 Canvas를 찾지만, 결과가 null이면 다음 조회에서 같은 탐색을 반복한다 [관찰: 현재 GameAssembly의 Cpp2IL ISIL]. [Unity uGUI 소스](https://raw.githubusercontent.com/Unity-Technologies/uGUI/main/com.unity.ugui/Runtime/UGUI/UI/Core/Graphic.cs)도 이 구조다. upstream과 일치한다는 것만으로 게임의 구현을 추정하지 않았으며 현재 바이너리와 대조했다.

실험은 null 결과만 포인터 집합에 보관했다. 원래의 양성 캐시는 그대로 두고 `OnCanvasHierarchyChanged`, `OnTransformParentChanged`, `OnEnable`, `OnDisable`, `OnDestroy` **Prefix에서 집합 전체를 비웠다**. 비활성 Graphic은 알림을 못 받을 수 있어 null 결과를 저장하지 않았다. 원본이 강제로 `CacheCanvas`를 부를 때도 앞선 무효화가 먼저 실행된다. 시간 간격을 두거나 UI 그림자를 늦추는 방식이 아니다.

- REA interop 멤버 근거: `ev_b47bbe19e661db5b99957fd8842a80f1612332b869c1de30879fe1396d70a715`. `UnityEngine.UI.dll` SHA-256 `59d1775e0d4a916e031b56606b96cddc82723d37eba86f60384221ad29841148`, MVID `cf951409-d813-4479-9e3f-8c164cba3112`. 래퍼 본문을 게임 구현으로 취급하지 않았다.
- 전체 메서드 선언 127,775개를 중복 제거 전에 검사. 위 6개 패치 주소는 모두 선언 1개: `21F7790`, `21F85F0`, `21F7470`, `21F8060`, `21F8260`, `21F8400`. `tools/Prof`의 새 `check-canvas-hooks` 명령으로 재검사 가능.
- Debug 내부 수명 검사 통과: null 연속 조회, Canvas 끔·켬, 활성/비활성 Graphic의 부모 이동, Graphic 비활성 상태에서 Canvas 켬, GameObject 비활성·복귀, Canvas 삭제 후 다음 프레임 조회·재추가. 검사 객체는 투명하며 끝에 제거했다. 전체 UI 연출·다른 모드 조합 검증은 아니다.

### 측정 전체와 결론

Windows 11 / Iris Xe / D3D11 / 1920×1080 FullScreenWindow, quality 0, 프레임 제한·VSync 끔. 기존 `Stage.HalfRate=true`, `Text.RevealReuse=false`를 포함한 사용자 설정에서 비교했다. **수동 저장 11번의 ‘메루루 짱……!’ 대화**를 그대로 유지했다. 18차의 자동 저장 장면과 다르므로 기존 372 fps에 이어 붙여 비교하지 않는다.

각 구간은 기존 실험실과 같이 안정 2초+측정 6초. `GpuTimer`, `LayerDiag`, 실험실 켬, 측정 중 안내 IMGUI 숨김. Off/On 모두 Harmony 경계가 있는 빌드를 두 번 실행했고, 별도 빌드에서는 **새 6개 패치 자체를 설치하지 않고** 같은 장면을 측정했다. `canvasLookup=0`으로 그 구분을 확인했다.

| 실행 / 구간 | fps | 평균 ms | p50 ms | p99 ms | GPU ms |
|---|---:|---:|---:|---:|---:|
| `1019_013058` 00 Off | 291.8 | 3.43 | 3.03 | 7.24 | 0.989 |
| 01 On | 336.4 | 2.97 | 2.63 | 6.08 | 0.954 |
| 02 Off | 329.9 | 3.03 | 2.72 | 6.19 | 0.905 |
| 03 On | 324.3 | 3.08 | 2.64 | 6.43 | 1.011 |
| `1019_013311` 00 Off | 298.8 | 3.35 | 2.98 | 6.96 | 0.974 |
| 01 On | 340.6 | 2.94 | 2.61 | 5.97 | 0.942 |
| 02 Off | 320.6 | 3.12 | 2.72 | 6.70 | 0.944 |
| 03 On | 334.9 | 2.99 | 2.61 | 6.31 | 0.945 |
| `1019_013755` 00 패치 없음 | 257.1 | 3.89 | 3.32 | 9.16 | 1.093 |
| 01 패치 없음 | 283.9 | 3.52 | 2.94 | 9.10 | 1.050 |
| 02 패치 없음 | 343.2 | 2.91 | 2.61 | 5.76 | 0.930 |
| 03 패치 없음 | 332.2 | 3.01 | 2.67 | 6.33 | 0.924 |

- **국소 결과:** 호출 약 70~70.4회/프레임 중 켬에서 거의 전부 탐색 생략. 부모 탐색 제거 자체는 확인했다.
- **채택 판단:** 패치 없는 뒤쪽 구간 332~343 fps와 두 번째 실행의 켬 335~341 fps가 겹친다(첫 실행의 켬은 324~336 fps). 첫 구간의 큰 변동, 관리/네이티브 경계 비용, CPU 클럭·스케줄링 영향을 분리하지 못했다. **순이득이 입증되지 않아 코드·설정·빌드 플래그·추가 interop 참조를 전부 제거했다.** ‘70회 생략’을 전체 FPS 개선으로 바꾸어 쓰지 않는다. 이 결과만으로 느려지는 원인을 Harmony라고 확정하지도 않는다.
- 첫 실행의 GPU 성능 카운터: 게임 평균 약 40%, `dwm` 약 15%, 나머지 관찰 프로세스는 평균 0.1% 미만. 다른 앱을 종료하거나 우선순위·코어 배치를 바꾸지 않았다. 노트북 전력·클럭 변동까지 통제한 장기 벤치마크는 아니다.
- 두 번째 실행 네 JPG의 대사 ROI `(410,850)–(1300,940)`와 법정 중앙 ROI `(800,200)–(1100,650)`는 디코딩 RGB 최대 차이 0. 캐릭터 눈 깜빡임·다음 아이콘을 제외한 제한된 비교다.
- 원본 로그/JPG, GPU CSV, 실험 소스 `CanvasLookup.cs`·`prototype.patch`, 패치 없는 Debug DLL은 작업 공간 옆 `LXIUM-prof/ui18/`에 보관했다. 제품 빌드 입력이나 배포 ZIP에 넣지 않는다. `CanvasLookupReuse`, `CpuBench`, `CpuBaseline`은 **현재 기능/설정/빌드 옵션이 아니다**.

### 엔진에 이미 있는 최적화와 보류 후보

| 후보 | 현재 근거 | 판단 |
|---|---|---|
| 정지한 RectMask2D 전체 생략 | `PerformClipping 2494B70`은 이미 이전 clip rect와 대상 `CanvasRenderer.hasMoved` 검사 | 중복 구현하지 않음. 이동하는 자식·입력 영역을 잘못 보존할 수 있음 |
| 대상 없는 RectMask2D 조기 반환 | 원본은 대상이 없어도 사각형을 계산. `AddClippable 24958B0`은 재계산·강제 클립 플래그를 켬. 이번 정지 장면은 활성 16개 중 빈 것 1개 | 미구현·미측정. 한 개의 일을 빼려고 16개 호출에 관리 훅을 추가하는 비용을 먼저 고려해야 함 |
| 정지한 TMP 전체 갱신 생략 | `TextMeshProUGUI.InternalUpdate 1ECE030`에 이미 배율 변화 검사, `TMP_UpdateManager.DoRebuilds 1F5E150`에 등록/dirty 큐 처리 | 중복 구현하지 않음 |
| 렌더 그래프 별도 컴파일 캐시 | `UpdateCurrentCompiledGraph 1CA40B0`이 `RenderGraphCompilationCache`를 사용. `RenderGraphGlobalSettings::.ctor 1C54BD0`의 캐시 기본값 켬 | 미구현. 실행 에셋의 직렬화 값이 다른지는 별도 확인 대상 |

### 다음 실험: 렌더 제출 경로 (아직 구현하지 않음)

1. **그림자 없는 카메라의 컬링 준비**: `UniversalRenderer.SetupCullingParameters 1E18270`은 에셋이 양쪽 그림자를 지원하지 않거나 `maxShadowDistance≈0`일 때 `ShadowCasters`를 뺀다. 실제 조명이 모두 `shadows=None`인지는 컬링 뒤에 판정할 수 있어 GPU 그림자 패스가 없어도 준비 비용이 남을 가능성이 있다 [관찰+추론]. `InitializeAdditionalCameraData 1E2EA70`은 `renderShadows=false`를 거리 0으로 바꾼다. [Unity 해당 계열 구현](https://raw.githubusercontent.com/Unity-Technologies/Graphics/6000.0/staging/Packages/com.unity.render-pipelines.universal/Runtime/UniversalRenderer.cs). **현재 장면에 그림자가 없다는 사실만으로 전체 게임에 일괄 적용하지 않는다.** 대상 카메라·새 조명 등장 시 복원 조건부터 확인하고 한 카메라에서 A/B 측정한다.
2. **액터의 매 프레임 DrawMesh 제출을 상주 MeshRenderer로 대체**: `TransitionalSpriteRenderer.Update 5F7970`은 재질·전환 RT 처리 뒤 액터당 `Graphics.DrawMesh` 1~2회(`5F7D3C`, `5F7DDB`)를 제출한다. [Unity API 문서](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Graphics.DrawMesh.html)는 이 제출이 한 프레임 동안 유효하고 내부 자원도 유지한다고 설명한다. 같은 mesh/material을 런타임 MeshFilter/MeshRenderer로 유지하면 반복 제출을 줄일 후보지만 **실제 이득은 미측정**이다. 원본 변환은 `TRS(world position, world rotation, localScale)`이며 `lossyScale`이 아니다. 프록시를 액터 자식으로 두면 부모 스케일·전단이 추가될 수 있다. 한 액터부터 레이어·투명 정렬·깊이용 두 번째 제출·메시 교체/파괴·이동/확대/회전·페이드/전환·법정 분할을 비교한다. 전환 RT 경로를 먼저 없애지 않는다.

17차 `tree11`의 렌더 표본 25.3%, 컬링 4.4%, `GetOrCreateSharedRendererScene` 1.8%를 이 후보의 절감량으로 간주하면 안 된다. TSR Update의 기존 약 0.09 ms와도 단순 합산하지 않는다. 새 패치 전에는 해당 메서드의 전체 선언 주소 공유 검사부터 한다.

### 자동화 범위와 최종 인계

- `lab.start`는 **실험실 시작 신호**다. 프리셋 전환·프레임 통계·JPG 수집·정상 종료 시 설정 복원을 자동화한다. 게임 전체 회귀 테스트나 세이브 로딩 스크립트는 아니다. 정상 종료 복원 코드를 예외 시 복원 보장으로 읽지 않는다.
- 이전 회차 Win32 클릭 자동화와 이번 외부 창 클릭은 별개다. 이번에는 창 상태/스크린샷을 확인하고 Load Game→수동 저장 11번만 선택했다. Auto·저장 덮어쓰기·삭제 버튼은 누르지 않았다. 초기 A1/A2 불러오기는 시간 제한 토론으로 이어져 성능 자료에서 제외했다. 새 자동 저장 유무는 사전/사후 파일 해시로 검사하지 않았으므로 ‘저장 데이터 변화 없음’을 확정하지 않는다.
- 화면·대사가 고정되었는지 확인한 뒤 트리거를 만든다. 측정 중 입력·도구 실행을 쉬고 첫 구간의 큰 변동도 원본 결과에 남긴다. Off/On 비교 외에 **후보 패치가 전혀 없는 기준**을 반드시 둔다.
- 원래 설치된 0.8.0 DLL을 바이트 그대로 복원했다(SHA-256 `4779755f8721828ad6cfe2dc88fd50395dea155d9af3eaf91be6cc5875274a91`). 사용자 설정을 복원한 뒤 `GpuTimer/LayerDiag/RenderLab`만 false로 정리했다. 테스트용 Canvas 설정은 남기지 않았다. 게임 종료, GameAssembly·metadata는 2장의 해시와 같음.
- 버전 0.8.0 유지. 릴리스·원격 push·catalog·README 버전 변경 없음. 남는 코드는 분석 도구의 `check-canvas-hooks`뿐이다. Windows 10 별도 실기와 후속 렌더 제출 후보의 성능/화질은 미검증이다.

```powershell
dotnet run --project tools/Prof -c Release -- check-canvas-hooks "<게임 폴더>"
```
