using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Tessera.App.ViewModels;

namespace Tessera.App.Views;

/// <summary>The Options dialog; closes with true on OK.</summary>
public partial class OptionsWindow : Window
{
    public OptionsWindow()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
        OptionsCatalog.PickFolder = async current =>
        {
            var start = Directory.Exists(current) ? await StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(current))) : null;
            var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Choose a folder", SuggestedStartLocation = start,
            });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is OptionsViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(OptionsViewModel.SelectedPage)) BodyScroller.Offset = default;
                };
        };
    }
}
