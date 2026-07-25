using System.Globalization;
using STRMshelf.App.Localization;

namespace STRMshelf.Tests;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection;

[Collection("Localization")]
public sealed class LocalizationTests
{
    [Fact]
    public void Get_UsesSelectedLanguageInsteadOfAmbientCulture()
    {
        LocalizationManager.SetLanguage("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
        Assert.Equal("Settings", LocalizationManager.Get("Settings"));

        LocalizationManager.SetLanguage("ru");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        Assert.Equal("Настройки", LocalizationManager.Get("Settings"));

        LocalizationManager.SetLanguage("en");
    }
}
