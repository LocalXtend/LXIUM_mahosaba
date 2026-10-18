using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace LXIUM_mahosaba
{
    // 메인 스레드에서 매 프레임 할 필요 없는 일 줄이기 (17차 ETW, 1080p 재판 대화 300 fps에서 프레임당 메인 스레드 3.2 ms 중)
    static class CpuTuning
    {
        internal static bool SteamThrottle, RaycastReuse;

        internal static void Install() => Raycast.Install();
        // 실험실 진단: UI 그림자(TrueShadow)의 변화 추적·다시 그리기를 모두 건너뜀 (그림자가 멈춤). 그 비용의 상한을 잼
        internal static bool ShadowFreeze;
        static Harmony _diag;
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
        // Harmony 패치는 RaycastResult(참조가 든 구조체)를 interop 래퍼로 주고받아 매번 관리 힙에 할당했음(22차, GC 할당의 70%) →
        // 네이티브 함수를 직접 가로채고 결과는 메모리 복사로 돌려줌. 결과를 따로 복사해 보관하던 첫 구현은 접근 위반으로 꺼졌음 (17차)
        static class Raycast
        {
            // RaycastResult PerformRaycast(this, eventData): 구조체 반환이라 rcx가 반환 버퍼, rdx가 this, r8이 eventData (ISIL 확인)
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            delegate IntPtr PerformRaycastFn(IntPtr ret, IntPtr self, IntPtr eventData, IntPtr method);

            static PerformRaycastFn _original, _detour;
            static INativeDetour _hook;
            static int _resultOffset, _resultSize, _positionOffset;
            static IntPtr _data;
            static float _x, _y;
            static long _at;

            internal static void Install()
            {
                try
                {
                    // 필드 위치·크기를 IL2CPP 메타데이터에서 읽음. ProcessPointer가 결과를 eventData+0x50에 80바이트로 복사하는 것과 같아야 함
                    var pointerClass = Il2CppClassPointerStore<PointerEventData>.NativeClassPtr;
                    _resultOffset = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(pointerClass, "<pointerCurrentRaycast>k__BackingField"));
                    _positionOffset = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(pointerClass, "<position>k__BackingField"));
                    uint align = 0;
                    _resultSize = IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore<RaycastResult>.NativeClassPtr, ref align);
                    if (_resultOffset != 0x50 || _resultSize != 80 || _positionOffset <= 0)
                    {
                        Plugin.L.LogWarning($"raycast reuse: 필드 배치가 달라 걸지 않음 (result={_resultOffset:X}/{_resultSize} position={_positionOffset:X})");
                        return;
                    }
                    var info = (IntPtr)typeof(InputSystemUIInputModule).GetField("NativeMethodInfoPtr_PerformRaycast_Private_RaycastResult_ExtendedPointerEventData_0",
                        BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                    IntPtr target = Marshal.ReadIntPtr(info);
                    _detour = Detour;
                    _hook = INativeDetour.CreateAndApply(target, _detour, out _original);
                    Plugin.L.LogInfo("raycast reuse: PerformRaycast 네이티브 훅 켬");
                }
                catch (Exception e) { Plugin.L.LogWarning("raycast reuse: 걸지 않음 " + e.Message); }
            }

            static unsafe IntPtr Detour(IntPtr ret, IntPtr self, IntPtr eventData, IntPtr method)
            {
                if (!RaycastReuse || eventData == IntPtr.Zero) return _original(ret, self, eventData, method);
                long t = Stopwatch.GetTimestamp(), a = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    var pos = (float*)(eventData + _positionOffset);
                    if (eventData == _data && pos[0] == _x && pos[1] == _y && t - _at <= RaycastInterval && !InputChanged())
                    {
                        byte* last = (byte*)(eventData + _resultOffset);
                        // 맞힌 GameObject(구조체 첫 필드)의 Unity 객체가 파괴됐으면(m_CachedPtr = 0) 새로 검사
                        IntPtr go = *(IntPtr*)last;
                        if (go == IntPtr.Zero || *(IntPtr*)(go + 0x10) != IntPtr.Zero)
                        {
                            Buffer.MemoryCopy(last, (void*)ret, _resultSize, _resultSize);
                            return ret;
                        }
                    }
                    IntPtr r = _original(ret, self, eventData, method);
                    _data = eventData;
                    _x = pos[0];
                    _y = pos[1];
                    _at = Stopwatch.GetTimestamp();
                    return r;
                }
                finally { ModProf.Add(ModProf.Slot.Input, t, a); }
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

        // 실험실 진단 패치는 측정하는 동안만 붙임. 상시 설치하면 그림자 61개 × 2 = 프레임당 122번 네이티브↔관리 경계를 지나는
        // 비용이 늘 붙음 (0.8.0 개발 중 한동안 그랬음). on이면 빈 패치(경계 비용만), freeze면 그림자 갱신을 건너뜀
        internal static void ShadowHooks(bool on)
        {
            if (on == (_diag != null)) return;
            if (!on)
            {
                _diag.UnpatchSelf();
                _diag = null;
                return;
            }
            _diag = new Harmony(MyPluginInfo.PLUGIN_GUID + ".diag");
            var prefix = new HarmonyMethod(typeof(CpuTuning), nameof(ShadowPrefix));
            _diag.Patch(AccessTools.Method(typeof(LeTai.TrueShadow.TrueShadow), "LateUpdate"), prefix);
            _diag.Patch(AccessTools.Method(typeof(LeTai.TrueShadow.TrueShadow), "OnWillRenderCanvas"), prefix);
        }

        static bool ShadowPrefix() => !ShadowFreeze;
    }
}
