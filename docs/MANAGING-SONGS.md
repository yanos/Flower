# Managing songs

How to add, edit, move, remove and restore songs. Some steps depend on whether you use Flower locally or connect to a server.

[Add music](#adding-songs) · [Delete a download](#deleting-a-copy-vs-removing-a-song) · [Remove or restore](#removing-songs) · [Edit tags and covers](#editing-tags-and-covers) · [Move or replace files](#renaming-and-moving-files)

## Local files and server access

| How you use Flower | What you can change |
|---|---|
| **Local only** (not paired) | Your files and library |
| **Server administrator** | Your files, the shared server library, and server settings—changes can affect everyone |
| **Listener** | Your own files and playlists; server songs are read-only |

The server owner grants administrator access when pairing a device. Below, **owner's device** means a paired administrator device.

## Adding songs

### On a computer

| Setup | How to add music |
|---|---|
| **Local only** | Choose **Settings → Library → Add folder…**. After that, drag songs into Flower to copy them to your first library folder; the originals remain in place. |
| **Owner's device** | Drag songs into Flower. They're copied to `Music/Flower` and uploaded to the server. |
| **Listener's device** | Drag songs into Flower. They're stored locally and not uploaded. |

Flower detects new songs in its music folders automatically. On a local-only computer, add a folder before using drag and drop. Paired devices use Flower's own folder and downloads instead of user-selected library folders.

Flower offers to import **Music.app** on first launch. On an owner's device, **Settings → Library → Add songs from Music.app to Flower** also uploads any songs missing from the server.

### On a phone

| Device | How to add music |
|---|---|
| iPhone / iPad | In Finder (iTunes on Windows), select your device → **Files** and drag songs into **Flower**. Or copy files into **On My iPhone → Flower** in the Files app. |
| Android | Allow music access when prompted; Flower reads your phone's music library. |

### From a server

Songs from a paired server appear automatically. To keep offline copies in an installed app, use **Download** or **Download Album** from a song, album, or playlist menu.

Owners can also copy files into the server's music folder. Local changes usually appear within seconds; changes on network drives can take up to five minutes. See [Self-hosting](SELF-HOSTING.md#adding-music-to-the-servers-folder).

## Deleting a copy vs removing a song

These commands have different effects:

| Action | Result |
|---|---|
| **Delete Local File** | Removes the copy on this device. If the server still has it, you can stream or download it again. |
| **Remove from Library** | Removes the song from the library and playlists. The file remains unless you also choose to delete it. |
| **Remove from Playlist** | Removes it from that playlist only. |

If **Delete Local File** would remove your only copy, Flower warns you first. Deleted files go to the Trash on computers; **phone deletions are permanent**.

Songs temporarily unavailable from a server remain in your playlists until they're available again.

## Removing songs

### With “Remove from Library”

| Setup | What happens |
|---|---|
| Local only | The song disappears from your library, including after a rescan. To remove the file too, select **Also move the files to the Trash**. |
| Owner's device | Removing a server song **removes it for everyone**. To delete the files too, select **Also delete the files, here and on the server**. |
| Listener's device | You can remove your own songs; only an administrator can remove server songs. |

### By deleting a file outside Flower

| File location | What happens |
|---|---|
| Local-only device | The song disappears on the next scan. Its history returns if the file comes back. |
| Owner's device: Flower folder or Music.app | Only this device's copy disappears; the song stays on the server. |
| Owner's device: an older library folder retained from before pairing | **The song is removed from the server for everyone.** Other independently owned files stay on disk; downloaded copies are removed. |
| Listener's device | Only the local copy disappears. |

Flower treats a missing or empty library folder as a disconnected drive instead of immediately removing its songs. Back up your music before moving large numbers of files.

### Removed Songs: undo or clean up

On desktop, open **Settings → Library → Removed Songs**. Administrators can also use the server settings. This page isn't available on phones.

- **Restore** brings back the song, play history, and playlist entries. Restoring a server song affects everyone.
- **Delete Files…** permanently deletes remaining files after confirmation.
- On a local-only computer, dragging a removed file back into Flower also restores it.

## Changing songs

### Editing tags and covers

Open **Track Info** from a song menu, or press `Cmd/Ctrl+I` on desktop. Edit the title, artist, album, other metadata, or cover art. Changes are written to the music file.

| Setup | Where edits apply |
|---|---|
| Local only | Your local file |
| Owner's device | The server and other paired devices, including downloaded copies. You can edit streamed songs; the latest edit wins. |
| Listener's device | Your own files only |

- Artwork is stored in songs by default. Turn on **Save covers in the album's folder** to save a separate image instead.
- **Find online** looks for artwork. You can adjust search words without changing song information.
- On desktop, you can drop an image onto the cover at the top of Track Info or onto the **Artwork** tab. A new or removed cover is written when you click **Save**, like the other fields; **Cancel** leaves the files unchanged.
- You can't edit tags in the browser player yet.

### Editing in another program

Flower detects changed tags on its next scan. On an owner's device, the edited file replaces the server's copy and copies on other devices. A listener's separately added files are never replaced by the owner's edits.

### Renaming and moving files

Editing a song in Flower **does not rename its file**.

| Change outside Flower | Result |
|---|---|
| Move or rename a file without changing its tags | Play history and playlists stay linked. Moves on an owner's device also sync to the server. |
| Move a file and change its title, artist, or album at the same time | Flower may see it as a new song, breaking the link to its history and creating a duplicate on a server. |
| Rename a file without title, artist, or album tags | Flower treats it as a new song. |

To keep the song's history, move the file, run **Settings → Library → Rescan files**, and *then* edit its tags. Music.app's *Keep Music Media folder organised* can perform both changes together, so be careful when editing there.

### Replacing a file

| Replacement | What to do |
|---|---|
| Same format, such as a better MP3 rip | Replace the file at the same path and name. Its history stays; on an owner's device, the new file syncs to the server and other devices. |
| Different format on a local-only device, such as MP3 to FLAC | Replace the old file. To keep its history, the title, artist, album, and duration (to the second) must match. |
| Different format on a paired device | Not supported yet. |

**By default, automatic uploads and replacement downloads wait until you're home.** Enable **Transfer songs when away from home** to allow them over remote or cellular connections. Tag edits, artwork, moves, and removals sync on any connection. Streaming and manual downloads already work remotely.

## Duplicate songs

Flower flags songs with the same **title, artist, album, and duration to the second**. Select **Show Duplicates** in the banner to compare file paths and remove unwanted copies.

While duplicates exist, the **Duplicates** page appears in the desktop sidebar or phone **Home** menu. It's available to local-only and administrator devices, not listeners.

## How Flower recognises a song

Flower first checks the file path. For a moved file or a match against the server, it compares **title, artist, album, and duration to the second**. File format, artwork, genre, and year don't affect the match.

Copies in different folders can remain separate library entries. Flower also keeps records of removed songs, so a returning file may recover its original **Date Added**, play counts, star rating, and playlist entries. On administrator devices, plays and stars sync; **Date Added** uses the earliest known date.
