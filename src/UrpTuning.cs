using System;
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
        static IntermediateTextureMode _origIntermediate;

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
                    if (features[i].TryCast<ScreenSpaceAmbientOcclusion>() != null) _ssao = features[i];
            }
            RenderPipelineManager.add_beginCameraRendering((Action<ScriptableRenderContext, Camera>)OnBeginCamera);
            Set(Settings.MsaaOff.Value, Settings.Ssao.Value, Settings.IntermediateAuto.Value);
            Plugin.L.LogInfo($"urp asset={asset.name} msaa={asset.msaaSampleCount} intermediate={_origIntermediate} ssao={_ssao != null}");
            return true;
        }

        public static void Set(bool msaaOff, SsaoMode ssao, bool intermediateAuto)
        {
            MsaaOff = msaaOff;
            Ssao = ssao;
            IntermediateAuto = intermediateAuto;
            if (_data == null) return;
            var mode = intermediateAuto ? IntermediateTextureMode.Auto : _origIntermediate;
            if (_data.m_IntermediateTextureMode == mode) return;
            _data.m_IntermediateTextureMode = mode;
            _data.SetDirty();
        }

        // URP는 카메라마다 렌더 직전에 beginCameraRendering을 부른 뒤 카메라 데이터(MSAA 등)를 만들고
        // 기능 목록의 isActive를 본다. 그래서 여기서 카메라별로 MSAA·SSAO를 정한다.
        // - MSAA: 직교 카메라(메인·캐릭터 합성)만 끔. 무대 카메라는 RT의 MSAA 2x를 그대로 씀
        // - SSAO: 렌더러 단위 기능이라 카메라별 설정이 없어 켜고 끄기를 카메라마다 바꿈
        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            RenderLab.CountCamera();
            if (cam.orthographic && cam.allowMSAA == MsaaOff) cam.allowMSAA = !MsaaOff;
            if (_ssao == null) return;
            bool active = Ssao == SsaoMode.Game || (Ssao == SsaoMode.StageOnly && StageRender.IsStageCamera(cam));
            if (_ssao.isActive != active) _ssao.SetActive(active);
        }
    }
}
