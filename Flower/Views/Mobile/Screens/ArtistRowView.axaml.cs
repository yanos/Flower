using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

// One artist's row, on the Artists tab and in Search, set from code rather
// than bound - see ModelWatch. The row itself never changes; what does is its cover, which is
// loaded once something first asks for it, so the cover is what it follows.
public partial class ArtistRowView : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private readonly ModelWatch _cover;
    private IArtistRowHost? _host;

    public ArtistRowView()
    {
        InitializeComponent();
        _cover = new ModelWatch(this, Show);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _cover.Watch((DataContext as ArtistPickerRow)?.Cover);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _host = null;
    }

    private void Show()
    {
        if (DataContext is not ArtistPickerRow row)
            return;

        Command = Host?.OpenArtistCommand;
        CommandParameter = row.Name;
        LongPress.SetCommandParameter(this, row.Name);
        Cover.Covers = row.Cover.CoverArt;
        ArtistName.Text = row.Name;
        Summary.Text = row.SummaryText;
    }

    // Found once it is on screen, and kept: a row stays in the list it was
    // built for.
    private IArtistRowHost? Host =>
        _host ??= this.IsAttachedToVisualTree() ? this.GetVisualAncestors().OfType<IArtistRowHost>().FirstOrDefault() : null;
}
