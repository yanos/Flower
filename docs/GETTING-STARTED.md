# Getting started

Flower plays music in one of two ways, and you can switch later:

- **From someone's server.** A friend or family member runs a Flower server
  with their music on it and sends you a link. You open it, and their whole
  library is in your app, at home or away. Start at
  [Listening to a server](#listening-to-a-server).
- **On its own.** Flower plays music that is already on your computer or
  phone. Nothing else is needed. Start at
  [Using Flower on its own](#using-flower-on-its-own).

Running a server yourself is a different guide:
[SELF-HOSTING.md](SELF-HOSTING.md).

---

## Get the app

| Device | Where |
|---|---|
| Mac, Windows, Linux | [The latest release](https://github.com/yanos/Flower/releases/latest): the `.dmg` for a Mac (Apple silicon), `Setup.exe` for Windows, the `.AppImage` for Linux. Flower updates itself after that. |
| iPhone, Android | Not in the App Store or Google Play yet. Ask whoever runs your server. |
| A web browser | Nothing to install: whoever runs your server can send you a link that opens in the browser. |

---

## Listening to a server

### 1. Get a link

Ask whoever runs the server. They make one in Flower's settings and send it
to you in a message. It starts with `flower://pair`.

- **A link works once, on one device, for ten minutes.** If it expires, or you
  want Flower on a second device too, ask for another one.
- **Sometimes it's a five-character code instead** (`G6RJR`). That works too.
  A link is safer, because it can only pair you with the server that made it.

### 2. Open it

Tap or click the link on the device you want to listen on. Flower opens and
asks **Pair With "…"?**, naming the server. Tap **Pair**.

That's it. The server's songs fill your library within a few seconds.

**If tapping the link does nothing** (some messaging apps won't open it),
copy it and paste it into Flower instead:

| Device | Where to paste it |
|---|---|
| Computer | **Settings → Servers**, into **Pairing code or link**, then **Pair**. |
| Phone | **Settings → Add Server**, then **Add**. |

**With a code instead of a link:**

| Device | How |
|---|---|
| Computer | Click the server in the sidebar (it's listed when you're on the same Wi-Fi), type the code into **Pairing code or link**, and press Enter. |
| Phone | **Settings**, under **Nearby servers**, tap **Pair** next to the server, then type the code. |

The server only appears in that list when you're on the same Wi-Fi as it. If
you're not, ask for a link.

### 3. What changes

- **Your library shows the server's music** instead of your device's own.
  Everything on it plays straight away, streamed. Music files already on your
  device are not deleted.
- **To listen offline**, choose **Download** (or **Download Album**) from a
  song's or album's menu, or at the top of a playlist, and Flower keeps a
  copy on your device.
- **Away from home**, Flower carries on with nothing to set up, as long as
  whoever runs the server has made it reachable from outside. If it only
  works at home, tell them.
- **Your playlists are shared** with everyone on the server, and your plays
  count towards its play counts.

### If something goes wrong

| What you see | What to do |
|---|---|
| The code or link is wrong or has expired | Ask for a new one. Each one works once, for ten minutes. |
| "This is not the server that invite came from" | Don't try again yet. Tell whoever runs the server: either the link was for a different server, or something else is answering in its place. |
| No servers found on this Wi-Fi | Use a link instead of a code. |
| It worked at home but not outside | Whoever runs the server has to make it reachable from outside ([SELF-HOSTING.md](SELF-HOSTING.md#remote-access-with-tailscale)). |
| In a browser: an empty library, or a warning about the certificate | Open the link exactly as it was sent (it starts with `https://`). A warning that the browser doesn't know who issued the certificate is expected the first time. Continue past it. |

---

## Using Flower on its own

Add music once, and Flower notices new songs in those places by itself from
then on.

| Device | How |
|---|---|
| Mac, Windows, Linux | **Settings → Library → Add folder…**, and pick the folder your music is in. You can also drag songs onto the window once a folder is added. |
| Mac with Music.app | The first time Flower opens, it offers to bring in your Music.app library, with its play counts. |
| iPhone, iPad | Plug it into a computer, select it in Finder (iTunes on Windows) → **Files**, and drag songs onto **Flower**. Or, in the Files app: **On My iPhone → Flower**. |
| Android | Allow music access when Flower asks. It plays the songs other music apps on the phone see. |

More on adding, removing, editing and replacing songs:
[MANAGING-SONGS.md](MANAGING-SONGS.md).

---

## Good to know

- **Crash reports.** When Flower crashes, it sends an anonymous report to its
  developer, so the crash gets fixed. The first time it does, it says so,
  with a button to turn it off. What is sent, in full:
  [PRIVACY.md](PRIVACY.md).
- **Keyboard shortcuts on a computer:** `Space` play/pause, `Enter` play the
  selected song, `L` jump to what's playing, Cmd/Ctrl+`I` song info,
  Cmd/Ctrl+`,` settings.
