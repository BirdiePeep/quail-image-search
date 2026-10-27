namespace QuailImageSearch.Services;

/// <summary>
/// User-facing privacy disclosures, kept in one place so the welcome dialog and Preferences say the
/// same thing (and match PRIVACY.md, rule 2).
/// </summary>
public static class PrivacyText
{
    /// <summary>For the welcome dialog.</summary>
    public const string DirectMLNote =
        "On Windows, GPU acceleration uses Microsoft DirectML, which follows Windows' diagnostic data settings; " +
        "you can turn the GPU off in Preferences.";

    /// <summary>For the Use GPU setting in Preferences.</summary>
    public const string DirectMLSettingNote =
        "GPU acceleration uses Microsoft DirectML, which follows Windows' diagnostic data settings. Choose Off to avoid it.";
}
