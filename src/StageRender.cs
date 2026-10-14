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
        // 실험실이 측정 동안 덮어쓰는 배율. null이면 설정값 사용
        internal static float? ResolutionOverride;
        internal static int Skipped;

        // 게임이 무대 카메라를 켜고 끈 마지막 값. 한 번도 못 봤으면(null) 카메라를 건드리지 않음
        static bool? _presented;
        static StageScreenController _screens;
        static Camera[] _cameras;
        static RawImage[] _images;

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
            Resize();
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

        // 원래 RT는 화면 해상도와 무관하게 1920x1080. RawImage가 화면 크기로 보여 주므로
        // 화면보다 큰 RT는 낭비다. 0 = 화면 높이에 맞춤(1080 이상이면 원래 크기). 해상도를 바꾸면 따라감.
        // RT 크기만 바꾸면 RawImage가 늘려 보여 주므로 화면 배치는 그대로
        static void Resize()
        {
            float scale = ResolutionOverride ?? Settings.StageResolution.Value;
            if (scale <= 0f) scale = Screen.height / 1080f;
            scale = Mathf.Min(scale, 1f);
            int w = Mathf.RoundToInt(1920 * scale), h = Mathf.RoundToInt(1080 * scale);
            foreach (var cam in _cameras)
            {
                var rt = cam?.targetTexture;
                if (rt == null || rt.width == w) continue;
                rt.Release();
                rt.width = w;
                rt.height = h;
                Plugin.L.LogInfo($"stage rt {w}x{h}");
            }
        }
    }
}
