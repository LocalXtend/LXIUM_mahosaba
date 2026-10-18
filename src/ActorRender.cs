using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using Naninovel;
using UnityEngine;

namespace LXIUM_mahosaba
{
    // Naninovel 액터(캐릭터·배경) 렌더 경로 최적화 (docs/ai/mahosaba.md 5장, mahosaba-render.md 2장).
    // 게임 경로: LayeredDrawer.DrawLayers(카메라로 레이어 합성 → RT)
    //          → TransitionalRenderer.RenderToTexture(전환 셰이더로 RT 복사, 매 프레임)
    //          → Graphics.DrawMesh(화면). 앞의 두 단계는 입력이 그대로면 결과도 그대로라 건너뛴다.
    static class ActorRender
    {
        // 입력이 같아도 이 간격마다 한 번은 다시 그림 (놓친 변화가 화면에 남는 시간의 상한)
        const float RefreshInterval = 1f;
        // CameraConfiguration.ReferenceResolution 2560x1440, ReferencePPU 100 → 1440p에서 1:1
        const float ReferenceHeight = 1440f;

        internal static bool ChangeOnly;
        // 빠른 변화 감지: 최근에 바뀐(hot) 캐릭터만 매 프레임 검사하고, 나머지는 4프레임에 한 번씩 돌아가며 검사.
        // 놓친 변화가 화면에 남는 시간은 최대 3프레임. 전환 효과가 도는 동안(복사 입력이 바뀐 직후)은 모두 매 프레임 검사
        internal static bool FastDetect;
        const float HotTime = 1f;
        static float _allHotUntil;
        static int _nextSlot;
        // 안전 갱신은 프레임당 하나씩만. 재판처럼 캐릭터가 많으면 같은 프레임에 몰려 1초마다 끊김이 생김 (mahosaba.md 10장 2차)
        static int _layerRefreshFrame = -1, _copyRefreshFrame = -1;

        static bool TakeRefreshSlot(ref int lastFrame)
        {
            int frame = Time.frameCount;
            if (lastFrame == frame) return false;
            lastFrame = frame;
            return true;
        }
        internal static int LayerDraws, LayerSkips, CopyDraws, CopySkips;

        sealed class DrawerState
        {
            public string Name;
            // 진단용: 렌더러별 마지막 상태 (켜짐, 스프라이트, 재질, 레이어, 색)
            public (bool On, IntPtr Sprite, IntPtr Material, int Layer, Color Color)[] Parts;
            public SpriteRenderer[] Renderers;
            // 렌더러마다 gameObject를 매 프레임 꺼내면 interop 래퍼가 새로 만들어지므로 한 번만 꺼내 둠
            public GameObject[] Objects;
            public long Hash;
            public IntPtr Texture;
            public float LastDraw, HotUntil;
            public int Slot;
        }

        sealed class CopyState
        {
            public long Hash, Inputs;
            public float LastDraw;
        }

        static readonly Dictionary<IntPtr, DrawerState> Drawers = new();
        static readonly Dictionary<IntPtr, CopyState> Copies = new();
        // LayeredDrawer가 실제로 다시 그린 RT의 내용 버전
        static readonly Dictionary<IntPtr, int> TextureVersions = new();
        static readonly Dictionary<IntPtr, float> OriginalPpu = new();

        // 레이어 상태 해시. 이 게임의 캐릭터 프리팹에는 Animator가 없고 표정·포즈는
        // 렌더러 켜고 끄기, 스프라이트·색·재질·레이어 교체로만 바뀐다 (docs/ai/mahosaba.md 5.2)
        static long LayerHash(DrawerState s, float ppu)
        {
            long h = BitConverter.SingleToInt32Bits(ppu);
            for (int i = 0; i < s.Renderers.Length; i++)
            {
                var r = s.Renderers[i];
                var go = s.Objects[i];
                bool on = go.activeInHierarchy && r.enabled;
                h = h * 31 + (on ? 1 : 0);
                if (!on) continue;
                var c = r.color;
                h = h * 31 + (r.sprite?.Pointer.ToInt64() ?? 0);
                h = h * 31 + (r.sharedMaterial?.Pointer.ToInt64() ?? 0);
                h = h * 31 + go.layer;
                h = h * 31 + HashCode.Combine(c.r, c.g, c.b, c.a);
            }
            return h;
        }

        // 진단 (설정 Debug.LayerDiag): 해시가 바뀔 때 렌더러별 상태를 지난번과 비교해 "캐릭터 / 레이어 / 바뀐 항목"별
        // 횟수를 모으고 5초마다 로그에 남김
        static readonly Dictionary<string, int> DiagCounts = new();
        static float _diagDump;

        static void DiagNote(DrawerState s, RenderTexture rt)
        {
            var parts = new (bool On, IntPtr Sprite, IntPtr Material, int Layer, Color Color)[s.Renderers.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var r = s.Renderers[i];
                bool on = s.Objects[i].activeInHierarchy && r.enabled;
                parts[i] = (on, r.sprite?.Pointer ?? IntPtr.Zero, r.sharedMaterial?.Pointer ?? IntPtr.Zero, s.Objects[i].layer, r.color);
            }
            void Count(string what) => DiagCounts[$"{s.Name} / {what}"] = DiagCounts.GetValueOrDefault($"{s.Name} / {what}") + 1;
            if (rt == null || rt.Pointer != s.Texture) Count("RT 바뀜");
            if (s.Parts != null)
                for (int i = 0; i < parts.Length; i++)
                {
                    var (a, b) = (s.Parts[i], parts[i]);
                    if (a == b) continue;
                    string n = s.Objects[i].name;
                    if (a.On != b.On) Count(n + " 켜짐");
                    else if (!b.On) continue;
                    else if (a.Sprite != b.Sprite) Count(n + " 스프라이트");
                    else if (a.Material != b.Material) Count(n + " 재질");
                    else if (a.Layer != b.Layer) Count(n + $" 레이어 {a.Layer}->{b.Layer}");
                    else Count(n + $" 색 {a.Color}->{b.Color}");
                }
            s.Parts = parts;
        }

        static long _diagStart, _diagTicks, _diagMaxTicks;
        static int _diagFrame;
        static int _diagDraws, _diagRefreshes;
        static readonly HashSet<IntPtr> DiagRefreshed = new();

        // Host.Update에서 부름: 5초마다 합성 횟수·원인·원래 합성에 든 시간을 로그에 남김
        internal static void DiagTick()
        {
            if (!Settings.LayerDiag.Value || Time.unscaledTime < _diagDump) return;
            _diagDump = Time.unscaledTime + 5f;
            double ms = _diagTicks * 1000.0 / Stopwatch.Frequency, max = _diagMaxTicks * 1000.0 / Stopwatch.Frequency;
            Plugin.L.LogInfo($"layer diag 5s: draws={_diagDraws} (safety={_diagRefreshes} from {DiagRefreshed.Count} of {Drawers.Count} drawers, frames={Time.frameCount - _diagFrame}) total={ms:F1}ms avg={(_diagDraws > 0 ? ms / _diagDraws : 0):F2}ms max={max:F2}ms");
            foreach (var (k, v) in DiagCounts.OrderByDescending(x => x.Value).Take(12)) Plugin.L.LogInfo("layer diag " + v.ToString().PadLeft(5) + " " + k);
            DiagCounts.Clear();
            DiagRefreshed.Clear();
            _diagFrame = Time.frameCount;
            _diagDraws = _diagRefreshes = 0;
            _diagTicks = _diagMaxTicks = 0;
        }

        [HarmonyPatch(typeof(LayeredDrawer), nameof(LayeredDrawer.DrawLayers))]
        static class DrawLayersPatch
        {
            static bool Prefix(LayeredDrawer __instance, float pixelsPerUnit, RenderTexture renderTexture, ref RenderTexture __result, out DrawerState __state)
            {
                long t = Stopwatch.GetTimestamp();
                try { return Run(__instance, pixelsPerUnit, renderTexture, ref __result, out __state); }
                finally { RenderLab.ModTicks += Stopwatch.GetTimestamp() - t; }
            }

            static bool Run(LayeredDrawer drawer, float pixelsPerUnit, RenderTexture renderTexture, ref RenderTexture __result, out DrawerState __state)
            {
                var key = drawer.Pointer;
                if (!Drawers.TryGetValue(key, out __state))
                {
                    var comps = drawer.transform.GetComponentsInChildren<SpriteRenderer>(true);
                    var objects = new GameObject[comps.Length];
                    for (int i = 0; i < comps.Length; i++) objects[i] = comps[i].gameObject;
                    __state = new DrawerState { Renderers = comps, Objects = objects, Slot = _nextSlot++ & 3, Name = drawer.transform.root.name };
                    Drawers[key] = __state;
                }
                float now = Time.unscaledTime;
                bool due = now - __state.LastDraw >= RefreshInterval;
                // 식은 캐릭터는 자기 차례(4프레임에 한 번)가 아니면 해시 없이 지난 그림을 씀
                if (ChangeOnly && FastDetect && !due && now >= __state.HotUntil && now >= _allHotUntil
                    && renderTexture != null && renderTexture.Pointer == __state.Texture
                    && ((Time.frameCount + __state.Slot) & 3) != 0)
                {
                    LayerSkips++;
                    __result = renderTexture;
                    return false;
                }
                long hash;
                // 프리팹 렌더러가 파괴됐는데 ClearLayers를 거치지 않은 경우: 상태를 버리고 원래대로 그림
                try { hash = LayerHash(__state, pixelsPerUnit); }
                catch (Exception e)
                {
                    Plugin.L.LogWarning("layer hash 실패, 원래 경로로 그림: " + e.Message);
                    Drawers.Remove(key);
                    __state = new DrawerState();
                    return true;
                }
                bool same = hash == __state.Hash && renderTexture != null && renderTexture.Pointer == __state.Texture;
                if (!same) __state.HotUntil = now + HotTime;
                if (!same && Settings.LayerDiag.Value) DiagNote(__state, renderTexture);
                if (ChangeOnly && same && (!due || !TakeRefreshSlot(ref _layerRefreshFrame)))
                {
                    LayerSkips++;
                    __result = renderTexture;
                    return false;
                }
                __state.Hash = hash;
                if (same) { _diagRefreshes++; DiagRefreshed.Add(key); }
                _diagStart = Stopwatch.GetTimestamp();
                return true;
            }

            static void Postfix(RenderTexture __result, DrawerState __state, bool __runOriginal)
            {
                if (!__runOriginal || __result == null) return;
                LayerDraws++;
                long dt = Stopwatch.GetTimestamp() - _diagStart;
                _diagDraws++;
                _diagTicks += dt;
                if (dt > _diagMaxTicks) _diagMaxTicks = dt;
                // RT가 바뀌면 이전 RT는 RenderAppearance가 임시 풀에 돌려주므로 버전 기록도 지움
                if (__state.Texture != __result.Pointer) TextureVersions.Remove(__state.Texture);
                __state.Texture = __result.Pointer;
                __state.LastDraw = Time.unscaledTime;
                TextureVersions.TryGetValue(__result.Pointer, out int v);
                TextureVersions[__result.Pointer] = v + 1;
            }
        }

        // Dispose와 레이어 재구성 모두 ClearLayers를 거침 (Dispose는 다른 함수와 코드가 합쳐져 있어 직접 패치하지 않음)
        [HarmonyPatch(typeof(LayeredDrawer), "ClearLayers")]
        static class ClearLayersPatch
        {
            static void Postfix(LayeredDrawer __instance)
            {
                if (!Drawers.Remove(__instance.Pointer, out var s)) return;
                TextureVersions.Remove(s.Texture);
            }
        }

        [HarmonyPatch(typeof(TransitionalRenderer), "RenderToTexture")]
        static class RenderToTexturePatch
        {
            static bool Prefix(TransitionalRenderer __instance, RenderTexture texture)
            {
                long t = Stopwatch.GetTimestamp();
                try
                {
                    if (StageRender.ChangeOnly) StageRender.NoteActor(__instance);
                    return Decide(__instance, texture);
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning("copy hash 실패, 원래 경로로 그림: " + e.Message);
                    return true;
                }
                finally { RenderLab.ModTicks += Stopwatch.GetTimestamp() - t; }
            }

            static bool Decide(TransitionalRenderer renderer, RenderTexture texture)
            {
                // 복사가 새 그림을 만들면 법정 카메라(바뀔 때만 그리기)에도 알림
                if (!ChangeOnly || texture == null) { CopyDraws++; StageRender.Dirty = true; return true; }
                var main = renderer.MainTexture;
                if (main == null) { StageRender.Dirty = true; return true; }

                int version = 0;
                var mainRt = main.TryCast<RenderTexture>();
                // 출처를 모르는 RT(DicedSprite·영상 등)는 내용 변화를 알 수 없으므로 항상 그림
                if (mainRt != null && !TextureVersions.TryGetValue(mainRt.Pointer, out version)) { CopyDraws++; StageRender.Dirty = true; return true; }

                // 내용 버전을 뺀 입력 (전환·색·대상). 이것이 바뀌면 연출이 시작된 것
                var tint = renderer.TintColor;
                long h = main.Pointer.ToInt64();
                h = h * 31 + texture.Pointer.ToInt64();
                h = h * 31 + (renderer.TransitionTexture?.Pointer.ToInt64() ?? 0);
                h = h * 31 + BitConverter.SingleToInt32Bits(renderer.TransitionProgress);
                h = h * 31 + BitConverter.SingleToInt32Bits(renderer.BlurIntensity);
                h = h * 31 + HashCode.Combine(tint.r, tint.g, tint.b, tint.a);
                h = h * 31 + (renderer.FlipX ? 1 : 0) + (renderer.FlipY ? 2 : 0);
                long inputs = h;
                h = h * 31 + version;

                var key = renderer.Pointer;
                if (!Copies.TryGetValue(key, out var s)) Copies[key] = s = new CopyState { Hash = h - 1, Inputs = inputs };
                float now = Time.unscaledTime;
                bool due = now - s.LastDraw >= RefreshInterval;
                if (s.Hash == h && (!due || !TakeRefreshSlot(ref _copyRefreshFrame)))
                {
                    CopySkips++;
                    return false;
                }
                // 전환·색 변화가 시작됨 → 레이어 캐릭터를 잠시 모두 매 프레임 검사.
                // 내용 버전만 바뀐 경우(1초 안전 갱신 포함)는 해당 없음
                if (s.Inputs != inputs) _allHotUntil = now + HotTime;
                // 안전 갱신(입력이 같음)은 알리지 않음. 법정 카메라는 자기 안전 갱신으로 따라감
                if (s.Hash != h) StageRender.Dirty = true;
                s.Inputs = inputs;
                s.Hash = h;
                s.LastDraw = now;
                CopyDraws++;
                return true;
            }
        }

        // 레이어 캐릭터 합성 해상도. 메타데이터 PPU를 바꾸면 합성 RT 크기와 화면 쿼드 크기가 같이 바뀌어
        // 화면상 크기는 그대로다. 액터가 처음 만들어질 때(CreateFor) 한 번 적용된다.
        [HarmonyPatch(typeof(TransitionalRenderer), nameof(TransitionalRenderer.CreateFor))]
        static class CreateForPatch
        {
            static void Prefix(OrthoActorMetadata actorMetadata)
            {
                if (actorMetadata == null || actorMetadata.Implementation == null) return;
                if (!actorMetadata.Implementation.Contains("LayeredCharacter")) return;
                var key = actorMetadata.Pointer;
                if (!OriginalPpu.TryGetValue(key, out float ppu)) OriginalPpu[key] = ppu = actorMetadata.PixelsPerUnit;
                float scale = Settings.CharacterResolution.Value;
                if (scale <= 0f) scale = Mathf.Clamp(Screen.height / ReferenceHeight, 0.25f, 1f);
                actorMetadata.PixelsPerUnit = ppu * Mathf.Min(scale, 1f);
                Plugin.L.LogInfo($"character ppu {ppu} -> {actorMetadata.PixelsPerUnit} ({actorMetadata.Implementation})");
            }
        }
    }
}
