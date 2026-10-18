using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LXIUM_mahosaba
{
    // 렌더 실험실 (docs/ai/mahosaba-render.md 3장). 설정 Debug.RenderLab=true일 때 F10으로 시작.
    // 게임 원래 상태와 지금 설정을 잰 뒤, 지금 설정에서 하나씩만 바꿔 8초씩(안정 2초 + 측정 6초)
    // 프레임 통계·스크린샷을 남긴다. 마지막에 지금 설정을 다시 재서 발열 등으로 생긴 차이를 본다.
    // 끝나면 설정값으로 되돌린다. 측정 중에는 프레임 제한과 VSync를 끈다.
    static class RenderLab
    {
        const float Settle = 2f, Sample = 6f;
        static readonly string Dir = Path.Combine(Paths.BepInExRootPath, "LXIUM_mahosaba");

        static int _cameraRenders;
        // 모드가 매 프레임 쓰는 메인 스레드 시간 (패치·Host·카메라 콜백). Stopwatch 틱
        internal static long ModTicks;
        static bool _running;
        internal static string Status;
        // 스크린샷을 찍는 프레임. 화면 패널(Hud)을 숨김
        internal static bool Capturing;
        internal static bool Running => _running;

        public static void CountCamera() => _cameraRenders++;

        public static void Tick(Host host)
        {
#if DEBUG && LXIUM_UI_BENCH
            // 입력 자동화가 새 Input System의 기능키를 전달하지 못할 때 쓰는 개발 전용 비교 실행.
            if (!_autoStarted && Settings.Lab.Value && Time.unscaledTime > 120f)
            {
                _autoStarted = _running = true;
                host.StartCoroutine(Run().WrapToIl2Cpp());
            }
#endif
            if (_running || !Settings.Lab.Value) return;
            var kb = Keyboard.current;
            if ((kb == null || !kb.f10Key.wasPressedThisFrame) && !Triggered()) return;
            _running = true;
            host.StartCoroutine(Run().WrapToIl2Cpp());
        }

        // 자동 측정용: BepInEx/LXIUM_mahosaba/lab.start 파일이 생기면 F10 없이 시작 (키 입력은 창 포커스·입력 도우미 창에 막힐 수 있음).
        // 1초마다 확인
        static float _nextPoll;

        static bool Triggered()
        {
            if (Time.unscaledTime < _nextPoll) return false;
            _nextPoll = Time.unscaledTime + 1f;
            string path = Path.Combine(Dir, "lab.start");
            if (!File.Exists(path)) return false;
            // 게임을 켜기 전에 만든 파일은 지난 실행의 것이라 지우기만 함
            bool fresh = File.GetLastWriteTime(path) >= System.Diagnostics.Process.GetCurrentProcess().StartTime;
            File.Delete(path);
            return fresh;
        }

        // 각 실험은 ApplySettings() 뒤에 적용됨. null이면 지금 설정 그대로
        static readonly (string Name, Action Apply)[] Presets =
        {
#if DEBUG && LXIUM_UI_BENCH
            ("00_revealOff", () => RevealWork.Enabled = false),
            ("01_revealOn", () => RevealWork.Enabled = true),
            ("02_revealOff", () => RevealWork.Enabled = false),
            ("03_revealOn", () => RevealWork.Enabled = true),
#else
            ("00_original", () =>
            {
                UrpTuning.Set(false, SsaoMode.Game, false);
                ActorRender.ChangeOnly = false;
                StageRender.HiddenSkip = StageRender.MsaaOff = StageRender.PostOff = StageRender.HalfRate = StageRender.ChangeOnly = StageRender.MainPostOff = StageRender.MaskSkip = false;
                StageRender.ResolutionOverride = 1f;
                CourtShaders.Fire.On = CourtShaders.Court.On = false;
                ActorRender.FastDetect = false;
                RevealWork.Enabled = false;
                CpuTuning.SteamThrottle = CpuTuning.RaycastReuse = false;
            }),
            ("01_current", null),
            // 진단: 법정 카메라를 멈춤. 남는 시간 = 메인 카메라(UI·대사·무대 화면 붙이기) + 캐릭터 + CPU
            ("02_noStage", () => StageRender.Freeze = true),
            // 법정 카메라를 장면이 바뀐 프레임에만 그림. 03과 04의 차이 = 효과, 02가 상한
            ("03_stageChangeOnly", () => StageRender.ChangeOnly = true),
            ("04_stageChangeOnlyOff", () => StageRender.ChangeOnly = false),
            ("05_current", null),
            ("06_revealOn", () => RevealWork.Enabled = true),
            ("07_revealOff", () => RevealWork.Enabled = false),
            ("08_current", null),
            // 진단: 메인 카메라(GPU의 대부분, 14차)를 한 부분씩 빼고 GPU 타이머로 그 몫을 잼. 화면이 잠깐 이상해짐
            // 메인 카메라 후처리 켬 (게임 옵션 값). 01과의 차이 = Urp.MainPostOff 효과
            ("09_mainPostOn", () => StageRender.MainPostOff = false),
            ("10_noUi", () => MainDiag("ui")),
            ("11_noStageScreen", () => MainDiag("screen")),
            ("12_noWorld", () => MainDiag("world")),
            ("13_current", null),
            // 법정 화면 마스크 생략 끔. 13과의 차이 = Stage.ScreenMaskSkip 효과
            ("14_screenMaskOn", () => StageRender.MaskSkip = false),
            ("15_current", null),
            // Steam 콜백 빈도 제한 끔. 15와의 차이 = 효과
            ("16_steamEveryFrame", () => CpuTuning.SteamThrottle = false),
            ("17_current", null),
            // 진단: UI 그림자 갱신 전부 건너뛰기 / EventSystem 끄기(측정 동안 입력 없음). 기준과의 차이 = 그 비용의 상한
            ("18_shadowFreeze", () => CpuTuning.ShadowFreeze = true),
            ("19_noEventSystem", () => { _diagEvents = UnityEngine.EventSystems.EventSystem.current; if (_diagEvents != null) _diagEvents.enabled = false; }),
            ("20_current", null),
            // UI 마우스 검사 재사용 끔. 기준과의 차이 = 효과
            ("21_raycastEveryFrame", () => CpuTuning.RaycastReuse = false),
            ("22_current", null),
#endif
        };
#if DEBUG && LXIUM_UI_BENCH
        static bool _autoStarted;
#endif

        // 메인 카메라 진단에서 바꾼 값 (ApplySettings가 되돌림)
        static Camera _diagCam;
        static int _diagMask;
        static bool _diagPost;
        static Canvas _diagCanvas;
        static UnityEngine.EventSystems.EventSystem _diagEvents;

        static void MainDiag(string part)
        {
            var cam = Camera.main;
            if (cam == null) return;
            var data = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            _diagCam = cam;
            _diagMask = cam.cullingMask;
            _diagPost = data != null && data.renderPostProcessing;
            switch (part)
            {
                case "ui": cam.cullingMask &= ~(1 << LayerMask.NameToLayer("UI")); break;
                case "world": cam.cullingMask &= ~1; break;
                case "screen":
                    _diagCanvas = StageRender.ScreenCanvas;
                    if (_diagCanvas != null) _diagCanvas.enabled = false;
                    break;
            }
        }

        static void RestoreDiag()
        {
            if (_diagEvents != null) _diagEvents.enabled = true;
            _diagEvents = null;
            if (_diagCam == null) return;
            _diagCam.cullingMask = _diagMask;
            var data = _diagCam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (data != null) data.renderPostProcessing = _diagPost;
            if (_diagCanvas != null) _diagCanvas.enabled = true;
            _diagCam = null;
            _diagCanvas = null;
        }

        public static void ApplySettings()
        {
            RestoreDiag();
            UrpTuning.Set(Settings.MsaaOff.Value, Settings.Ssao.Value, Settings.IntermediateAuto.Value);
            ActorRender.ChangeOnly = Settings.CharacterChangeOnly.Value;
            ActorRender.FastDetect = Settings.FastDetect.Value;
            RevealWork.Enabled = Settings.RevealReuse.Value;
            StageRender.HiddenSkip = Settings.StageHiddenSkip.Value;
            StageRender.MsaaOff = Settings.StageMsaaOff.Value;
            StageRender.PostOff = Settings.StagePostOff.Value;
            StageRender.HalfRate = Settings.StageHalfRate.Value;
            StageRender.ChangeOnly = Settings.StageChangeOnly.Value;
            StageRender.MainPostOff = Settings.MainPostOff.Value;
            StageRender.MaskSkip = Settings.StageMaskSkip.Value;
            CpuTuning.SteamThrottle = Settings.SteamThrottle.Value;
            CpuTuning.RaycastReuse = Settings.RaycastReuse.Value;
            CpuTuning.ShadowFreeze = false;
            // 설정이 바뀌면 법정 그림도 달라질 수 있으므로 한 번 다시 그림
            StageRender.Dirty = true;
            StageRender.ResolutionOverride = null;
            StageRender.Freeze = false;
            StageRender.CullHidden = true;
            CourtShaders.Fire.On = Settings.FireLite.Value;
            CourtShaders.Court.On = Settings.CourtLite.Value;
        }

        // 진단 (Debug.LayerDiag): 켜진 루트 캔버스와 화면의 4분의 1보다 큰 그래픽을 로그에 남김 (메인 카메라 UI 비용 분석)
        static void DumpCanvases()
        {
            float screen = Screen.width * Screen.height;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Canvas>()))
            {
                var c = o.Cast<Canvas>();
                if (!c.isActiveAndEnabled || !c.isRootCanvas) continue;
                int drawn = 0, big = 0;
                var lines = new List<string>();
                foreach (var g in c.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
                {
                    if (!g.isActiveAndEnabled || g.canvasRenderer.cull) continue;
                    drawn++;
                    var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                    g.rectTransform.GetWorldCorners(corners);
                    var cam = c.renderMode == RenderMode.ScreenSpaceOverlay ? null : c.worldCamera;
                    Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, corners[0]), b = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                    // 화면 밖으로 나간 부분은 GPU가 그리지 않으므로 화면 안 넓이만 셈
                    float w = Mathf.Min(Mathf.Max(a.x, b.x), Screen.width) - Mathf.Max(Mathf.Min(a.x, b.x), 0f);
                    float h = Mathf.Min(Mathf.Max(a.y, b.y), Screen.height) - Mathf.Max(Mathf.Min(a.y, b.y), 0f);
                    float area = Mathf.Max(w, 0f) * Mathf.Max(h, 0f);
                    if (area < screen * 0.25f) continue;
                    big++;
                    var cr = g.canvasRenderer;
                    lines.Add($"    {g.name} {g.GetIl2CppType().Name} tex={g.mainTexture?.width}x{g.mainTexture?.height} inherited={cr.GetInheritedAlpha():F2} color={g.color.a:F2} ctm={cr.cullTransparentMesh} ray={Raycastable(g)} mask={g.GetComponent<UnityEngine.UI.Mask>() != null} area={area / screen:F2}");
                }
                int groups = 0, ignoring = 0;
                foreach (var cg in c.GetComponentsInChildren<CanvasGroup>(false))
                {
                    groups++;
                    if (cg.ignoreParentGroups) ignoring++;
                }
                var ray = c.GetComponent<UnityEngine.UI.GraphicRaycaster>();
                Plugin.L.LogInfo($"canvas {c.name} mode={c.renderMode} order={c.sortingOrder} cam={c.worldCamera?.name} raycaster={ray != null && ray.enabled} groups={groups} ignoreParent={ignoring} drawn={drawn} big={big}");
                foreach (var l in lines) Plugin.L.LogInfo(l);
            }
            // 법정 화면 캔버스(메인 카메라 GPU의 약 70%, 15차)의 그리기 상세
            var sc = StageRender.ScreenCanvas;
            if (sc != null)
                foreach (var g in sc.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
                {
                    var m = g.GetComponent<UnityEngine.UI.Mask>();
                    var rt = g.mainTexture?.TryCast<RenderTexture>();
                    Plugin.L.LogInfo($"screen {g.name} {g.GetIl2CppType().Name} cull={g.canvasRenderer.cull} shader={g.materialForRendering?.shader?.name} " +
                                     $"mask={(m == null ? "-" : $"{m.enabled}/show={m.showMaskGraphic}")} " +
                                     (rt == null ? "" : $"rt={rt.width}x{rt.height} {rt.graphicsFormat} aa={rt.antiAliasing} mip={rt.useMipMap} depth={rt.depthStencilFormat}"));
                }
            int shadows = 0, shadowsOn = 0;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<LeTai.TrueShadow.TrueShadow>()))
            {
                shadows++;
                if (o.Cast<LeTai.TrueShadow.TrueShadow>().isActiveAndEnabled) shadowsOn++;
            }
            Plugin.L.LogInfo($"true shadow: {shadowsOn} 켜짐 / {shadows}");
            // UI 레이캐스터마다 캔버스 상태와 검사하는 그래픽 수 (꺼진 Canvas의 그래픽은 depth -1이라 맞지 않음)
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.UI.GraphicRaycaster>()))
            {
                var r = o.Cast<UnityEngine.UI.GraphicRaycaster>();
                if (!r.isActiveAndEnabled) continue;
                var c = r.GetComponent<Canvas>();
                var list = c == null ? null : UnityEngine.UI.GraphicRegistry.GetRaycastableGraphicsForCanvas(c);
                int n = list == null ? 0 : list.Cast<Il2CppSystem.Collections.Generic.ICollection<UnityEngine.UI.Graphic>>().Count, drawn = 0;
                for (int i = 0; i < n; i++)
                    if (list[i].depth != -1) drawn++;
                Plugin.L.LogInfo($"raycaster {r.name} canvas={(c == null ? "-" : c.enabled.ToString())} root={c?.isRootCanvas} graphics={n} drawn={drawn}");
            }
            var main = Camera.main;
            var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.TryCast<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
            if (main != null)
                Plugin.L.LogInfo($"main camera hdr={main.allowHDR} assetHdr={asset?.supportsHDR} precision={asset?.hdrColorBufferPrecision} target={main.targetTexture?.name} " +
                                 $"clear={main.clearFlags} msaa={main.allowMSAA} renderScale={asset?.renderScale}");
        }

        // GraphicRaycaster가 이 그래픽을 맞힐 수 있는지 (raycastTarget, 위쪽 CanvasGroup의 blocksRaycasts)
        static bool Raycastable(UnityEngine.UI.Graphic g)
        {
            if (!g.raycastTarget) return false;
            for (var t = g.transform; t != null; t = t.parent)
            {
                var cg = t.GetComponent<CanvasGroup>();
                if (cg == null || !cg.enabled) continue;
                if (!cg.blocksRaycasts) return false;
                if (cg.ignoreParentGroups) break;
            }
            return true;
        }

        static IEnumerator Run()
        {
            if (Settings.LayerDiag.Value) DumpCanvases();
            var dir = Path.Combine(Dir, DateTime.Now.ToString("MMdd_HHmmss"));
            Directory.CreateDirectory(dir);
            var log = new List<string>
            {
                $"screen={Screen.width}x{Screen.height} {Screen.fullScreenMode} gfx={SystemInfo.graphicsDeviceType} ({SystemInfo.graphicsDeviceName}) quality={QualitySettings.GetQualityLevel()}",
                $"characterResolution={Settings.CharacterResolution.Value} (실험 중 고정)",
            };
            FrameRate.Override = -1;
            var times = new List<float>(4096);

            for (int i = 0; i < Presets.Length; i++)
            {
                var (name, apply) = Presets[i];
                ApplySettings();
                apply?.Invoke();
                Status = $"LXIUM 렌더 실험 {i + 1}/{Presets.Length} ({name}) — 화면을 그대로 두세요";
                for (float t = 0; t < Settle; t += Time.unscaledDeltaTime) yield return null;

                // 안내 상자(IMGUI)는 프레임당 약 0.13 ms라 측정 동안은 숨김 (17차)
                Status = null;
                times.Clear();
                long mod = ModTicks;
                double gpu0 = GpuTimer.TotalMs, main0 = GpuTimer.Cameras.GetValueOrDefault("MainCamera").Ms;
                int gpuFrames0 = GpuTimer.Frames;
                int cams = _cameraRenders, ld = ActorRender.LayerDraws, ls = ActorRender.LayerSkips,
                    cd = ActorRender.CopyDraws, cs = ActorRender.CopySkips, ss = StageRender.Skipped;
                // ETW 추적 등 바깥 기록과 맞추는 측정 시작 시각
                var at = DateTime.Now;
                for (float t = 0; t < Sample; t += Time.unscaledDeltaTime)
                {
                    times.Add(Time.unscaledDeltaTime);
                    yield return null;
                }
                int frames = times.Count;
                string extra = $" modCpu={(ModTicks - mod) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames:F3}ms";
                int gpuFrames = GpuTimer.Frames - gpuFrames0;
                if (GpuTimer.Active && gpuFrames > 0)
                    extra += $" gpu={(GpuTimer.TotalMs - gpu0) / gpuFrames:F3}ms mainCam={(GpuTimer.Cameras.GetValueOrDefault("MainCamera").Ms - main0) / gpuFrames:F3}ms";
                log.Add($"{name} {Stats(times)} perFrame cameras={(float)(_cameraRenders - cams) / frames:F2} " +
                        $"layerDraw={(float)(ActorRender.LayerDraws - ld) / frames:F2} layerSkip={(float)(ActorRender.LayerSkips - ls) / frames:F2} " +
                        $"copyDraw={(float)(ActorRender.CopyDraws - cd) / frames:F2} copySkip={(float)(ActorRender.CopySkips - cs) / frames:F2} " +
                        $"stageSkip={(float)(StageRender.Skipped - ss) / frames:F2}{extra} at={at:HH:mm:ss.fff}");
                Plugin.L.LogInfo("lab " + log[^1]);

                // 안내 문구·패널 없는 프레임을 찍음
                Status = null;
                Capturing = true;
                yield return null;
                yield return new WaitForEndOfFrame();
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                Capturing = false;
                File.WriteAllBytes(Path.Combine(dir, name + ".jpg"), ImageConversion.EncodeToJPG(shot, 90));
                UnityEngine.Object.Destroy(shot);
            }

            FrameRate.Override = null;
            ApplySettings();
            File.WriteAllLines(Path.Combine(dir, "lab.txt"), log);
            Plugin.L.LogInfo("lab done " + dir);
            Status = "LXIUM 렌더 실험 완료 — 원래 설정으로 돌아왔습니다: " + dir;
            for (float t = 0; t < 4f; t += Time.unscaledDeltaTime) yield return null;
            Status = null;
            _running = false;
        }

        static string Stats(List<float> dt)
        {
            var ms = dt.Select(x => x * 1000f).OrderBy(x => x).ToArray();
            float P(float q) => ms[Math.Min(ms.Length - 1, (int)(q * ms.Length))];
            return $"frames={ms.Length} fps={ms.Length / dt.Sum():F1} avg={ms.Average():F2}ms p50={P(0.5f):F2} p95={P(0.95f):F2} p99={P(0.99f):F2} max={ms[^1]:F2}";
        }
    }
}
