using System;
using System.Diagnostics;
using UnityEngine;

namespace LXIUM_mahosaba
{
    // 모드가 쓰는 메인 스레드 시간을 부분별로 잼. 합계는 RenderLab.ModTicks(실험실·화면 패널의 모드 CPU).
    // 진단 설정(Debug.LayerDiag)이면 5초마다 부분별 프레임당 시간·호출 수를 로그에 남김
    static class ModProf
    {
        // Court는 LateTick 안의 법정 변화 검사라 합계에 두 번 넣지 않음
        internal enum Slot { Update, Late, Layers, Copy, Camera, Gui, Index, Text, Court, Masks, Input }
        static readonly string[] Names = { "Update", "LateTick", "DrawLayers", "Copy", "BeginCamera", "OnGUI", "ResourceIndex", "Reveal", "Court", "Masks", "Raycast" };
        static readonly long[] Ticks = new long[Names.Length];
        // 그 부분이 메인 스레드에서 할당한 관리 메모리 (바이트)
        static readonly long[] Bytes = new long[Names.Length];
        static readonly int[] Calls = new int[Names.Length];
        static float _next;
        static int _frame;
        // 관리(CoreCLR) 힙 할당·GC 횟수. 모드가 할당하면 BepInEx 런타임의 GC가 메인 스레드를 10~25 ms 멈춤 (22차)
        static long _alloc;
        static readonly int[] _gcs = new int[3];

        internal static void Add(Slot slot, long start, long allocStart)
        {
            long d = Stopwatch.GetTimestamp() - start;
            Bytes[(int)slot] += GC.GetAllocatedBytesForCurrentThread() - allocStart;
            Ticks[(int)slot] += d;
            Calls[(int)slot]++;
            if (slot != Slot.Index && slot != Slot.Court) RenderLab.ModTicks += d;
        }

        internal static void Tick()
        {
            if (!Settings.LayerDiag.Value || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 5f;
            int frames = Mathf.Max(1, Time.frameCount - _frame);
            _frame = Time.frameCount;
            var line = new System.Text.StringBuilder($"mod prof {frames} frames:");
            for (int i = 0; i < Names.Length; i++)
            {
                line.Append($" {Names[i]}={Ticks[i] * 1000.0 / Stopwatch.Frequency / frames:F3}ms/{(float)Calls[i] / frames:F1}/{Bytes[i] / frames}B");
                Ticks[i] = 0;
                Bytes[i] = 0;
                Calls[i] = 0;
            }
            Plugin.L.LogInfo(line.ToString());
            long alloc = GC.GetAllocatedBytesForCurrentThread();
            var info = GC.GetGCMemoryInfo();
            Plugin.L.LogInfo($"managed alloc {(alloc - _alloc) / 1024.0 / frames:F2}KB/frame gc0={GC.CollectionCount(0) - _gcs[0]} gc1={GC.CollectionCount(1) - _gcs[1]} gc2={GC.CollectionCount(2) - _gcs[2]} " +
                             $"heap={info.HeapSizeBytes / 1048576.0:F1}MB lastPause={(info.PauseDurations.Length > 0 ? info.PauseDurations[0].TotalMilliseconds : 0):F1}ms gen={info.Generation}");
            Plugin.L.LogInfo($"alloc gap (모드 프레임 일 사이) {Host.GapBytes / frames}B/frame");
            Host.GapBytes = 0;
            _alloc = alloc;
            for (int g = 0; g < 3; g++) _gcs[g] = GC.CollectionCount(g);
        }
    }
}
