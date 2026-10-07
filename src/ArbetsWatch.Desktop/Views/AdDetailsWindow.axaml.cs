using Avalonia.Controls;
using Avalonia.Input;

namespace ArbetsWatch.Desktop.Views;

public sealed partial class AdDetailsWindow : Window
{
    public AdDetailsWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }
}
