# Privacy

Worklapse records your screen so that you can look back at your own day. Everything it records stays on your computer,
in folders you can open, move and delete.

## Nothing leaves your computer

Worklapse has no account, no telemetry, no analytics, no crash reporting service and no update check. It makes no
network connections at all. (On Linux it talks to your own desktop session over local sockets and D-Bus, to take
screenshots and show notifications.) You can check this in the source code.

## What Worklapse records

At each capture (every 10 seconds by default), while you're at the computer:

- **Pictures of the screen**: the monitors you chose, or only the active window, stored as video or as JPEG/WebP images.
- **The active window's title**, and the program it belongs to (its name and file path).
- **The website**, for Firefox, Chrome and Edge: the site's address without its path (for example `github.com`), read
  from the browser's address bar. Private and incognito windows give no site; their window title is still recorded.
- **Whether you were idle**, and how your monitors were arranged.

Worklapse records nothing while recording is paused, while the screen is locked (unless you turn that off), while you're
idle, or while an app or site you've excluded (Settings ▸ Applications) has the focus.

## Where it's kept

| What | Where (Windows) | Where (Linux) |
|---|---|---|
| Recordings and activity | the history folder in Settings (default `%LOCALAPPDATA%\Worklapse`) | the history folder in Settings (default `~/.local/share/Worklapse`) |
| Settings | `%APPDATA%\Worklapse\settings.json` | `~/.config/Worklapse/settings.json` |
| Log of errors | `%APPDATA%\Worklapse\Logs` | `~/.config/Worklapse/Logs` |

The log holds error messages and the times Worklapse started and stopped. Error messages can include file paths. It
holds no pictures or window titles.

Anyone who can read these folders can see what was recorded. Your operating system's disk encryption (BitLocker,
LUKS) protects them when the computer is off.

## Deleting

Deleting a range of time in Day review removes its pictures from disk and its captures (window titles included) from
the database. A program or site recorded nowhere else goes too, unless you've marked it productive, excluded it or
linked it to a project. Pictures imported from TimeSnapper are deleted from TimeSnapper's folder too.

Deleted files aren't overwritten, so forensic tools might still recover them from an unlocked drive. Overwriting isn't
reliable on SSDs. To remove everything, delete the history folder.

## TimeSnapper import

Importing TimeSnapper's history reads a temporary copy of its database, deleted afterwards, and leaves TimeSnapper's
pictures where they are.
