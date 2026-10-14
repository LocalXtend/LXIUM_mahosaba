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

        public override void Load()
        {
            L = Log;
            Settings.Bind(Config);
            RenderLab.ApplySettings();
            new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll(typeof(Plugin).Assembly);
            AddComponent<Host>();
            L.LogInfo($"loaded {MyPluginInfo.PLUGIN_VERSION}");
        }
    }

    // 매 프레임 동작을 맡는 상주 컴포넌트 (BasePlugin.AddComponent가 IL2CPP에 등록)
    public sealed class Host : MonoBehaviour
    {
        public Host(IntPtr ptr) : base(ptr) { }

        bool _urpApplied;

        void Update()
        {
            long t = Stopwatch.GetTimestamp();
            FrameRate.Enforce();
            if (!_urpApplied) _urpApplied = UrpTuning.Apply();
            CourtShaders.Tick();
            RenderLab.Tick(this);
            Hud.Tick();
            RenderLab.ModTicks += Stopwatch.GetTimestamp() - t;
        }

        void LateUpdate()
        {
            long t = Stopwatch.GetTimestamp();
            StageRender.LateTick();
            RenderLab.ModTicks += Stopwatch.GetTimestamp() - t;
        }

        void OnGUI()
        {
            long t = Stopwatch.GetTimestamp();
            if (RenderLab.Status != null && !RenderLab.Capturing) GUI.Box(new Rect(10, 10, 760, 26), RenderLab.Status);
            Hud.Draw();
            RenderLab.ModTicks += Stopwatch.GetTimestamp() - t;
        }
    }
}
