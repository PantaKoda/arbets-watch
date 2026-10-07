using ArbetsWatch.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ArbetsWatch.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private ScrollViewer? _listScroll;

    public MainWindow()
    {
        InitializeComponent();
        AdList.AddHandler(DoubleTappedEvent, OnAdActivated);
        AdList.AddHandler(KeyDownEvent, OnAdListKeyDown, RoutingStrategies.Tunnel);
        AdList.TemplateApplied += (_, _) => AttachScroll();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                vm.ScrollToTopRequested += (_, _) => Dispatcher.UIThread.Post(() => _listScroll?.ScrollToHome());
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.IsPlacesOpen) && vm.IsPlacesOpen)
                    {
                        Dispatcher.UIThread.Post(() => PlaceSearch.Focus(NavigationMethod.Tab));
                    }
                };
            }
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void AttachScroll()
    {
        _listScroll = AdList.FindDescendantOfType<ScrollViewer>();
        if (_listScroll is not null)
        {
            _listScroll.ScrollChanged += (_, _) =>
            {
                if (ViewModel is { } vm)
                {
                    var atTop = _listScroll.Offset.Y < 24;
                    vm.IsListAtTop = atTop;
                    if (atTop && vm.HasHeldUpdates)
                    {
                        vm.ShowHeldCommand.Execute(null);
                    }
                }
            };
        }
    }

    // A single click selects (keyboard focus follows); double-click or Enter opens the ad.
    private void OnAdActivated(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>() is { DataContext: AdRowViewModel row } &&
            source.FindAncestorOfType<Button>(includeSelf: true) is null)
        {
            ViewModel?.OpenAdCommand.Execute(row);
        }
    }

    // Ctrl+F jumps to the ad search, ready to type over.
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && AdSearch.IsEffectivelyVisible)
        {
            AdSearch.Focus(NavigationMethod.Tab);
            AdSearch.SelectAll();
            e.Handled = true;
        }
    }

    private void OnAdListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AdList.SelectedItem is AdRowViewModel row)
        {
            ViewModel?.OpenAdCommand.Execute(row);
            e.Handled = true;
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>The footer grip and the frame's edges and corners; each names its edge in <c>Tag</c>.</summary>
    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && sender is Control { Tag: string tag } &&
            Enum.TryParse<WindowEdge>(tag, out var edge))
        {
            BeginResizeDrag(edge, e);
            e.Handled = true;
        }
    }
}
