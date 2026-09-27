using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

namespace Flower.ViewModels.Mobile;

// ── Tab bar ───────────────────────────────────────────────────────────
//
// Which tabs the bar shows and in what order, chosen in Settings. The bar is
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

    // Settings' list: the tabs in the bar first, in bar order, then the rest.
    public ObservableCollection<MobileTabSettingRow> TabSettingRows { get; } = new();

    private ICommand? _toggleTabShownCommand;
    public ICommand ToggleTabShownCommand => _toggleTabShownCommand ??= new RelayCommand<MobileTabSettingRow>(ToggleTabShown);

    private ICommand? _moveTabUpCommand;
    public ICommand MoveTabUpCommand => _moveTabUpCommand ??= new RelayCommand<MobileTabSettingRow>(row => MoveTab(row, -1));

    private ICommand? _moveTabDownCommand;
    public ICommand MoveTabDownCommand => _moveTabDownCommand ??= new RelayCommand<MobileTabSettingRow>(row => MoveTab(row, 1));

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
    // keeps at least one tab and at most MobileTabs.MaxShown - the rows say
    // so by greying the box out (CanToggle), and this refuses the same.
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
        else if (tabs.Count < MobileTabs.MaxShown)
        {
            tabs.Add(row.Tab);
        }

        ApplyVisibleTabs(tabs);
    }

    private void MoveTab(MobileTabSettingRow? row, int by)
    {
        if (row == null)
            return;

        var tabs = _visibleTabs.ToList();
        var from = tabs.IndexOf(row.Tab);
        var to = from + by;
        if (from < 0 || to < 0 || to >= tabs.Count)
            return;

        tabs.RemoveAt(from);
        tabs.Insert(to, row.Tab);
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
        // flipped its CheckBox, and new rows are what put it back.
        RebuildTabSettingRows();

        if (PositionInBar(_selectedTab) < 0 && !_hasDrilledIn)
            LeaveHiddenTab();
    }

    // The tab on screen was just taken out of the bar, from Settings, over
    // it. Its screen goes too, for the first tab left - as a fresh start
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
        TabSettingRows.Clear();
        var shown = _visibleTabs.Count;
        for (var i = 0; i < shown; i++)
        {
            TabSettingRows.Add(new MobileTabSettingRow(
                _visibleTabs[i],
                IsShown: true,
                CanToggle: shown > 1,
                CanMoveUp: i > 0,
                CanMoveDown: i < shown - 1));
        }

        foreach (var tab in MobileTabs.All.Where(t => !_visibleTabs.Contains(t)))
        {
            TabSettingRows.Add(new MobileTabSettingRow(
                tab,
                IsShown: false,
                CanToggle: shown < MobileTabs.MaxShown,
                CanMoveUp: false,
                CanMoveDown: false));
        }
    }
}
