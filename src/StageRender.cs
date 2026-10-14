using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
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

        internal static bool HiddenSkip;
        internal static int Skipped;

        // 게임이 무대 카메라를 켜고 끈 마지막 값. 한 번도 못 봤으면(null) 카메라를 건드리지 않음
        static bool? _presented;
        static StageScreenController _screens;
        static Camera[] _cameras;
        static RawImage[] _images;
        static bool _resized;

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
            for (int i = 0; i < n; i++)
            {
                _cameras[i] = cams[i]._camera;
                _images[i] = scrs[i].GetComponentInChildren<RawImage>(true);
            }
            var rt = n > 0 ? _cameras[0]?.targetTexture : null;
            Plugin.L.LogInfo($"stage cameras={n} rt={rt?.width}x{rt?.height}");
            return true;
        }

        // 매 프레임 LateUpdate: 게임이 무대를 띄운 동안에도 화면에서 안 보이는 카메라는 끔
        public static void LateTick()
        {
            if (!Find()) return;
            if (!_resized) _resized = Resize(Settings.StageResolution.Value);
            if (_presented is not bool presented) return;
            for (int i = 0; i < _cameras.Length; i++)
            {
                var cam = _cameras[i];
                if (cam == null) continue;
                bool want = presented && (!HiddenSkip || Visible(_images[i]));
                if (cam.enabled != want) cam.enabled = want;
                if (presented && !want) Skipped++;
            }
        }

        static bool Visible(RawImage img) =>
            img != null && img.isActiveAndEnabled && img.canvasRenderer.GetInheritedAlpha() > 0.001f;

        // RT 크기만 바꾸면 RawImage가 늘려 보여 주므로 화면 배치는 그대로
        static bool Resize(float scale)
        {
            if (scale >= 1f || scale <= 0f) return true;
            foreach (var cam in _cameras)
            {
                var rt = cam?.targetTexture;
                if (rt == null) return false;
                int w = Mathf.RoundToInt(1920 * scale), h = Mathf.RoundToInt(1080 * scale);
                if (rt.width == w) continue;
                rt.Release();
                rt.width = w;
                rt.height = h;
            }
            Plugin.L.LogInfo($"stage rt scale {scale}");
            return true;
        }
    }
}
