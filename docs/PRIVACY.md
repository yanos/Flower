# Privacy

What Flower sends off your devices and your server, to whom, and how to turn
it off. Flower has no accounts of its own and no analytics: the only data
that goes to Flower's developer is crash reports, described first and in
full.

## Crash reports

When Flower crashes, or hits an error that means Flower itself did
something wrong, it sends a report to Flower's developer so it can be fixed.
Reports go to [Sentry](https://sentry.io), a crash-reporting service, in the
developer's account; nobody else receives them.

### When

- **Release versions only.** A pre-release (a beta, or any build made from
  source without a release tag) never sends anything, and nor does a debug
  build.
- **On by default, and a switch.** In the app: Settings, "Send anonymous
  crash reports" (desktop: Settings > General; phone: near the bottom of
  Settings). It takes effect at once. The first time a release runs, a
  notice says reporting is on, links here, and has a "Turn Off" button. On a server: set
  `"SendCrashReports": false` in `flower-server.json` and restart.
- **Never from the browser tab**, which has no crash to report.

### What a report contains

- **What went wrong:** the exception's type (`System.IO.IOException`), its
  message with names removed (below), and the names of the methods on the
  stack with their source file names and line numbers
  (`Flower.Playback.Player.Open`, `Player.cs`, line 42).
- **Which build:** Flower's version, whether it is the app or the server,
  and the name and version of each library it had loaded (Avalonia,
  FFAudio.NET, .NET's own).
- **What it ran on:** the operating system and its version, the .NET runtime,
  the processor architecture, and the time zone.
- **How often:** how many times this problem happened on this install since
  it last reported it (see "How often", below).
- **A random install number,** made up by Flower the first time it sends a
  report. It says nothing about you, your device or your server; its only use
  is counting how many installs a problem reaches. Turning the switch off
  deletes it, so turning it back on starts a new one with nothing linking the
  two.
- **How long startup took**, for one launch in a hundred, and on a server how
  long one request in a hundred took, by route (`GET
  /api/flower/v1/media/{id}`, never a query string).
- **For a crash in native code** (audio output, decoding), found at the next
  launch: the kind of fault (`SIGSEGV`), the memory address it touched, and
  the libraries and functions it happened in. On a Mac this comes from the
  crash report macOS itself writes; Flower reads only those parts of it,
  never the report's other contents (your Mac's model, its crash-reporter
  ID, the folder Flower ran from). On an iPhone it comes from Apple's own
  crash diagnostics (MetricKit), which iOS hands over up to a day after
  the crash: the fault and the names of the libraries on the stack, with
  offsets into them. On Android, from Sentry's Android SDK, under the same
  removal rules as everything else.

### What is removed before a report leaves

- **Names.** File and folder paths are replaced by `<path>` (keeping only the
  extension: `<path>.flac`); anything in quotes in a message becomes `'…'`;
  web addresses become `<url>`. So "Could not open /Music/Some Artist/01
  Song.flac" arrives as "Could not open `<path>.flac`".
- **Who and where.** No account, user name or e-mail; no device, computer or
  server name; no IP address (Sentry does not store it, and is not given one
  to work out a location from); no device boot time; none of the permanent
  device identifiers Sentry's phone SDKs would otherwise add.
- **On a server:** a request's query string (where stream tickets travel),
  headers, cookies and body.
- **Never sent at all:** log files or log lines, your library, playlists,
  settings, play history, and anything about your paired devices or server.

The removal happens on your device, before sending, and Sentry scrubs again
on arrival.

### How often

A problem is reported at most once an hour per install. Repeats inside the
hour are counted, and the count goes with the next report, so how often a
problem happens is known without one install sending hundreds of reports.

### Errors Flower catches

Some errors don't crash Flower but still mean a bug - a decoder that refused a
valid file, say. Flower can report those in the same way, under the same
rules and the same switch, tagged with where they happened.

## Reporting an issue on GitHub

"Report an Issue" (About box, phone Settings) and, with crash reports off, the
banner after a crash open a new GitHub issue in your browser with the details
written in: the version, your operating system and, after a crash, the error
and its stack, with your home folder written as `~`. **Nothing is sent unless
you submit it**, and you can read and edit everything first. GitHub issues
are public. Your log file is not attached; the issue says where it is, and it
contains the names of your songs and folders.

## Logs to your own server

A device paired to a server sends its log to that server, for the server's
owner to diagnose problems ("Send diagnostic logs to server", on by default).
It goes only to your own server, never to Flower's developer, and it
contains file names.

## Other connections Flower makes

None of these go to Flower's developer.

- **Artist pictures** from Deezer: Flower sends an artist's name. A server
  does this for every device paired to it, so a listener's phone sends
  nothing. Switch: "Look up artist pictures online" in the phone's Settings;
  for a server and everything paired to it, the server's own settings.
- **Find album art online**, only when you ask for it: the album and artist
  names go to Apple's iTunes Search, MusicBrainz with the Cover Art Archive,
  and Bandcamp.
- **Update checks** (desktop installs from a release): once a day, GitHub is
  asked for the latest release. Switch: Settings > General, "Check for updates
  automatically".
- **Vibe Playlists' model**, downloaded once from GitHub the first time Vibe
  Playlists analyse the library.
- **A server's public address**, only with "Allow connections from the
  internet" on or its network page open: the server asks ipify and Cloudflare
  what its public IP address is.
