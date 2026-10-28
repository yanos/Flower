using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Audio;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Material.Icons;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.ViewModels;

public class SidebarItemTests
{
    // Both reachability glyphs are gated on IsPairedServer: an ordinary
    // discovered-device row shows neither, whatever IsReachable says.
    [Fact]
    public void Reachability_icons_only_show_for_the_paired_server_row()
    {
        var ordinary = new SidebarItem(SidebarItemKind.Device, "Some Phone") { IsReachable = true };
        Assert.False(ordinary.ShowReachableIcon);
        Assert.False(ordinary.ShowUnreachableIcon);

        var paired = new SidebarItem(SidebarItemKind.Device, "Server") { IsPairedServer = true };
        Assert.False(paired.ShowReachableIcon);
        Assert.True(paired.ShowUnreachableIcon);   // pinned but not reachable right now

        paired.IsReachable = true;
        Assert.True(paired.ShowReachableIcon);
        Assert.False(paired.ShowUnreachableIcon);
    }

    // Both setters have to fan out to the two computed glyph properties, or the
    // icon silently stops tracking reachability.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Setting_IsPairedServer_or_IsReachable_raises_both_glyph_properties(bool viaReachable)
    {
        var item = new SidebarItem(SidebarItemKind.Device, "Server");
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        if (viaReachable)
            item.IsReachable = true;
        else
            item.IsPairedServer = true;

        Assert.Contains(nameof(SidebarItem.ShowReachableIcon), raised);
        Assert.Contains(nameof(SidebarItem.ShowUnreachableIcon), raised);
    }
}
