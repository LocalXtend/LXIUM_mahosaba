# 대사·UI 최적화 연구와 인계 (AI용)

- 사람용: `../human/mahosaba-ui.md`
- 조사일: 2026-10-18 KST. 출발 커밋 `52ca387` (0.6.1).
- 기존 렌더링·로딩 연구는 `mahosaba-render.md`와 `mahosaba.md`. 이 문서는 그 작업과 별도로 추가한 대사·UI 연구를 기록한다.
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
- 실험실은 `BepInEx/LXIUM_mahosaba/<시각>/lab.txt`와 JPG를 기록. 최초 시험은 세이브 로딩·대사 없음 상태가 섞여 성능 결론에 쓰지 않았다.
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
