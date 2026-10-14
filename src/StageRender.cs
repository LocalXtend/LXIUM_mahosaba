using HarmonyLib;
using Il2CppInterop.Runtime;
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
        internal static bool HiddenSkip, MsaaOff, PostOff, HalfRate;
        // 실험실 진단용: 무대 카메라를 모두 멈춰(RT는 마지막 그림 유지) 메인 카메라·UI·캐릭터만의 비용을 잼
        internal static bool Freeze;
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
            Resize();
            ApplyCameraData();
            if (_presented is not bool presented) return;
            for (int i = 0; i < _cameras.Length; i++)
            {
                var cam = _cameras[i];
                if (cam == null) continue;
                bool want = presented && !Freeze && (!HiddenSkip || Visible(_images[i]));
                // 반 프레임: 카메라가 멈춰 있으면 두 프레임에 한 번만 그리고 그 사이에는 RT의 지난 그림을 그대로 보여 줌.
                // 카메라가 움직이는 동안은 매 프레임 그려서 팬·줌이 끊겨 보이지 않게 함. 두 카메라는 번갈아 그림
                if (want && HalfRate && !Moved(i, cam) && ((Time.frameCount + i) & 1) == 1) want = false;
                if (cam.enabled != want) cam.enabled = want;
                if (presented && !want) Skipped++;
            }
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
        static void ApplyCameraData()
        {
            bool needDepth = UrpTuning.Ssao == SsaoMode.Off && !FireRender.UsingLite;
            if (_main == null) _main = Camera.main?.GetComponent<UniversalAdditionalCameraData>();
            bool mainPost = _main == null || _main.renderPostProcessing;
            for (int i = 0; i < _data.Length; i++)
            {
                var d = _data[i];
                if (d == null) continue;
                bool post = _origPost[i] && !(PostOff && mainPost);
                if (d.renderPostProcessing != post) d.renderPostProcessing = post;
                var depth = needDepth ? CameraOverrideOption.On : _origDepth[i];
                if (d.requiresDepthOption != depth) d.requiresDepthOption = depth;
            }
        }

        // 원래 RT는 화면 해상도와 무관하게 1920x1080. RawImage가 화면 크기로 보여 주므로
        // 화면보다 큰 RT는 낭비다. 0 = 화면 높이에 맞춤(1080 이상이면 원래 크기). 해상도를 바꾸면 따라감.
        // RT 크기만 바꾸면 RawImage가 늘려 보여 주므로 화면 배치는 그대로. MSAA를 끄면 FXAA만 남음
        static void Resize()
        {
            float scale = ResolutionOverride ?? Settings.StageResolution.Value;
            if (scale <= 0f) scale = Screen.height / 1080f;
            scale = Mathf.Min(scale, 1f);
            int w = Mathf.RoundToInt(1920 * scale), h = Mathf.RoundToInt(1080 * scale);
            int aa = MsaaOff ? 1 : _origAa;
            foreach (var cam in _cameras)
            {
                var rt = cam?.targetTexture;
                if (rt == null || (rt.width == w && rt.antiAliasing == aa)) continue;
                rt.Release();
                rt.width = w;
                rt.height = h;
                rt.antiAliasing = aa;
                Plugin.L.LogInfo($"stage rt {w}x{h} msaa={aa}");
            }
        }
    }
}
