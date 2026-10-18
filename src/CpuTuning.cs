using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace LXIUM_mahosaba
{
    // 메인 스레드에서 매 프레임 할 필요 없는 일 줄이기 (17차 ETW, 1080p 재판 대화 300 fps에서 프레임당 메인 스레드 3.2 ms 중)
    static class CpuTuning
    {
        internal static bool SteamThrottle, RaycastReuse;
        // 실험실 진단: UI 그림자(TrueShadow)의 변화 추적·다시 그리기를 모두 건너뜀 (그림자가 멈춤). 그 비용의 상한을 잼
        internal static bool ShadowFreeze;
        static readonly long SteamInterval = Stopwatch.Frequency / 60, RaycastInterval = Stopwatch.Frequency / 60;

        // Steam 콜백 처리(SteamAPI_RunCallbacks, 0.09 ms: 매번 Steam 클라이언트 파이프를 확인)를 초당 60번까지만.
        // 업적·오버레이 알림 같은 콜백이 최대 16 ms 늦을 뿐 놓치지는 않음 (Steam은 쌓아 두었다 다음 호출에 줌)
        [HarmonyPatch(typeof(Steamworks.CallbackDispatcher), "RunFrame")]
        static class SteamPatch
        {
            static long _next;

            static bool Prefix()
            {
                if (!SteamThrottle) return true;
                long now = Stopwatch.GetTimestamp();
                if (now < _next) return false;
                _next = now + SteamInterval;
                return true;
            }
        }

        // UI 마우스 검사(InputSystemUIInputModule.PerformRaycast → 모든 레이캐스터) 결과는 마우스가 그대로면 거의 그대로라,
        // 같은 포인터·같은 위치·버튼과 휠 변화 없음이면 16 ms 안의 지난 결과(모듈이 eventData.pointerCurrentRaycast에 보관한 것)를 돌려줌.
        // 결과 오브젝트가 파괴됐거나 클릭·휠·이동이 있으면 항상 새로 검사. 멈춘 마우스 아래로 나타난 UI의 강조가 최대 16 ms 늦음.
        // 결과를 따로 복사해 보관하던 첫 구현은 시작하자마자 GameObject.get_transform 접근 위반으로 꺼졌음 (17차)
        [HarmonyPatch(typeof(InputSystemUIInputModule), "PerformRaycast")]
        static class RaycastPatch
        {
            static IntPtr _data;
            static float _x, _y;
            static long _at;
            static bool _reused;

            static bool Prefix(ExtendedPointerEventData eventData, ref RaycastResult __result)
            {
                _reused = false;
                if (!RaycastReuse || eventData == null) return true;
                var pos = eventData.position;
                if (eventData.Pointer != _data || pos.x != _x || pos.y != _y || Stopwatch.GetTimestamp() - _at > RaycastInterval || InputChanged())
                    return true;
                var last = eventData.pointerCurrentRaycast;
                var go = last.gameObject;
                // 맞힌 것이 파괴됨 (래퍼는 있는데 Unity 객체가 없음)
                if (!ReferenceEquals(go, null) && go == null) return true;
                __result = last;
                _reused = true;
                return false;
            }

            static void Postfix(ExtendedPointerEventData eventData)
            {
                if (_reused || !RaycastReuse || eventData == null) return;
                var pos = eventData.position;
                _data = eventData.Pointer;
                _x = pos.x;
                _y = pos.y;
                _at = Stopwatch.GetTimestamp();
            }

            static bool InputChanged()
            {
                var m = Mouse.current;
                if (m == null) return true;
                var scroll = m.scroll.ReadValue();
                return scroll.x != 0 || scroll.y != 0 || m.leftButton.wasPressedThisFrame || m.leftButton.wasReleasedThisFrame ||
                       m.rightButton.wasPressedThisFrame || m.rightButton.wasReleasedThisFrame ||
                       m.middleButton.wasPressedThisFrame || m.middleButton.wasReleasedThisFrame;
            }
        }

        [HarmonyPatch(typeof(LeTai.TrueShadow.TrueShadow), "LateUpdate")]
        static class ShadowLatePatch
        {
            static bool Prefix() => !ShadowFreeze;
        }

        [HarmonyPatch(typeof(LeTai.TrueShadow.TrueShadow), "OnWillRenderCanvas")]
        static class ShadowCanvasPatch
        {
            static bool Prefix() => !ShadowFreeze;
        }
    }
}
