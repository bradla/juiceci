using Avalonia.Controls;

namespace Tessera.App.Views;

/// <summary>Modeling › Manage relationships; closes with true on Save.</summary>
public partial class RelationshipsWindow : Window
{
    public RelationshipsWindow()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
