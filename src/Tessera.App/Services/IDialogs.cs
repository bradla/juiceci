namespace Tessera.App.Services;

public enum SaveChoice { Save, Discard, Cancel }

/// <summary>Window-owned dialogs, so view models stay free of UI types.</summary>
public interface IDialogs
{
    Task<IReadOnlyList<string>> PickDataFilesAsync(string kind);
    Task<string?> PickProjectAsync();
    Task<string?> PickSavePathAsync(string suggestedName);
    Task<SaveChoice> AskSaveChangesAsync(string documentName);
    Task ShowErrorAsync(string title, string message);
    Task ShowMessageAsync(string title, string message);

    /// <summary>Shows the Options dialog; true if the user pressed OK.</summary>
    Task<bool> ShowOptionsAsync(ViewModels.OptionsViewModel options);

    Task<string?> PickImageAsync();

    /// <summary>Shows the measure editor; true once the user saved a valid measure.</summary>
    Task<bool> ShowMeasureAsync(ViewModels.MeasureViewModel measure);
}
