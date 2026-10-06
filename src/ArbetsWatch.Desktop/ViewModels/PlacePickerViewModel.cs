using System.Collections.ObjectModel;
using System.Globalization;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Places;
using ArbetsWatch.Core.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArbetsWatch.Desktop.ViewModels;

/// <summary>
/// Län and kommuner with checkboxes. A län's own checkbox means the whole län (including ads without a
/// municipality); municipality checkboxes are individual choices. Edits go through <see cref="PlaceSelection"/>.
/// </summary>
public sealed partial class PlacePickerViewModel : ObservableObject
{
    private readonly PlaceCatalog _catalog;
    private readonly Func<AdFilter> _current;
    private readonly Action<AdFilter> _apply;
    private bool _syncing;

    [ObservableProperty]
    public partial bool AllSweden { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public PlacePickerViewModel(PlaceCatalog catalog, Func<AdFilter> current, Action<AdFilter> apply)
    {
        _catalog = catalog;
        _current = current;
        _apply = apply;
        var sv = StringComparer.Create(CultureInfo.GetCultureInfo("sv-SE"), false);
        Regions = [.. catalog.Regions.OrderBy(r => r.Label, sv).Select(r => new RegionNodeViewModel(this, r, sv))];
        Sync(current());
    }

    public ObservableCollection<RegionNodeViewModel> Regions { get; }

    /// <summary>Updates every checkbox from the filter without applying anything.</summary>
    public void Sync(AdFilter filter)
    {
        _syncing = true;
        try
        {
            AllSweden = filter.AllSweden;
            foreach (var region in Regions)
            {
                region.Sync(filter);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    internal void SetRegion(string id, bool on)
    {
        if (!_syncing)
        {
            Apply(PlaceSelection.SetRegion(_current(), _catalog, id, on));
        }
    }

    internal void SetMunicipality(string id, bool on)
    {
        if (!_syncing)
        {
            Apply(PlaceSelection.SetMunicipality(_current(), id, on));
        }
    }

    partial void OnAllSwedenChanged(bool value)
    {
        if (!_syncing)
        {
            Apply(PlaceSelection.SetAllSweden(_current(), value));
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        var query = value.Trim();
        foreach (var region in Regions)
        {
            region.ApplySearch(query);
        }
    }

    [RelayCommand]
    private void ClearPlaces() => Apply(_current() with
    {
        AllSweden = false,
        RegionIds = new HashSet<string>(StringComparer.Ordinal),
        MunicipalityIds = new HashSet<string>(StringComparer.Ordinal),
    });

    private void Apply(AdFilter filter)
    {
        _apply(filter);
        Sync(filter);
    }
}

public sealed partial class RegionNodeViewModel : ObservableObject
{
    private readonly PlacePickerViewModel _owner;
    private bool _syncing;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    [ObservableProperty]
    public partial string SelectionText { get; set; } = string.Empty;

    internal RegionNodeViewModel(PlacePickerViewModel owner, Region region, StringComparer order)
    {
        _owner = owner;
        Id = region.Id;
        Label = region.Label;
        Municipalities = [.. region.Municipalities.OrderBy(m => m.Label, order).Select(m => new MunicipalityNodeViewModel(owner, m))];
    }

    public string Id { get; }

    public string Label { get; }

    public IReadOnlyList<MunicipalityNodeViewModel> Municipalities { get; }

    internal void Sync(AdFilter filter)
    {
        _syncing = true;
        try
        {
            IsSelected = filter.RegionIds.Contains(Id);
            // Only a whole län covers (and locks) its kommuner. Under All of Sweden they stay clickable: ticking one
            // switches from All of Sweden to that choice.
            foreach (var m in Municipalities)
            {
                m.Sync(filter, IsSelected);
            }

            var picked = Municipalities.Count(m => m.IsSelected);
            SelectionText = IsSelected ? "whole län"
                : picked > 0 ? string.Create(CultureInfo.InvariantCulture, $"{picked} of {Municipalities.Count}")
                : string.Empty;
        }
        finally
        {
            _syncing = false;
        }
    }

    internal void ApplySearch(string query)
    {
        if (query.Length == 0)
        {
            IsVisible = true;
            foreach (var m in Municipalities)
            {
                m.IsVisible = true;
            }

            return;
        }

        var regionMatches = Label.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        var any = false;
        foreach (var m in Municipalities)
        {
            m.IsVisible = regionMatches || m.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            any |= m.IsVisible;
        }

        IsVisible = regionMatches || any;
        IsExpanded = any && !regionMatches;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_syncing)
        {
            _owner.SetRegion(Id, value);
        }
    }
}

public sealed partial class MunicipalityNodeViewModel : ObservableObject
{
    private readonly PlacePickerViewModel _owner;
    private bool _syncing;

    /// <summary>Shown checked when chosen individually or covered by a whole län.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Covered by a whole län: shown checked and disabled.</summary>
    [ObservableProperty]
    public partial bool IsCovered { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    internal MunicipalityNodeViewModel(PlacePickerViewModel owner, Municipality municipality)
    {
        _owner = owner;
        Id = municipality.Id;
        Label = municipality.Label;
    }

    public string Id { get; }

    public string Label { get; }

    public bool IsSelected { get; private set; }

    internal void Sync(AdFilter filter, bool covered)
    {
        _syncing = true;
        try
        {
            IsSelected = filter.MunicipalityIds.Contains(Id);
            IsCovered = covered;
            IsChecked = IsSelected || covered;
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_syncing && !IsCovered)
        {
            _owner.SetMunicipality(Id, value);
        }
    }
}
