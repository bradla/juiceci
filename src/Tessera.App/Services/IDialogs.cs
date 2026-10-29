namespace Tessera.App.Services;

public enum SaveChoice { Save, Discard, Cancel }

/// <summary>Window-owned dialogs, so view models stay free of UI types.</summary>
public interface IDialogs
{
    Task<IReadOnlyList<string>> PickDataFilesAsync(string kind);
    Task<string?> PickProjectAsync();
    /// <param name="folder">Where the picker starts, when the report hasn't been saved before.</param>
    Task<string?> PickSavePathAsync(string suggestedName, string? folder = null);
    Task<SaveChoice> AskSaveChangesAsync(string documentName);
    Task ShowErrorAsync(string title, string message);
    Task ShowMessageAsync(string title, string message);

    /// <summary>Shows the Options dialog; true if the user pressed OK.</summary>
    Task<bool> ShowOptionsAsync(ViewModels.OptionsViewModel options);

    Task<string?> PickImageAsync();

    /// <summary>Shows the measure editor; true once the user saved a valid measure.</summary>
    Task<bool> ShowMeasureAsync(ViewModels.MeasureViewModel measure);

    /// <summary>Shows Home › Enter data; true once the user loads a valid table.</summary>
    Task<bool> ShowEnterDataAsync(ViewModels.EnterDataViewModel data);

    Task<bool> ShowRelationshipsAsync(ViewModels.RelationshipsViewModel relationships);
    Task<bool> ShowCalculationAsync(ViewModels.CalculationViewModel calculation);
    Task<bool> ShowRolesAsync(ViewModels.RolesViewModel roles);

    Task<bool> ShowCustomizeThemeAsync(ViewModels.CustomizeThemeViewModel theme);

    /// <summary>A Power BI theme (.json) to import; null if cancelled.</summary>
    Task<string?> PickThemeFileAsync();

    /// <summary>Where to save a theme file; null if cancelled.</summary>
    Task<string?> PickThemeSavePathAsync(string suggestedName);

    /// <summary>Opens a web page in the default browser.</summary>
    void OpenUrl(string url);
}
