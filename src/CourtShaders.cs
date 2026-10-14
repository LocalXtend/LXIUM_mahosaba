using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;
using WitchTrials.Views;

namespace LXIUM_mahosaba
{
    // 법정(Court 프리팹) 재질의 셰이더를 LXIUM 셰이더로 바꿔 끼움 (docs/ai/mahosaba-render.md C장).
    // - FireLite: 불 파티클 25개 (원본 SH_Vefects_VFX_URP_Fire_Flames_01)
    // - CourtLite: 바닥·계단·벽·단상 (원본 URP Lit)
    // 재질의 셰이더만 바꾸므로 게임의 불 켜고 끄기(Court.ChangeFireFxActivity)와 재질 값·키워드는 그대로 따른다.
    // LXIUM 셰이더는 D3D11 전용이라 다른 그래픽 API에서는 원본 유지
    static class CourtShaders
    {
        internal sealed class Swap
        {
            public readonly string Original, Lite;
            // 설정·실험실이 정하는 값
            public bool On;
            internal bool Applied;
            internal Shader OriginalShader;
            internal readonly List<Material> Materials = new();
            // 원본 셰이더일 때의 렌더 큐. Material.shader를 바꾸면 Unity가 큐를 셰이더 기본값(Lit은 2000)으로
            // 되돌려, 투명 단상(3000)이 불투명 순서에 그려지고 뒤의 벽에 덮여 사라짐 → 바꿀 때마다 다시 넣음
            internal readonly Dictionary<IntPtr, int> Queues = new();

            public Swap(string original, string lite)
            {
                Original = original;
                Lite = lite;
            }

            // LXIUM 셰이더가 실제로 쓰이는 중인지 (D3D11이 아니면 지원되지 않아 원본 유지)
            public bool Active => Applied && Get(Lite) != null;
        }

        internal static readonly Swap Fire = new("SH_Vefects_VFX_URP_Fire_Flames_01", "LXIUM/FireLite");
        internal static readonly Swap Court = new("Universal Render Pipeline/Lit", "LXIUM/CourtLite");
        static readonly Swap[] All = { Fire, Court };

        static float _nextFind;
        static Dictionary<string, Shader> _lite;

        public static void Tick()
        {
            bool changed = false, any = false;
            foreach (var s in All)
            {
                changed |= s.On != s.Applied;
                any |= s.On || s.Applied;
            }
            if (!any) return;
            // 법정 프리팹은 재판마다 다시 로드될 수 있어 1초마다 재질을 다시 모음
            if (!changed && Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 1f;
            Collect();
            foreach (var s in All)
            {
                s.Applied = s.On;
                var want = s.On ? Get(s.Lite) ?? s.OriginalShader : s.OriginalShader;
                if (want == null) continue;
                foreach (var m in s.Materials)
                {
                    if (m == null || m.shader == want) continue;
                    m.shader = want;
                    if (s.Queues.TryGetValue(m.Pointer, out int queue)) m.renderQueue = queue;
                }
            }
        }

        // 번들의 LXIUM 셰이더 (지원되는 것만). 처음 부를 때 DLL에 든 번들을 로드
        internal static Shader Get(string name)
        {
            if (_lite == null)
            {
                _lite = new Dictionary<string, Shader>();
                try
                {
                    var mem = new MemoryStream();
                    using (var s = typeof(CourtShaders).Assembly.GetManifestResourceStream("lxium_mahosaba.bundle"))
                        s.CopyTo(mem);
                    var bundle = AssetBundle.LoadFromMemory(mem.ToArray());
                    foreach (var o in bundle.LoadAllAssetsAsync(Il2CppType.Of<Shader>()).allAssets)
                    {
                        var shader = o.Cast<Shader>();
                        // 쓰지 않는 동안 게임의 UnloadUnusedAssets가 지우지 않게
                        shader.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        Plugin.L.LogInfo($"lite shader {shader.name} supported={shader.isSupported}");
                        if (shader.isSupported) _lite[shader.name] = shader;
                    }
                    bundle.Unload(false);
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning("lite shader: " + e.Message);
                }
            }
            return _lite.TryGetValue(name, out var found) ? found : null;
        }

        static void Collect()
        {
            foreach (var s in All) s.Materials.Clear();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Court>()))
            {
                var court = o.Cast<Court>();
                Add(court.GetComponentsInChildren<Renderer>(true));
                if (court._fireVfx != null) Add(court._fireVfx.GetComponentsInChildren<Renderer>(true));
            }
        }

        static void Add(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Renderer> renderers)
        {
            foreach (var r in renderers)
            {
                var m = CourtCache.LiveOf(r.sharedMaterial);
                var shader = m?.shader;
                if (shader == null) continue;
                string name = shader.name;
                foreach (var s in All)
                {
                    if (name == s.Original)
                    {
                        s.OriginalShader = shader;
                        s.Queues[m.Pointer] = m.renderQueue;
                    }
                    else if (name != s.Lite) continue;
                    if (!s.Materials.Exists(x => x.Pointer == m.Pointer)) s.Materials.Add(m);
                    break;
                }
            }
        }

        internal static Court ActiveCourt()
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Court>()))
            {
                var court = o.Cast<Court>();
                if (court.gameObject.activeInHierarchy) return court;
            }
            return null;
        }
    }
}
