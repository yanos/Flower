using System;
using System.IO;

namespace Flower.Services;

// The repository's NOTICE and the licence texts it points to, embedded in
// Flower.Core.dll (Flower.Core.csproj) so that every build carries them, the
// server's included - an iOS or Android app has no folder beside it to put
// files in, and FFmpeg's LGPL asks for its text to go with each copy. Shown
// under "Open-Source Licences": the About window's button on a desktop, the
// foot of Settings on a phone, the foot of the sidebar in a browser, which
// opens the server's /licences (WebUiHosting).
public static class OpenSourceNotices
{
    // Where Flower.Server serves Text, outside /api (WebUiHosting.MapWebUi).
    public const string ServerPath = "/licences";

    private static readonly Lazy<string> _text = new(Build);

    // NOTICE first, since it says what each licence covers, then the LGPL
    // that FFmpeg and TagLib# are under, then the Apache licence Flower is.
    public static string Text => _text.Value;

    private static string Build() => string.Join(
        "\n\n",
        Read("Flower.Licences.NOTICE"),
        Rule("GNU Lesser General Public License, version 2.1 (licenses/LGPL-2.1.txt)"),
        Read("Flower.Licences.LGPL-2.1"),
        Rule("Apache License, Version 2.0 (LICENSE)"),
        Read("Flower.Licences.Apache-2.0"));

    private static string Rule(string title) => $"{new string('=', 74)}\n{title}\n{new string('=', 74)}";

    private static string Read(string name)
    {
        using var stream = typeof(OpenSourceNotices).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded in Flower.Core.dll; see Flower.Core.csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
