# Adding, removing and changing songs

Find your question in the table, then read that section only.

| I want to… | Go to |
|---|---|
| Know which setup I have | [Your setup](#your-setup) |
| Add songs | [Adding songs](#adding-songs) |
| Free up space on a phone | [Deleting a copy vs removing a song](#deleting-a-copy-vs-removing-a-song) |
| Get rid of a song | [Removing songs](#removing-songs) |
| Get a removed song back | [Removed Songs: undo or clean up](#removed-songs-undo-or-clean-up) |
| Fix a title, artist, album or cover | [Editing tags and covers](#editing-tags-and-covers) |
| Rename or move a file | [Renaming and moving files](#renaming-and-moving-files) |
| Swap a song for a better file | [Replacing a file](#replacing-a-file) |
| Know how Flower tells songs apart | [How Flower recognises a song](#how-flower-recognises-a-song) |

---

## Your setup

What happens depends on which of these describes your device.

| Setup | What it means |
|---|---|
| **On its own** | Not paired to a server. Your library is the folders you picked. |
| **Owner's device** | Paired to a server as an **administrator**. What you change reaches the server, and from there everyone. |
| **Listener's device** | Paired to a server as a listener. You can play everything on the server, but you can't change the server's songs. |

> The server's owner chooses whether a device is an administrator when they
> pair it.

---

## Adding songs

### On a computer

| Setup | How |
|---|---|
| On its own | **Settings → Library → Add folder…**, or drag songs onto the Flower window. Dragged songs are **copied** into your first library folder (as `Album Artist/Album/`). The originals stay where they were. |
| Owner's device | Drag songs onto the window. They're copied into Flower's folder (`Music/Flower`) and **uploaded to the server**, so everyone gets them. |
| Listener's device | Drag songs onto the window. They stay on this computer only. The server never gets them. |

- **New files in a library folder** show up by themselves. You don't need to press anything.
- **Mac + Music.app:**
  - **On its own:** the first time you open Flower, it offers to add your Music.app library.
  - **Owner's device:** turn on **Settings → Library → Add songs from Music.app to Flower** to send the server songs from Music.app that it doesn't have yet.
- **No library folder yet?** Dragging songs onto the window does nothing until you add one in Settings.
- **Paired devices can't pick library folders.** Songs come from drag and drop, downloads, and (for an owner's Mac) Music.app.

### On a phone

| Phone | How |
|---|---|
| iPhone / iPad | Plug it into a computer, select it in Finder (or iTunes on Windows) → **Files**, and drag songs onto **Flower**. Or use the Files app: **On My iPhone → Flower**. |
| Android | Flower plays the songs in your phone's music library, the same ones other music apps see. Allow music access when Flower asks. |

### From the server

On a paired device, every song on the server is already in your library, ready
to stream. Tap **Download** to keep a copy for listening offline.

### Onto the server directly (owners)

Copy files into the server's music folder and they show up on their own. On a
network drive (NAS) that can take up to 5 minutes. See
[SELF-HOSTING.md](SELF-HOSTING.md#adding-music-to-the-servers-folder).

---

## Deleting a copy vs removing a song

These are two different menu items.

| Menu item | What it does |
|---|---|
| **Delete Local File** | Deletes the file **from this device only**. If the server has the song, it stays in your library as a stream and you can download it again later. Flower warns you if this file is your only copy. |
| **Remove from Library** | Takes the song out of your library **and every playlist**. See below. |

---

## Removing songs

### With "Remove from Library"

| Setup | What happens |
|---|---|
| On its own | The song leaves your library. The file stays on disk unless you tick **Also move the files to the Trash**. A rescan won't add it back. |
| Owner's device | If the song is on the server, it's **removed for everyone**. Tick **Also delete the files, here and on the server** to delete the files too. |
| Listener's device | You can only remove your own songs. The menu doesn't offer it for the server's songs, so ask the owner. |

### By deleting the file yourself (Finder, Explorer, Files)

| Setup | What happens |
|---|---|
| On its own | The song leaves your library at the next scan. If the file comes back, the song comes back with its plays and star. |
| Owner's device, file in **Flower's folder** or **Music.app** | The song leaves this device only. It stays on the server and streams from there. |
| Owner's device, file in a **folder you had before pairing** | The song is **removed from the server, for everyone**. |
| Listener's device | The song leaves this device only. |

> **An unplugged drive removes nothing.** If a whole music folder is missing
> or empty, Flower assumes the drive is just disconnected, not that you deleted
> everything.

### Removed Songs: undo or clean up

Removing a song doesn't delete its file unless you ticked the box. The file
waits in **Settings → Library → Removed Songs** (on computers and on the
server, not on phones):

- **Restore** puts the song back, with its plays, star and playlists. On the server, it comes back for everyone.
- **Delete Files…** deletes the files for good. Nothing else in Flower ever deletes a file you own.

---

## Changing songs

### Editing tags and covers

Open **Track Info** (`Cmd+I` / `Ctrl+I`, or from the song's menu) and edit.
Flower saves the change **into the song file**.

| Setup | What happens |
|---|---|
| On its own | Saved into the file. |
| Owner's device | Saved, sent to the server, and passed on to **every device**, listeners included. It works for songs you only stream too, and over any connection. If two devices edit the same song, the latest edit wins. |
| Listener's device | You can only edit your own songs. The server's songs are read-only. |

- **Covers** are saved inside each song file by default. To save one image file in the album's folder instead, turn on **Save covers in the album's folder**.
- **In a web browser**, you can't edit tags yet.

### Editing in another program

Flower reads the new tags on its next scan, and the song stays the same song.
On an **owner's device**, the changed file is uploaded and replaces the copy on
the server and on every other device.

### Renaming and moving files

Flower **never renames files** when you edit tags. If you rename or move a file
yourself:

| What you did | Result |
|---|---|
| Renamed or moved it, tags unchanged | **Same song.** Plays, star and playlists are kept. On an owner's device, the server's copy moves too. |
| Renamed it **and** changed its title, artist or album at the same time | **Treated as a new song.** Plays, star and playlists start over. On an owner's device, the server gets it as a duplicate. |
| Renamed a file with no title, artist or album | **Treated as a new song.** |

> **Tip:** do it in two steps. Rename, let Flower see it (**Rescan files** in
> Settings → Library), then edit the tags.
>
> Watch out for **Music.app** with *Keep Music Media folder organised* turned
> on. Editing an album there renames its files, which is the "both at once"
> case above.

### Replacing a file

| Replacement | What to do |
|---|---|
| Same format (for example a better MP3 rip) | Overwrite the old file, keeping the same name. The song keeps its history. On an owner's device, every copy everywhere is updated. |
| Different format (MP3 → FLAC), **on its own** | Delete the old file and add the new one. It's the same song only if the title, artist, album and length (to the second) all match. Otherwise it's a new song. |
| Different format, **paired device** | **Not supported yet.** A *Replace File…* command is planned. |

> **Files only travel on your home network.** Uploads and updated files wait
> until you're home. Tag edits, covers and removals go over any connection. To
> send files over cellular or remote connections too, turn on **Transfer songs
> when away from home**.

---

## How Flower recognises a song

| Flower checks | When |
|---|---|
| **Where the file is** (its folder and name) | First, always. |
| **Title + artist + album + length** (to the second) | When the file isn't where it used to be, and when matching your songs with the server's. |

- **These don't count:** file name, format, cover, genre, year or other tags.
- **Two copies of the same album in two folders** are two separate songs.
- **Flower remembers every song it removes, forever.** When the file returns at the same place, or with the same title, artist, album and length, the song comes back as it was: same **Date Added**, plays, star and playlists.
- **Between your devices**, plays and stars are shared, and **Date Added** is the earliest any of the owner's devices knows.
