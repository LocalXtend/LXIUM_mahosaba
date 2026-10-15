using BepInEx.Configuration;

namespace LXIUM_mahosaba
{
    // 설정 항목. 기본값은 측정에서 화질이 바뀌지 않거나 거의 같은 것만 켬 (docs/ai/mahosaba-render.md 2·4장)
    static class Settings
    {
        public static ConfigEntry<int> TargetFps;
        public static ConfigEntry<bool> MsaaOff;
        public static ConfigEntry<SsaoMode> Ssao;
        public static ConfigEntry<bool> IntermediateAuto;
        public static ConfigEntry<bool> CharacterChangeOnly;
        public static ConfigEntry<bool> FastDetect;
        public static ConfigEntry<float> CharacterResolution;
        public static ConfigEntry<float> StageResolution;
        public static ConfigEntry<bool> StageHiddenSkip;
        public static ConfigEntry<bool> StageMsaaOff;
        public static ConfigEntry<bool> StagePostOff;
        public static ConfigEntry<bool> StageHalfRate;
        public static ConfigEntry<bool> FireLite;
        public static ConfigEntry<bool> CourtLite;
        public static ConfigEntry<bool> StageChangeOnly;
        public static ConfigEntry<bool> Lab;

        public static void Bind(ConfigFile c)
        {
            TargetFps = c.Bind("FrameRate", "TargetFps", 0,
                "0 = 모니터 주사율에 맞춤(VSync 켬), -1 = 제한 없음(VSync 끔), 그 밖의 숫자 = 그 fps로 제한(VSync 끔). 게임의 60/30 설정보다 우선");
            MsaaOff = c.Bind("Urp", "MsaaOff", true,
                "2D(직교) 카메라의 MSAA 4x를 끔. 스프라이트·UI는 가장자리가 투명이라 MSAA 효과가 없음. 3D 법정 카메라는 [Stage] MsaaOff가 정함");
            Ssao = c.Bind("Urp", "Ssao", SsaoMode.Off,
                "SSAO(화면 공간 앰비언트 오클루전) 적용 범위. Off = 모두 끔, StageLite = 3D 법정 카메라만 + 절반 해상도·낮은 품질, StageOnly = 법정만 원래 품질, Game = 원래대로 모든 카메라. 법정 조명이 스포트라이트뿐이라 768p·1080p 측정 모두 넷의 법정 화면이 화소까지 같았음");
            IntermediateAuto = c.Bind("Urp", "IntermediateAuto", true,
                "렌더러의 중간 텍스처를 Always에서 Auto로. 필요 없는 카메라(캐릭터 합성 등)의 복사 패스를 없앰");
            CharacterChangeOnly = c.Bind("Character", "ChangeOnly", true,
                "레이어 캐릭터를 바뀐 프레임에만 다시 합성. 게임은 표정이 그대로여도 매 프레임 카메라 렌더를 함");
            FastDetect = c.Bind("Character", "FastDetect", true,
                "바뀔 때만 다시 그리기의 변화 확인을 가볍게: 최근에 바뀐 캐릭터만 매 프레임, 나머지는 4프레임에 한 번씩 돌아가며 확인 (놓친 변화는 최대 3프레임 늦음)");
            CharacterResolution = c.Bind("Character", "Resolution", 0f,
                "레이어 캐릭터 합성 해상도 배율. 0 = 화면 해상도에 맞춤(1080p에서 0.75), 1 = 원래(100 PPU)");
            StageResolution = c.Bind("Stage", "Resolution", 0f,
                "재판 3D 무대 렌더 텍스처 배율 (원래 1920x1080 고정). 0 = 화면 높이에 맞춤(1080 이상이면 원래 크기), 1 = 원래");
            StageHiddenSkip = c.Bind("Stage", "HiddenSkip", true,
                "화면에 보이지 않는 무대 카메라는 그리지 않음");
            StageMsaaOff = c.Bind("Stage", "MsaaOff", true,
                "법정 렌더 텍스처의 MSAA 2x를 끔. 가장자리는 FXAA가 다듬음 (측정 장면에서 화면 차이 없음)");
            StagePostOff = c.Bind("Stage", "PostOff", true,
                "법정 카메라의 후처리(색 보정 표 + FXAA)를 끔. 볼륨 효과가 모두 0이라 색은 같고, 화면 전체 FXAA는 메인 카메라가 한 번 더 함");
            StageHalfRate = c.Bind("Stage", "HalfRate", false,
                "법정 카메라가 멈춰 있으면 두 프레임에 한 번만 그림. 움직일 때는 매 프레임. 불·캐릭터 변화가 반 박자 늦을 수 있음");
            FireLite = c.Bind("Stage", "FireLite", true,
                "법정 불을 LXIUM FireLite 셰이더로 그림 (텍스처 읽기 8 → 4회, 불 비용 약 1/4. 벽·바닥과 닿는 곳의 흐림 없음). Direct3D 11 전용, 다른 API에서는 원본");
            CourtLite = c.Bind("Stage", "CourtLite", true,
                "법정 바닥·계단·벽·단상을 LXIUM CourtLite 셰이더로 그림 (URP Lit에서 그림자·SSAO·반사 프로브 블렌딩을 뺀 판, 텍스처 읽기 9 → 5회, 측정 장면에서 화면 차이 없음). Direct3D 11 전용, 다른 API에서는 원본");
            StageChangeOnly = c.Bind("Stage", "CourtChangeOnly", true,
                "법정 카메라가 보는 것(카메라 위치, 캐릭터 그림·위치·불투명도, 법정 렌더러·조명)이 그대로인 프레임은 다시 그리지 않고 지난 그림을 씀. 불이 켜져 있으면 매 프레임 그림. 놓친 변화는 최대 1초 늦음. 캐릭터 쪽 바뀔 때만 그리기는 [Character] ChangeOnly");
            Lab = c.Bind("Debug", "RenderLab", false,
                "렌더 실험실: F10을 누르면 설정 조합을 8초씩 바꿔 가며 프레임 통계·스크린샷을 BepInEx/LXIUM_mahosaba 폴더에 기록");
        }
    }

    enum SsaoMode { StageOnly, Off, Game, StageLite }
}
