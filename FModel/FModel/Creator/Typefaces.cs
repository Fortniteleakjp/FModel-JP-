using System;
using System.IO;
using System.Windows;
using CUE4Parse.UE4.Versions;
using FModel.Settings;
using SkiaSharp;
using CUE4ParseViewModel = FModel.ViewModels.CUE4Parse.CUE4ParseViewModel;

namespace FModel.Creator;

public class Typefaces
{
    private readonly Uri _BURBANK_BIG_CONDENSED_BOLD = new("pack://application:,,,/Resources/BurbankBigCondensed-Bold.ttf");
    private const string _EXT = ".ufont";

    // FortniteGame
    private const string _FORTNITE_BASE_PATH = "FortniteGame/Plugins/FortUILibrary/Content/Fonts/";
    private const string _ASIA_ERINM = "AsiaERINM"; // korean fortnite
    private const string _BURBANK_BIG_CONDENSED_BLACK = "BurbankBigCondensed-Black"; // russian
    private const string _BURBANK_BIG_REGULAR_BLACK = "BurbankBigRegular-Black";
    private const string _BURBANK_BIG_REGULAR_BOLD = "BurbankBigRegular-Bold"; // official fortnite ig
    private const string _BURBANK_SMALL_MEDIUM = "BurbankSmall-Medium";
    private const string _DROID_SANS_FORTNITE_SUBSET = "DroidSans-Fortnite-Subset";
    private const string _NOTO_COLOR_EMOJI = "NotoColorEmoji";
    private const string _NOTO_SANS_BOLD = "NotoSans-Bold";
    private const string _NOTO_SANS_FORTNITE_BOLD = "NotoSans-Fortnite-Bold";
    private const string _NOTO_SANS_FORTNITE_ITALIC = "NotoSans-Fortnite-Italic";
    private const string _NOTO_SANS_FORTNITE_REGULAR = "NotoSans-Fortnite-Regular";
    private const string _NOTO_SANS_ITALIC = "NotoSans-Italic";
    private const string _NOTO_SANS_REGULAR = "NotoSans-Regular";
    private const string _NOTO_SANS_ARABIC_BLACK = "NotoSansArabic-Black"; // arabic fortnite
    private const string _NOTO_SANS_ARABIC_BOLD = "NotoSansArabic-Bold";
    private const string _NOTO_SANS_ARABIC_REGULAR = "NotoSansArabic-Regular";
    private const string _NOTO_SANS_JP_BOLD = "NotoSansJP-Bold"; // japanese fortnite
    private const string _NOTO_SANS_KR_REGULAR = "NotoSansKR-Regular";
    private const string _NOTO_SANS_SC_BLACK = "NotoSansSC-Black"; // simplified chinese fortnite
    private const string _NOTO_SANS_SC_REGULAR = "NotoSansSC-Regular";
    private const string _NOTO_SANS_TC_BLACK = "NotoSansTC-Black"; // traditional chinese fortnite
    private const string _NOTO_SANS_TC_REGULAR = "NotoSansTC-Regular";
    private const string _BURBANK_SMALL_BLACK = "burbanksmall-black";
    private const string _BURBANK_SMALL_BOLD = "burbanksmall-bold";

    // PandaGame
    private const string _PANDAGAME_BASE_PATH = "/Game/Panda_Main/UI/Fonts/";
    private const string _NORMS_STD_CONDENSED_EXTRABOLD_ITALIC = "Norms/TT_Norms_Std_Condensed_ExtraBold_Italic";
    private const string _NORMS_PRO_EXTRABOLD_ITALIC = "Norms/TT_Norms_Pro_ExtraBold_Italic";
    private const string _NORMS_STD_CONDENSED_MEDIUM = "Norms/TT_Norms_Std_Condensed_Medium";
    private const string _XIANGHEHEI_SC_PRO_BLACK = "XiangHeHei_SC/MXiangHeHeiSCPro-Black";
    private const string _XIANGHEHEI_SC_PRO_HEAVY = "XiangHeHei_SC/MXiangHeHeiSCPro-Heavy";

    private readonly CUE4ParseViewModel _viewModel;

    public readonly SKTypeface Default; // used as a fallback font for all untranslated strings (item source, ...)
    public readonly SKTypeface DisplayName;
    public readonly SKTypeface Description;
    public readonly SKTypeface Bottom; // must be null for non-latin base languages
    public readonly SKTypeface Bundle;
    public readonly SKTypeface BundleNumber;
    public readonly SKTypeface TandemDisplayName;
    public readonly SKTypeface TandemGenDescription;
    public readonly SKTypeface TandemAddDescription;

    public Typefaces(CUE4ParseViewModel viewModel)
    {
        _viewModel = viewModel;
        var language = UserSettings.Default.AssetLanguage;

        Default = SKTypeface.FromStream(Application.GetResourceStream(_BURBANK_BIG_CONDENSED_BOLD)?.Stream);

        switch (viewModel.Provider.ProjectName.ToUpperInvariant())
        {
            case "FORTNITEGAME":
            {
                DisplayName = OnTheFly(_FORTNITE_BASE_PATH +
                                       language switch
                                       {
                                           ELanguage.Korean => _ASIA_ERINM,
                                           ELanguage.Russian => _BURBANK_BIG_CONDENSED_BLACK,
                                           ELanguage.Japanese => _NOTO_SANS_JP_BOLD,
                                           ELanguage.Arabic => _NOTO_SANS_ARABIC_BLACK,
                                           ELanguage.TraditionalChinese => _NOTO_SANS_TC_BLACK,
                                           ELanguage.Chinese => _NOTO_SANS_SC_BLACK,
                                           _ => string.Empty
                                       } + _EXT);

                Description = OnTheFly(_FORTNITE_BASE_PATH +
                                       language switch
                                       {
                                           ELanguage.Korean => _NOTO_SANS_KR_REGULAR,
                                           ELanguage.Japanese => _NOTO_SANS_JP_BOLD,
                                           ELanguage.Arabic => _NOTO_SANS_ARABIC_REGULAR,
                                           ELanguage.TraditionalChinese => _NOTO_SANS_TC_REGULAR,
                                           ELanguage.Chinese => _NOTO_SANS_SC_REGULAR,
                                           _ => _NOTO_SANS_REGULAR
                                       } + _EXT);

                Bottom = OnTheFly(_FORTNITE_BASE_PATH +
                                  language switch
                                  {
                                      ELanguage.Korean => string.Empty,
                                      ELanguage.Japanese => string.Empty,
                                      ELanguage.Arabic => string.Empty,
                                      ELanguage.TraditionalChinese => string.Empty,
                                      ELanguage.Chinese => string.Empty,
                                      _ => _BURBANK_SMALL_BOLD
                                  } + _EXT, true);

                BundleNumber = OnTheFly(_FORTNITE_BASE_PATH + _BURBANK_SMALL_BOLD + _EXT);

                Bundle = OnTheFly(_FORTNITE_BASE_PATH +
                                  language switch
                                  {
                                      ELanguage.Korean => _ASIA_ERINM,
                                      ELanguage.Russian => _BURBANK_BIG_CONDENSED_BLACK,
                                      ELanguage.Japanese => _NOTO_SANS_JP_BOLD,
                                      ELanguage.Arabic => _NOTO_SANS_ARABIC_BLACK,
                                      ELanguage.TraditionalChinese => _NOTO_SANS_TC_BLACK,
                                      ELanguage.Chinese => _NOTO_SANS_SC_BLACK,
                                      _ => _BURBANK_SMALL_BOLD
                                  } + _EXT, true) ?? BundleNumber;

                TandemDisplayName = OnTheFly(_FORTNITE_BASE_PATH +
                                             language switch
                                             {
                                                 ELanguage.Korean => _ASIA_ERINM,
                                                 ELanguage.Russian => _BURBANK_BIG_CONDENSED_BLACK,
                                                 ELanguage.Japanese => _NOTO_SANS_JP_BOLD,
                                                 ELanguage.Arabic => _NOTO_SANS_ARABIC_BLACK,
                                                 ELanguage.TraditionalChinese => _NOTO_SANS_TC_BLACK,
                                                 ELanguage.Chinese => _NOTO_SANS_SC_BLACK,
                                                 _ => _BURBANK_BIG_REGULAR_BLACK
                                             } + _EXT);

                TandemGenDescription = OnTheFly(_FORTNITE_BASE_PATH +
                                                language switch
                                                {
                                                    ELanguage.Korean => _ASIA_ERINM,
                                                    ELanguage.Japanese => _NOTO_SANS_JP_BOLD,
                                                    ELanguage.Arabic => _NOTO_SANS_ARABIC_BLACK,
                                                    ELanguage.TraditionalChinese => _NOTO_SANS_TC_BLACK,
                                                    ELanguage.Chinese => _NOTO_SANS_SC_BLACK,
                                                    _ => _BURBANK_SMALL_BLACK
                                                } + _EXT);

                TandemAddDescription = OnTheFly(_FORTNITE_BASE_PATH +
                                                language switch
                                                {
                                                    ELanguage.Korean => _ASIA_ERINM,
                                                    ELanguage.Japanese => _NOTO_SANS_JP_BOLD,
                                                    ELanguage.Arabic => _NOTO_SANS_ARABIC_BLACK,
                                                    ELanguage.TraditionalChinese => _NOTO_SANS_TC_BLACK,
                                                    ELanguage.Chinese => _NOTO_SANS_SC_BLACK,
                                                    _ => _BURBANK_SMALL_BOLD
                                                } + _EXT);
                break;
            }
            case "MULTIVERSUS":
            {
                DisplayName = OnTheFly(_PANDAGAME_BASE_PATH + language switch
                {
                    ELanguage.Chinese => _XIANGHEHEI_SC_PRO_HEAVY,
                    _ => _NORMS_PRO_EXTRABOLD_ITALIC
                } + _EXT);

                Description = OnTheFly(_PANDAGAME_BASE_PATH + language switch
                {
                    ELanguage.Chinese => _XIANGHEHEI_SC_PRO_BLACK,
                    _ => _NORMS_STD_CONDENSED_MEDIUM
                } + _EXT);

                TandemDisplayName = OnTheFly(_PANDAGAME_BASE_PATH + language switch
                {
                    ELanguage.Chinese => _XIANGHEHEI_SC_PRO_BLACK,
                    _ => _NORMS_STD_CONDENSED_EXTRABOLD_ITALIC
                } + _EXT);

                TandemGenDescription = OnTheFly(_PANDAGAME_BASE_PATH + language switch
                {
                    ELanguage.Chinese => _XIANGHEHEI_SC_PRO_HEAVY,
                    _ => _NORMS_STD_CONDENSED_MEDIUM
                } + _EXT);
                break;
            }
            default:
            {
                DisplayName = Default;
                Description = Default;
                break;
            }
        }
    }

    public SKTypeface OnTheFly(string path, bool fallback = false)
    {
        if (!_viewModel.Provider.TrySaveAsset(path, out var data)) return fallback ? null : Default;
        var m = new MemoryStream(data) { Position = _viewModel.Provider.Versions.Game >= EGame.GAME_UE5_6 ? 4 : 0 };
        return SKTypeface.FromStream(m);
    }
}
