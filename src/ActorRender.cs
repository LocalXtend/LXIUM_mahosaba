using System;
using System.Collections.Generic;
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
            public SpriteRenderer[] Renderers;
            public long Hash;
            public IntPtr Texture;
            public float LastDraw;
        }

        sealed class CopyState
        {
            public long Hash;
            public float LastDraw;
        }

        static readonly Dictionary<IntPtr, DrawerState> Drawers = new();
        static readonly Dictionary<IntPtr, CopyState> Copies = new();
        // LayeredDrawer가 실제로 다시 그린 RT의 내용 버전
        static readonly Dictionary<IntPtr, int> TextureVersions = new();
        static readonly Dictionary<IntPtr, float> OriginalPpu = new();

        // 레이어 상태 해시. 이 게임의 캐릭터 프리팹에는 Animator가 없고 표정·포즈는
        // 렌더러 켜고 끄기, 스프라이트·색·재질·레이어 교체로만 바뀐다 (docs/ai/mahosaba.md 5.2)
        static long LayerHash(SpriteRenderer[] renderers, float ppu)
        {
            long h = BitConverter.SingleToInt32Bits(ppu);
            foreach (var r in renderers)
            {
                bool on = r.enabled && r.gameObject.activeInHierarchy;
                h = h * 31 + (on ? 1 : 0);
                if (!on) continue;
                var c = r.color;
                h = h * 31 + (r.sprite?.Pointer.ToInt64() ?? 0);
                h = h * 31 + (r.sharedMaterial?.Pointer.ToInt64() ?? 0);
                h = h * 31 + r.gameObject.layer;
                h = h * 31 + HashCode.Combine(c.r, c.g, c.b, c.a);
            }
            return h;
        }

        [HarmonyPatch(typeof(LayeredDrawer), nameof(LayeredDrawer.DrawLayers))]
        static class DrawLayersPatch
        {
            static bool Prefix(LayeredDrawer __instance, float pixelsPerUnit, RenderTexture renderTexture, ref RenderTexture __result, out DrawerState __state)
            {
                var key = __instance.Pointer;
                if (!Drawers.TryGetValue(key, out __state))
                {
                    var comps = __instance.transform.GetComponentsInChildren<SpriteRenderer>(true);
                    __state = new DrawerState { Renderers = comps };
                    Drawers[key] = __state;
                }
                long hash;
                // 프리팹 렌더러가 파괴됐는데 ClearLayers를 거치지 않은 경우: 상태를 버리고 원래대로 그림
                try { hash = LayerHash(__state.Renderers, pixelsPerUnit); }
                catch (Exception e)
                {
                    Plugin.L.LogWarning("layer hash 실패, 원래 경로로 그림: " + e.Message);
                    Drawers.Remove(key);
                    __state = new DrawerState();
                    return true;
                }
                bool same = hash == __state.Hash && renderTexture != null && renderTexture.Pointer == __state.Texture;
                bool due = Time.unscaledTime - __state.LastDraw >= RefreshInterval;
                if (ChangeOnly && same && (!due || !TakeRefreshSlot(ref _layerRefreshFrame)))
                {
                    LayerSkips++;
                    __result = renderTexture;
                    return false;
                }
                __state.Hash = hash;
                return true;
            }

            static void Postfix(RenderTexture __result, DrawerState __state, bool __runOriginal)
            {
                if (!__runOriginal || __result == null) return;
                LayerDraws++;
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
                try { return Decide(__instance, texture); }
                catch (Exception e)
                {
                    Plugin.L.LogWarning("copy hash 실패, 원래 경로로 그림: " + e.Message);
                    return true;
                }
            }

            static bool Decide(TransitionalRenderer renderer, RenderTexture texture)
            {
                if (!ChangeOnly || texture == null) { CopyDraws++; return true; }
                var main = renderer.MainTexture;
                if (main == null) return true;

                long h;
                var mainRt = main.TryCast<RenderTexture>();
                if (mainRt != null)
                {
                    // 출처를 모르는 RT(DicedSprite·영상 등)는 내용 변화를 알 수 없으므로 항상 그림
                    if (!TextureVersions.TryGetValue(mainRt.Pointer, out int version)) { CopyDraws++; return true; }
                    h = mainRt.Pointer.ToInt64() * 31 + version;
                }
                else h = main.Pointer.ToInt64();

                var tint = renderer.TintColor;
                h = h * 31 + texture.Pointer.ToInt64();
                h = h * 31 + (renderer.TransitionTexture?.Pointer.ToInt64() ?? 0);
                h = h * 31 + BitConverter.SingleToInt32Bits(renderer.TransitionProgress);
                h = h * 31 + BitConverter.SingleToInt32Bits(renderer.BlurIntensity);
                h = h * 31 + HashCode.Combine(tint.r, tint.g, tint.b, tint.a);
                h = h * 31 + (renderer.FlipX ? 1 : 0) + (renderer.FlipY ? 2 : 0);

                var key = renderer.Pointer;
                if (!Copies.TryGetValue(key, out var s)) Copies[key] = s = new CopyState { Hash = h - 1 };
                float now = Time.unscaledTime;
                bool due = now - s.LastDraw >= RefreshInterval;
                if (s.Hash == h && (!due || !TakeRefreshSlot(ref _copyRefreshFrame)))
                {
                    CopySkips++;
                    return false;
                }
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
