using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

namespace Flower.ViewModels.Mobile;

// ── Tab bar ───────────────────────────────────────────────────────────
//
// Which tabs the bar shows and in what order, chosen on the Navigation Bar
// page Settings opens (NavigationBarSettingsView). The bar is
// a left-to-right strip, so this order is also what a tap and a swipe read
// direction from - see IsRightOf and SwipeBack/SwipeForward.
public partial class MobileMainViewModel
{
    private IReadOnlyList<MobileTab> _visibleTabs = MobileTabs.Default;

    public IReadOnlyList<MobileTab> VisibleTabs
    {
        get => _visibleTabs;
        private set
        {
            _visibleTabs = value;
            OnPropertyChanged();
        }
    }

    // Whether each tab has its name under its icon - the Navigation Bar
    // page's switch, kept in AppSettings.MobileTabLabels.
    private bool _showTabLabels = true;

    public bool ShowTabLabels
    {
        get => _showTabLabels;
        set
        {
            if (_showTabLabels == value)
                return;
            _showTabLabels = value;
            OnPropertyChanged();
            Main.PersistMobileTabLabels(value);
        }
    }

    // Whether an album's screen has its row of buttons under the art -
    // Settings' switch, kept in AppSettings.MobileAlbumButtons. A playlist's
    // screen keeps its row either way: its pencil is the one thing in it the
    // header menu does not also offer.
    private bool _showAlbumButtons = true;

    public bool ShowAlbumButtons
    {
        get => _showAlbumButtons;
        set
        {
            if (_showAlbumButtons == value)
                return;
            _showAlbumButtons = value;
            OnPropertyChanged();
            Main.PersistMobileAlbumButtons(value);
        }
    }

    // The Navigation Bar page's two lists: the tabs in the bar, in bar order,
    // and the rest, in MobileTabs.All's.
    public ObservableCollection<MobileTabSettingRow> VisibleTabRows { get; } = new();
    public ObservableCollection<MobileTabSettingRow> HiddenTabRows { get; } = new();

    public bool HasHiddenTabs => HiddenTabRows.Count > 0;

    private ICommand? _toggleTabShownCommand;
    public ICommand ToggleTabShownCommand => _toggleTabShownCommand ??= new RelayCommand<MobileTabSettingRow>(ToggleTabShown);

    // The Navigation Bar page, pushed over Settings from its "Customize
    // Navigation Bar" row. A flag beside ActiveSheet rather than a sheet of its
    // own, because it stacks on Settings instead of taking its place: back
    // from here is Settings, not the library. Settings going away for any
    // reason takes it too (see ActiveSheet).
    private bool _isShowingNavigationBarSettings;

    public bool IsShowingNavigationBarSettings
    {
        get => _isShowingNavigationBarSettings;
        private set
        {
            if (_isShowingNavigationBarSettings == value)
                return;
            _isShowingNavigationBarSettings = value;
            OnPropertyChanged();
        }
    }

    private ICommand? _openNavigationBarSettingsCommand;
    public ICommand OpenNavigationBarSettingsCommand => _openNavigationBarSettingsCommand ??= new RelayCommand(() =>
    {
        if (ActiveSheet == MobileSheet.Settings)
            IsShowingNavigationBarSettings = true;
    });

    private ICommand? _closeNavigationBarSettingsCommand;
    public ICommand CloseNavigationBarSettingsCommand => _closeNavigationBarSettingsCommand ??= new RelayCommand(() => IsShowingNavigationBarSettings = false);

    // -1 for a tab the bar does not show - reachable all the same, by a jump
    // from Search or Now Playing into Albums or Artists.
    private int PositionInBar(MobileTab tab)
    {
        for (var i = 0; i < _visibleTabs.Count; i++)
        {
            if (_visibleTabs[i] == tab)
                return i;
        }

        return -1;
    }

    // Whether `tab` sits to the right of `other` in the bar, which is the side
    // its screen arrives from. A tab not in the bar has no side, and falls
    // back to MobileTabs.All's order so a jump into one still has a direction.
    private bool IsRightOf(MobileTab tab, MobileTab other)
    {
        var a = PositionInBar(tab);
        var b = PositionInBar(other);
        if (a >= 0 && b >= 0)
            return a > b;

        return IndexIn(MobileTabs.All, tab) > IndexIn(MobileTabs.All, other);
    }

    private static int IndexIn(IReadOnlyList<MobileTab> tabs, MobileTab tab)
    {
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i] == tab)
                return i;
        }

        return -1;
    }

    // Shown tabs go to the end of the bar; a hidden one leaves it. The bar
    // keeps at least one tab - the last row says so by greying its switch out
    // (CanToggle), and this refuses the same.
    private void ToggleTabShown(MobileTabSettingRow? row)
    {
        if (row == null)
            return;

        var tabs = _visibleTabs.ToList();
        if (tabs.Contains(row.Tab))
        {
            if (tabs.Count > 1)
                tabs.Remove(row.Tab);
        }
        else
        {
            tabs.Add(row.Tab);
        }

        ApplyVisibleTabs(tabs);
    }

    // A drop on the Navigation Bar page: `tab` now sits at `index` of the bar,
    // counted as the bar is after it has been lifted out. Only a tab already
    // in the bar moves - showing one is its switch, which puts it at the end.
    public void MoveTab(MobileTab tab, int index)
    {
        var tabs = _visibleTabs.ToList();
        if (!tabs.Remove(tab))
            return;

        tabs.Insert(Math.Clamp(index, 0, tabs.Count), tab);
        ApplyVisibleTabs(tabs);
    }

    private void ApplyVisibleTabs(List<MobileTab> tabs)
    {
        if (!tabs.SequenceEqual(_visibleTabs))
        {
            VisibleTabs = tabs;
            Main.PersistMobileTabs(tabs.Select(t => t.ToString()));
        }

        // Always, even when nothing changed: a refused toggle has already
        // flipped its switch, and new rows are what put it back.
        RebuildTabSettingRows();

        if (PositionInBar(_selectedTab) < 0 && !_hasDrilledIn)
            LeaveHiddenTab();
    }

    // The tab on screen was just taken out of the bar, from the Navigation Bar
    // page over it. Its screen goes too, for the first tab left - as a fresh start
    // rather than a navigation, since Back into a tab the user just removed
    // is somewhere they asked not to be.
    private void LeaveHiddenTab()
    {
        NavigationLeaving?.Invoke(this, BuildLeavingFrame());
        _navigationHistory.Clear();
        _forwardHistory.Clear();
        _tabScreens.Remove(_selectedTab);
        _pendingTransition = MobileNavigationTransition.None;
        _restoredFrame = null;
        ClearScreenFilter();
        SetSelectedTabCore(_visibleTabs[0]);
        OnPropertyChanged(nameof(CanGoForward));
    }

    private void RebuildTabSettingRows()
    {
        VisibleTabRows.Clear();
        foreach (var tab in _visibleTabs)
            VisibleTabRows.Add(new MobileTabSettingRow(tab, IsShown: true, CanToggle: _visibleTabs.Count > 1));

        HiddenTabRows.Clear();
        foreach (var tab in MobileTabs.All.Where(t => !_visibleTabs.Contains(t)))
            HiddenTabRows.Add(new MobileTabSettingRow(tab, IsShown: false, CanToggle: true));

        OnPropertyChanged(nameof(HasHiddenTabs));
    }
}
