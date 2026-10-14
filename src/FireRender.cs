using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;
using WitchTrials.Views;

namespace LXIUM_mahosaba
{
    // 법정 불(Court._fireVfx 아래 파티클 25개, 셰이더 SH_Vefects_VFX_URP_Fire_Flames_01)을 LXIUM FireLite로 그림
    // (docs/ai/mahosaba-render.md C장). 불 재질의 셰이더만 바꿔 끼우므로 게임의 불 켜고 끄기
    // (Court.ChangeFireFxActivity)와 재질의 색·속도 값은 그대로 따른다. FireLite는 D3D11 전용이라 다른 그래픽 API에서는 원본 유지
    static class FireRender
    {
        const string OriginalName = "SH_Vefects_VFX_URP_Fire_Flames_01";

        // 설정·실험실이 정하는 값
        internal static bool Lite;
        static bool _applied;
        static float _nextFind;
        static Shader _lite, _original;
        static bool _loaded;
        static readonly List<Material> Materials = new();

        // FireLite가 실제로 쓰이는 중인지 (D3D11이 아니면 셰이더가 지원되지 않아 원본 유지)
        internal static bool UsingLite => _applied && _lite != null;

        public static void Tick()
        {
            if (!Lite && !_applied) return;
            // 법정 프리팹은 재판마다 다시 로드될 수 있어 1초마다 불 재질을 다시 모음
            if (Lite == _applied && Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 1f;
            _applied = Lite;
            Collect();
            var want = Lite ? LiteShader() ?? _original : _original;
            if (want == null) return;
            foreach (var m in Materials)
                if (m != null && m.shader != want) m.shader = want;
        }

        static Shader LiteShader()
        {
            if (_loaded) return _lite;
            _loaded = true;
            try
            {
                var mem = new MemoryStream();
                using (var s = typeof(FireRender).Assembly.GetManifestResourceStream("lxium_mahosaba.bundle"))
                    s.CopyTo(mem);
                var bundle = AssetBundle.LoadFromMemory(mem.ToArray());
                var shader = bundle.LoadAllAssetsAsync(Il2CppType.Of<Shader>()).allAssets[0].Cast<Shader>();
                bundle.Unload(false);
                // 쓰지 않는 동안 게임의 UnloadUnusedAssets가 지우지 않게
                shader.hideFlags = HideFlags.DontUnloadUnusedAsset;
                if (shader.isSupported) _lite = shader;
                Plugin.L.LogInfo($"fire lite shader {shader.name} supported={shader.isSupported}");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("fire lite shader: " + e.Message);
            }
            return _lite;
        }

        static void Collect()
        {
            Materials.Clear();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Court>()))
            {
                var fire = o.Cast<Court>()._fireVfx;
                if (fire == null) continue;
                foreach (var r in fire.GetComponentsInChildren<Renderer>(true))
                {
                    var m = r.sharedMaterial;
                    var s = m?.shader;
                    if (s == null) continue;
                    if (s.name == OriginalName) _original = s;
                    else if (_lite == null || s.Pointer != _lite.Pointer) continue;
                    if (!Materials.Exists(x => x.Pointer == m.Pointer)) Materials.Add(m);
                }
            }
        }

        static GameObject _forced;
        static bool _forcedWasActive;

        // 실험실 전용: 지금 쓰는 법정의 불을 잠시 켬. 게임 상태(TrialStateManager)·조명 색은 건드리지 않음.
        // 반환값: 켠 불 렌더러 수 (법정이 없으면 0)
        public static int ForceFire(bool on)
        {
            if (!on)
            {
                if (_forced != null) _forced.SetActive(_forcedWasActive);
                _forced = null;
                return 0;
            }
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Court>()))
            {
                var court = o.Cast<Court>();
                var fire = court._fireVfx;
                if (fire == null || !court.gameObject.activeInHierarchy) continue;
                _forced = fire;
                _forcedWasActive = fire.activeSelf;
                fire.SetActive(true);
                return fire.GetComponentsInChildren<Renderer>(true).Length;
            }
            return 0;
        }

        public static int VisibleFire()
        {
            if (_forced == null) return 0;
            int n = 0;
            foreach (var r in _forced.GetComponentsInChildren<Renderer>(false))
                if (r.isVisible) n++;
            return n;
        }
    }
}
