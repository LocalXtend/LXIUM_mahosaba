using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace LXIUM_mahosaba
{
    // BepInEx 6 IL2CPP 진입점. 설정 파일은 BepInEx/config/LXIUM_mahosaba.cfg (첫 실행 때 생성)
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BasePlugin
    {
        internal static ManualLogSource L;
        internal static GuiHost Gui;

        // 모드 코드를 게임 시작 때 미리 JIT 컴파일. 처음 부를 때 메인 스레드에서 컴파일하면 프레임이 수십 ms 멈춤 (12차 추적: clrjit 73 ms)
        static void PrepareMethods()
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            int n = 0;
            foreach (var type in typeof(Plugin).Assembly.GetTypes())
            {
                if (type.ContainsGenericParameters) continue;
                foreach (var m in type.GetMethods(all))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                    try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle); n++; }
                    catch (Exception) { }
                }
            }
            L.LogInfo($"prepared {n} methods");
        }

        public override void Load()
        {
            L = Log;
            Settings.Bind(Config);
            RenderLab.ApplySettings();
            new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll(typeof(Plugin).Assembly);
            ResourceIndex.Install();
            CpuTuning.Install();
            PrepareMethods();
            Host.Instance = AddComponent<Host>();
            FrameHook.Install();
            Gui = AddComponent<GuiHost>();
            Gui.enabled = false;
            L.LogInfo($"loaded {MyPluginInfo.PLUGIN_VERSION}");
            // 로딩 랙 분석용: 비동기 업로드·백그라운드 로딩 설정
            L.LogInfo($"loading: priority={Application.backgroundLoadingPriority} uploadSlice={QualitySettings.asyncUploadTimeSlice}ms " +
                      $"uploadBuffer={QualitySettings.asyncUploadBufferSize}MB persistent={QualitySettings.asyncUploadPersistentBuffer} " +
                      $"maxQueuedFrames={QualitySettings.maxQueuedFrames} streamingMips={QualitySettings.streamingMipmapsActive}");
        }
    }

    // 코루틴(실험실·GPU 타이머)을 돌리는 상주 컴포넌트. 매 프레임 일은 Update·LateUpdate가 아니라 FrameHook(캔버스 갱신 직전)에서 함:
    // 엔진이 주입된 관리 메서드(Update·LateUpdate·대리자)를 부를 때마다 interop 호출기가 관리 힙에 약 24 B를 할당하고,
    // 쌓인 할당은 BepInEx 런타임의 GC로 메인 스레드를 4~25 ms 멈춤 (22차)
    public sealed class Host : MonoBehaviour
    {
        public Host(IntPtr ptr) : base(ptr) { }

        internal static Host Instance;
        static bool _urpApplied, _textApplied;
        // 진단: 모드의 프레임 일 사이(엔진·게임 코드, interop 경계)에서 생긴 관리 할당
        internal static long GapBytes;
        static long _frameEnd;

        // FrameHook이 프레임마다 한 번 부름 (LateUpdate 뒤, 캔버스 갱신·렌더 전)
        internal static void Frame()
        {
            long t = Stopwatch.GetTimestamp(), a = System.GC.GetAllocatedBytesForCurrentThread();
            if (_frameEnd != 0) GapBytes += a - _frameEnd;
            ActorRender.Now = Time.unscaledTime;
            FrameRate.Enforce();
            if (!_urpApplied && (_urpApplied = UrpTuning.Apply())) GpuTimer.Install(Instance);
            CourtShaders.Tick();
            RenderLab.Tick(Instance);
            Hud.Tick();
            ActorRender.DiagTick();
            ResourceIndex.DiagTick();
            RevealWork.Tick();
            ModProf.Tick();
            if (!_textApplied) _textApplied = TextTuning.Apply();
            // IMGUI(OnGUI)는 켜 두기만 해도 매 프레임 비용이 있어 화면 패널·실험실 안내가 있을 때만 켬
            bool gui = Hud.Visible || RenderLab.Status != null;
            if (Plugin.Gui.enabled != gui) Plugin.Gui.enabled = gui;
            ModProf.Add(ModProf.Slot.Update, t, a);
            t = Stopwatch.GetTimestamp();
            a = System.GC.GetAllocatedBytesForCurrentThread();
            StageRender.LateTick();
            ModProf.Add(ModProf.Slot.Late, t, a);
            _frameEnd = System.GC.GetAllocatedBytesForCurrentThread();
        }
    }

    // 매 프레임 진입점: UGUI CanvasUpdateRegistry.PerformUpdate(엔진이 Canvas.willRenderCanvases로 프레임마다 부름, ICF 검사 통과)를
    // 네이티브로 가로채 원래 일 앞에서 모드 일을 함. 네이티브 → 관리 직접 호출이라 interop 호출기 할당이 없음.
    // Canvas.ForceUpdateCanvases로 한 프레임에 여러 번 불릴 수 있어 프레임 일은 처음 한 번만, 법정 화면 마스크는 매번 정함
    static class FrameHook
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void PerformUpdateFn(IntPtr self, IntPtr method);

        static PerformUpdateFn _original, _detour;
        static INativeDetour _hook;
        static int _frame = -1;
        static bool _failed;

        internal static void Install()
        {
            try
            {
                var field = typeof(UnityEngine.UI.CanvasUpdateRegistry).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                    .First(f => f.Name.StartsWith("NativeMethodInfoPtr_PerformUpdate_"));
                IntPtr target = Marshal.ReadIntPtr((IntPtr)field.GetValue(null));
                _detour = Detour;
                _hook = INativeDetour.CreateAndApply(target, _detour, out _original);
                Plugin.L.LogInfo("frame hook: CanvasUpdateRegistry.PerformUpdate");
            }
            catch (Exception e)
            {
                // 대신 대리자로 (프레임마다 할당이 생기지만 동작은 같음)
                Plugin.L.LogWarning("frame hook: 네이티브 훅 실패, 대리자로 " + e.Message);
                Canvas.add_preWillRenderCanvases((Canvas.WillRenderCanvases)(Action)Run);
            }
        }

        static void Detour(IntPtr self, IntPtr method)
        {
            Run();
            _original(self, method);
        }

        static void Run()
        {
            try
            {
                int frame = Time.frameCount;
                if (frame != _frame)
                {
                    _frame = frame;
                    Host.Frame();
                }
                StageRender.UpdateMasks();
            }
            catch (Exception e)
            {
                // 네이티브 호출 안이라 예외를 밖으로 내보내지 않음
                if (!_failed) Plugin.L.LogError("frame hook: " + e);
                _failed = true;
            }
        }
    }

    // 화면 패널·실험실 안내 그리기 (IMGUI). 필요할 때만 켜짐
    public sealed class GuiHost : MonoBehaviour
    {
        public GuiHost(IntPtr ptr) : base(ptr) { }

        void OnGUI()
        {
            long t = Stopwatch.GetTimestamp(), a = System.GC.GetAllocatedBytesForCurrentThread();
            if (RenderLab.Status != null && !RenderLab.Capturing) GUI.Box(new Rect(10, 10, 760, 26), RenderLab.Status);
            Hud.Draw();
            ModProf.Add(ModProf.Slot.Gui, t, a);
        }
    }
}
