# Privacy

Flower doesn't require an account or a hosted music service. Your music stays on your devices and, if you run one, your Flower server.

Some features connect to external services. Release builds also send crash reports and sampled performance timings to the developer by default; you can disable reporting. The connections are listed below.

## External connections

| Feature | Connects to | When |
|---|---|---|
| Crash and performance reporting | Developer's Sentry account | On by default in release builds; can be disabled |
| Device diagnostics | Your own Flower server | On by default when paired; can be disabled |
| Artist photos | Deezer | Automatically, when looking up artist images |
| Online artwork search | Apple iTunes Search, MusicBrainz / Cover Art Archive, Bandcamp | Only when you select **Find online** |
| Update checks | GitHub | On by default on desktop; can be disabled |
| AI model download | GitHub | Only if server AI features are enabled |
| Public IP lookup | ipify and Cloudflare | When public access is enabled or the server Network page is open |


## Crash reports and performance timings

Flower sends diagnostic reports to the developer's [Sentry](https://sentry.io) account. Reports may cover handled errors as well as crashes. **The browser player doesn't send these reports.**

### Turn reporting off

To stop sending crash reports and sampled performance timings:

- **Desktop:** **Settings → General → Send anonymous crash reports**.
- **Phone:** **Send anonymous crash reports**, near the bottom of Settings.
- **Server:** set `"SendCrashReports": false` inside the `Flower` object in
  `flower-server.json`, then restart the server.

The app switch takes effect immediately. On first launch of a release, a
notice explains that reporting is on and offers a **Turn Off** button.

### What is included

| Data | Details |
|---|---|
| Error | Exception type, cleaned message, method names, source file names and line numbers |
| Build | Flower version, app or server, and loaded library names and versions |
| System | Operating system and version, .NET runtime, processor architecture and time zone |
| Frequency | How often the same problem occurred on this installation |
| Install ID | A random number used to count affected installations |
| Performance | Startup timing for about 1 in 100 launches; server request timing for about 1 in 100 requests, by route without query strings |
| Native crash | Fault type, faulting address, and library / function names or offsets |

The random install ID does not include device or account identifiers. Turning
reporting off deletes it; turning reporting back on creates a new ID.
Repeated errors are reported at most once an hour per installation, with a
count of repeats included in the next report.

### What is removed

Before sending, Flower removes or replaces:

- **File paths, quoted names and web addresses.** Paths become `<path>` with
  the extension retained; quoted text becomes `'…'`; addresses become `<url>`.
- **Personal and device details:** account, user and email names; device,
  computer and server names; permanent device identifiers; device boot time.
- **Server request contents:** query strings, headers, cookies and bodies.

Flower doesn't attach logs, your library, playlists, settings, listening
history or paired-device details. Sentry is configured not to store IP
addresses or derive location from them, and applies additional scrubbing
on arrival.

For example, a message about `/Music/Artist/Song.flac` contains
`<path>.flac` after cleaning.

### Which builds send reports

Only release versions in Release builds send reports. Debug builds, tests,
betas and untagged source builds don't report. A later release also skips a
saved crash identified as coming from a pre-release.

If a crash ended too abruptly to record its version, a later release may send
it marked as an unknown version, together with the version that found it.

<details>
<summary>Native crash collection by platform</summary>

| Platform | Source and timing |
|---|---|
| macOS | Selected fields from the OS crash report; excludes the Mac model, crash-reporter ID and app folder |
| Windows | The OS crash event, with paths removed |
| Linux | .NET's crash report, with paths removed |
| iOS | Apple's MetricKit diagnostics, sometimes delivered up to a day later; fault and library names with stack offsets |
| Android | Sentry's Android SDK, under the same removal rules |

On desktop, a helper can send the report shortly after a crash. If the network
is unavailable, it can be sent at the next launch.

</details>

## Diagnostic logs sent to your server

A paired device sends logs to its Flower server when **Send diagnostic logs to
server** is enabled. The owner can use them to diagnose connection and playback
problems. These logs do **not** go to Flower's developer or accompany crash reports.

Logs can include song and file names, full paths, errors and network addresses.
At startup and when networks change, they record IP addresses and routers, so
the owner can see which networks the device used and when.

Turn the switch off in Settings to stop future uploads. The server retains
uploaded logs for up to seven days, capped at 128 MB per device.

## Reporting an issue on GitHub

**Report an Issue** opens a draft in your browser. It includes the Flower
version and operating system; after a crash it also includes the error and
stack, with your home directory replaced by `~`.

**Nothing is posted until you submit it.** Review the draft first:
GitHub issues are public. A log file isn't attached automatically. If you add
one, review it for song names, folders and network addresses.

## Artwork, updates and other connections

### Artist pictures and cover searches

For artist pictures, Flower sends the artist's name to Deezer. If the name is
ambiguous, it can also send a few song titles to find the right artist. The
server makes requests for its songs; a device makes requests for local songs.
There is currently no setting to disable artist lookups.

**Find online** sends the album and artist, title and artist, or your edited
search words to Apple's iTunes Search, MusicBrainz / Cover Art Archive and
Bandcamp. This happens only when you start an artwork search.

### Updates

Desktop release installations check GitHub's release list once a day.
**Settings → General → Check for updates automatically** disables automatic
checks. **Get beta releases** changes which release is offered, not the
information sent.

### Vibe Playlists and Endless Play

Enabling server AI features downloads the model from GitHub. Music analysis
and playlist prompts are processed on your Flower server; songs and prompts
aren't sent to a third-party AI provider. Paired devices send prompts to that server and
don't download the model themselves. Turning off all server AI features
deletes the model and stored analysis.

### Public address lookup

When **Allow connections from the internet** is on, or the server's Network
page is open, the server asks ipify and Cloudflare for its public IP address.
Flower uses the result to show and advertise its public address.
