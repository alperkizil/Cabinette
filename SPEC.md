# Cabinette — Specification (draft for approval)

Status: **Stages 1, 2, 3 and 4 are built** (see section 13; stage 4, the MCP server, on 2026-10-24), plus the additions of 2026-10-22: drag-and-drop animations with an upload ring, sort by type, Move to category, Starred files, the Ctrl+K quick switcher and Clean My Downloads mode. The desktop check of the AI path with you and the test of the installed build both passed on 2026-10-24. Next: GitHub.
Items marked **[my default]** are places where your answers left room and I picked a default. You accepted all of them on 2026-10-24 and their marks were removed (git history shows where they were), so any mark you see now is newer and still waits for your review. Change any of them at any time.

---

## 1. What Cabinette is

A personal Windows file organizer laid out like a chat app. The left pane lists **categories** the way a chat app lists chats. Opening a category shows its files as a **thread** of bubbles.

Files can be stored in two modes:

- **Copy mode:** the file is copied into a **vault** folder you choose. The original stays where it was.
- **Pointer mode:** the file stays where it already is on disk, and Cabinette only references it.

A tray icon watches the vault in real time, the way Google Drive watches its folder. Pointer files are not watched.

- **Name:** Cabinette. The repo folder stays `Cabinnette`.
- **License:** GPL-3.0. It will be published on GitHub.
- **Platform:** Windows 10 and 11, x64 only.
- **UI language:** English. All strings live in resource files, so adding a language means translating one file.

## 2. Technology

| Area | Choice |
|---|---|
| Language / runtime | C# on .NET 10 |
| UI | WPF with custom chat-style templates, a Mica backdrop on Windows 11 and a solid background on Windows 10 |
| MVVM | CommunityToolkit.Mvvm |
| Database | SQLite (Microsoft.Data.Sqlite + Dapper), with FTS5 trigram search for fast substring matching |
| Hashing | XXH128 (System.IO.Hashing) |
| Icons | Fluent UI System Icons (MIT, bundled via WPF-UI) |
| Tray | H.NotifyIcon.Wpf |
| Installer | Inno Setup 7, built from the `Tools.InnoSetup` NuGet package so nothing needs installing on the PC |
| AI API (stage 4) | MCP server using the official C# MCP SDK |
| Tests | xUnit |

Every dependency uses a permissive license (MIT or Apache-2.0), all compatible with GPL-3.0.

There is no code-signing certificate, so Windows SmartScreen will show "unknown publisher" the first time the installer runs.

### Solution layout
```
Cabinnette/
  src/
    Cabinette.Core/        domain models, SQLite repository, library services, categorization,
                           vault ops, hashing, backup/migrate, IPC protocol (no UI)
    Cabinette.Windows/     Win32/Shell interop: classic shell context menu, thumbnails,
                           Recycle Bin, clipboard file-drop, startup registration
    Cabinette.App/         WPF app: windows, views, themes, tray, single-instance + named-pipe server
    Cabinette.Ipc/         (stage 4) the named pipe's names, line format and messages, shared with the MCP server
    Cabinette.Mcp/         (stage 4) stdio MCP server → talks to the running app over the named pipe
  tests/
    Cabinette.Core.Tests/
  installer/
    Cabinette.iss          (stage 3; registers the Explorer entries and their COM drop targets)
  LICENSE                  GPL-3.0
  README.md
```

### Where data lives
| What | Where |
|---|---|
| Database | `%LocalAppData%\Cabinette\cabinette.db` |
| Thumbnail cache | `%LocalAppData%\Cabinette\thumbs\` (can be rebuilt any time). Kept under 500 MB: two minutes after startup, entries unused for 90 days are removed, and the oldest go first until it's under 400 MB |
| Category icons | Stored as PNG data inside the database, so backups include them |
| Vault | Any folder you choose on first run. Only one vault. If the folder isn't empty, a warning explains that its subfolders become categories and its loose files are moved into type folders, and offers "Choose another folder" or "Use this folder anyway". A folder that contains Cabinette's data folder (`%LocalAppData%`, the whole user profile, the root of that drive) is refused, with no "use it anyway" |

---

## 3. Categories (= threads)

- **Fields:** PNG icon, name, creation date, last file upload. Subcategories have exactly the same fields and features.
- **Starting categories:** Audio, Video, Images, Text, Documents, Spreadsheets, Presentations, Code, Executable, Archives, E-books, Fonts, Design, 3D models, Disk images, Notes, Links, Other.
  - Code has these subcategories: Python, Java, C#, C/C++, JS/TS, Web, Shell.
  - "Text" means plain text. Word and PDF files go to Documents.
  - The full extension mapping is in Appendix A.
- **Deleted:** a system category that acts as a trash for files whose category was deleted. You can move items out of it, and "Empty Deleted" sends its contents to the Recycle Bin.
  - Files can't be added to Deleted directly: its thread has no message box or paperclip, and it doesn't accept drops or pastes.
- **Editing:** any category, built-in ones included, can be renamed or deleted, and new ones can be created at any level.
  - If a folder with the category's name already holds files that aren't in the library, the new category gets a numbered folder (`Name (1)`) instead of taking it over.
- **Deleting a category:** a setting, defaulting to *move its files to Deleted*. The other choices are *delete the files (Recycle Bin)* and *ask each time*.
  - The dialog spells out what deleting the files means, including the real files of linked (pointer) items.
  - Only files that are in the library are moved or deleted. Anything else in the category's folder stays, and so does the folder.
- **Icons:** the default icons are a colored circle with a white Fluent icon, saved as PNG. You can replace any icon with your own PNG of any size; it gets scaled.
- **Subcategory navigation:** clicking a parent opens its subcategory list in the left pane, with a back arrow. The first entry, "All in <Parent>", opens the parent's thread.
- **A parent's thread shows its own files plus the files of all its subcategories.**
- **Chat list:**
  - Sorting can be switched between last upload (the default) and A–Z.
  - With last upload, empty categories (never used, or whose files have all left) go to the bottom, A–Z, and show no time. A category that still has files sorts by, and shows the time of, its newest remaining file (when that file arrived there; a move or "Add to category" counts as arriving), so it falls back when its newest file leaves (decided 2026-10-27, after the user found a category left on top with only older files; from 2026-10-24 it kept its place).
  - Categories can be pinned to the top.
  - Unread badges count new files you haven't seen yet.
- **What counts as unread:** files added since you last opened that thread by anything other than you in the app. That means the Explorer menu, the vault monitor or the AI.

### Files in more than one category
- A file can belong to several categories. Its hash detects duplicates.
- A copy-mode file physically lives in one **home** folder in the vault: the category it was first added to. Its other categories link to it in the database (option *a*).
- **Deleting a file's home category:** the file is physically moved to its next remaining category. If it has no other category, it goes to Deleted.

---

## 4. Thread view (optimized for files)

- **Header: a banner** (decided 2026-10-24, after Windows Longhorn's folder header). One thin band, about 64 px, in place of the old header line:
  - Its color is the category's own avatar color (the same palette as the avatars). A category with your own picture as its icon uses the theme's banner color instead.
  - It shows the category name, and below it the file count and the total size of its files ("14 files · 11 MB"), in the same text sizes as the rest of the app's headers (decided 2026-10-24 after the first screenshots: large type looked out of place).
  - A large, faint picture of what the category holds sits on it: a film strip for Video, photos for Images, notes for Audio, and so on for every built-in category **[my default]**. A category you made shows its own icon there.
  - Search, filter, view toggle and the subcategory list stay on the right of the band.
  - Starred and Deleted have bands too, in their own colors **[my default]**.
  - The text on the band is white. The palette only guarantees 3:1 against white, so a band whose color gives the second line less than 4.5:1 is darkened just enough (same hue; the avatar keeps its color) **[my default]**.
- **View toggle:**
  - **Chat** (default): bubbles under date headers ("Today", "Yesterday", "Monday", "05/11/2026").
    - **Copy-mode files sit on the right** in a warm amber bubble; **pointer files sit on the left** in a soft blue bubble, slate blue in dark mode (decided 2026-10-27, for a look of Cabinette's own; green and blue from 2026-10-24; before, wheat and white). Chat bubbles and the Gallery's frames get these colors (the frames since the user saw the first Gallery screenshots); Grid and Details stay as they are.
    - **Bubbles have all four corners rounded and no tail** (decided 2026-10-27; before, the first bubble of a run had a tail). The first bubble of a run still gets a little more space above it.
    - **Tacks** (decided 2026-10-27, the user's idea, for fun and an identity of Cabinette's own): every chat bubble, albums included, is pinned at its outer top corner with a push pin Cabinette draws itself, in one of eight colors (red, blue, yellow, green, pink, orange, white, black). The color is picked from the file's id, so a file keeps its color, and any five files added one after another have five different colors. Copies get it on the top-right corner, pointers on the top-left, mirrored so the needle points into the bubble. It takes no clicks. Grid, Gallery and Details have none. Bubbles have a little more space above them for it. The exact colors and the outer corner are **[my default]**.
  - **Grid:** large thumbnails, gallery-style.
  - **Gallery:** only in Images and its subcategories (see "Gallery" below).
  - **Details:** sortable columns for name, type, size, date added, mode and tags.
- **Bubble contents:** thumbnail or type icon, file name, size, type and tags. Pointer bubbles have a small link marker, and a "missing" badge when the file can't be found.
- **Album bubbles:** images or videos added together in one batch collapse into a single 2×2 album bubble showing "+N".
- **Previews:**
  - Images, video, PDF and Office files use Windows' own thumbnails, the same ones Explorer shows.
  - Audio has an inline play button with a progress bar, like voice notes.
  - Video shows a thumbnail and its duration. Clicking it plays the video in a built-in viewer, which also has "Open in default player".
  - Notes show their first few lines.
- **Multi-select** supports bulk tag, copy, delete, star, move to category, add to category and remove from category.
- **Starred files** (decided 2026-10-22):
  - "Star" and "Unstar" are in the right-click menu and the multi-select actions. Starred bubbles show a small star.
  - A **"Starred" row at the top of the chat list**, above pinned categories, appears while at least one file is starred. It opens a thread of every starred file from every category.
    - Ordered by when they were starred, the newest at the bottom like a chat.
    - It's a view, like Deleted: no message box, paperclip or drops, and no "Move to category…" (there is no category to move out of). The other actions work.
  - Stars stay through moves and renames, and are stored in the library database, so backups keep them. Deleting a file removes its star.
  - Starring a file that already is starred keeps its first star time. In the Starred view the date headers, the bubble times and the details view's date column ("Starred") show when files were starred.
  - The Starred view shows one bubble per file, without albums. In a normal thread, an album shows one star in its footer if any of its photos is starred.
  - The Starred row shows the count and the time of the latest star. It stays first with A–Z sort too, has no badge or pin, takes no drops, and its menu only opens it.
  - The Starred view also hides "Remove from this category". Delete there always deletes everywhere, after asking.
  - Files in Deleted keep their star and still show in Starred. When the last star goes, the row disappears; an open Starred view stays, saying "No starred files".
  - Starring shows no notice, and the selection stays.
  - Stars live in their own table, without raising the database version, so earlier builds (and the prototype) still open the library and its backups.
- **Gallery** (decided 2026-10-24, after Windows Longhorn's photo gallery):
  - **Where:** the Images category (found by its built-in identity, so renaming it doesn't matter), its subcategories and their "All in" rows. They open in Gallery; you can switch views, and the choice is remembered per category while the app runs, as for the other views.
  - **Cells are all the same size.** Each photo is fitted inside its cell, keeping its shape and never cropped, in a thin frame with a soft shadow, like a print. The frame shows the file's mode: amber for copies, blue for pointers (decided 2026-10-24; amber instead of green since 2026-10-27), a little stronger than the chat bubbles' shades so a thin frame still shows its color, and muted in dark mode so a page of prints doesn't glare. Photos sit on a common bottom line **[my default]**, and the name is centered below on up to two lines, then "…". The tooltip has the full name, size, date added and date taken.
  - Videos show their framed thumbnail with a play mark and their length. Other files (a PDF in Images) show their type icon without a frame. Star, pointer and "missing" markers are as in Grid.
  - **Sort header** above the cells: Name, Size, Type and Date. Clicking one sorts by it, clicking again reverses. Date offers **Date added** and **Date taken**. The default is Date added, newest first. The choice is remembered per category while the app runs.
  - **Date sections:** files are always separated by the chosen date, under day headers ("Today", "Yesterday", "Monday", "5 November 2026"). Sorting by Name, Size or Type sorts within each section **[my default]**.
  - **Date taken** is read from the photo's or video's own data through the Windows property system, without decoding the image. A file without one (screenshots, most downloads) uses its last-modified date. It is stored in the library database, in its own table without raising the database version (like stars), so backups keep it. It is read in the background when a file is added, once for the files already in the library, and again when a pointer file changes **[my default]**.
  - **Three cell sizes** (about 96, 128 and 176 px), changed with Ctrl+mouse wheel and remembered across restarts **[my default]**.
  - Everything else works as in Grid: double-click opens, right-click shows the Windows menu, Ctrl/Shift-click and the selection bar, dragging in and out, search and filters. The arrow keys move between cells **[my default]**.
  - Because every cell has the same size, the layout follows from the number of files alone: nothing has to be measured, nothing moves while you scroll, and only the rows on screen are drawn.
- **Performance:** lists and grids only draw the rows on screen, and thumbnails load in the background and are cached. A thread with tens of thousands of files stays smooth.
- **Message box:** typing and pressing Enter creates a **note**. It is saved as `Note YYYY-MM-DD HH-mm-ss.md` in that thread's vault folder.
  - The button next to the box is a gold **Save note** button (decided 2026-10-27, instead of a round send arrow: a note is saved, not sent).

---

## 5. Adding files

| Method | Where it goes |
|---|---|
| Drop onto an open thread | That thread; in a built-in type category, known files of another type go to their own type category (see below) |
| Drop onto a category row in the chat list | That category, with the same type rule |
| Drop onto the chat list background or main window | Auto-categorized by extension |
| Paperclip button in a thread (multi-select file dialog) | That thread, with the same type rule |
| "+" button in the main window (multi-select file dialog) | Auto-categorized |
| Explorer: "Copy to Cabinette" / "Add to Cabinette (Pointer)" | Auto-categorized |
| AI (MCP) | The category it names, or auto-categorized |

- **Mode:** decided by a setting, *default add mode*, which defaults to Copy. The Explorer menu has an entry for each mode.
- **Copy mode** copies the file into the home category's vault folder. The original stays where it was. The copy keeps the Mark of the Web (the "from the internet" mark SmartScreen and Office's Protected View rely on) and is written to disk before the add is reported.
  - The copy is written to a hidden staging folder in the vault (`.cabinette-tmp`) first and moved into the category folder once it is complete, so a category can be renamed or deleted while files are copied into it.
  - If the vault is moved while a file is being copied, the file is copied again, into the new vault. If its original is gone by then, that file fails with "Add it again".
- **Auto-categorizing** happens when no thread was chosen. The extension mapping (Appendix A) is built in and not editable.
- **Type categories sort by type** (decided 2026-10-22; setting *Sort files by type in type categories*, **on** by default):
  - In a **built-in type category** (Images, Spreadsheets, Code › Java, Other and so on), files of a **known type that belongs elsewhere** go to their own type category. An image dropped on Spreadsheets goes to Images; a `.py` dropped on Java goes to Code › Python.
  - **Your own categories** (Work, Invoices, Travel 2026, and subcategories you create inside a built-in one, like Images › Holidays) keep whatever you drop or pick.
  - Renamed built-in categories still count as type categories.
  - It applies to drops and the paperclip file dialog. Unknown types, folders, pastes and notes stay in the thread.
  - If every file went to one other category, that thread opens. If they were split, you stay, and a notice says where they went, with "Show".
  - With the setting off, a thread keeps whatever is dropped on it (the stage 1 rule).
  - **Notes keeps text files** (Text's types, such as `.md` and `.txt`) dropped on it: markdown notes belong in Notes (decided 2026-10-22). Other types dropped on Notes still go to their own category.
  - The most specific type wins, as in auto-categorizing (`.json` dropped on Code › Java goes to Code). A type category you deleted is re-created when a file needs it.
  - A file whose content is already in Cabinette joins its type category, and the notice says so. If it already was there, nothing is "sorted", and only the usual duplicate notice shows.
  - The thread opens only when every file went to one category that the drop target's thread doesn't show. Files sorted into a subcategory of the thread you're in stay visible, so you stay, with a notice.
  - **Bubbles dragged out of a thread** onto a chat-list row are added to that category, like "Add to category…", and aren't sorted: the row is where you put them. The list background doesn't take them.

### Clean My Downloads mode (decided 2026-10-22)
A setting, **off** by default: Cabinette keeps your Downloads folder empty by moving new downloads into the vault.
- It watches the Windows **Downloads** folder (wherever you moved it), top level only. Subfolders and folders that appear there are left alone.
- Only files that arrive after you switch it on. Files that arrive while Cabinette is closed are handled at its next start.
- A download moves once it is complete and has been **untouched for 1 minute**, so the browser's "Open" right after downloading still works. It waits while the file is open in another program. Partial and temporary files (`.crdownload`, `.part`, `.tmp` and so on) are ignored, like in the vault monitor.
- **Moving** means: copied into the vault and sorted by type (like a drop onto the main window), checked, then the original goes to the **Recycle Bin**.
  - If its content is already in Cabinette, the original still goes to the Recycle Bin, since the vault has it.
  - If the original can't go to the Recycle Bin, it stays, and a notice says so. It is never deleted permanently.
- New files count as unread and get the usual grouped toast ("3 files from Downloads added to Images"), covered by the notifications setting.
- "Pause monitoring" in the tray pauses it too.
- Dev runs never watch the real Downloads folder. Developers point a `--data-dir` run at a test folder with `--downloads-dir`.
- **Which files count as new:**
  - Judged by the file's creation time, compared with when you switched the mode on.
  - Files copied in from another drive, or extracted there, count as new and are moved too (decided 2026-10-22).
  - A file moved in from the same drive keeps its old creation time and is left alone.
  - If Downloads itself moves to another folder, the mode starts afresh there.
- **Waiting:**
  - "Untouched" counts from the latest write or change Cabinette saw. Files that arrived while Cabinette was closed move at its next start.
  - Partial downloads are recognized by their names: `.crdownload`, `.part`, `.partial`, `.download`, `.opdownload`, `.!qB`, `.!ut`, `.!bt`, `.bc!`, `.az!`, `.fdmdownload`, `.jc!`, `.td`. A file also waits while a `name.part`, `name.crdownload` or `name.aria2` sits next to it.
  - A partial download left untouched for a day stops holding the others up.
  - Hidden and system files, links and cloud placeholders are skipped.
  - Many viewers don't keep a file open, so a file you are only looking at can still move after the minute (the setting's hint says so).
- **The checks before an original goes to the Recycle Bin:**
  - The vault copy must be there with the right size, and written to disk. For a file whose content was already in Cabinette, the vault file is read back and must still have that content.
  - The original must still match. If it changed during the copy, the half copy goes to the Recycle Bin, and the file waits for another quiet minute.
  - Files ready at the same time are added together, as one batch with one notification.
- **When something goes wrong**:
  - A duplicate of a linked (pointer) item keeps the original, since the vault has no copy of it.
  - If the Recycle Bin refuses a file, it stays, with a notice. Other Recycle Bin hiccups are retried.
  - A vault copy that fails its check is retried with growing pauses. After 3 attempts the file stays, with a notice.
  - It pauses whenever the vault monitor does (Pause, a vault move, "Vault not found").
  - It doesn't run if Downloads is the vault or inside it.
  - Switching the mode on asks once per library.
  - Its toasts are titled "Clean My Downloads". Files left in place and failures always show.
- **The Mark of the Web is kept:** browsers mark downloads as coming from the internet, and SmartScreen and Office's Protected View rely on that mark. Every copy into the vault keeps it, from any add, Move vault or Migrate. A backup `.zip` can't hold it, so restored files lose it.
- **Duplicates:**
  - When a file is added, its hash is computed. Large pointer files are hashed in the background.
  - If identical content is already in the library, no second copy is stored. If it was added to a different category, it is also added to that category.
  - A notice says "Already in Cabinette (in: Images)".
  - A vault file only counts as already there if it really is at its place on disk (checked while the vault folder can be reached). A file deleted while monitoring was paused is therefore "found again", not a duplicate.
  - A pointer file only counts as already there while it still has the size and date stored with its hash. If it was edited, it is hashed again: files up to 16 MB right away, larger ones in the background.
  - **Found again:** if the matching item's file is missing (moved or deleted), the add re-links that item instead of reporting a duplicate. It keeps its tags, categories and place in the thread, and joins the target category.
    - In copy mode the file is copied into the item's home folder and the item becomes a vault file, like migrating does.
    - Pastes and the vault monitor don't re-link; they add a new item.
- **Name clash** with a different file in the same vault folder: the new file becomes `name (1).ext`.
- **Adding a folder:** the folder becomes its own category, named after the folder. Two settings control the details:
  - *Subfolders become subcategories:* **on** by default.
  - *Also add files to their type categories (Audio, Images and so on):* **off** by default.

### Pasting (Ctrl+V)
Pasted items always become files inside the vault.

| Pasted content | Becomes |
|---|---|
| Text | `Pasted text YYYY-MM-DD HH-mm-ss.md` |
| Image | `Pasted image YYYY-MM-DD HH-mm-ss.png` |
| Text that is a single web address | `Link YYYY-MM-DD HH-mm-ss.url`, a web shortcut that opens in your browser |
| Files copied in Explorer | Added as vault copies, like a drop |

- Pasting **inside a thread** adds the item to that thread.
- Pasting **in the main window** opens a dialog asking which category to use. It pre-selects Notes for text, Images for images and Links for web addresses.

---

## 6. Actions on files

- **Open:** double-click opens the file in its default app.
- **Missing pointer files:** when you try to open one that has moved or been deleted, it is marked **missing** and a "Locate…" dialog appears.
  - Choosing the new location re-links it. If the chosen file's hash differs from the original, the app warns you.
  - A "Missing files" filter lists all missing items.
- **Right-click** shows the **real Windows classic shell menu** (Open with, Send to, 7-Zip and so on) with Cabinette's own items at the top: Show in folder, Rename, Copy, Tags…, Move to category…, Add to category…, Remove from this category, Delete.
- **Move to category…** (decided 2026-10-22) moves the selected files **from the thread you're in** to the category you pick; their other categories stay.
  - In a parent's thread ("All in Code"), it moves them out of the parent and its subcategories.
  - A copy-mode file whose home is the category it leaves is **physically moved** into the new category's vault folder, so the vault folders keep matching the categories (`name (1).ext` on a name clash). Pointer files only change category.
  - You moved them yourself, so nothing turns unread. You stay in the thread, and a notice "Moved 3 files to Images" offers "Show".
  - Moving out of Deleted works the same way. If a file's copy is gone from the Deleted folder, both Move and Add to category refuse ("could not be found").
  - Choosing the thread you're in does nothing. From a parent's thread into one of its own subcategories re-files the files there.
  - A vault file that is missing, or held open by a program without letting others move it (an editor), fails the whole move, and nothing changes. A file another program only reads (the vault monitor, a viewer) moves fine, like a rename.
  - The target's "last upload" is updated, as with Add to category, so it moves up a list sorted by last upload. Moving into Deleted isn't offered.
  - When the selection bar is too narrow for all its actions, they show as icons only; their tooltips keep the names. Where even that is too wide (extra-large text, a wide font, the narrowest window), "3 selected" becomes "3", "Select all" becomes "All" (its tooltip keeps the whole text), and last the icon pills get narrower, so every action stays whole **[my default]**.
  - The menu's own Delete and Rename are routed through Cabinette, so the library stays in sync.
  - The menu's **Cut** is removed, because pasting it elsewhere would move the file out of the library. Copy stays.
- **Rename:**
  - Copy mode renames the file in the vault.
  - Pointer mode **renames the real file on disk**.
- **Copy** puts the file on the clipboard so it can be pasted in Explorer.
- **Delete:** if the file is in several categories, you choose between "Remove from this category" and "Delete everywhere". Both behaviors below are settings:
  - *Vault files:* Recycle Bin by default, or permanent.
  - *Pointer files:* by default the real file on disk is **also deleted, to the Recycle Bin**. The alternative is to only remove it from the library.
- **Can't use the Recycle Bin** (network shares, USB sticks, or a file larger than its drive's Recycle Bin size limit): Cabinette asks "Delete permanently?" before anything changes, and deletes permanently only if you confirm. The same applies to Empty Deleted, category deletes and the old vault after a move.
  - Rarely, Windows refuses a file only after the delete went through. Then "Delete permanently" deletes exactly the refused files. "No" keeps them on disk, but their library entries are already gone: a vault file comes back as a new item without its tags at the next rescan.
- **A file in use** by another program stops the whole delete, and nothing changes.

---

## 7. Search, filters, tags

- **Main window search** covers category names plus **file names across the whole library**. Results come back in two sections, Categories and Files.
- **Search inside a thread** covers only that thread's file names and note text, including its subcategories.
- **Tags:**
  - Tags apply to files only. You add them from the right-click menu or in bulk with multi-select.
  - Tags show as chips on bubbles.
  - Searching `#tag` returns the tagged files. Several tags, as in `#invoice #2026`, return files that have **all** of them.
  - Tags work in both main and thread search, and can be combined with text, as in `#invoice march`.
- **Filters** in the main window and in threads: type, date added (range), size, copy/pointer, missing, extension.
  - The Notes type covers notes typed or pasted in Cabinette; they don't count as Text. Pasted links count as Links and pasted images as Images.
  - The extension box accepts `pdf`, `.pdf` or `*.pdf`, several separated by spaces or commas.
- Searching inside the contents of files is **not in this release**.
- **Quick switcher, Ctrl+K** (decided 2026-10-22):
  - Opens a small search box over the window that finds categories and files by name, the same way as the main search, and shows the best matches as you type. Arrow keys choose, Esc closes.
  - **Enter on a category** opens its thread. **Enter on a file** opens its thread, scrolled to the file and highlighted. **Shift+Enter** opens the file in its default app.
  - An empty box lists the recently opened categories.
  - It works from anywhere in the window, also while typing a note, but not while a dialog is open.
  - **Results**:
    - One list of at most 10, updated 150 ms after you stop typing.
    - Order: exact names, then names that start with the text, then names with a word that starts with it, then any match. Categories come first on a tie.
    - In a big library it also finds older exact and starts-with matches that the main search's first 500 would miss.
    - Pressing Enter before the list catches up takes the best match. The arrow keys stop at the ends.
  - **Recently opened** shows the last 8 categories, leaving out the one that is open.
  - **Esc** closes it, even with text typed.
  - **Opening a file**:
    - A file hidden by the thread's search or filter clears them, then the thread scrolls to the file and flashes it.
    - A photo behind an album's "+N" tile flashes that tile.
  - Opening any dialog closes the filter popups.

---

## 8. Vault monitor and tray (stage 2)

- **Watching:** the vault is watched continuously. Changes made outside Cabinette update the library:
  - A new file is added to the category that matches its folder.
  - A deleted file is removed from the library.
  - A renamed file is updated. A file moved to another category folder keeps its tags and moves to that category.
  - A modified file gets its size, date and hash updated.
  - A new folder becomes a new category, and a subfolder becomes a subcategory.
  - A renamed or deleted folder renames or deletes its category.
  - A vault file marked missing (for example opened while its drive was unplugged) loses the mark once the monitor finds it in place.
- **Files placed directly in the vault's top folder** (not inside a category folder) are moved into their type category, as if dropped onto the main window.
- **A file copied into the vault whose content is already in the library** stays as its own item, and the notification says it's a duplicate. The monitor never deletes your files on its own.
- **Vault unavailable** (for example a disconnected drive): nothing is removed from the library. The tray shows "Vault not found" and Cabinette retries every 30 seconds.
- **Ignored files:** temporary and system files (`~$*`, `*.tmp`, `*.part`, `*.crdownload`, `desktop.ini`, `Thumbs.db`, hidden/system files). Files still being written are picked up once they're complete.
  - Cabinette's own `.cabinette-*` files and its staging folder `.cabinette-tmp` are ignored too, and no category folder gets a name starting with `.cabinette-`.
  - Temp files an interrupted copy left behind (the app quit or crashed mid-copy) are deleted at the next start: in the staging folder when the library opens, and older ones in category folders by the startup rescan.
- **Startup rescan:** on startup the vault is rescanned to catch changes made while the app was closed.
- **Pause / resume:** while paused, nothing is applied; resuming rescans the vault. A pause isn't remembered after a restart.
- **Pointer files are never watched.**
- **Single instance:** only one Cabinette runs at a time. Opening it again brings the existing window to the front, even when it's only in the tray. It talks to the running copy over a local channel only your Windows account can reach; stage 3's Explorer menu falls back to it (section 9).
- **Tray icon:** its status is idle, syncing, paused or error ("Vault not found"). Clicking it opens the window. The menu has Open Cabinette, Open vault folder, Pause/Resume monitoring and Quit.
- **Start with Windows:** on by default, as a setting. Cabinette then **starts quietly in the tray** (no window).
- **Closing the window** sends the app to the tray. This is a setting. The first time, a notification explains that Cabinette is still running.
- **Notifications** appear on vault changes and are grouped, for example "3 files added to Images". They can be switched off.
- **App icon:** Cabinette gets its own icon (taskbar, title bar, tray, and later the installer).

### Vault move, migrate, backup, restore (stage 2)
- **Move vault** (Settings › Library):
  - Monitoring pauses. Everything is copied to the new, empty location and checked by size and hash, then the library switches to it.
  - Before switching, the old vault is checked again: files other programs added, changed or deleted meanwhile are brought over too. This repeats up to 3 times; if the vault keeps changing, the move stops, names a file and nothing changes.
  - Hidden and system folders keep those flags. Open threads, the chat list and search reload with the new paths.
  - **The old vault is kept.** A message offers "Send old vault to Recycle Bin". If files in the old vault were written or created after the switch, they are listed first, with "Recycle anyway" or "Keep old vault".
- **Cancelling** Move vault or Restore, or a failure, removes only the files and folders the operation created. Anything else in the chosen folder stays.
- **Migrate:** every pointer file is copied into its category's vault folder and becomes a copy-mode item. The originals stay on disk. Missing pointer files are skipped and listed.
- **Portable backup:** a single `.zip` holding a database snapshot, the vault and a manifest. The flow offers to migrate first, so the backup holds everything.
  - Each file and folder keeps its hidden, system and read-only flags, and Restore puts them back. Backups made before 2026-10-21 don't have them and restore as before.
- **Restore** works on another PC, from the **first-run screen** ("Restore from a backup") and from **Settings › Library**:
  - Choose the `.zip`, then choose an empty folder for the new vault. Paths are rewritten automatically.
  - Restoring over an existing library first saves the current database as a `.bak` file next to it, then Cabinette restarts.
  - A backup whose library database is damaged or was edited (folder or file names that would point outside the vault, a broken category tree) is refused, and the previous library is put back. Pointer paths and settings in a backup are taken as they are.

---
## 9. Explorer integration and installer (stage 3)

- **Installer:** a single `Cabinette-Setup.exe` built with Inno Setup 7 (rather than 6).
  - It installs to `C:\Program Files\Cabinette` **for all users**, with a Start Menu shortcut and an uninstaller. Publisher: **alperkizil**, linking to https://github.com/alperkizil (the repository once it exists).
  - It looks like a modern Windows wizard and follows dark mode. It has no license page; the GPL is installed as `LICENSE.txt` next to the app.
  - It is framework-dependent: if the .NET 10 Desktop Runtime is missing, it downloads the latest 10.x from Microsoft (about 60 MB, shown on the Ready page) and installs it quietly. Preview runtimes count as missing.
    - The download only runs if it carries a valid Microsoft signature; otherwise setup stops and links to Microsoft's download page.
  - **A running Cabinette is closed** before installing or uninstalling. The uninstaller asks first. While a vault move, migrate, backup or restore runs, Cabinette refuses to close, and setup offers Retry (a Windows sign-out is refused the same way).
  - After an upgrade Cabinette isn't restarted. The last page offers "Launch Cabinette" (ticked), which starts it at your normal level, never as administrator.
  - The installer never writes "start with Windows" itself (it runs as administrator, so it could land in the wrong account). The installed Cabinette updates the entry to its own path the first time it starts, so it takes over from an earlier copy, with the same library and vault.
  - Uninstalling removes the Explorer entries and the "start with Windows" entry of the account that uninstalls, if it points at the installed copy. It leaves your vault and database alone.
  - Setup and the uninstaller always write a log to `%TEMP%`, and the debug symbols are installed so the app's own log shows line numbers. On Arm64 Windows 11 it installs the x64 build (untested).
- **Explorer right-click entries**, using the classic menu: **"Copy to Cabinette"** and **"Add to Cabinette (Pointer)"**.
  - They appear on files, folders and mixed selections, including in 32-bit programs' file dialogs. A folder works like dropping it: it becomes a category.
  - On Windows 11 they are under "Show more options", or Shift+right-click.
  - They add like a drop onto the main window (auto-categorized), count as unread, and show the usual notification ("3 files added to Images").
- **Multi-select:** Explorer hands the whole selection to Cabinette in one call, as one batch, however many items are selected. (Windows registers the entries as a COM drop target, which has no item limit. Plain command-line entries would vanish above 100 selected items and start one process per file.) Selections that arrive close together are added side by side, like two drops.
- **Notifications:** unless you are looking at Cabinette (its window shown and active), the result is a Windows toast titled with the entry you used, worded like the vault monitor's. Clicking it opens Cabinette, at the category when all files went to one. The "Notifications on vault changes" setting covers these toasts too, but a failure is always shown: nothing else would tell you the files didn't arrive.
- **Cabinette not running:** Windows starts it quietly in the tray, it adds the files and shows the usual notification.
- **No vault chosen yet:** the first-run window opens, and the files are added once a vault is chosen. The main window then opens instead of staying in the tray. Closing first run without choosing adds nothing.
  - "Restore from a backup" restarts Cabinette: the waiting selections are kept for the restart and added to the restored library. If the restart doesn't happen within 10 minutes, they are dropped.
- **Single instance** already exists from stage 2. If the running Cabinette can't take the files directly (for example it was started as administrator), the copy Windows starts passes them on over the local channel and exits. If even that fails, it shows a message.
  - A second normal launch while a copy started as administrator runs now brings that copy forward. Before stage 3 it opened a second full copy on the same library.

---

## 10. AI API (stage 4)

- **What it is:** an **MCP server** (`cabinette-mcp.exe`) that Claude Code and Codex can use, built on the official C# MCP SDK (`ModelContextProtocol`, its license checked for GPL compatibility first).
  - You register it once in each. The README gives the exact commands, and Settings › AI shows them ready to copy, with the installed path filled in. The installer doesn't touch Claude's or Codex's configuration.
  - It talks to the running Cabinette over the local channel. If Cabinette isn't running, it starts it in tray mode. A dev build of the MCP server only ever reaches a dev run of Cabinette.
  - Changes appear live in the UI.
- **Decisions confirmed on 2026-10-21:**
  - **Reading contents:** text-like files (txt, md, code, csv, json and so on) as text, up to 256 KB per call with paging. PDF and Office files through Windows' own text extractors where they are installed. Images as images, scaled down. Other binary files are refused with a clear message.
  - **Deletes by an AI** always go to the Recycle Bin, even when your setting says permanent. Where the Recycle Bin can't take a file, the AI gets an error instead of a permanent delete.
  - **Notifications:** AI changes get the usual grouped toast, named after the tool ("Claude Code added 3 files to Images"); your notifications setting covers them.
  - **Vault operations:** move vault and migrate show the same progress dialog as from Settings; backup writes to the path the AI gives. One long operation at a time; otherwise the AI is told Cabinette is busy. Restore isn't offered.
  - The installed build is tested once, at the end of stage 4.
- **Tools:**
  - Search
  - List categories and threads
  - Get file details
  - **Read file contents**
  - Add files (copy or pointer)
  - Create notes
  - Tag and untag
  - Add to or remove from a category
  - Move (as "Move to category…" in section 6), rename and delete files
  - Create, rename and delete categories
  - Move vault, migrate and backup
- **Permissions:** destructive actions and reading file contents are **allowed without confirmation**, as you requested.
- **AI activity log:** every AI action is logged, in the library database (so backups include it), and kept for 1 year. Settings › AI activity shows it, filterable by date and action, with links that open the items. Since AI deletes are allowed without asking, this lets you see what happened.
- **No AI features are built into the app itself.** Only outside agents (Claude Code and Codex) are supported.

### Decided on 2026-10-23 (stage 4 start)
- **Long vault operations** (move vault, migrate, backup) start and answer at once; the AI follows them with a status tool. Cabinette shows its usual progress dialog meanwhile. (Codex gives up on a tool call after 60 s by default, so waiting wouldn't work there.)
- **The activity log has every call**, searches, listings and reads too. The viewer can show "changes only".
- **Extra tools:** list tags, star and unstar, and "show in Cabinette" (opens the window at a file or category, on your desktop).
- **An AI deleting a category** follows your "Deleting a category" setting. When that is "Ask each time", its files move to Deleted. Files that are deleted go to the Recycle Bin.

### How it works (stage 4)
- **Pieces:** `cabinette-mcp.exe` (`src/Cabinette.Mcp`) is a thin translator. Claude Code or Codex start it and talk MCP to it over its standard input and output. It passes each tool call to the running Cabinette over the local channel (`src/Cabinette.Ipc`: the pipe names, the line format, and the `ai` command with one record per tool), and Cabinette does the work through the same library code as its own buttons.
  - The SDK is `ModelContextProtocol` 2.2.0 (Apache-2.0, compatible with the GPL), rather than 2.3.0, released three days before stage 4 started.
  - The local channel only accepts connections from your own Windows account (and none from the network), as before. A program running as you could already change these files directly, so the new commands give nothing new rights.
  - A Cabinette and a `cabinette-mcp.exe` from different versions refuse to talk ("update both").
- **Which Cabinette:** the one that uses your normal data folder. A `--data-dir` passed to `cabinette-mcp.exe` reaches only the dev run with that data folder, and a debug build refuses to run without one, so a dev build can't reach your real library. If Cabinette isn't running, `cabinette-mcp.exe` starts it in the tray (from next to itself; dev builds take `--app <Cabinette.exe>`) and waits up to 30 s for it. Without a vault yet, the AI is told to finish first-run setup in the window that opened.
- **Client names** come from what the client says about itself: Claude Code reports `claude-code` and shows as "Claude Code", Codex (`codex-mcp-client`) shows as "Codex", others under their own name.
- **The MCP server's own rules:**
  - The SDK is its core package (`ModelContextProtocol.Core` 2.2.0), without the hosting extras.
  - It refuses relative `--app` and `--data-dir`, so nothing is ever taken from the AI's project folder.
  - A `--data-dir` naming your real data folder is refused by a debug build and ignored by a release build.
  - When the AI client goes away, pending calls are cancelled and the server exits within 2 s (Cabinette may still finish a change it had started).
  - If Cabinette doesn't answer 10 s after starting it, it is started once more. Waiting for a start and waiting while it says "not ready" are 30 s each, so a cold first call can take about a minute.
  - Tools that delete, remove, move or rename are marked as destructive for the AI client, which may ask you first (Codex does). Backup and migrate aren't marked.
  - A lost connection is reported as "the change may or may not have been made".
- **Files are named by their library ids.** The AI gets ids from search and listings. It can read only files that are in the library, not any path. A call naming an id that doesn't exist changes nothing.
- **Adding:** full paths only (relative ones are resolved against the AI client's working folder). Files added to a category the AI names stay in it (no sorting by type); without a category they are sorted by type, like a drop on the main window. They count as unread. The mode is the AI's choice, or your default add mode.
- **Deleting:** always to the Recycle Bin, whatever your setting says. A file the Recycle Bin can't take fails the call before anything is deleted. A linked (pointer) file follows your pointer setting: either it's only removed from the library, or the real file goes to the Recycle Bin too. The AI can't delete or empty Deleted, and can't move files into it (like you).
- **Reading contents:**
  - Text-like files (the Text, Code, Notes and Links types, `csv`, `tsv`, `json`, `xml`, `svg` and files that look like text) are read as they are on disk, in pages of 256 KB that never split a character. The encoding comes from a byte-order mark, else UTF-8.
  - PDF and Office files go through Windows' IFilters where installed, in pages of 256 K characters.
  - Images are scaled so the longer side is at most 1568 px: PNG when they have transparency, else JPEG.
  - Everything else (videos, audio, archives, programs) is refused with a message. Shortcuts and compiled code (`.lnk`, `.class`, `.pyc`) are refused as what they are.
  - A file "looks like text" when its first 8 KB has no NUL bytes and at most 2% control characters. Text that isn't valid UTF-8 is read in the system's code page, and the AI is told which.
  - Images: an icon's largest size; JPEG photos are turned upright.
  - Extracted text is read up to 16 M characters, and the last 4 extractions are kept briefly for paging.
- **The text extractors run in a hidden helper process** (`Cabinette.exe --extract-text`) rather than inside Cabinette:
  - Third-party extractors crash and hang, which is also why Windows Search isolates them.
  - The helper is killed after 30 s, can use at most 2 GB, and never shows a window.
  - Decided on 2026-10-24: Office's extractor needs Windows' CET protection turned off. Only the helper runs without it, at Low integrity and without privileges, so it can read your files but not change them. Cabinette itself keeps CET.
- **Matching the UI's rules:**
  - The AI can't rename Deleted.
  - Moving files "from" a category they aren't in is refused, and nothing changes.
  - Removing a file from its only category is refused, because it would land in Deleted. The AI is told to delete or move it instead.
  - Paths written in device form (`\\?\…`, `\\.\…`) are refused for vault moves and backups.
  - A backup never overwrites an existing file, and its folder must exist.
  - Migrate is refused when there are no linked files.
  - Tags are cleaned up like in the tag editor; at most 100 per call, each at most 200 characters.
- **What the AI is told:** results count only what really changed (no toast when nothing did). A search without text or a filter is refused. `list_categories` with a category lists its subcategories, pinned first, then A–Z; Deleted only when it holds files. `list_files` takes exactly one of a category or the Starred view, newest first. "Show in Cabinette" with both a file and a category opens that category at the file.
- **A pending "Delete permanently?" of yours** (after the Recycle Bin refused a file late) is kept even when an AI deletes something meanwhile: only the same delete again uses or drops it. Up to 8 are remembered.
- **While a long vault operation runs** (from Settings or an AI), AI changes are refused as "busy"; reading still works.
- **Vault operations started by an AI** skip the folder picker and the confirmation, and show the progress dialog, which brings the window up; you can cancel there. When one finishes, a notice says so instead of the result dialog. After a move the old vault is kept (only you can send it to the Recycle Bin). A backup by an AI doesn't migrate first, and its status says how many linked files it left out.
- **Toasts:** the AI's changes are merged over 5 s like the vault monitor's, one toast per client, titled with its name, with lines like "3 files added to Images". While you are looking at Cabinette they show as an in-app notice ("Claude Code added 3 files to Images"). "Notifications on vault changes" covers them, but a failed vault operation always shows. Reads and "show in Cabinette" make none.
- **The activity log** stores the client, the tool, what it named (up to 50 files by name, plus the count) and whether it worked. Failed calls are logged too, with the error. Entries older than a year are removed when the library opens and once a day.
  - "Older than a year" means more than 365 days ago. The daily removal runs with the first log entry or read at least a day after the last one, in the background, and the log never shows older entries even before they're removed.
  - Times are stored in UTC and shown in your time zone.
  - Entry numbers are never reused, even after everything was removed.
  - Logging never waits behind a long vault operation: an AI search during a vault move is logged at once.
  - The client filter ignores case, beyond English letters too.
  - Searches and listings are logged with their text and the number of results, not the names found. Each logged text is cut at 2,000 characters.
  - Answers saying Cabinette isn't ready yet aren't logged.
- **Settings › AI:**
  - The registration commands for Claude Code (`claude mcp add --scope user cabinette -- "<path>\cabinette-mcp.exe"`) and Codex (`codex mcp add cabinette -- "<path>\cabinette-mcp.exe"`), with copy buttons and the installed path filled in.
  - The commands are written for PowerShell, Windows' default terminal (paths with `$` or a backtick are escaped for it). Paths without those characters also work in cmd.
  - A dev run (`--data-dir`) shows commands for its own build: named `cabinette-dev`, in Claude Code's local scope, with full `--data-dir` and `--app` paths, so it never replaces the real registration.
  - The activity log, newest first: filters by date (the presets of the main filter), tool, client and "changes only". Clicking a file opens its thread at it, closing Settings; a file that is gone says so, and any other problem says it couldn't be opened and why.
  - The client filter lists the clients seen in the newest 1,000 entries. An entry that can't be read shows just the tool, client and time. Every link can be reached with Tab.
- **Toast wording** (the vault monitor's style): "3 files added to Images", "2 files deleted (Recycle Bin)", "1 file renamed to x.pdf". In the in-app notice the parts are joined with " · ".
- **The installer** puts `cabinette-mcp.exe` next to `Cabinette.exe` as one file and doesn't touch Claude's or Codex's settings.
  - On an upgrade, setup ends running copies of the installed `cabinette-mcp.exe` (only those, recognized by the file itself) after Cabinette has closed and before files are replaced. It doesn't use Windows' Restart Manager for them, because that would also interrupt Claude Code or Codex.
  - The uninstaller does the same after closing Cabinette. In Claude Code, `/mcp reconnect cabinette` reconnects afterwards.
  - If a copy can't be ended you get Retry/Cancel, and Cancel rolls back. A failing check is logged and setup continues. There's no notice on the Ready page.
  - The build fails on any warning, on a stray file in the publish output, or when the two programs' versions differ.

---

## 11. Look and feel

- **Theme:** a setting with Light, Dark or Follow Windows (the default).
- **Bold text (added 2026-10-25):** an on/off switch, off by default, like the iPhone's Bold Text. It is called "Bold text" and sits under Text size in Settings › Appearance, and it is saved with the other settings **[my default]**. It applies at once, like Text size.
  - On, every text weight is one step heavier, so titles still stand out: Regular becomes Semibold, Medium and Semibold become Bold, Bold stays Bold, and Light (the large app name on the empty screen) becomes Regular **[my default]**. Off, everything looks exactly as before.
  - It covers all text Cabinette draws itself: the main window, every dialog (Settings and First run too), the right-click menus and the tray menu, tooltips, the media viewer, the tag editor, and text boxes with what you type in them, the Settings › AI command boxes included. It leaves alone what Windows draws (the classic right-click menu, the file dialogs, toast notifications) and pictures with text in them, such as the category avatars "PDF" and "W" **[my default]**.
- **Font (added 2026-10-25):** a short list of fonts in Settings › Appearance, each checked in screenshots. Every font on it has a real Semibold and Bold, so Bold text keeps titles heavier than the rest.
  - The list, in this order: Segoe UI Variable (today's font, the default), Segoe UI, Inter, Source Sans 3, Atkinson Hyperlegible Next, Lexend, Bahnschrift, Sitka, Cascadia Mono and JetBrains Mono. Sans-serif fonts come first, then the serif one, then the monospaced ones **[my default]**.
  - It is a drop-down in a "Font" group between Theme and Text size, styled like the app's other controls, with each font's name drawn in that font **[my default]**.
  - Inter, Source Sans 3, Atkinson Hyperlegible Next, Lexend and JetBrains Mono come with Cabinette, under the SIL Open Font License (their licenses are in the `licenses` folder next to Cabinette.exe). The others come with Windows, and one that isn't installed is left out of the list; on Windows 10, Segoe UI Variable and Cascadia Mono usually aren't **[my default]**.
  - A saved font that is no longer there falls back to the default, and the default falls back to Segoe UI, as it does today **[my default]**.
  - Titles and the rest use the same font, except with the default, which keeps today's pair: Segoe UI Variable Display for titles and Segoe UI Variable Text for the rest **[my default]**.
  - It leaves alone the Settings › AI command boxes (always Cascadia Mono), the icons, the category avatars (they are pictures) and what Windows draws **[my default]**.
  - It applies at once and is saved with the other settings, like Text size **[my default]**.
- **Light theme:** **"Classic wheat"**, with the whole theme tinted cream and wheat, as previewed. It should be easy on the eyes.
- **Dark theme:** **"Graphite"** (decided 2026-10-27): neutral charcoal with the light theme's gold accent, amber copies and slate-blue pointers. Until then it was a blue-green charcoal with a green accent; the user changed it for a look of Cabinette's own, choosing from rendered options.
- **Selection outline and keyboard focus ring:** gold in both themes (decided 2026-10-27; green before). The light theme uses a deeper gold, `#A87A2A`, so the ring keeps 3:1 contrast against the near-white sidebar **[my default]**.

| Token | Light (Classic wheat) | Dark (Graphite) |
|---|---|---|
| Window / chat background | `#FBF3E4` | `#121212` |
| Sidebar | `#FFFDF8` | `#181818` |
| Header bars | `#F5EBD6` | `#222222` |
| Copy-mode bubble (right) | `#F2DDB0` (warm amber; soft green `#DCEBC9` from 2026-10-24 to 27, wheat `#F5DEB3` before) | `#4A3A1E` (muted amber) |
| Pointer bubble (left) | `#DCE9F5` (soft blue) with a `#BCD3EA` border | `#2B3A4D` (slate blue) |
| Gallery frame, copy | `#D2AE66` | `#8A6A30` |
| Gallery frame, pointer | `#98BCE3` | `#4A6A90` |
| Banner, when the category has your own picture | `#8A6A3A` | `#4A4A4A` |
| Accent (buttons, badges, selection) | `#C8963E` | `#D9A441` |
| Selection outline, focus ring | `#A87A2A` | `#D9A441` |
| Text on accent | `#3D2B0F` (dark brown; white on this gold is hard to read) | `#2A1D07` |
| Primary text | `#3D3222` | `#ECECEC` |
| Secondary text | `#9C8B6B` | `#9C9C9C` |
| Borders / dividers | `#E6D6B8` | `#2A2A2A` |

The palette values may be nudged slightly on screen for readability, but the look stays as previewed.

- **Typography:** Segoe UI Variable on Windows 11 and Segoe UI on Windows 10.
- **Shape:** rounded corners and Fluent icons.
- **Identity:** the layout is inspired by chat apps in general; Cabinette's name, logo, icons and colors are its own.

### Motion: drag and drop (added 2026-10-21, "playful" style)
Animations are purely visual: nothing waits for them, and the files are added the moment you drop.
- **While dragging over a thread:** its "Drop to add" overlay fades in and springs from 82% to full size with a slight overshoot (about 0.4 s). Its dashed border slowly marches and the download icon gently bobs. Dragging away fades and shrinks it out.
- **On drop:** the card pops slightly, the icon dips "into the tray", and the overlay shrinks and fades.
- **Upload ring (added 2026-10-22, cosmetic):** after a drop on the thread, window or empty-state card, the dim and the dashed frame fade at once. The card's icon turns into a ring that fills clockwise from the top (0.9 s), a check pops in and holds, then the card shrinks and fades: about 1.75 s in all. The ring is in the accent color; on the accent-colored pill it is white.
  - The chat-list background pill keeps the short pop, because the "Adding N files" card covers it a quarter second after a drop.
  - A new drag ends a ring still playing: on the same card it turns back into the normal card; on another card, the ringing card leaves at once.
- **Chat list:** a row you drag over glows in, with a faint accent fill even at rest. Dropping on it makes it pulse once.
- **The chat list reacting:** a category that received files glides to its new place (sorted by last upload) over the rows making room, and its unread badge pops with a spring. With A–Z sort only the badge pops. This happens whatever added the files: a drop, Explorer, the vault monitor or an AI.
  - Bursts stay calm: if more than 3 rows move or more than 4 badges grow at once, the list updates without motion, and after 4 animated updates within 2 s it pauses animating until 1.5 s of quiet.
  - Drilling into or out of subcategories keeps the existing slide.
- **Window overlay "Drop to sort automatically":** dropping on the title bar, the chat-list header or the search box sorts files by type. These parts now show a matching overlay, after a short hover (120 ms) so that crossing the pane divider doesn't flash it. A bubble dragged out of a thread isn't offered for re-adding.
- **Windows' "Animation effects" off** (Settings › Accessibility › Visual effects): everything appears in its end state, without motion.

---

## 12. Settings (with defaults)

| Setting | Default |
|---|---|
| Vault location | Asked on first run |
| Default add mode | Copy |
| Theme | Follow Windows |
| Bold text | Off |
| Font | Segoe UI Variable |
| Start with Windows | On |
| Close button sends the app to the tray | On |
| Notifications on vault changes | On |
| Deleting vault files | Recycle Bin (alternative: permanent) |
| Deleting a pointer item | Also delete the real file, to the Recycle Bin (alternative: library only) |
| Deleting a category | Move its files to Deleted (alternatives: delete files, ask) |
| Adding a folder: subfolders become subcategories | On |
| Adding a folder: also add files to type categories | Off |
| Sort files by type in type categories | On |
| Clean My Downloads mode | Off |
| Chat list sort | Last upload (alternative: A–Z) |
| AI activity log | (viewer) |

---

## 13. Delivery plan

Each stage ends with a working build for you to try, and I ask before starting the next one.

1. **Core app** (done):
   - Database, library services and categories (including drill-in and icons)
   - The thread view in chat, grid and details modes, with previews
   - Adding by drag-and-drop, the file dialog and paste; notes
   - Open, rename, copy and delete; the classic shell right-click menu
   - Search, filters and tags; missing files and Locate…
   - Themes, settings and first-run vault setup; unit tests for the core
2. **Vault monitor and tray** (done, tested end to end on 2026-10-09, plus the 22 stage 1 review fixes and the 13 bug-hunt fixes of 2026-10-21): watcher, startup rescan, single instance, tray, app icon, start with Windows, close to tray, notifications, move vault, migrate, backup and restore.
3. **Explorer and installer** (built 2026-10-21): the Explorer menu entries, batching of multi-file selections, the Inno Setup installer, README and LICENSE. The dev Explorer entries and the animations were tested on your desktop with you on 2026-10-22; the installed build was tested at the end of stage 4, on 2026-10-24 (`docs/review/install-test-2026-10-24`).
   - **Added on your request (2026-10-21 and 22):** drag-and-drop animations and the upload ring, type categories sort by type, Move to category, Starred files, the Ctrl+K quick switcher and Clean My Downloads mode.
   - Every piece was bug-checked by an independent reviewer before merging, and every finding fixed (`docs/review/bughunt-2026-10-21-*`, `docs/review/bughunt-2026-10-22-*`).
4. **AI API** (done, built 2026-10-23/24): the MCP server, the activity log, Settings › AI, and setup instructions for Claude Code and Codex. Every piece was bug-checked by an independent reviewer and every finding fixed (`docs/review/bughunt-2026-10-24-*`). On 2026-10-24 Codex and Claude Code were tested on your desktop against a dev Cabinette (`docs/review/codex-test-2026-10-24`), and the installed build was installed, upgraded and uninstalled once (`docs/review/install-test-2026-10-24`).
   - **Added on your request (2026-10-24, after using the installed build):** the category banner, the Longhorn-style Gallery for Images with date taken, and green/blue chat bubbles (sections 4 and 11).
5. **GitHub** (after stage 4, as you asked): commit authorship, which notes go public, the repository, and a release with `Cabinette-Setup.exe`. Nothing is pushed without your OK.

### How the work is split across parallel agents (stage 1)
1. **Shared foundation (me):** the solution skeleton, domain models, service interfaces and database schema, so every agent builds against the same contracts.
2. **In parallel:**
   - (A) The core implementation and its tests.
   - (B) The WPF UI and themes, built against the interfaces with sample data.
   - (C) The Windows interop: shell menu, thumbnails, Recycle Bin, clipboard and icon generation.
3. **Integration and review (me):** wiring the pieces together, building, running and checking before I hand it to you.

---

## Appendix A — Auto-categorization by extension

| Category | Extensions |
|---|---|
| Audio | mp3 wav flac aac m4a ogg opus wma aiff aif mid midi amr |
| Video | mp4 mkv avi mov wmv webm flv m4v mpg mpeg 3gp mts m2ts |
| Images | jpg jpeg png gif bmp webp tif tiff heic heif avif ico raw cr2 nef arw dng |
| Text | txt md markdown log nfo |
| Documents › PDF | pdf |
| Documents › Word | doc docx docm dot dotx dotm |
| Documents (other) | odt rtf xps oxps pages |
| Spreadsheets | xls xlsx xlsm ods csv tsv numbers |
| Presentations | ppt pptx odp key |
| Code › Python | py pyw pyi ipynb |
| Code › Java | java class gradle |
| Code › C# | cs csproj sln xaml razor resx |
| Code › C/C++ | c h cpp hpp cc cxx hxx |
| Code › JS/TS | js mjs cjs ts tsx jsx |
| Code › Web | html htm css scss sass less vue svelte |
| Code › Shell | sh bash zsh ps1 psm1 bat cmd |
| Code (other) | go rs rb php swift kt kts lua sql r json yaml yml xml toml dart scala |
| Executable › MSI | msi |
| Executable › Exe | exe |
| Executable (other) | msix msixbundle appx appxbundle com jar apk dll |
| Archives | zip 7z rar tar gz tgz bz2 xz zst cab |
| E-books › EPUB | epub |
| E-books › PDF | none automatically: new PDFs go to Documents › PDF, but E-books › PDF keeps the PDFs you put there, also with "sort files by type" on |
| E-books (other) | mobi azw azw3 fb2 djvu cbz cbr |
| Fonts | ttf otf woff woff2 fon |
| Design | psd ai xd fig sketch svg eps indd afdesign afphoto kra xcf ora |
| 3D models | obj fbx stl blend gltf glb 3ds dae step stp 3mf ply |
| Disk images | iso img vhd vhdx vmdk dmg wim |
| Links | url website lnk webloc |
| Notes | note enex org, plus notes created inside Cabinette (typed or pasted text) |
| Other | anything else |

**Subcategories added on 2026-10-24** (Documents › PDF and Word, Executable › MSI and Exe, E-books › EPUB and PDF, at the user's request):
- A new library gets them with the other built-in categories. An existing library gets them once, at its next start, and only under parents that still exist: a category you deleted doesn't come back, and one of the six you delete later isn't added again (like every built-in category, it is only re-created when a file needs it). While the vault can't be reached (a drive that isn't connected, a vault renamed in Explorer), nothing is added and nothing in the vault is touched; they come at the first start where it can be reached. A restored older backup gets them in the restored vault. A folder you made with one of their names becomes that category's folder. A subcategory of yours with the same name takes the built-in role instead of getting a "PDF (1)" twin.
- Files already in the library stay where they are (a parent's thread shows its subcategories' files anyway); only files added from then on sort into the subcategories.
- The type filter's "Documents", "Executable" and "E-books" still cover their subcategories' files.
- **[my default]:** the names "Exe" and "EPUB"; Word also takes docm, dot, dotx and dotm; the avatars are text labels (PDF, W, MSI, EXE, EPUB) like Python's and C#'s.

## Appendix B — Database schema (first version)

The live schema is [src/Cabinette.Core/Data/Schema.sql](src/Cabinette.Core/Data/Schema.sql). Copy-mode files have no stored path: their location is the vault, plus the home category's folder chain, plus the file name. Renaming a category therefore never rewrites item rows.
