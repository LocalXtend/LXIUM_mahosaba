using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Naninovel.UI;
using UnityEngine;

namespace LXIUM_manosaba
{
    // RevealFader는 완성된 대사도 매 프레임 글자별 알파를 계산하고 색상 버퍼를 다시 올림.
    static class RevealWork
    {
        static bool _enabled;
        internal static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                Entries.Clear();
            }
        }

        readonly record struct Stamp(IntPtr Text, IntPtr Info, IntPtr Characters, IntPtr Meshes,
            IntPtr Reveal, int Count, int Last, float Length, long Revision);

        sealed class Entry
        {
            internal Stamp Stamp;
            internal bool Settled;
        }

        struct Call
        {
            internal long Start;
            internal Entry Candidate;
            internal Stamp Stamp;
        }

        static readonly Dictionary<IntPtr, Entry> Entries = new();
        static long _revision, _ticks;
        static int _calls, _skipped, _frame;
        static float _next;
        static readonly int[] Blocks = new int[8];
        static bool _probe = true;
        static string _sample = "none";

        static bool Original(RevealFader fader, int reason)
        {
            if (Settings.LayerDiag.Value) Blocks[reason]++;
            Entries.Remove(fader.Pointer);
            return true;
        }

        internal static void Tick()
        {
            if (!Settings.LayerDiag.Value || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 5f;
            int frames = Mathf.Max(1, Time.frameCount - _frame);
            Plugin.L.LogInfo($"text reveal {frames} frames: on={Enabled} calls={_calls} skipped={_skipped} " +
                $"cpu={_ticks * 1000.0 / Stopwatch.Frequency / frames:F3}ms/frame");
            Plugin.L.LogInfo($"text reveal blocks: custom={Blocks[0]} slack={Blocks[1]} length={Blocks[2]} " +
                $"dirty={Blocks[3]} missing={Blocks[4]} partial={Blocks[5]} tail={Blocks[6]} earlier={Blocks[7]}; sample: {_sample}");
            _ticks = 0;
            _calls = _skipped = 0;
            _frame = Time.frameCount;
            Array.Clear(Blocks, 0, Blocks.Length);
            _probe = true;
        }

        [HarmonyPatch(typeof(RevealFader), nameof(RevealFader.Update))]
        static class UpdatePatch
        {
            static bool Prefix(RevealFader __instance, out Call __state)
            {
                long start = Stopwatch.GetTimestamp(), a = System.GC.GetAllocatedBytesForCurrentThread();
                try { return Run(__instance, out __state); }
                finally { ModProf.Add(ModProf.Slot.Text, start, a); }
            }

            static bool Run(RevealFader __instance, out Call __state)
            {
                __state = default;
                if (Settings.LayerDiag.Value)
                {
                    __state.Start = Stopwatch.GetTimestamp();
                    _calls++;
                }
                if (!Enabled) return true;
                try
                {
                    if (__state.Start != 0 && _probe)
                    {
                        _probe = false;
                        var sampleText = __instance.Text;
                        var sampleInfo = sampleText?.textInfo;
                        _sample = $"custom={__instance.supportCustomOpacity} customCount={__instance.customOpacity?.Count ?? -1} " +
                            $"slack={__instance.slackOpacity} length={__instance.length} dirty={sampleText?.havePropertiesChanged} " +
                            $"chars={sampleInfo?.characterCount ?? -1} last={sampleText?.RevealState?.LastRevealedCharIndex ?? -1}";
                    }
                    // shortcut: 문자를 흐리게 하는 slack·사용자 알파 경로는 원본 유지, 별도 검증 후 확장.
                    if (__instance.supportCustomOpacity) return Original(__instance, 0);
                    if (!(__instance.slackOpacity >= 1f)) return Original(__instance, 1);
                    if (!(__instance.length > 0f) || !float.IsFinite(__instance.length)) return Original(__instance, 2);
                    var text = __instance.Text;
                    if (text == null) return Original(__instance, 4);
                    if (text.havePropertiesChanged) return Original(__instance, 3);
                    var info = text.textInfo;
                    var reveal = text.RevealState;
                    if (info == null || reveal == null || info.characterCount == 0) return Original(__instance, 4);
                    if (reveal.LastRevealedCharIndex < info.characterCount - 1) return Original(__instance, 5);
                    var stamp = new Stamp(text.Pointer, info.Pointer, info.characterInfo?.Pointer ?? IntPtr.Zero,
                        info.meshInfo?.Pointer ?? IntPtr.Zero, reveal.Pointer, info.characterCount,
                        reveal.LastRevealedCharIndex, __instance.length, _revision);
                    if (!Entries.TryGetValue(__instance.Pointer, out var entry))
                        Entries.Add(__instance.Pointer, entry = new Entry());
                    if (entry.Settled && entry.Stamp == stamp)
                    {
                        if (__state.Start != 0) _skipped++;
                        return false;
                    }
                    entry.Settled = false;
                    if (!(reveal.GetRevealRatio(info.characterCount - 1, __instance.length) >= 1f))
                    {
                        if (__state.Start != 0) Blocks[6]++;
                        return true;
                    }
                    // 마지막 글자만 검사하면 루비·즉시 출력·되감기에서 앞 글자의 남은 페이드를 놓칠 수 있음.
                    for (int i = 0; i < info.characterCount; i++)
                        if (!(reveal.GetRevealRatio(i, __instance.length) >= 1f))
                        {
                            if (__state.Start != 0) Blocks[7]++;
                            return true;
                        }
                    __state.Candidate = entry;
                    __state.Stamp = stamp;
                }
                catch (Exception ex)
                {
                    Enabled = false;
                    Plugin.L.LogWarning($"text reveal reuse disabled: {ex.GetType().Name}: {ex.Message}");
                }
                return true;
            }

            static void Postfix(Call __state)
            {
                long start = Stopwatch.GetTimestamp(), a = System.GC.GetAllocatedBytesForCurrentThread();
                // 완성된 최종 알파를 원본이 한 번 적용한 다음 프레임부터 재사용.
                if (__state.Candidate != null && __state.Stamp.Revision == _revision)
                {
                    __state.Candidate.Stamp = __state.Stamp;
                    __state.Candidate.Settled = true;
                }
                if (__state.Start != 0) _ticks += Stopwatch.GetTimestamp() - __state.Start;
                ModProf.Add(ModProf.Slot.Text, start, a);
            }
        }

        // 원본도 이 콜백에서 TMP 재생성 뒤 즉시 Update를 부르므로 같은 문장의 재배치도 무효화함.
        [HarmonyPatch(typeof(RevealFader), nameof(RevealFader.HandleTextChanged))]
        static class TextChangedPatch
        {
            static void Prefix() => _revision++;
        }

        [HarmonyPatch]
        static class RevealChangedPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(RevealState), nameof(RevealState.SetLast));
                yield return AccessTools.Method(typeof(RevealState), nameof(RevealState.Reset));
                yield return AccessTools.Method(typeof(RevealState), nameof(RevealState.SetLatAppendIndex));
            }

            static void Prefix() => _revision++;
        }

        [HarmonyPatch]
        static class LifecyclePatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(RevealFader), nameof(RevealFader.OnEnable));
                yield return AccessTools.Method(typeof(RevealFader), nameof(RevealFader.OnDisable));
            }

            static void Prefix(RevealFader __instance) => Entries.Remove(__instance.Pointer);
        }
    }
}
