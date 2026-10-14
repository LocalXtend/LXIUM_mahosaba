using System;
using System.Collections.Generic;
using UnityEngine;
using WitchTrials.Views;

namespace LXIUM_mahosaba
{
    // 법정 건물 캐시 (docs/ai/mahosaba-render.md B15). 법정 카메라가 멈춰 있고 조명이 그대로인 동안 바닥·벽·계단의
    // 조명 결과를 한 번만 그려 두고(CourtLite), 무대 카메라는 같은 물체를 그 결과를 픽셀 그대로 읽는
    // LXIUM/CourtCached 재질로 그린다. 깊이는 그대로 써서 캐릭터·단상·불과의 앞뒤 관계는 원래와 같다.
    // 캐시 렌더는 건물을 전용 레이어로 옮겨 그 레이어만 보는 카메라로 한다. 캐릭터는 Graphics.DrawMesh로
    // Default 레이어에 그려져 레이어로만 뺄 수 있기 때문. 무대 카메라 두 대가 함께 그려지는 분할 화면에서는 쓰지 않음
    static class CourtCache
    {
        const int Layer = 31;
        // 감지하지 못한 변화가 남는 시간의 상한
        const float RefreshInterval = 3f;

        // 설정·실험실이 정하는 값
        internal static bool On;
        internal static int Refreshes;

        sealed class Item
        {
            public Renderer Renderer;
            public Material Live, Cached;
            public int Layer;
        }

        static Court _court;
        static readonly List<Item> Items = new();
        static readonly Dictionary<IntPtr, Material> LiveOfCached = new();
        static readonly List<Material> Copies = new();
        static readonly List<(Light light, int mask)> Lights = new();
        static readonly List<(Camera cam, int mask)> StageMasks = new();
        static Camera _cacheCam, _lastCam;
        static RenderTexture _cache;
        static bool _usingCached;
        static Vector3 _pos;
        static Quaternion _rot;
        static float _fov, _lastRefresh;
        static long _lightKey;

        // 무대 카메라 렌더 직전(LateUpdate)에 부름. rendering[i] = 이번 프레임에 그 카메라를 그리는지
        public static void Tick(Camera[] cams, bool[] rendering)
        {
            var cached = CourtShaders.Get("LXIUM/CourtCached");
            if (!On || cached == null || !CourtShaders.Court.Active)
            {
                Teardown();
                return;
            }
            Camera cam = null;
            for (int i = 0; i < cams.Length; i++)
            {
                if (!rendering[i] || cams[i] == null) continue;
                if (cam != null) { UseLive(); return; }
                cam = cams[i];
            }
            if (cam == null || !Setup(cams, cached)) return;

            var rt = cam.targetTexture;
            if (rt == null) { UseLive(); return; }
            bool stale = cam != _lastCam || Time.unscaledTime - _lastRefresh >= RefreshInterval;
            if (_cache == null || _cache.width != rt.width || _cache.height != rt.height)
            {
                if (_cache != null) UnityEngine.Object.Destroy(_cache);
                _cache = new RenderTexture(rt.width, rt.height, 24, rt.format) { filterMode = FilterMode.Point };
                _cache.Create();
                foreach (var item in Items) item.Cached.SetTexture("_OcclusionMap", _cache);
                stale = true;
            }
            var tr = cam.transform;
            Vector3 pos = tr.position;
            Quaternion rot = tr.rotation;
            float fov = cam.fieldOfView;
            long lightKey = LightKey();
            stale |= pos != _pos || rot != _rot || fov != _fov || lightKey != _lightKey;
            if (stale)
            {
                Refresh(cam);
                _pos = pos;
                _rot = rot;
                _fov = fov;
                _lightKey = lightKey;
                _lastCam = cam;
                _lastRefresh = Time.unscaledTime;
            }
            UseCached();
        }

        // 원래 재질(CourtLite)로 건물만 캐시에 그림
        static void Refresh(Camera cam)
        {
            UseLive();
            _cacheCam.CopyFrom(cam);
            _cacheCam.enabled = false;
            _cacheCam.cullingMask = 1 << Layer;
            _cacheCam.targetTexture = _cache;
            _cacheCam.Render();
            Refreshes++;
        }

        static bool Setup(Camera[] cams, Shader cached)
        {
            var court = CourtShaders.ActiveCourt();
            if (court == null) { Teardown(); return false; }
            if (_court != null && court.Pointer == _court.Pointer) return true;
            Teardown();
            var copies = new Dictionary<IntPtr, Material>();
            foreach (var r in court.GetComponentsInChildren<Renderer>(true))
            {
                var live = r.sharedMaterial;
                if (live == null || live.shader?.name != CourtShaders.Court.Lite) continue;
                // 불투명·알파 테스트만 (단상 같은 투명 재질은 캐릭터 위에 섞여야 해서 매 프레임 그림).
                // 캐시 셰이더의 벽 변형은 기준값 0.5로 고정이라 다른 값이면 원래대로 그림
                if (live.renderQueue >= 2500) continue;
                if (live.renderQueue > 2000 && Math.Abs(live.GetFloat("_Cutoff") - 0.5f) > 1e-4f) continue;
                if (!copies.TryGetValue(live.Pointer, out var copy))
                {
                    copy = new Material(live) { shader = cached, renderQueue = live.renderQueue };
                    copies[live.Pointer] = copy;
                    LiveOfCached[copy.Pointer] = live;
                    Copies.Add(copy);
                }
                Items.Add(new Item { Renderer = r, Live = live, Cached = copy, Layer = r.gameObject.layer });
            }
            // CourtLite가 아직 적용되기 전이면(1초 안) 다음 프레임에 다시 찾음
            if (Items.Count == 0) return false;
            _court = court;
            foreach (var item in Items) item.Renderer.gameObject.layer = Layer;
            foreach (var light in court.GetComponentsInChildren<Light>(true))
            {
                Lights.Add((light, light.cullingMask));
                light.cullingMask |= 1 << Layer;
            }
            foreach (var cam in cams)
            {
                if (cam == null) continue;
                StageMasks.Add((cam, cam.cullingMask));
                cam.cullingMask |= 1 << Layer;
            }
            if (_cacheCam == null)
            {
                var go = new GameObject("LXIUM CourtCache");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _cacheCam = go.AddComponent<Camera>();
                _cacheCam.enabled = false;
            }
            _lastCam = null;
            Plugin.L.LogInfo($"court cache: {Items.Count} renderers, {copies.Count} materials, {Lights.Count} lights");
            return true;
        }

        static void Teardown()
        {
            if (_court == null) return;
            UseLive();
            foreach (var item in Items)
                if (item.Renderer != null) item.Renderer.gameObject.layer = item.Layer;
            foreach (var (light, mask) in Lights)
                if (light != null) light.cullingMask = mask;
            foreach (var (cam, mask) in StageMasks)
                if (cam != null) cam.cullingMask = mask;
            foreach (var copy in Copies)
                if (copy != null) UnityEngine.Object.Destroy(copy);
            Copies.Clear();
            Items.Clear();
            Lights.Clear();
            StageMasks.Clear();
            LiveOfCached.Clear();
            if (_cache != null) UnityEngine.Object.Destroy(_cache);
            _cache = null;
            _court = null;
        }

        static void UseCached()
        {
            if (_usingCached) return;
            foreach (var item in Items)
                if (item.Renderer != null) item.Renderer.sharedMaterial = item.Cached;
            _usingCached = true;
        }

        static void UseLive()
        {
            if (!_usingCached) return;
            foreach (var item in Items)
                if (item.Renderer != null) item.Renderer.sharedMaterial = item.Live;
            _usingCached = false;
        }

        // 법정 조명(스포트라이트)이 바뀌면 캐시를 다시 그림 (불 연출의 불빛 색 전환 등)
        static long LightKey()
        {
            long h = 17;
            foreach (var (light, _) in Lights)
            {
                if (light == null) continue;
                var c = light.color;
                var t = light.transform;
                Vector3 p = t.position;
                Quaternion q = t.rotation;
                h = h * 31 + (light.isActiveAndEnabled ? 1 : 0);
                h = h * 31 + HashCode.Combine(c.r, c.g, c.b, light.intensity, light.range, light.spotAngle);
                h = h * 31 + HashCode.Combine(p.x, p.y, p.z, q.x, q.y, q.z, q.w);
            }
            return h;
        }

        // 캐시 재질을 쓰는 동안 렌더러가 가진 재질을 원래 재질로 바꿔 줌 (CourtShaders가 원래 재질을 찾을 때)
        public static Material LiveOf(Material m) => m != null && LiveOfCached.TryGetValue(m.Pointer, out var live) ? live : m;
    }
}
