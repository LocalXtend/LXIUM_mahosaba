using System;
using System.Diagnostics;
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
            PrepareMethods();
            AddComponent<Host>();
            Gui = AddComponent<GuiHost>();
            Gui.enabled = false;
            L.LogInfo($"loaded {MyPluginInfo.PLUGIN_VERSION}");
            // 로딩 랙 분석용: 비동기 업로드·백그라운드 로딩 설정
            L.LogInfo($"loading: priority={Application.backgroundLoadingPriority} uploadSlice={QualitySettings.asyncUploadTimeSlice}ms " +
                      $"uploadBuffer={QualitySettings.asyncUploadBufferSize}MB persistent={QualitySettings.asyncUploadPersistentBuffer} " +
                      $"maxQueuedFrames={QualitySettings.maxQueuedFrames} streamingMips={QualitySettings.streamingMipmapsActive}");
        }
    }

    // 매 프레임 동작을 맡는 상주 컴포넌트 (BasePlugin.AddComponent가 IL2CPP에 등록)
    public sealed class Host : MonoBehaviour
    {
        public Host(IntPtr ptr) : base(ptr) { }

        bool _urpApplied, _textApplied;

        void Update()
        {
            long t = Stopwatch.GetTimestamp();
            FrameRate.Enforce();
            if (!_urpApplied && (_urpApplied = UrpTuning.Apply())) GpuTimer.Install();
            CourtShaders.Tick();
            RenderLab.Tick(this);
            Hud.Tick();
            ActorRender.DiagTick();
            ResourceIndex.DiagTick();
            ModProf.Tick();
            if (!_textApplied) _textApplied = TextTuning.Apply();
            // IMGUI(OnGUI)는 켜 두기만 해도 매 프레임 비용이 있어 화면 패널·실험실 안내가 있을 때만 켬
            bool gui = Hud.Visible || RenderLab.Status != null;
            if (Plugin.Gui.enabled != gui) Plugin.Gui.enabled = gui;
            ModProf.Add(ModProf.Slot.Update, t);
        }

        void LateUpdate()
        {
            long t = Stopwatch.GetTimestamp();
            StageRender.LateTick();
            ModProf.Add(ModProf.Slot.Late, t);
        }

    }

    // 화면 패널·실험실 안내 그리기 (IMGUI). 필요할 때만 켜짐
    public sealed class GuiHost : MonoBehaviour
    {
        public GuiHost(IntPtr ptr) : base(ptr) { }

        void OnGUI()
        {
            long t = Stopwatch.GetTimestamp();
            if (RenderLab.Status != null && !RenderLab.Capturing) GUI.Box(new Rect(10, 10, 760, 26), RenderLab.Status);
            Hud.Draw();
            ModProf.Add(ModProf.Slot.Gui, t);
        }
    }
}
