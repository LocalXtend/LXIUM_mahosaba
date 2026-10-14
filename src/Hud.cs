using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LXIUM_mahosaba
{
    // 화면 오른쪽 패널. PgUp = 프레임 통계, PgDn = LXIUM 플래그 목록 (켜짐 초록·꺼짐 빨강, 마우스를 올리면 설명).
    // 플래그는 지금 실제로 적용된 값을 읽으므로 렌더 실험실(F10)이 바꾸는 것도 그대로 보인다. 실험실 스크린샷에는 숨김
    static class Hud
    {
        sealed class Flag
        {
            public Func<string> Text;
            public Func<bool> On;
            public string Help;
            public bool LabOnly;
        }

        static readonly Flag[] Flags =
        {
            new()
            {
                Text = () => $"프레임 제한 해제 (TargetFps {FrameRate.Override ?? Settings.TargetFps.Value})",
                On = () => (FrameRate.Override ?? Settings.TargetFps.Value) != 0,
                Help = "기능: 게임의 60/30 상한과 품질 단계의 VSync 대신 TargetFps로 정함. 0 = 모니터 주사율(VSync), -1 = 제한 없음, 숫자 = 그 fps.\n" +
                       "개선 가능성: 성능 기능이 아니라 상한 설정. 60Hz 모니터에서는 0(VSync)이 발열·소음이 가장 적음.",
            },
            new()
            {
                Text = () => "2D 카메라 MSAA 끄기",
                On = () => UrpTuning.MsaaOff,
                Help = "기능: 메인·캐릭터 합성 카메라(직교)의 MSAA 4x를 끔. 스프라이트·UI는 가장자리가 투명이라 효과가 없음.\n" +
                       "측정: 768p에서 -0.5 ms 안팎, 화면 차이 없음.\n개선 가능성: 다 함.",
            },
            new()
            {
                Text = () => "중간 텍스처 Auto",
                On = () => UrpTuning.IntermediateAuto,
                Help = "기능: URP의 중간 텍스처를 Always → Auto. 필요 없는 카메라는 복사 없이 바로 그림.\n" +
                       "측정: -0.2 ms 안팎, 화면 차이 없음.\n개선 가능성: 다 함.",
            },
            new()
            {
                Text = () => $"SSAO 줄이기 (현재 {UrpTuning.Ssao})",
                On = () => UrpTuning.Ssao != SsaoMode.Game,
                Help = "기능: 구석 음영(SSAO)을 2D 카메라에서 빼고(StageOnly), 법정에서도 가볍게(StageLite) 또는 끔(Off).\n" +
                       "측정: 원래(Game) 대비 -10.8 ms. 1080p에서 법정 원래 품질은 혼자 5.1 ms인데 법정 조명이 스포트라이트뿐이라 화면 영향 0.\n" +
                       "개선 가능성: Off가 끝.",
            },
            new()
            {
                Text = () => "바뀔 때만 다시 그리기",
                On = () => ActorRender.ChangeOnly,
                Help = "기능: 레이어 캐릭터(조각 그림 43~126장)를 표정·옷이 바뀐 프레임에만 다시 합성하고, 전환 효과 복사도 입력이 같으면 건너뜀. 1초마다 한 번은 안전하게 다시 그림.\n" +
                       "측정: -8.9 ms (재판, 캐릭터 11명).\n개선 가능성: 카메라 없는 합성(Naninovel 절차적 경로)로 바꾸면 바뀔 때의 비용도 줄일 수 있음.",
            },
            new()
            {
                Text = () => "빠른 변화 감지",
                On = () => ActorRender.ChangeOnly && ActorRender.FastDetect,
                Help = "기능: 바뀔 때만 다시 그리기의 변화 확인을 가볍게. 최근 1초 안에 바뀐 캐릭터와 전환 효과가 도는 동안은 매 프레임, 나머지는 4프레임에 한 번씩 돌아가며 확인.\n" +
                       "효과: 모드 CPU 감소 (실험실 05_fastDetectOff와 비교). 놓친 변화는 최대 3프레임 늦음.\n" +
                       "개선 가능성: Naninovel 표정 변경 함수에 직접 걸어 확인 자체를 없앨 수 있음.",
            },
            new()
            {
                Text = () => "캐릭터 해상도 맞춤",
                On = () => Settings.CharacterResolution.Value != 1f,
                Help = "기능: 캐릭터 합성 그림을 1440p 기준(1500×3000)이 아니라 화면 높이에 맞는 크기로. 캐릭터가 처음 만들어질 때 적용.\n" +
                       "개선 가능성: 줄인 그림을 키울 때 선명화(RCAS) 셰이더를 더할 수 있음.",
            },
            new()
            {
                Text = () => "숨은 법정 카메라 끄기",
                On = () => StageRender.HiddenSkip,
                Help = "기능: 화면 분할에서 안 보이는(알파 0) 법정 카메라를 그리지 않음.\n측정: -10.2 ms, 화면 차이 없음.\n개선 가능성: 다 함.",
            },
            new()
            {
                Text = () => "숨은 법정 화면 컬링",
                On = () => StageRender.HiddenSkip && StageRender.CullHidden,
                Help = "기능: 알파 0인 법정 화면(RawImage)도 그리기 목록에서 뺌. UI는 투명해도 화면 크기 사각형을 그리기 때문.\n측정: 1080p -0.14 ms.\n개선 가능성: 다 함.",
            },
            new()
            {
                Text = () => "법정 해상도 맞춤",
                On = () => (StageRender.ResolutionOverride ?? Settings.StageResolution.Value) != 1f,
                Help = "기능: 법정 렌더 텍스처(원래 1920×1080 고정)를 화면 크기에 맞춤. 1080p 이상이면 원래와 같음.\n" +
                       "측정: 768p에서 -5.1 ms.\n개선 가능성: 0.75배처럼 더 낮추면 -1 ms 안팎이지만 법정·캐릭터가 흐려짐 (선명화 셰이더와 함께라면 후보).",
            },
            new()
            {
                Text = () => "법정 MSAA 끄기",
                On = () => StageRender.MsaaOff,
                Help = "기능: 법정 렌더 텍스처의 MSAA 2x를 끔. 가장자리는 FXAA가 다듬음.\n측정: -1.13 ms, 화면 차이 없음.\n개선 가능성: 다 함.",
            },
            new()
            {
                Text = () => "법정 후처리 끄기",
                On = () => StageRender.PostOff,
                Help = "기능: 법정 카메라의 후처리(색 보정 표 + FXAA)를 끔. 화면 전체 FXAA는 메인 카메라가 한 번 더 하므로 같은 결과. 게임 옵션에서 후처리를 끄면 법정 쪽은 다시 켬.\n" +
                       "측정: -0.66 ms.\n개선 가능성: 다 함.",
            },
            new()
            {
                Text = () => "법정 반 프레임",
                On = () => StageRender.HalfRate,
                Help = "기능: 법정 카메라가 멈춰 있으면 두 프레임에 한 번만 그림. 움직일 때는 매 프레임.\n" +
                       "측정: 평균 -1.0 ms이지만 프레임 간격이 번갈아 짧고 길어짐 (기본 끔).\n개선 가능성: 법정 건물 캐시가 같은 일을 더 정확히 함.",
            },
            new()
            {
                Text = () => "FireLite (가벼운 불)",
                On = () => CourtShaders.Fire.Active,
                Help = "기능: 법정 불 셰이더를 LXIUM판으로. 텍스처 읽기 8 → 4회, 빈 영역은 중간에 끝냄. D3D11 전용.\n" +
                       "측정: 불 비용 0.9~1.2 ms → 0.1~0.5 ms, 색·모양 같음.\n개선 가능성: 불 판을 마스크 모양에 맞게 잘라 빈 픽셀 자체를 줄일 수 있음.",
            },
            new()
            {
                Text = () => "CourtLite (가벼운 법정)",
                On = () => CourtShaders.Court.Active,
                Help = "기능: 법정 바닥·벽·계단·단상의 URP Lit을 LXIUM판으로. 같은 조명식에서 쓰이지 않는 그림자·SSAO·반사 프로브 블렌딩을 뺌 (텍스처 읽기 9 → 5회). D3D11 전용.\n" +
                       "측정: -0.74 ms, 화면 화소까지 같음.\n개선 가능성: 법정 건물 캐시와 함께 쓰면 멈춘 장면에서 조명 계산 자체가 빠짐.",
            },
            new()
            {
                Text = () => $"법정 건물 캐시 (갱신 {CourtCache.Refreshes}회)",
                On = () => CourtCache.On && CourtShaders.Court.Active,
                Help = "기능: 법정 카메라가 멈춰 있고 조명이 그대로인 동안 바닥·벽·계단의 조명 결과를 한 번만 그려 두고, 다음 프레임부터는 그 색을 픽셀당 한 번 읽어서 그림. 깊이는 그대로라 캐릭터·단상·불과의 앞뒤는 같음. 카메라가 움직이거나 조명이 바뀌면 다시 그림. CourtLite 필요, 분할 화면에서는 쓰지 않음.\n" +
                       "효과: 실험실 03_courtCache로 측정.\n개선 가능성: 분할 화면용 카메라별 캐시, 단상(투명)까지 캐시.",
            },
            new()
            {
                Text = () => "법정 카메라 정지 (진단)",
                On = () => StageRender.Freeze,
                Help = "렌더 실험실 진단: 법정 카메라를 멈춰 나머지(대사·UI·화면 합치기)의 비용만 잼.",
                LabOnly = true,
            },
        };

        static bool _showFps, _showFlags;

        // 최근 1초 창의 프레임 시간(ms)
        const float Window = 1f;
        static readonly List<float> Frames = new(1024);
        static float _windowStart;
        static long _modStart;
        static float _lastAvg = -1, _refAvg = -1;
        static ulong _signature = ulong.MaxValue;
        static string[] _fpsLines = { "측정 중…" };

        public static void Tick()
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.pageUpKey.wasPressedThisFrame) _showFps = !_showFps;
                if (kb.pageDownKey.wasPressedThisFrame) _showFlags = !_showFlags;
            }
            if (!_showFps) return;

            // 플래그가 바뀌면 그 직전 창의 평균을 기준으로 "바뀐 시간"을 보여 주고 새 창을 시작
            ulong sig = Signature();
            if (sig != _signature)
            {
                if (_signature != ulong.MaxValue && _lastAvg > 0) _refAvg = _lastAvg;
                _signature = sig;
                StartWindow();
                return;
            }
            Frames.Add(Time.unscaledDeltaTime * 1000f);
            if (Time.unscaledTime - _windowStart >= Window && Frames.Count > 0) Summarize();
        }

        static void StartWindow()
        {
            Frames.Clear();
            _windowStart = Time.unscaledTime;
            _modStart = RenderLab.ModTicks;
        }

        static void Summarize()
        {
            int n = Frames.Count;
            float sum = 0;
            foreach (var f in Frames) sum += f;
            Frames.Sort();
            float avg = sum / n;
            float slow = Frames[Math.Min(n - 1, (int)(n * 0.99f))];
            float fast = Frames[(int)(n * 0.01f)];
            double mod = (RenderLab.ModTicks - _modStart) * 1000.0 / Stopwatch.Frequency / n;
            string delta = _refAvg > 0 ? $"{avg - _refAvg:+0.00;-0.00} ms (전 {_refAvg:F2})" : "— (플래그가 바뀌면)";
            _fpsLines = new[]
            {
                $"FPS  {n * 1000f / sum:F1}",
                $"하위 1%  {1000f / slow:F1}",
                $"상위 1%  {1000f / fast:F1}",
                $"프레임 시간  {avg:F2} ms",
                $"바뀐 시간  {delta}",
                $"모드 CPU  {mod:F3} ms",
            };
            _lastAvg = avg;
            StartWindow();
        }

        static ulong Signature()
        {
            ulong bits = 0;
            for (int i = 0; i < Flags.Length; i++)
                if (Flags[i].On()) bits |= 1UL << i;
            return bits;
        }

        static readonly Color OnColor = new(0.4f, 1f, 0.5f), OffColor = new(1f, 0.42f, 0.42f);

        // 한 줄 글자용 스타일: 줄바꿈 없음, 줄 높이는 실제 글꼴로 잼. 글자 크기의 고정 배수로 잡으면
        // 화면 크기·비율에 따라 글자 아래가 잘림
        static GUIStyle _line;
        static int _lineSize = -1;
        static float _lineHeight;

        static void EnsureStyle(int fs)
        {
            if (_line == null)
            {
                _line = new GUIStyle();
                _line.normal.textColor = Color.white;
            }
            if (_lineSize == fs) return;
            _line.fontSize = fs;
            _lineSize = fs;
            _lineHeight = Mathf.Ceil(_line.CalcHeight(new GUIContent("가Ag|"), 10000f)) + 2;
        }

        public static void Draw()
        {
            if (RenderLab.Capturing || (!_showFps && !_showFlags)) return;
            // 글자·상자만 그리고 입력을 받지 않으므로 그리기 이벤트에서만 (레이아웃·마우스 이벤트 때의 중복 비용을 뺌)
            if (Event.current.type != EventType.Repaint) return;
            var oldColor = GUI.contentColor;
            int fs = Math.Max(12, Screen.height / 60);
            EnsureStyle(fs);
            float line = _lineHeight, pad = fs * 0.6f, w = fs * 24f;
            float x = Screen.width - w - pad, y = pad;

            if (_showFps)
            {
                float h = line * _fpsLines.Length + pad * 2;
                Panel(new Rect(x, y, w, h));
                for (int i = 0; i < _fpsLines.Length; i++)
                    GUI.Label(new Rect(x + pad, y + pad + i * line, w - pad * 2, line), _fpsLines[i], _line);
                y += h + pad;
            }

            if (_showFlags)
            {
                var shown = new List<Flag>();
                foreach (var f in Flags)
                    if (!f.LabOnly || RenderLab.Running) shown.Add(f);
                float h = line * (shown.Count + 1) + pad * 2;
                Panel(new Rect(x, y, w, h));
                GUI.Label(new Rect(x + pad, y + pad, w - pad * 2, line), "LXIUM 플래그 (마우스를 올리면 설명)", _line);
                var mouse = MousePosition();
                Flag hovered = null;
                float hoverY = 0;
                for (int i = 0; i < shown.Count; i++)
                {
                    var r = new Rect(x + pad, y + pad + (i + 1) * line, w - pad * 2, line);
                    bool on = shown[i].On();
                    GUI.contentColor = on ? OnColor : OffColor;
                    GUI.Label(r, (on ? "● " : "○ ") + shown[i].Text(), _line);
                    if (r.Contains(mouse))
                    {
                        hovered = shown[i];
                        hoverY = r.y;
                    }
                }
                GUI.contentColor = oldColor;
                if (hovered != null)
                {
                    // 패널 왼쪽에 설명 상자. 여러 줄이라 줄바꿈되는 기본 label 스타일로 높이를 잼
                    var label = GUI.skin.label;
                    int oldSize = label.fontSize;
                    label.fontSize = fs;
                    float tw = w * 1.4f;
                    var content = new GUIContent(hovered.Help);
                    float textH = Mathf.Ceil(label.CalcHeight(content, tw - pad * 2)) + line * 0.5f;
                    float th = textH + pad * 2;
                    float ty = Mathf.Clamp(hoverY, pad, Screen.height - th - pad);
                    var tr = new Rect(x - tw - pad, ty, tw, th);
                    Panel(tr);
                    GUI.Label(new Rect(tr.x + pad, tr.y + pad, tw - pad * 2, textH), content);
                    label.fontSize = oldSize;
                }
            }

            GUI.contentColor = oldColor;
        }

        // 이 게임은 새 Input System만 써서 IMGUI의 Event.mousePosition이 갱신되지 않음 → Input System에서 읽어
        // IMGUI 좌표(위가 0)로 바꿈
        static Vector2 MousePosition()
        {
            var mouse = Mouse.current;
            if (mouse == null) return Event.current.mousePosition;
            Vector2 p = mouse.position.ReadValue();
            return new Vector2(p.x, Screen.height - p.y);
        }

        // 기본 상자는 반투명이라 두 번 그려 글자가 잘 보이게
        static void Panel(Rect r)
        {
            GUI.Box(r, "");
            GUI.Box(r, "");
        }
    }
}
