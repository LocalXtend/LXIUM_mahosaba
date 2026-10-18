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
            if (kb == null || !kb.f10Key.wasPressedThisFrame) return;
            _running = true;
            host.StartCoroutine(Run().WrapToIl2Cpp());
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
                StageRender.HiddenSkip = StageRender.MsaaOff = StageRender.PostOff = StageRender.HalfRate = StageRender.ChangeOnly = false;
                StageRender.ResolutionOverride = 1f;
                CourtShaders.Fire.On = CourtShaders.Court.On = false;
                ActorRender.FastDetect = false;
                RevealWork.Enabled = false;
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
#endif
        };
#if DEBUG && LXIUM_UI_BENCH
        static bool _autoStarted;
#endif

        public static void ApplySettings()
        {
            UrpTuning.Set(Settings.MsaaOff.Value, Settings.Ssao.Value, Settings.IntermediateAuto.Value);
            ActorRender.ChangeOnly = Settings.CharacterChangeOnly.Value;
            ActorRender.FastDetect = Settings.FastDetect.Value;
            RevealWork.Enabled = Settings.RevealReuse.Value;
            StageRender.HiddenSkip = Settings.StageHiddenSkip.Value;
            StageRender.MsaaOff = Settings.StageMsaaOff.Value;
            StageRender.PostOff = Settings.StagePostOff.Value;
            StageRender.HalfRate = Settings.StageHalfRate.Value;
            StageRender.ChangeOnly = Settings.StageChangeOnly.Value;
            // 설정이 바뀌면 법정 그림도 달라질 수 있으므로 한 번 다시 그림
            StageRender.Dirty = true;
            StageRender.ResolutionOverride = null;
            StageRender.Freeze = false;
            StageRender.CullHidden = true;
            CourtShaders.Fire.On = Settings.FireLite.Value;
            CourtShaders.Court.On = Settings.CourtLite.Value;
        }

        static IEnumerator Run()
        {
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

                times.Clear();
                long mod = ModTicks;
                int cams = _cameraRenders, ld = ActorRender.LayerDraws, ls = ActorRender.LayerSkips,
                    cd = ActorRender.CopyDraws, cs = ActorRender.CopySkips, ss = StageRender.Skipped;
                for (float t = 0; t < Sample; t += Time.unscaledDeltaTime)
                {
                    times.Add(Time.unscaledDeltaTime);
                    yield return null;
                }
                int frames = times.Count;
                string extra = $" modCpu={(ModTicks - mod) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames:F3}ms";
                log.Add($"{name} {Stats(times)} perFrame cameras={(float)(_cameraRenders - cams) / frames:F2} " +
                        $"layerDraw={(float)(ActorRender.LayerDraws - ld) / frames:F2} layerSkip={(float)(ActorRender.LayerSkips - ls) / frames:F2} " +
                        $"copyDraw={(float)(ActorRender.CopyDraws - cd) / frames:F2} copySkip={(float)(ActorRender.CopySkips - cs) / frames:F2} " +
                        $"stageSkip={(float)(StageRender.Skipped - ss) / frames:F2}{extra}");
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
