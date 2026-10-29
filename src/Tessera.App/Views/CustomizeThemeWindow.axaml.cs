using Avalonia.Controls;
using Tessera.App.ViewModels;

namespace Tessera.App.Views;

/// <summary>Design › Customize theme; closes with true once every colour reads as #RRGGBB.</summary>
public partial class CustomizeThemeWindow : Window
{
    public CustomizeThemeWindow()
    {
        InitializeComponent();
        OkButton.Click += (_, _) =>
        {
            if (((CustomizeThemeViewModel)DataContext!).Validate()) Close(true);
        };
        CancelButton.Click += (_, _) => Close(false);
    }
}
