using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Naninovel;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using WitchTrials.Views;

namespace LXIUM_mahosaba
{
    // 재판 3D 무대 (Boot 씬의 PerspectiveStage). 원근 카메라 2대가 각각 1920x1080 RT에 그리고,
    // ScreenCanvas의 RawImage 2개가 그 RT를 보여 준다 (화면 분할). 게임은 무대를 띄우면 두 카메라를
    // 함께 켜고(StageCameraController.SetCameraEnabled), 화면별 표시는 CanvasGroup 알파로만 바꾼다.
    static class StageRender
    {
        // 재판 무대 카메라만 원근 카메라. 메인·캐릭터 합성 카메라는 모두 직교
        public static bool IsStageCamera(Camera cam) => !cam.orthographic;

        // 설정·실험실이 정하는 값
        internal static bool HiddenSkip, MsaaOff, PostOff, HalfRate, ChangeOnly;
        // 법정 카메라가 보는 것이 바뀌었다는 표시. 캐릭터 그림(ActorRender)·법정 셰이더 교체(CourtShaders)·실험실 설정이 켬
        internal static bool Dirty;
        // 바뀔 때만 그리기에서 놓친 변화가 화면에 남는 시간의 상한
        const float RefreshInterval = 1f;
        // 실험실 진단용: 무대 카메라를 모두 멈춰(RT는 마지막 그림 유지) 메인 카메라·UI·캐릭터만의 비용을 잼
        internal static bool Freeze;
        // HiddenSkip과 함께: 안 보이는(알파 0) 무대 화면 RawImage도 그리지 않음. 실험실이 끄고 비교함
        internal static bool CullHidden = true;
        // 실험실이 측정 동안 덮어쓰는 배율. null이면 설정값 사용
        internal static float? ResolutionOverride;
        internal static int Skipped;

        // 게임이 무대 카메라를 켜고 끈 마지막 값. 한 번도 못 봤으면(null) 카메라를 건드리지 않음
        static bool? _presented;
        static StageScreenController _screens;
        static Camera[] _cameras;
        static RawImage[] _images;
        static UniversalAdditionalCameraData[] _data;
        static bool[] _origPost;
        static CameraOverrideOption[] _origDepth;
        static int _origAa;
        static Vector3[] _lastPos;
        static Quaternion[] _lastRot;
        static float[] _lastFov;
        static UniversalAdditionalCameraData _main;
        // 장면 버전: 바뀐 프레임마다 1 증가. 카메라마다 마지막으로 그린 버전(-1 = 지난 그림이 맞지 않음)과 시각
        static int _version;
        static int[] _drawnVersion;
        static float[] _lastDraw;
        static long _actorSum, _actorKey, _courtKey;

        [HarmonyPatch(typeof(StageCameraController), "SetCameraEnabled")]
        static class SetCameraEnabledPatch
        {
            static void Postfix(bool active) => _presented = active;
        }

        static float _nextFind;

        // 무대는 Boot 씬에 늘 있으므로 보통 첫 프레임에 찾음. 못 찾으면 1초마다 다시 (전체 객체 검색이라 비쌈)
        static bool Find()
        {
            if (_screens != null) return true;
            if (Time.unscaledTime < _nextFind) return false;
            _nextFind = Time.unscaledTime + 1f;
            var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<StageScreenController>());
            if (found.Length == 0) return false;
            _screens = found[0].Cast<StageScreenController>();
            var cams = _screens._cameraContainers;
            var scrs = _screens._screenContainers;
            int n = Mathf.Min(cams.Length, scrs.Length);
            _cameras = new Camera[n];
            _images = new RawImage[n];
            _data = new UniversalAdditionalCameraData[n];
            _origPost = new bool[n];
            _origDepth = new CameraOverrideOption[n];
            _lastPos = new Vector3[n];
            _lastRot = new Quaternion[n];
            _lastFov = new float[n];
            _drawnVersion = new int[n];
            Array.Fill(_drawnVersion, -1);
            _lastDraw = new float[n];
            for (int i = 0; i < n; i++)
            {
                _cameras[i] = cams[i]._camera;
                _images[i] = scrs[i].GetComponentInChildren<RawImage>(true);
                _data[i] = _cameras[i]?.GetComponent<UniversalAdditionalCameraData>();
                if (_data[i] == null) continue;
                _origPost[i] = _data[i].renderPostProcessing;
                _origDepth[i] = _data[i].requiresDepthOption;
            }
            var rt = n > 0 ? _cameras[0]?.targetTexture : null;
            _origAa = rt != null ? rt.antiAliasing : 1;
            Plugin.L.LogInfo($"stage cameras={n} rt={rt?.width}x{rt?.height} msaa={_origAa} post={(n > 0 && _origPost[0])}");
            return true;
        }

        // 매 프레임 LateUpdate: 게임이 무대를 띄운 동안에도 화면에서 안 보이는 카메라는 끔
        public static void LateTick()
        {
            if (!Find()) return;
            // RT를 다시 만들거나 후처리·깊이를 바꾸면 그림이 달라짐
            if (Resize() | ApplyCameraData()) Dirty = true;
            if (_presented is not bool presented) return;
            if (ChangeOnly && SceneChanged()) _version++;
            float now = Time.unscaledTime;
            for (int i = 0; i < _cameras.Length; i++)
            {
                var cam = _cameras[i];
                if (cam == null) continue;
                bool visible = Visible(_images[i]);
                bool want = presented && !Freeze && (!HiddenSkip || visible);
                // CanvasGroup 알파가 0이어도 UI는 화면 전체 크기 투명 사각형을 그대로 그림 → 그리기 목록에서 뺌
                var cr = _images[i]?.canvasRenderer;
                bool cull = HiddenSkip && CullHidden && !visible;
                if (cr != null && cr.cull != cull) cr.cull = cull;
                // 카메라가 움직였으면 다시 그릴 때까지 지난 그림은 맞지 않음 (안 그리는 동안 움직였어도)
                bool moved = Moved(i, cam);
                if (moved) _drawnVersion[i] = -1;
                // 반 프레임: 카메라가 멈춰 있으면 두 프레임에 한 번만 그리고 그 사이에는 RT의 지난 그림을 그대로 보여 줌.
                // 카메라가 움직이는 동안은 매 프레임 그려서 팬·줌이 끊겨 보이지 않게 함. 두 카메라는 번갈아 그림
                if (want && HalfRate && !moved && ((Time.frameCount + i) & 1) == 1) want = false;
                // 바뀔 때만 그리기: 마지막으로 그린 뒤 장면이 그대로면 RT의 지난 그림을 그대로 보여 줌. 1초마다 한 번은 그림
                if (want && ChangeOnly && _drawnVersion[i] == _version && now - _lastDraw[i] < RefreshInterval) want = false;
                if (cam.enabled != want) cam.enabled = want;
                if (presented && !want) Skipped++;
                if (!want) continue;
                _drawnVersion[i] = _version;
                _lastDraw[i] = now;
            }
        }

        // 무대 카메라가 보는 것이 지난 프레임과 달라졌는지. 캐릭터 그림 내용은 ActorRender가 Dirty로 알리고,
        // 캐릭터 위치·크기·불투명도와 법정 렌더러·조명은 여기서 매 프레임 비교함
        static bool SceneChanged()
        {
            long actors = _actorSum;
            _actorSum = 0;
            long court = CourtKey(out bool moving);
            bool changed = Dirty || moving || actors != _actorKey || court != _courtKey;
            Dirty = false;
            _actorKey = actors;
            _courtKey = court;
            return changed;
        }

        // ActorRender가 RenderToTexture마다 부름 (Update에서 화면에 그려지는 액터마다 한 번, 그 직후 DrawMesh).
        // 순서와 무관하게 더해 두고 LateTick이 지난 프레임 합과 비교함. 액터가 사라지면 합에서 빠져 바뀐 것으로 잡힘
        internal static void NoteActor(TransitionalRenderer r) =>
            _actorSum += HashCode.Combine(r.Pointer, r.Opacity, MatrixHash(r.transform.localToWorldMatrix));

        static int MatrixHash(Matrix4x4 m) => HashCode.Combine(
            HashCode.Combine(m.m00, m.m01, m.m02, m.m03),
            HashCode.Combine(m.m10, m.m11, m.m12, m.m13),
            HashCode.Combine(m.m20, m.m21, m.m22, m.m23));

        sealed class Part
        {
            public Renderer Renderer;
            public GameObject Object;
            public Transform Transform;
            // 파티클·불처럼 스스로 움직이는 렌더러. 켜져 있는 동안은 매 프레임 그림
            public bool Moving;
        }

        static readonly List<Part> Parts = new();
        static readonly List<Light> Lights = new();
        static bool _courtKnown;
        static IntPtr _loggedCourt;
        // 시간에 따라 바뀌지 않는 법정 셰이더. 그 밖의 셰이더(불 등)는 켜져 있으면 움직이는 것으로 봄
        static readonly HashSet<string> StillShaders = new() { "Universal Render Pipeline/Lit", "LXIUM/CourtLite" };

        // CourtShaders가 1초마다 법정을 다시 모을 때 부름 (지금 보이는 법정, 없으면 null)
        internal static void SetCourt(Court court)
        {
            Parts.Clear();
            Lights.Clear();
            _courtKnown = court != null;
            if (court == null) return;
            var reasons = new Dictionary<string, int>();
            void Add(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Renderer> renderers)
            {
                foreach (var r in renderers)
                {
                    string why = MovingReason(r);
                    if (why != null) reasons[why] = reasons.GetValueOrDefault(why) + 1;
                    Parts.Add(new Part { Renderer = r, Object = r.gameObject, Transform = r.transform, Moving = why != null });
                }
            }
            Add(court.GetComponentsInChildren<Renderer>(true));
            if (court._fireVfx != null) Add(court._fireVfx.GetComponentsInChildren<Renderer>(true));
            foreach (var light in court.GetComponentsInChildren<Light>(true)) Lights.Add(light);
            if (court.Pointer == _loggedCourt) return;
            _loggedCourt = court.Pointer;
            Plugin.L.LogInfo($"stage court: {Parts.Count} renderers, {Lights.Count} lights, moving: {string.Join(", ", reasons)}");
        }

        static string MovingReason(Renderer r)
        {
            string type = r.GetIl2CppType().Name;
            if (type != "MeshRenderer") return type;
            foreach (var m in r.sharedMaterials)
            {
                string shader = m?.shader?.name;
                if (shader != null && !StillShaders.Contains(shader)) return shader;
            }
            return null;
        }

        // 법정 렌더러의 켜짐·변환과 조명. 스스로 움직이는 렌더러가 켜져 있으면 moving
        static long CourtKey(out bool moving)
        {
            moving = !_courtKnown;
            long h = 17;
            if (moving) return h;
            try
            {
                foreach (var p in Parts)
                {
                    bool on = p.Object.activeInHierarchy && p.Renderer.enabled;
                    h = h * 31 + (on ? 1 : 0);
                    if (!on) continue;
                    if (p.Moving) { moving = true; return h; }
                    h = h * 31 + MatrixHash(p.Transform.localToWorldMatrix);
                }
                foreach (var light in Lights)
                {
                    var c = light.color;
                    h = h * 31 + (light.isActiveAndEnabled ? 1 : 0);
                    h = h * 31 + HashCode.Combine(c.r, c.g, c.b, light.intensity, light.range, light.spotAngle);
                    h = h * 31 + MatrixHash(light.transform.localToWorldMatrix);
                }
            }
            // 파괴된 렌더러가 목록에 남은 경우: 다음 수집(1초 안)까지 매 프레임 그림
            catch (Exception) { moving = true; }
            return h;
        }

        static bool Visible(RawImage img) =>
            img != null && img.isActiveAndEnabled && img.canvasRenderer.GetInheritedAlpha() > 0.001f;

        static bool Moved(int i, Camera cam)
        {
            var tr = cam.transform;
            Vector3 p = tr.position;
            Quaternion q = tr.rotation;
            float fov = cam.fieldOfView;
            bool moved = p != _lastPos[i] || q != _lastRot[i] || fov != _lastFov[i];
            _lastPos[i] = p;
            _lastRot[i] = q;
            _lastFov[i] = fov;
            return moved;
        }

        // 실험실이 UI를 숨길 때 남겨 둘 캔버스 (무대 화면 RawImage가 있는 곳)
        internal static Canvas ScreenCanvas => _images?.Length > 0 ? _images[0]?.canvas?.rootCanvas : null;

        // 후처리: 볼륨 효과가 모두 0이라 무대 카메라의 후처리는 LUT 만들기 + FXAA뿐이고, 메인 카메라가 화면 전체에
        // FXAA를 한 번 더 함. 그래서 메인 카메라 후처리가 켜져 있을 때만 무대 쪽을 끔. 게임 옵션에서 후처리를 끄면
        // 무대 FXAA를 살려 법정 가장자리가 그대로 다듬어지게 함.
        // 깊이: SSAO를 끄면 깊이 텍스처가 안 생기는데 원본 불 셰이더는 깊이를 읽으므로 따로 요청 (FireLite는 깊이를 읽지 않음)
        static bool ApplyCameraData()
        {
            bool changed = false;
            bool needDepth = UrpTuning.Ssao == SsaoMode.Off && !CourtShaders.Fire.Active;
            if (_main == null) _main = Camera.main?.GetComponent<UniversalAdditionalCameraData>();
            bool mainPost = _main == null || _main.renderPostProcessing;
            for (int i = 0; i < _data.Length; i++)
            {
                var d = _data[i];
                if (d == null) continue;
                bool post = _origPost[i] && !(PostOff && mainPost);
                if (d.renderPostProcessing != post) { d.renderPostProcessing = post; changed = true; }
                var depth = needDepth ? CameraOverrideOption.On : _origDepth[i];
                if (d.requiresDepthOption != depth) { d.requiresDepthOption = depth; changed = true; }
            }
            return changed;
        }

        // 원래 RT는 화면 해상도와 무관하게 1920x1080. RawImage가 화면 크기로 보여 주므로
        // 화면보다 큰 RT는 낭비다. 0 = 화면 높이에 맞춤(1080 이상이면 원래 크기). 해상도를 바꾸면 따라감.
        // RT 크기만 바꾸면 RawImage가 늘려 보여 주므로 화면 배치는 그대로. MSAA를 끄면 FXAA만 남음
        static bool Resize()
        {
            float scale = ResolutionOverride ?? Settings.StageResolution.Value;
            if (scale <= 0f) scale = Screen.height / 1080f;
            scale = Mathf.Min(scale, 1f);
            int w = Mathf.RoundToInt(1920 * scale), h = Mathf.RoundToInt(1080 * scale);
            int aa = MsaaOff ? 1 : _origAa;
            bool changed = false;
            foreach (var cam in _cameras)
            {
                var rt = cam?.targetTexture;
                if (rt == null || (rt.width == w && rt.antiAliasing == aa)) continue;
                rt.Release();
                rt.width = w;
                rt.height = h;
                rt.antiAliasing = aa;
                changed = true;
                Plugin.L.LogInfo($"stage rt {w}x{h} msaa={aa}");
            }
            return changed;
        }
    }
}
