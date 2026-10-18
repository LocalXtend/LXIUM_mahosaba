using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LXIUM_mahosaba
{
    // 게임이 3D 기본 템플릿(URP-HighFidelity)을 그대로 써서 생기는 비용을 줄임 (docs/ai/mahosaba.md 4장).
    // 에셋은 하나뿐이고 품질 단계별 교체가 없으므로 한 번 찾으면 유지됨.
    static class UrpTuning
    {
        static UniversalRendererData _data;
        static ScriptableRendererFeature _ssao;
        static ScreenSpaceAmbientOcclusionSettings _ssaoSettings;
        static IntermediateTextureMode _origIntermediate;
        static bool _origDownsample;
        static ScreenSpaceAmbientOcclusionSettings.AOSampleOption _origSamples;
        static ScreenSpaceAmbientOcclusionSettings.BlurQualityOptions _origBlur;

        // 실험실이 바꾸는 현재 상태
        internal static bool MsaaOff, IntermediateAuto;
        internal static SsaoMode Ssao;

        public static bool Apply()
        {
            var asset = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
            if (asset == null) return false;
            _data = asset.scriptableRendererData?.TryCast<UniversalRendererData>();
            if (_data != null)
            {
                _origIntermediate = _data.m_IntermediateTextureMode;
                var features = _data.rendererFeatures;
                for (int i = 0; i < features.Count; i++)
                {
                    var ao = features[i].TryCast<ScreenSpaceAmbientOcclusion>();
                    if (ao == null) continue;
                    _ssao = features[i];
                    _ssaoSettings = ao.m_Settings;
                    _origDownsample = _ssaoSettings.Downsample;
                    _origSamples = _ssaoSettings.Samples;
                    _origBlur = _ssaoSettings.BlurQuality;
                }
            }
            RenderLab.ApplySettings();
            Plugin.L.LogInfo($"urp asset={asset.name} msaa={asset.msaaSampleCount} intermediate={_origIntermediate} ssao={_ssao != null}");
            return true;
        }

        public static void Set(bool msaaOff, SsaoMode ssao, bool intermediateAuto)
        {
            MsaaOff = msaaOff;
            Ssao = ssao;
            IntermediateAuto = intermediateAuto;
            if (_ssaoSettings != null)
            {
                // 가볍게: 절반 해상도, 샘플·블러 Low. 4차 측정 장면에서는 끈 것과도 화소 차이 0 (법정 조명이 스포트라이트뿐)
                bool lite = ssao == SsaoMode.StageLite;
                _ssaoSettings.Downsample = lite || _origDownsample;
                _ssaoSettings.Samples = lite ? ScreenSpaceAmbientOcclusionSettings.AOSampleOption.Low : _origSamples;
                _ssaoSettings.BlurQuality = lite ? ScreenSpaceAmbientOcclusionSettings.BlurQualityOptions.Low : _origBlur;
            }
            // SSAO를 카메라마다 다르게 켜야 할 때(법정만)만 카메라 콜백을 붙임. 콜백은 카메라마다 interop 래퍼를 만들어
            // 관리 힙에 할당이 쌓이고 GC가 메인 스레드를 멈춤 (22차: 프레임당 약 50 B, GC 할당의 30%)
            bool perCamera = _ssao != null && ssao is SsaoMode.StageOnly or SsaoMode.StageLite;
            HookCameras(perCamera);
            if (_ssao != null && !perCamera && _ssao.isActive != (ssao == SsaoMode.Game)) _ssao.SetActive(ssao == SsaoMode.Game);
            if (_data == null) return;
            var mode = intermediateAuto ? IntermediateTextureMode.Auto : _origIntermediate;
            if (_data.m_IntermediateTextureMode == mode) return;
            _data.m_IntermediateTextureMode = mode;
            _data.SetDirty();
        }

        static Il2CppSystem.Action<ScriptableRenderContext, Camera> _onBegin;

        static void HookCameras(bool on)
        {
            if (on == (_onBegin != null)) return;
            if (on)
            {
                _onBegin = (Action<ScriptableRenderContext, Camera>)OnBeginCamera;
                RenderPipelineManager.add_beginCameraRendering(_onBegin);
            }
            else
            {
                RenderPipelineManager.remove_beginCameraRendering(_onBegin);
                _onBegin = null;
            }
        }

        // 직교 카메라(메인·캐릭터 합성)의 MSAA 끄기. 무대 카메라는 RT의 MSAA를 따름.
        // 메인 카메라는 StageRender가 16프레임마다, 캐릭터 합성 카메라는 ActorRender가 합성할 때 부름
        internal static void ApplyMsaa(Camera cam)
        {
            if (!ReferenceEquals(cam, null) && cam.orthographic && cam.allowMSAA == MsaaOff) cam.allowMSAA = !MsaaOff;
        }

        // URP는 카메라마다 렌더 직전에 beginCameraRendering을 부른 뒤 기능 목록의 isActive를 본다.
        // SSAO는 렌더러 단위 기능이라 카메라별 설정이 없어 켜고 끄기를 카메라마다 바꿈 (법정만 켜는 설정일 때만 붙음)
        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            long t = Stopwatch.GetTimestamp(), a = System.GC.GetAllocatedBytesForCurrentThread();
            if (_ssao != null)
            {
                bool active = Ssao == SsaoMode.Game || (StageRender.IsStageCamera(cam) && Ssao is SsaoMode.StageOnly or SsaoMode.StageLite);
                if (_ssao.isActive != active) _ssao.SetActive(active);
            }
            ModProf.Add(ModProf.Slot.Camera, t, a);
        }
    }
}
