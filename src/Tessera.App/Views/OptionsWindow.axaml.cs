using Avalonia.Controls;

namespace Tessera.App.Views;

/// <summary>The Options dialog; closes with true on OK.</summary>
public partial class OptionsWindow : Window
{
    public OptionsWindow()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
