using Avalonia.Controls;
using Tessera.App.ViewModels;

namespace Tessera.App.Views;

/// <summary>Home › Enter data; closes with true once the table has a name and a header row.</summary>
public partial class EnterDataWindow : Window
{
    public EnterDataWindow()
    {
        InitializeComponent();
        OkButton.Click += (_, _) =>
        {
            if (((EnterDataViewModel)DataContext!).Validate()) Close(true);
        };
        CancelButton.Click += (_, _) => Close(false);
        Opened += (_, _) => NameBox.Focus();
    }
}
