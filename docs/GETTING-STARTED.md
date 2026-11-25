# Getting started

Flower can play music already on your device or stream from a Flower server.
You don't need to run a server to use it.

[Get the app](#get-the-app) · [Join a server](#listening-to-a-server) ·
[Play your own files](#using-flower-on-its-own) ·
[Playback and playlists](#playback-and-playlists) · [Get help](#troubleshooting)

## Get the app

| Device | Install |
|---|---|
| Mac (Apple silicon) | Download the `.dmg` from the [latest release](https://github.com/yanos/Flower/releases/latest), open it and copy Flower to Applications. |
| Windows (x64) | Download and run `Setup.exe` from the [latest release](https://github.com/yanos/Flower/releases/latest). |
| Linux (x64) | Download the `.AppImage` from the [latest release](https://github.com/yanos/Flower/releases/latest), make it executable and open it. |
| iPhone / Android | The apps aren't in the stores yet. Ask your server owner about installation, or see [Building Flower](BUILDING.md#phone-apps). |
| Browser | No installation needed. Ask your server owner for a browser link. |

Desktop builds aren't signed with a publisher certificate yet. macOS may need
**System Settings → Privacy & Security → Open Anyway** after the first launch
attempt. Windows may show **More info → Run anyway**. Continue only for a
download from the project's release page. [Apple's instructions](https://support.apple.com/en-us/102445)
explain the macOS prompt.

Desktop installations check for updates automatically. To include betas, turn
on **Settings → General → Get beta releases**.

The browser player supports the formats your browser can play. For the widest
format support, gapless playback, an equalizer and offline copies, use the app.

### Supported files

The installed apps play MP3, AAC / ALAC (`.m4a`), FLAC, WAV, AIFF, Ogg Vorbis,
Opus, Monkey's Audio (`.ape`) and DSF. DSF audio is converted to PCM for playback.
Protected subscription downloads aren't supported.

## Listening to a server

### 1. Ask for an invite

The server owner can send you an app link starting with `flower://pair`, or an
HTTPS link for listening in a browser. Each invite works on **one device** and
expires after **ten minutes**. Ask for another invite for each device you add.

An app link also checks the server's identity. Use it in preference to a plain
five-character code.

### 2. Connect

**In the app:** open the `flower://pair` link on the device where you installed
Flower. Check the server name, then choose **Pair**.

If your messaging app won't open the link, copy and paste it into Flower:

| Device | Where to paste |
|---|---|
| Computer | **Settings → Servers → Pairing code or link**, then **Pair** |
| Phone | **Settings → Add Server**, then **Add** |

**In a browser:** open the HTTPS invite from the server owner. It pairs that
browser automatically. Bookmark the server's address for next time. A different
browser or a cleared browser profile needs a new invite.

**Only have a code?** On the same Wi-Fi as the server, select it in the desktop
sidebar, or use **Settings → Nearby servers → Pair** on your phone. Enter the
code in **Pairing code or link**. Away from home, ask for a link instead.

### 3. Play and download music

The server's songs appear in your library, ready to stream. Your existing
music files stay on your device.

- **Listen offline:** open a song, album or playlist menu and choose
  **Download** or **Download Album**. Downloads need the installed app.
- **Free up space:** choose **Delete Local File** to remove a downloaded copy.
  A song still on the server remains available to stream.
- **Use playlists:** listeners have their own playlists. The owner's
  administrator devices share the owner's playlists.
- **Listen away from home:** the server owner needs to set up
  [remote access](REMOTE-SETUP.md). Your app then uses the available connection.

If a playlist says **“2 songs unavailable”**, those entries are still saved.
They return when Flower can find the songs again.

## Using Flower on its own

| Device | Add your music |
|---|---|
| Computer | Open **Settings → Library → Add folder…** and choose your music folder. Flower notices new songs there automatically. You can also drag songs onto the window after adding a folder. |
| Computer with Music.app | On first launch, Flower offers to import the Music.app library, including play counts. |
| iPhone / iPad | In Finder (iTunes on Windows), select the device → **Files** and drag songs onto **Flower**. You can also copy files into **On My iPhone → Flower** in the Files app. |
| Android | Allow music access when prompted. Flower reads the music files available to other music apps on your phone. |

See [Managing songs](MANAGING-SONGS.md) for editing, replacing and removing files.
To share your collection across devices, follow [Self-hosting](SELF-HOSTING.md).

## Playback and playlists

| To… | Do this |
|---|---|
| Play a song | Double-click it on a computer, or tap it on a phone. Use an album's **Play** button to play the album. |
| Choose what plays next | Open a song or album menu and choose **Play Next** or **Add to Queue**. Right-click on a computer; long-press a song on a phone. |
| Create a playlist | Use **+ → New Playlist** beside Playlists on desktop, or **New Playlist** in the phone's Playlists tab. Add songs with **Add to Playlist** from their menus. |
| Make a playlist from rules | Choose **New Smart Playlist…**, then set the rules for the songs to include. |
| Make a playlist for a mood | When the server offers it, choose **New Vibe Playlist…**, describe the sound and preview the songs before saving. English descriptions of sound or mood work best. |

## Keyboard shortcuts

On a computer, use **Cmd** on macOS and **Ctrl** on Windows or Linux.

| Action | Shortcut |
|---|---|
| Play / pause | `Space` |
| Play the selected song | `Enter` |
| Jump to the playing song | `L` |
| Open Track Info | `Cmd/Ctrl+I` |
| Open Settings | `Cmd/Ctrl+,` |

## Troubleshooting

| Problem | Try this |
|---|---|
| Invite expired or already used | Ask the server owner for a new one. |
| “This is not the server that invite came from” | Stop pairing and tell the owner. They should check the server's identity before sending a new link. |
| No nearby servers | Check that you're on the same Wi-Fi, or use an invite link. |
| Music works at home but not away | Ask the owner to check [remote access](REMOTE-SETUP.md). |
| Browser won't pair | Use the owner's HTTPS link. Plain HTTP works only at `localhost` on the server itself. |
| Browser shows a certificate warning | Ask the owner to confirm the address. A server using its built-in certificate can show this warning again after a restart; a trusted HTTPS address avoids it. |

For other problems, use **Report an Issue** in Flower or
[open an issue on GitHub](https://github.com/yanos/Flower/issues).

## Privacy settings

Release builds send crash reports to Flower's developer by default. Turn off
**Send anonymous crash reports** in Settings if you prefer. Paired devices also
have a separate **Send diagnostic logs to server** switch.
[Privacy](PRIVACY.md) explains what each report includes and who receives it.
