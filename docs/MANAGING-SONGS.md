# Managing songs

[Add music](#adding-songs) · [Free up space](#deleting-a-copy-vs-removing-a-song) ·
[Remove or restore](#removing-songs) · [Edit details](#editing-tags-and-covers) ·
[Move or replace files](#renaming-and-moving-files)

## Your setup

Some actions depend on how you use Flower:

| Setup | What you can change |
|---|---|
| **On its own** | Your local files and library |
| **Administrator** | Your files and the server's shared library; server changes reach everyone |
| **Listener** | Your own files and playlists; the server's songs are read-only |

The server owner chooses whether a device is an administrator when pairing it.
In this guide, **owner's device** means a device paired as an administrator.

## Adding songs

### On a computer

| Setup | How to add music |
|---|---|
| On its own | Choose **Settings → Library → Add folder…**. You can then drag songs onto the window; Flower copies them into your first library folder and leaves the originals alone. |
| Owner's device | Drag songs onto the window. Flower copies them into `Music/Flower` and uploads them to the server. |
| Listener's device | Drag songs onto the window. They stay on this computer and aren't uploaded. |

Flower notices new files in its music folders automatically. If you're using it
on its own, add a folder before using drag and drop. Paired devices use Flower's
own folder and downloads instead of choosing library folders.

With **Music.app**, Flower offers to import your library on first launch.
On an owner's device, **Settings → Library → Add songs from Music.app to
Flower** also sends songs the server doesn't have yet.

### On a phone

| Device | How to add music |
|---|---|
| iPhone / iPad | Select your device in Finder (iTunes on Windows) → **Files**, then drag songs onto **Flower**. Or copy files into **On My iPhone → Flower** in the Files app. |
| Android | Allow music access when prompted. Flower reads the phone's music library. |

### From a server

Server songs appear automatically, ready to stream. Choose **Download** or
**Download Album** from a song, album or playlist menu to keep an offline copy
in the installed app.

Owners can also copy songs straight into the server's music folder. Local
changes appear within seconds; a network drive can take up to five minutes.
See [Self-hosting](SELF-HOSTING.md#adding-music-to-the-servers-folder).

## Deleting a copy vs removing a song

| Menu item | Effect |
|---|---|
| **Delete Local File** | Deletes this device's copy. If the server still has the song, you can stream or download it again. |
| **Remove from Library** | Removes the song from the library and playlists. Files stay unless you also choose to delete them. |
| **Remove from Playlist** | Removes the entry from that playlist only. |

**If Delete Local File would remove your only copy, Flower warns you first.**
On a computer it goes to the Trash; on a phone it is permanently deleted.

Unavailable playlist entries are different: Flower keeps them in place until
the songs become available again.

## Removing songs

### With “Remove from Library”

| Setup | What happens |
|---|---|
| On its own | The song leaves your library. To remove the file too, select **Also move the files to the Trash**. A rescan won't add the song back. |
| Owner's device | A server song is **removed for everyone**. To delete the files too, select **Also delete the files, here and on the server**. |
| Listener's device | You can remove your own songs. Ask the owner to remove a server song. |

### By deleting a file outside Flower

| File location | What happens |
|---|---|
| Unpaired device | The song leaves at the next scan. If the file returns, its history returns too. |
| Owner's device: Flower's folder or Music.app | Only this device's copy is lost. The song stays on the server. |
| Owner's device: a library folder kept from before pairing | **The song is removed from the server for everyone.** Other independently owned files stay on disk; downloaded copies are removed. |
| Listener's device | Only this device's copy is lost. |

Flower treats a missing or empty library folder as a disconnected drive and
avoids removing its songs. Keep backups before reorganising a library.

### Removed Songs: undo or clean up

Open **Settings → Library → Removed Songs** on a computer, or open the server's
settings from an administrator device. This page isn't available on phones.

- **Restore** brings a song back with its history and playlist entries. A
  server restore brings it back for everyone.
- **Delete Files…** permanently deletes the remaining files after confirmation.
- On an unpaired computer, dragging a removed file onto Flower also restores it.

## Changing songs

### Editing tags and covers

Open **Track Info** from a song's menu, or press `Cmd/Ctrl+I` on a computer.
You can change the title, artist, album, other song details and cover art.
Changes are saved into the music file.

| Setup | Where edits go |
|---|---|
| On its own | Your file |
| Owner's device | The server and every paired device, including downloads. You can edit streamed songs too. The latest edit wins. |
| Listener's device | Your own files only |

- Covers are stored inside song files by default. Turn on **Save covers in
  the album's folder** to use a separate image instead.
- **Find online** searches for artwork. You can change the search words
  without changing the song's details.
- Tag editing isn't available in the browser player yet.

### Editing in another program

Flower reads changed tags on its next scan. On an owner's device, the updated
file replaces the server's copy and copies on other devices. A listener's
independently added file is never replaced.

### Renaming and moving files

Editing song details in Flower doesn't rename files. If you move files yourself:

| Change | Result |
|---|---|
| Rename or move a file, keeping its tags | History and playlists stay. A move on an owner's device also reaches the server. |
| Rename and change the title, artist or album at the same time | Flower may treat it as a new song, losing the match to its history. An owner's device can upload it as a duplicate. |
| Rename a file with no title, artist or album tags | Flower treats it as a new song. |

For both a move and a tag edit, do them separately: move the file, choose
**Settings → Library → Rescan files**, then edit its tags. Music.app's
*Keep Music Media folder organised* option can combine both changes, so take
care when editing albums there.

### Replacing a file

| Replacement | What to do |
|---|---|
| Same format, such as a better MP3 rip | Overwrite the file with the same name. It keeps its history. On an owner's device, the new file also reaches the server and other devices. |
| Different format on an unpaired device, such as MP3 to FLAC | Replace the old file. The title, artist, album and length to the second must match to keep its history. |
| Different format on a paired device | Not supported yet. |

**Automatic uploads and replacement downloads wait until you're home.** To allow
these transfers over remote connections or cellular, enable **Transfer songs
when away from home** in Settings. Tag edits, covers, moves and removals sync
on any connection. Normal streaming and downloads are available away from home.

## Duplicate songs

Flower flags files with the same title, artist, album and length to the second.
Choose **Show Duplicates** in the banner to compare their file paths and remove
the copy you don't want.

The **Duplicates** page stays in the desktop sidebar, or the phone's **Home**
menu, while duplicates exist. It appears only on unpaired and administrator
devices, since listeners can't remove server songs.

## How Flower recognises a song

Flower checks the file's location first. When a file moves, or when matching
it with the server, Flower uses **title, artist, album and length to the
second**. Format, cover, genre and year don't affect that match.

Two copies in separate folders can remain separate library entries. Flower
keeps records of removed songs so a returning file can recover its original
Date Added, plays, star and playlist entries. On the owner's devices, plays
and stars sync, and Date Added uses the earliest known date.
