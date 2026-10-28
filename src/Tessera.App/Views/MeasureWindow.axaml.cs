using Avalonia.Controls;
using Tessera.App.ViewModels;

namespace Tessera.App.Views;

/// <summary>The measure editor; closes with true once the formula checks out.</summary>
public partial class MeasureWindow : Window
{
    public MeasureWindow()
    {
        InitializeComponent();
        MeasureViewModel Vm() => (MeasureViewModel)DataContext!;
        CheckButton.Click += (_, _) => Vm().Validate();
        OkButton.Click += (_, _) =>
        {
            if (Vm().Validate()) Close(true);
        };
        CancelButton.Click += (_, _) => Close(false);
        Opened += (_, _) => NameBox.Focus();
    }
}
