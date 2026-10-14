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
    // 원래 설정부터 최적화를 하나씩 더해 가며 8초씩(안정 2초 + 측정 6초) 프레임 통계·스크린샷을 남기고
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

        sealed class Preset
        {
            public string Name;
            public bool Msaa, Intermediate, ChangeOnly, HiddenSkip;
            public SsaoMode Ssao;
        }

        // 앞 단계에 하나씩 더함. 0번이 게임 원래 상태
        static readonly Preset[] Presets =
        {
            new() { Name = "0_original", Ssao = SsaoMode.Game },
            new() { Name = "1_msaa", Msaa = true, Ssao = SsaoMode.Game },
            new() { Name = "2_ssao", Msaa = true, Ssao = SsaoMode.StageOnly },
            new() { Name = "3_intermediate", Msaa = true, Ssao = SsaoMode.StageOnly, Intermediate = true },
            new() { Name = "4_changeOnly", Msaa = true, Ssao = SsaoMode.StageOnly, Intermediate = true, ChangeOnly = true },
            new() { Name = "5_hiddenSkip", Msaa = true, Ssao = SsaoMode.StageOnly, Intermediate = true, ChangeOnly = true, HiddenSkip = true },
        };

        static void Apply(Preset p)
        {
            UrpTuning.Set(p.Msaa, p.Ssao, p.Intermediate);
            ActorRender.ChangeOnly = p.ChangeOnly;
            StageRender.HiddenSkip = p.HiddenSkip;
        }

        public static void ApplySettings()
        {
            UrpTuning.Set(Settings.MsaaOff.Value, Settings.Ssao.Value, Settings.IntermediateAuto.Value);
            ActorRender.ChangeOnly = Settings.CharacterChangeOnly.Value;
            StageRender.HiddenSkip = Settings.StageHiddenSkip.Value;
        }

        static IEnumerator Run()
        {
            var dir = Path.Combine(Dir, DateTime.Now.ToString("MMdd_HHmmss"));
            Directory.CreateDirectory(dir);
            var log = new List<string>
            {
                $"screen={Screen.width}x{Screen.height} {Screen.fullScreenMode} gfx={SystemInfo.graphicsDeviceType} ({SystemInfo.graphicsDeviceName}) quality={QualitySettings.GetQualityLevel()}",
                $"characterResolution={Settings.CharacterResolution.Value} stageResolution={Settings.StageResolution.Value}",
            };
            FrameRate.Override = -1;
            var times = new List<float>(4096);

            for (int i = 0; i < Presets.Length; i++)
            {
                var p = Presets[i];
                Apply(p);
                Status = $"LXIUM 렌더 실험 {i + 1}/{Presets.Length} ({p.Name}) — 화면을 그대로 두세요";
                for (float t = 0; t < Settle; t += Time.unscaledDeltaTime) yield return null;

                times.Clear();
                int cams = _cameraRenders, ld = ActorRender.LayerDraws, ls = ActorRender.LayerSkips,
                    cd = ActorRender.CopyDraws, cs = ActorRender.CopySkips, ss = StageRender.Skipped;
                for (float t = 0; t < Sample; t += Time.unscaledDeltaTime)
                {
                    times.Add(Time.unscaledDeltaTime);
                    yield return null;
                }
                int frames = times.Count;
                log.Add($"{p.Name} {Stats(times)} perFrame cameras={(float)(_cameraRenders - cams) / frames:F2} " +
                        $"layerDraw={(float)(ActorRender.LayerDraws - ld) / frames:F2} layerSkip={(float)(ActorRender.LayerSkips - ls) / frames:F2} " +
                        $"copyDraw={(float)(ActorRender.CopyDraws - cd) / frames:F2} copySkip={(float)(ActorRender.CopySkips - cs) / frames:F2} " +
                        $"stageSkip={(float)(StageRender.Skipped - ss) / frames:F2}");
                Plugin.L.LogInfo("lab " + log[^1]);

                // 안내 문구 없는 프레임을 찍음
                Status = null;
                yield return null;
                yield return new WaitForEndOfFrame();
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(dir, p.Name + ".jpg"), ImageConversion.EncodeToJPG(shot, 90));
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
