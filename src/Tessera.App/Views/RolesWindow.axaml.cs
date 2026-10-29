using Avalonia.Controls;
using Tessera.App.ViewModels;

namespace Tessera.App.Views;

/// <summary>Modeling › Manage roles; closes with true once every condition checks out.</summary>
public partial class RolesWindow : Window
{
    public RolesWindow()
    {
        InitializeComponent();
        OkButton.Click += (_, _) =>
        {
            if (((RolesViewModel)DataContext!).Validate()) Close(true);
        };
        CancelButton.Click += (_, _) => Close(false);
    }
}
