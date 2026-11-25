# Getting started

Flower plays music stored on your device or streamed from a Flower server. You don't need an account or server to play local files.

[Install](#get-the-app) · [Play local music](#using-flower-on-its-own) · [Join a server](#listening-to-a-server) · [Playback and playlists](#playback-and-playlists) · [Troubleshooting](#troubleshooting)

## Get the app

Download the [latest Flower release](https://github.com/yanos/Flower/releases/latest).

| Device | Installation |
|---|---|
| **Mac (Apple silicon)** | Open the `.dmg` and drag Flower to Applications. |
| **Windows (x64)** | Run `Setup.exe`. |
| **Linux (x64)** | Download the `.AppImage`, make it executable, and open it. |
| **iPhone / Android** | Not in app stores yet. Ask your server owner for installation help, or see [building the phone apps](BUILDING.md#phone-apps). |
| **Web browser** | No installation needed. Open an invite from a server owner. |

**Installer warnings:** Desktop releases aren't yet signed with a publisher certificate. On macOS, try **System Settings → Privacy & Security → Open Anyway** after attempting to open Flower. On Windows, you may see **More info → Run anyway**. Only bypass a warning if you downloaded Flower from the official release page. See [Apple's instructions](https://support.apple.com/en-us/102445).

Desktop versions check for updates automatically. To receive betas, enable **Settings → General → Get beta releases**.

### Supported files

The installed apps support **MP3, AAC/ALAC (`.m4a`), FLAC, WAV, AIFF, Ogg Vorbis, Opus, Monkey's Audio (`.ape`), and DSF**. DSF is converted to PCM during playback. DRM-protected subscription downloads are not supported.

The browser player is limited to formats supported by your browser. Use an installed app for broader format support, gapless playback, an equalizer, and offline downloads.

## Using Flower on its own

To play local files, add your music folder or copy songs onto your phone:

| Device | Add your music |
|---|---|
| **Computer** | Go to **Settings → Library → Add folder…** and select your music folder. Flower detects new files automatically. Once a folder is added, you can also drag songs into the window. |
| **Mac with Music.app** | Flower offers to import your Music.app library, including play counts, on first launch. |
| **iPhone / iPad** | In Finder (or iTunes on Windows), select your device → **Files** and drag songs into **Flower**. Or copy them to **On My iPhone → Flower** using the Files app. |
| **Android** | Allow music access when prompted. Flower reads the music files available to other music apps. |

To edit tags, replace files, or remove songs, see [Managing songs](MANAGING-SONGS.md). To stream your collection to other devices, see [Self-hosting](SELF-HOSTING.md).

## Listening to a server

### 1. Ask for an invite

The server owner sends you either a `flower://pair` link for the app or an HTTPS invite for a browser. **Each invite works once, on one device, and expires after 10 minutes.** You'll need a separate invite for each device.

Prefer the `flower://pair` link over a short code: the link also verifies which server you're joining.

### 2. Connect

**In the app:** open the `flower://pair` link on the device where Flower is installed, verify the server name, and select **Pair**.

If the link doesn't open the app, paste it here:

| Device | Where |
|---|---|
| Computer | **Settings → Servers → Pairing code or link** → **Pair** |
| Phone | **Settings → Add Server** → **Add** |

**In a browser:** open the owner's HTTPS invite. The browser pairs automatically. Bookmark the server address; another browser or a cleared browser profile will need a new invite.

**If you received a five-character code:** Connect to the same local network as the server first. Choose the nearby server in the desktop sidebar or **Settings → Nearby servers → Pair** on your phone, then enter the code in **Pairing code or link**. If you're away from home, request an invite link instead.

### 3. Play and download music

The server's songs appear automatically, ready to stream. Your existing local files stay where they are.

| Task | Steps |
|---|---|
| **Listen offline** | Open a song, album, or playlist menu and choose **Download** or **Download Album**. Requires the installed app. |
| **Save device storage** | Choose **Delete Local File** for a downloaded copy. The song remains streamable while it's on the server. |
| **Create playlists** | Make your own playlists. Listener devices keep separate playlists; the owner's administrator devices share the owner's playlists. |
| **Listen outside your home** | Ask the owner to set up [remote access](REMOTE-SETUP.md). Flower uses an available server address automatically. |

If a playlist shows songs as **unavailable**, Flower keeps their entries. They return when the songs are available again.

## Playback and playlists

| Task | Steps |
|---|---|
| Play a song or album | Double-click a song on desktop or tap it on phone. Use an album's **Play** button to play the album. |
| Control the queue | Use **Play Next** or **Add to Queue** in a song or album menu. Right-click on desktop or long-press on phone. |
| Create a playlist | Desktop: **+ → New Playlist** beside Playlists. Phone: **New Playlist** on the Playlists tab. Then choose **Add to Playlist** on songs. |
| Create a rule-based playlist | Choose **New Smart Playlist…** and set its rules. |
| Create a mood-based playlist | If the server offers it, choose **New Vibe Playlist…**, describe the sound or mood in English, preview, and save. |

## Keyboard shortcuts

Use **Cmd** on Mac and **Ctrl** on Windows or Linux.

| Action | Shortcut |
|---|---|
| Play / pause | `Space` |
| Play selected song | `Enter` |
| Jump to playing song | `L` |
| Open Track Info | `Cmd/Ctrl+I` |
| Open Settings | `Cmd/Ctrl+,` |

## Troubleshooting

| Problem | What to try |
|---|---|
| Invite has expired or been used | Request a new invite from the owner. |
| “This is not the server that invite came from” | **Stop pairing.** Ask the owner to verify the server identity before issuing another invite. |
| No nearby server found | Check that both devices are on the same Wi-Fi, or use an invite link. |
| Music works at home but not elsewhere | Ask the owner to check [remote access](REMOTE-SETUP.md). |
| Browser won't pair | Use an HTTPS invite. Plain HTTP browser pairing works only at `localhost` on the server. |
| Browser certificate warning | Confirm the server address with the owner. The built-in certificate may trigger a warning; a trusted HTTPS address avoids it. |

For help, select **Report an Issue** in Flower or [open a GitHub issue](https://github.com/yanos/Flower/issues).

## Privacy settings

Release builds send anonymous crash and sampled performance reports to the developer by default. Disable **Send anonymous crash reports** in Settings to opt out. Paired devices have a separate **Send diagnostic logs to server** setting.

See [Privacy](PRIVACY.md) for what is shared, with whom, and how to disable it.
