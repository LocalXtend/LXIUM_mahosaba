using BepInEx.Configuration;

namespace LXIUM_manosaba
{
    // 설정 항목. 기본값은 측정에서 화질이 바뀌지 않거나 거의 같은 것만 켬 (docs/ai/manosaba-render.md 2·4장)
    // 설정 파일에는 설명을 쓰지 않음: 형식·범위·언어별 설명은 저장소 루트의 LXIUM_manosaba.lxconfig (LXIUM docs/ai/lxconfig.md).
    // 항목을 더하거나 바꾸면 .lxconfig도 같은 커밋에서 고침
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
        public static ConfigEntry<bool> MainPostOff;
        public static ConfigEntry<bool> StageHalfRate;
        public static ConfigEntry<bool> FireLite;
        public static ConfigEntry<bool> CourtLite;
        public static ConfigEntry<bool> StageChangeOnly;
        public static ConfigEntry<bool> StageMaskSkip;
        public static ConfigEntry<bool> SteamThrottle;
        public static ConfigEntry<bool> RaycastReuse;
        public static ConfigEntry<bool> FastGlyphs;
        public static ConfigEntry<bool> RevealReuse;
        public static ConfigEntry<bool> GpuTimer;
        public static ConfigEntry<bool> Lab;
        public static ConfigEntry<bool> LayerDiag;

        public static void Bind(ConfigFile c)
        {
            TargetFps = c.Bind("FrameRate", "TargetFps", 0);
            MsaaOff = c.Bind("Urp", "MsaaOff", true);
            Ssao = c.Bind("Urp", "Ssao", SsaoMode.Off);
            IntermediateAuto = c.Bind("Urp", "IntermediateAuto", true);
            MainPostOff = c.Bind("Urp", "MainPostOff", true);
            CharacterChangeOnly = c.Bind("Character", "ChangeOnly", true);
            FastDetect = c.Bind("Character", "FastDetect", true);
            CharacterResolution = c.Bind("Character", "Resolution", 0f);
            StageResolution = c.Bind("Stage", "Resolution", 0f);
            StageHiddenSkip = c.Bind("Stage", "HiddenSkip", true);
            StageMsaaOff = c.Bind("Stage", "MsaaOff", true);
            StagePostOff = c.Bind("Stage", "PostOff", true);
            StageHalfRate = c.Bind("Stage", "HalfRate", false);
            FireLite = c.Bind("Stage", "FireLite", true);
            CourtLite = c.Bind("Stage", "CourtLite", true);
            StageChangeOnly = c.Bind("Stage", "CourtChangeOnly", true);
            StageMaskSkip = c.Bind("Stage", "ScreenMaskSkip", true);
            SteamThrottle = c.Bind("Cpu", "SteamThrottle", true);
            RaycastReuse = c.Bind("Cpu", "RaycastReuse", true);
            FastGlyphs = c.Bind("Text", "FastGlyphs", true);
            RevealReuse = c.Bind("Text", "RevealReuse", false);
            GpuTimer = c.Bind("Debug", "GpuTimer", false);
            LayerDiag = c.Bind("Debug", "LayerDiag", false);
            Lab = c.Bind("Debug", "RenderLab", false);
        }
    }

    enum SsaoMode { StageOnly, Off, Game, StageLite }
}
