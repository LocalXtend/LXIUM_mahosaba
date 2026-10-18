using System.Diagnostics;
using HarmonyLib;

namespace LXIUM_mahosaba
{
    // 메인 스레드에서 매 프레임 할 필요 없는 일 줄이기 (17차 ETW, 1080p 재판 대화 300 fps에서 프레임당 메인 스레드 3.2 ms 중)
    static class CpuTuning
    {
        internal static bool SteamThrottle;
        static readonly long SteamInterval = Stopwatch.Frequency / 60;

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
    }
}
