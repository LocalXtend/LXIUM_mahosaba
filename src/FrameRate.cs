using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace LXIUM_mahosaba
{
    // 프레임 제한 해제. 게임은 CameraManagerExtended.SetTargetFpsLevel로 60/30을 넣고,
    // VSync는 품질 단계(QualitySettings)가 정함. 둘 다 바뀔 수 있어 매 프레임 비교 후 다를 때만 씀.
    // QualitySettings.vSyncCount와 targetFrameRate getter는 IL2CPP 빌드에서 제거됐으므로
    // UnityPlayer.dll에 등록된 엔진 내부 호출을 직접 찾는다 (docs/ai/mahosaba.md 9.1).
    static class FrameRate
    {
        delegate int GetInt();
        delegate void SetInt(int value);

        static GetInt _getVSync, _getTarget;
        static SetInt _setVSync;
        static bool _resolved;

        // 실험실이 측정 동안 덮어쓰는 값. null이면 설정값 사용
        internal static int? Override;

        static void Resolve()
        {
            _resolved = true;
            try
            {
                _getVSync = IL2CPP.ResolveICall<GetInt>("UnityEngine.QualitySettings::get_vSyncCount");
                _setVSync = IL2CPP.ResolveICall<SetInt>("UnityEngine.QualitySettings::set_vSyncCount");
                _getTarget = IL2CPP.ResolveICall<GetInt>("UnityEngine.Application::get_targetFrameRate");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("vSync icall 없음, 프레임 상한만 설정: " + e.Message);
            }
        }

        public static void Enforce()
        {
            if (!_resolved) Resolve();
            int target = Override ?? Settings.TargetFps.Value;
            int vsync = target == 0 ? 1 : 0;
            int fps = target > 0 ? target : -1;
            if (_setVSync != null && _getVSync() != vsync) _setVSync(vsync);
            if (_getTarget == null || _getTarget() != fps) Application.targetFrameRate = fps;
        }
    }
}
