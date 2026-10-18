using TMPro;

namespace LXIUM_mahosaba
{
    // TextMeshPro 설정. 이 게임의 TMP 폰트는 동적이라 대사에 처음 나오는 글자를 그때 폰트에 추가하는데, 글자마다
    // TMP_FontAsset.UpdateGSUB/GPOSFontFeaturesForNewGlyphIndex가 폰트의 OpenType 표 전체를 다시 읽고 버림
    // (12차 추적: 한 프레임 430 ms 중 79%). TMP_Settings.getFontFeaturesAtRuntime이 꺼져 있으면 TMP가 이 단계를 건너뜀
    static class TextTuning
    {
        // TMP 설정이 로드된 뒤 한 번 적용. 성공하면 true
        public static bool Apply()
        {
            var settings = TMP_Settings.instance;
            if (settings == null) return false;
            bool features = !Settings.FastGlyphs.Value;
            if (settings.m_GetFontFeaturesAtRuntime != features) settings.m_GetFontFeaturesAtRuntime = features;
            Plugin.L.LogInfo($"tmp font features at runtime={features}");
            return true;
        }
    }
}
