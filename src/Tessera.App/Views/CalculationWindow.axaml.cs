using Avalonia.Controls;
using Tessera.App.ViewModels;

namespace Tessera.App.Views;

/// <summary>Modeling › New column or New table; closes with true once the DAX checks out.</summary>
public partial class CalculationWindow : Window
{
    public CalculationWindow()
    {
        InitializeComponent();
        OkButton.Click += (_, _) =>
        {
            if (((CalculationViewModel)DataContext!).Validate()) Close(true);
        };
        CancelButton.Click += (_, _) => Close(false);
        Opened += (_, _) => NameBox.Focus();
    }
}
