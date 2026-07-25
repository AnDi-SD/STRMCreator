using System.Globalization;
using System.Reflection;
using System.Resources;
using Avalonia.Markup.Xaml;

namespace STRMshelf.App.Localization;

public static class LocalizationManager
{
    private static readonly ResourceManager Resources =
        new("STRMshelf.App.Localization.Strings", Assembly.GetExecutingAssembly());

    public static string Language { get; private set; } = "en";

    public static void SetLanguage(string? language)
    {
        Language = string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
        var culture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
    }

    public static string Get(string key) =>
        Resources.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";

    public static string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), values);
}

public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        LocalizationManager.Get(key);
}
