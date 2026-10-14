using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

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
        static bool _running;
        internal static string Status;

        public static void CountCamera() => _cameraRenders++;

        public static void Tick(Host host)
        {
            if (_running || !Settings.Lab.Value) return;
            var kb = Keyboard.current;
            if (kb == null || !kb.f10Key.wasPressedThisFrame) return;
            _running = true;
            host.StartCoroutine(Run().WrapToIl2Cpp());
        }

        // 각 실험은 ApplySettings() 뒤에 적용됨. null이면 지금 설정 그대로
        static readonly (string Name, Action Apply)[] Presets =
        {
            ("00_original", () =>
            {
                UrpTuning.Set(false, SsaoMode.Game, false);
                ActorRender.ChangeOnly = false;
                StageRender.HiddenSkip = StageRender.MsaaOff = StageRender.PostOff = StageRender.HalfRate = false;
                StageRender.ResolutionOverride = 1f;
                FireRender.Lite = false;
            }),
            ("01_current", null),
            // 진단: 법정 카메라를 멈춤. 남는 시간 = 메인 카메라(UI·대사·무대 화면 붙이기) + 캐릭터 + CPU
            ("02_noStage", () => StageRender.Freeze = true),
            ("03_ssaoOff", () => SetSsao(SsaoMode.Off)),
            ("04_ssaoFull", () => SetSsao(SsaoMode.StageOnly)),
            ("05_stageHalfRate", () => StageRender.HalfRate = true),
            ("06_stageRes75", () => StageRender.ResolutionOverride = Screen.height / 1080f * 0.75f),
            ("07_mainPostOff", () => MainPost(true)),
            ("08_fireOn", () => Fire(Settings.FireLite.Value)),
            ("09_fireOriginal", () => Fire(false)),
            ("10_current", null),
        };

        static void SetSsao(SsaoMode mode) => UrpTuning.Set(UrpTuning.MsaaOff, mode, UrpTuning.IntermediateAuto);

        static int _fireCount;
        static void Fire(bool lite)
        {
            _fireCount = FireRender.ForceFire(true);
            FireRender.Lite = lite;
        }

        static UniversalAdditionalCameraData _main;
        static bool _mainPost;

        // 메인 카메라(UI·캐릭터·무대 화면을 그림)의 후처리. 게임 옵션의 "포스트 프로세싱"과 같은 효과
        static void MainPost(bool off)
        {
            if (!off)
            {
                if (_main != null) _main.renderPostProcessing = _mainPost;
                _main = null;
                return;
            }
            _main = Camera.main?.GetComponent<UniversalAdditionalCameraData>();
            if (_main == null) return;
            _mainPost = _main.renderPostProcessing;
            _main.renderPostProcessing = false;
        }

        public static void ApplySettings()
        {
            UrpTuning.Set(Settings.MsaaOff.Value, Settings.Ssao.Value, Settings.IntermediateAuto.Value);
            ActorRender.ChangeOnly = Settings.CharacterChangeOnly.Value;
            StageRender.HiddenSkip = Settings.StageHiddenSkip.Value;
            StageRender.MsaaOff = Settings.StageMsaaOff.Value;
            StageRender.PostOff = Settings.StagePostOff.Value;
            StageRender.HalfRate = Settings.StageHalfRate.Value;
            StageRender.ResolutionOverride = null;
            StageRender.Freeze = false;
            FireRender.Lite = Settings.FireLite.Value;
            FireRender.ForceFire(false);
            MainPost(false);
        }

        static IEnumerator Run()
        {
            var dir = Path.Combine(Dir, DateTime.Now.ToString("MMdd_HHmmss"));
            Directory.CreateDirectory(dir);
            var log = new List<string>
            {
                $"screen={Screen.width}x{Screen.height} {Screen.fullScreenMode} gfx={SystemInfo.graphicsDeviceType} ({SystemInfo.graphicsDeviceName}) quality={QualitySettings.GetQualityLevel()}",
                $"characterResolution={Settings.CharacterResolution.Value} (실험 중 고정), frameTiming={FrameTimingManager.IsFeatureEnabled()}",
            };
            FrameRate.Override = -1;
            var times = new List<float>(4096);
            var timing = new Il2CppStructArray<FrameTiming>(1);

            for (int i = 0; i < Presets.Length; i++)
            {
                var (name, apply) = Presets[i];
                ApplySettings();
                _fireCount = 0;
                apply?.Invoke();
                Status = $"LXIUM 렌더 실험 {i + 1}/{Presets.Length} ({name}) — 화면을 그대로 두세요";
                for (float t = 0; t < Settle; t += Time.unscaledDeltaTime) yield return null;

                times.Clear();
                double cpu = 0, wait = 0, gpu = 0;
                int timed = 0;
                int cams = _cameraRenders, ld = ActorRender.LayerDraws, ls = ActorRender.LayerSkips,
                    cd = ActorRender.CopyDraws, cs = ActorRender.CopySkips, ss = StageRender.Skipped;
                for (float t = 0; t < Sample; t += Time.unscaledDeltaTime)
                {
                    times.Add(Time.unscaledDeltaTime);
                    FrameTimingManager.CaptureFrameTimings();
                    if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timing[0].cpuMainThreadFrameTime > 0)
                    {
                        cpu += timing[0].cpuMainThreadFrameTime;
                        wait += timing[0].cpuMainThreadPresentWaitTime;
                        gpu += timing[0].gpuFrameTime;
                        timed++;
                    }
                    yield return null;
                }
                int frames = times.Count;
                // cpuMain에는 GPU를 기다리는 시간(presentWait)이 들어 있음. D3D11에서는 gpu가 0으로 올 수 있음
                string extra = timed > 0 ? $" cpuMain={cpu / timed:F2}ms presentWait={wait / timed:F2}ms gpu={gpu / timed:F2}ms" : "";
                if (_fireCount > 0) extra += $" fire={_fireCount} fireVisible={FireRender.VisibleFire()}";
                log.Add($"{name} {Stats(times)} perFrame cameras={(float)(_cameraRenders - cams) / frames:F2} " +
                        $"layerDraw={(float)(ActorRender.LayerDraws - ld) / frames:F2} layerSkip={(float)(ActorRender.LayerSkips - ls) / frames:F2} " +
                        $"copyDraw={(float)(ActorRender.CopyDraws - cd) / frames:F2} copySkip={(float)(ActorRender.CopySkips - cs) / frames:F2} " +
                        $"stageSkip={(float)(StageRender.Skipped - ss) / frames:F2}{extra}");
                Plugin.L.LogInfo("lab " + log[^1]);

                // 안내 문구 없는 프레임을 찍음
                Status = null;
                yield return null;
                yield return new WaitForEndOfFrame();
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
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
