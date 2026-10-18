using System.Diagnostics;
using UnityEngine;

namespace LXIUM_mahosaba
{
    // 모드가 쓰는 메인 스레드 시간을 부분별로 잼. 합계는 RenderLab.ModTicks(실험실·화면 패널의 모드 CPU).
    // 진단 설정(Debug.LayerDiag)이면 5초마다 부분별 프레임당 시간·호출 수를 로그에 남김
    static class ModProf
    {
        internal enum Slot { Update, Late, Layers, Copy, Camera, Gui, Index }
        static readonly string[] Names = { "Update", "LateTick", "DrawLayers", "Copy", "BeginCamera", "OnGUI", "ResourceIndex" };
        static readonly long[] Ticks = new long[Names.Length];
        static readonly int[] Calls = new int[Names.Length];
        static float _next;
        static int _frame;

        internal static void Add(Slot slot, long start)
        {
            long d = Stopwatch.GetTimestamp() - start;
            Ticks[(int)slot] += d;
            Calls[(int)slot]++;
            if (slot != Slot.Index) RenderLab.ModTicks += d;
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
                line.Append($" {Names[i]}={Ticks[i] * 1000.0 / Stopwatch.Frequency / frames:F3}ms/{(float)Calls[i] / frames:F1}");
                Ticks[i] = 0;
                Calls[i] = 0;
            }
            Plugin.L.LogInfo(line.ToString());
        }
    }
}
