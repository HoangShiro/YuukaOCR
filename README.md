# YuukaOCR

A floating bar on your screen: highlight / snip / copy anything and Yuuka reads the text inside it,
then puts the result on your clipboard. This is a rewrite of `img-to-text` (Python + PySide6) in
**C# / .NET 10 + WPF**.

> ### WARNING: DO NOT USE WITH ANTI-CHEAT SOFTWARE
> YuukaOCR installs a **global keyboard/mouse hook** (`SetWindowsHookEx`) and reads the clipboard.
> Vanguard, Easy Anti-Cheat, BattlEye and some antivirus products may treat that as suspicious
> behaviour. Do not run YuukaOCR while playing a game with anti-cheat, or while banking software is
> running.

---

## Why rewrite it?

| | Python build | C# build |
| --- | --- | --- |
| Install | Python + venv + `pip install` of 11 packages (2 unused) | Unzip and run `YuukaOCR.exe` |
| Startup | `YuukaOCR.vbs` → `pythonw launcher.pyw` → `main.py` | One process |
| Size | ~250 MB (Python + Qt) | 0.4 MB (framework-dependent) |
| Paths | Relative to `cwd` → breaks when started from a shortcut | Absolute, resolved from the exe folder |
| Silent failures | 8+ `except Exception: pass` | Every failure is logged, or the reason is commented |

Architecture in detail: see [`PROJECT_STRUCTURE.md`](PROJECT_STRUCTURE.md).

---

## Requirements

* Windows 10/11 (64-bit)
* [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — or use the
  `--self-contained` build if you do not want to install the runtime
* An API key for any provider (Gemini has a free tier)

---

## Build

```powershell
dotnet build YuukaOCR.slnx -c Release
dotnet run --project src/YuukaOCR.App            # run the dev build
dotnet run --project tests/YuukaOCR.Tests        # 53 tests
pwsh ./build.ps1                                 # publish into publish/
pwsh ./build.ps1 -SelfContained                  # bundle the runtime, no .NET install needed
```

Publish output:

```
publish/
├─ YuukaOCR.exe          ← double-click this (icon comes from Assets/app.ico)
├─ YuukaOCR.dll, YuukaOCR.Core.dll, *.json
├─ Assets/ui.png
├─ config/app_configs.json
└─ user/                 ← created on first run
```

### App icon

`Assets/app.ico` and `Assets/app-icon.png` are generated from the master artwork
`Assets/18 thg 8 2026.png` by `tools/makeicon.py` — stdlib only (`zlib` + `struct`), because the
solution takes no packages: a PNG decoder, an area-average downscaler, a rounded-corner mask and an
ICO writer.

```powershell
python tools/makeicon.py           # regenerate both files (~5 s)
python tools/makeicon.py --check   # non-zero exit when they are out of date
```

| File | Role |
| --- | --- |
| `Assets/18 thg 8 2026.png` | Master artwork. Never modified, never shipped |
| `Assets/app.ico` | 16, 20, 24, 32, 40, 48, 64, 96 (DIB) + 128, 256 (PNG) — Explorer, taskbar, shortcuts, title bar |
| `Assets/app-icon.png` | 256×256 RGBA — `Icon=` of every window (title bar, Alt+Tab) |

The artwork is a square tile, so the generator rounds it: a corner radius of 20 % of the side
(51 px at 256, 3 px at 16), masked with 4×4 sub-samples per pixel so the arc is anti-aliased at every
size instead of stair-stepping, and everything outside the arc is transparent. A hard 90° corner
reads as a white sticker on a dark taskbar — that is the whole reason the mask exists. The
`--check` run decodes the generated PNG and fails unless the four corners are fully transparent, the
centre is fully opaque and some pixels along the arc are partly transparent, so a regenerate that
loses the alpha channel is caught instead of shipped.

The PNG entries carry the alpha channel directly; the DIB entries of the small sizes carry it in
their 32-bit pixels and repeat it in the 1-bit AND mask (a set bit means transparent), because a
renderer that ignores the alpha channel would otherwise paint the rounded corner black. The `.ico` is
wired through `<ApplicationIcon>`; the PNG is a `<Resource>` referenced as
`pack://application:,,,/YuukaOCR;component/Assets/app-icon.png` (assembly-qualified, so the layout
harness can load the same window it checks). Replace the master, re-run the script, commit both files.

The target machine needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
To copy the app to another machine with nothing installed: `pwsh ./build.ps1 -SelfContained`.

---

## Usage

1. Run `YuukaOCR.exe`. A pink bar appears (Sakura is the default theme); drag it with the left
   mouse button.
2. **Set an API key**: click the orange button → *System* tab → paste the key → *Add*. *Add* is also
   the test: the key is checked against the provider first and only stored when it answers, so a
   rejected key never reaches `user/.env` and the reason appears right under the input. A key that
   answers **loads the model list immediately** — there is nothing else to press; *Refresh* only
   re-reads the list by hand. Keys live in `user/.env`, never in the source.
3. **The key is checked on every start.** If the selected provider has no key at all, or the provider
   rejects the stored one, Yuuka opens *Settings → System* with the reason right above the key field
   (and a short note in the bar). Being offline or rate limited is *not* treated as a bad key, and
   local providers (Ollama, LM Studio) are never prompted, because they need no key.
4. **OCR**:
   * **Middle mouse** (default hotkey) — if the bar is snapped to a window, capture that window;
     if it is not snapped, show the region-selection overlay.
   * **Ctrl+V with an image** — read the image currently on the clipboard (toggle in the System tab).
   * **Drag and drop a file** (image, PDF, audio, txt/md/json/csv) onto the bar.
   * **Right-click** the bar → *OCR selection*.
5. The result appears in a bubble next to the bar and **is already on the clipboard** — `Ctrl+V` and
   you are done. Drag the bubble to move it, use the small pink button to close it. The *Test* button
   in **Layout** shows the same bubble at the current position, so the placement can be checked
   without running an OCR.
6. **Edge snapping**: drag the bar near the top/bottom edge of another window and it attaches itself
   and follows that window. *Settings → Hooking → Hook mode* switches between **Outside** (the bar sits
   just past the window edge) and **Inside** (it stays within the window, which is what a fullscreen
   app needs); the target is always clamped to the monitor, so the bar cannot end up off-screen.
   Attaching **keeps the bar where you put it**: the hook remembers the bar's *offset* from the window's
   left edge, not a screen coordinate, so dragging the window sideways takes the bar with it and a bar
   sitting at the right end of a window does not jump to the middle. Drag the bar itself while hooked
   and the offset is re-taken where you drop it. The window is polled 20 times a second, so the bar
   trails a window being dragged instead of being left behind.

### The result bubble

* **The text is selectable.** Select any part of it and a small accent-filled copy button appears
  next to the selection — that copies exactly what you picked. It disappears again as soon as the
  selection is cleared, the bubble loses focus, or you click anywhere else in it, so it never floats
  over text it no longer points at. The icon-only **copy** button in the bottom-right corner (under
  **close**) copies the whole result.
* **The action bar** along the bottom holds your registered action buttons (icon-only, up to 10) and
  a chat box. Chat and actions use either the **whole result** or **just the selected text** — a
  selection switches the scope automatically, and the little label next to the input shows which one
  is active. The selection is *pinned* the moment you make it: clicking into the chat box to type the
  question keeps it, the highlight stays painted (dimmed, because the text box no longer has focus)
  and the label still reads *Selected text*, so what the agent is told cannot change between
  selecting and pressing Enter. The scope drops back to the whole result only when you clear the
  selection yourself or a new OCR result arrives.
* Actions are predefined quick-chat turns: each one sends the global *Agent instruction* plus its own
  *Action instruction* plus the context, through the **same conversation** as the chat box, so "and
  in Vietnamese?" works right after "summarize this".
* **The conversation is selectable too** — both your own messages and the agent's replies. Select any
  part of a message and the same floating copy button appears; `Ctrl+C` works as well, and the highlight
  stays painted (dimmed) when you click into the chat box, so a quote you picked is still visible while
  you type the follow-up. The layout is unchanged: the small role label still sits above each message,
  your turns stay right-aligned, and the panel scrolls as before.
* The conversation is shared with every bubble and survives a restart (`chat_history` in
  `user/log.json`); *Settings → History → Chat history* shows it in order.
* **Drag the action tabs** in *Settings → Prompt/Agent → Actions* to reorder them; the Action Bar in
  every open bubble is rebuilt in the same order before the mouse button is released, and the order is
  saved with the actions (`actions` in `user/settings.json`), so it survives a restart.
* Adding, renaming or removing an action in *Settings → Prompt/Agent* changes the bar **immediately**:
  the settings store raises `Changed`, `AppearanceSync` fans it out, and the bubble rebuilds its
  buttons — no restart, no reopening the bubble.
* The send button, the floating copy button and every action button are **accent-filled**: the glyph
  is painted with the preset's derived on-accent ink, so the same button is dark on the light presets
  and light on the dark ones instead of disappearing into the bubble.

### The model box

The model field is a search box, not a dropdown. The list behind it is **empty until the configured
key works**, because a suggestion the key cannot use is a trap; as soon as the key answers it is
filled from the provider and ranked:

* **OpenRouter `@preset/...` entries always sit at the top** (`@preset/tools`, `@preset/code`). A
  preset is not part of `/models`, so it comes from your own history and the saved model — which is
  why the box never loses the value you picked.
* Vision-capable models, then free ones, come next.
* **Click the box** to see the whole list, **type** to filter it (`claude`, `vision`, `free` — every
  word must match), arrow keys to move, **Enter** or a click to take one.
* A name that is not in the list is still accepted: local models (`llama3.2-vision`), a preset, or a
  model the provider has not listed yet.

### Settings (orange button)

| Tab | Contents |
| --- | --- |
| **System** | Provider, base URL and model on one row (wraps when the window is narrow) — the model field is the searchable picker described above, the list fills itself and *Refresh* re-reads it; multiple API keys with per-row *copy* / *remove* icons, hotkey, start with Windows, clipboard flags. **Update** shows the installed version, the latest release, the status, *Check for updates*, *Download & install* and — in a git checkout only — *Update with git*. This is also the tab the startup key check opens. |
| **Appearance** | UI language (10 languages: English, Tiếng Việt, 日本語, 한국어, 简体中文, Español, Français, Deutsch, Русский, Português), 8 theme presets, frame + background opacity, font, font size, custom bar image, UI scale |
| **Layout** | Result bubble direction (D-pad + Auto), **Test** — shows the bubble at the current position so the placement can be checked without running an OCR — spacing, minimum width, clear saved positions |
| **Hooking** | Hook mode (Outside / Inside), top/bottom snap zones, vertical offset, release distance, spring stiffness, damping, bounce |
| **Prompt/Agent** | **OCR** — the OCR prompt (moved here from System and renamed from *Custom prompt*; same `custom_prompt` key) and its enable toggle. **Agent** — instruction/persona, provider + model on one row (empty = follow the OCR provider), temperature + context-token budget on one row. **Actions** — a browser-style tab strip with a `+` button; each tab picks an icon and holds the action instruction (max 10); drag a tab to reorder, `import`/`export` at the end of the strip. Every prompt field carries the same two icon buttons: **expand** (Full Text Edit Mode) and **reset** (back to the shipped default) |
| **History** | *Recent results* and *Prompts used* side by side, *Chat history* below. Every row is selectable text with a floating copy button plus icon-only **copy / expand / remove** buttons at the bottom right; expand opens the entry in **Full Text Edit Mode, read-only**, so long results can be read and copied whole — *Done* or *Back* returns without touching the stored entry |
| **Status** | API calls per model, uptime, per-source statistics |
| **Console** | Live log lines |

Changing the language in **Appearance → Language** applies immediately: the settings window reopens
on the same tab, and the bar, tooltips, dialogs and log messages follow. The choice is stored as
`language` in `user/settings.json`. English is the default — a settings file without that key starts
in English, and an unknown code falls back to it too. Every table must hold exactly the English key
set; the test runner fails when one drifts, and it also fails when a language is offered in the menu
without a table behind it.

### Full Text Edit Mode

One window (`Ui/FullTextWindow.xaml`) serves every "this text is too long for its box" case — the three
prompt fields, the action instruction and the history entries. From *Settings* it covers the whole
settings window; from the bar it centres itself on the monitor.

* **Back** (top left) closes it and **returns the text exactly as it was** — nothing is applied, nothing
  is discarded.
* **Done** (bottom right) is the only thing that writes: the editor hands the text back to the field
  that opened it, which then saves it through the normal path (so the debounced store, the `Changed`
  fan-out and the Action Bar rebuild all still happen).
* Read-only mode (history) hides *Done*, keeps the text selectable and copies through the usual
  floating copy button.

### Action presets

*Settings → Prompt/Agent → Actions* ends with two icon-only buttons:

| Button | Effect |
| --- | --- |
| **export** | Writes the current action tabs to a `.json` preset, `yuukaocr-actions-{yyyyMMdd-HHmm}.json` by default |
| **import** | Asks for confirmation (*"all existing action tabs will be replaced"*), then replaces every action with the file's content |

```json
{
  "format": "yuukaocr.actions",
  "version": 1,
  "exported_at": "2026-11-22T10:00:00Z",
  "actions": [
    { "name": "Translate", "icon": "translate", "instruction": "Translate the context into ..." },
    { "name": "Summarize", "icon": "summarize", "instruction": "Summarize the context in ..." }
  ]
}
```

Order is the array order — that is the tab order and the Action Bar order. Ids are **not** stored: a
preset is content, and a fresh id per action keeps two imports from ever colliding. Import is
all-or-nothing: a file that is empty, not JSON, of another `format`, of a newer `version`, with zero
actions or more than 10 is refused with its own message and the current tabs are left untouched.

### Themes

**Appearance → Theme** offers 8 presets — Sakura (default), Midnight, Ocean, Forest, Sunset, Lavender,
Graphite and Daylight (the only light one). Picking a card repaints **the whole app at once**: the
floating bar, the result bubble and the settings window itself. A preset is not a set of loose
colours: it authors the bar, the accent, the close button, the background and the text, and the panel,
hover, border and on-accent colours are derived from them, so a theme can never drift out of sync.

The two sliders under **Transparency** control the floating frame and the window backgrounds
independently (`frame_opacity` / `background_opacity`, 20–100%). They are stored in
`user/settings.json` as plain numbers, so the file stays readable and hand-editable. The shipped
background opacity is **90 %** — applied only when the key is missing, so an existing value is never
rewritten by an upgrade.

**Every icon follows the theme, live.** The action buttons in the bubble, the action tab strip and its
icon picker, the History row buttons and the floating copy button all take their ink from the theme's
resources (`YuukaText`, `YuukaSubtle`, `YuukaOnAccent`) rather than from a colour captured when the
button was built, so switching between a dark preset and **Daylight** recolours them immediately — no
restart, no reopening the tab. The same rule paints the selected tab's accent fill.

Every slider in the settings window is **discrete**: it snaps to fixed steps and prints the step
values under the track, so a value is always a number you can name instead of a position you have to
guess. Range, step and default all come from one table (`Core/Settings/SliderSpecs.cs`), so a
default can never sit between two ticks — the test runner checks that.

The slider chrome is deliberately chunky: an 8 DIP track you can actually hit, a 20 DIP thumb row
(the ellipse is 18 DIP — a shorter row used to clip its top and bottom), and 3 DIP tick marks that
start below the thumb instead of behind it. On a crowded slider (0–32000 by 4000, 0–600 by 50 …)
every second mark is drawn at 38 % so the steps stay countable; the *Window edge snapping* sliders in
**Hooking** ask for doubled marks (6 DIP) through `controls:SliderTicks.Thickness`. Those five snapping
sliders step **50 / 25 / 50 px** — half the old steps, so twice the ticks over the same range and no
value an older build stored can fall between two marks. A slider's height
is never fixed: it measures its own thumb row plus tick row, which is also what keeps the labels from
clipping at 125 % or 150 %.

Appearance changes reach **every window immediately**, including the result bubble: all windows
repaint through one central subscription (`Ui/AppearanceSync.cs`), so a new window only has to
implement `IThemedWindow` and register itself.

---

## Updates

Every start compares the running version against the latest **GitHub release** of
`HoangShiro/YuukaOCR`. The repository and its tag scheme are read from the checkout (`git remote`,
`GitUpdateService.RemoteUrl`) rather than hardcoded, so a fork follows its own releases.

| Piece | Where |
| --- | --- |
| Version comparison | `Core/Update/AppVersion.cs` — lenient 4-component parse (`v2.1.0`, `2.1`, `2.2.0-beta.1`, `2.2.0+build.7`) |
| Check / download / install | `Core/Update/GitHubUpdateService.cs` — `GET /repos/{owner}/{repo}/releases/latest` |
| UI | *Settings → System → Update* (installed version, latest version, status, *Check*, *Download & install*) |
| Startup check | `App.xaml.cs` — fire and forget; a failure only reaches the log |

* A release is "newer" only when its tag parses **and** is greater; equal or older shows *up to date*.
* The download asset is the first `.zip` whose name contains `win-x64` (that is what
  `build.ps1` publishes); a release without one still reports the version, with *install* disabled.
* The zip is unpacked to a temp folder, the `user/` folder of the package is dropped, the install
  folder is overwritten, and the app restarts itself. **`user/` — settings, `.env`, `log.json`,
  action presets — is never touched.**
* A token may be supplied as `GITHUB_TOKEN` in `user/.env` for the (private) API; it is read through
  the same `.env` parser as the provider keys and is never logged.
* Nothing about an update check can delay or break startup: the check is off the UI thread, has a
  short timeout, and every failure is a message in the Update section instead of an exception.
* In a development checkout the old `git pull --ff-only` path is still offered as *Update with git*
  (it stashes local changes and restores them); a published build does not see that button.

---

## Providers

20 providers, 3 wire protocols (`OpenAI-compatible`, `Gemini`, `Anthropic`).
Full table and how to get each key: [`docs/PROVIDERS.md`](docs/PROVIDERS.md).

Keys live in `user/.env`:

```env
GEMINI_API_KEY=...
OPENAI_API_KEY=...
OPENAI_API_KEY_2=...        # several keys → rotated
ANTHROPIC_API_KEY=...
```

The variable name for each provider is in the *Env key* column of the provider table, and is also
shown in the System tab as soon as you pick the provider.

---

## User data

| Path | Contents |
| --- | --- |
| `user/settings.json` | Settings — including the theme `preset` and the two opacity values |
| `user/.env` | API keys — **never commit, never share** |
| `user/log.json` | Statistics, recent results, recent prompts, chat history |
| `user/api-debug.log` | **Only when `YUUKA_API_DEBUG=1`** — full request/response trace for debugging. `build.ps1` deletes it |
| `user/ui.png` | Custom bar image |

Deleting `user/` resets everything to defaults. The old Python build used `user/user_config.json`;
this build still reads that file on first run.

---

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| Bar does not appear | Check `user/log.json`; run `YuukaOCR.exe` from a terminal to see the log |
| Settings opened by itself on start | No key for the selected provider, or the stored key was rejected — the reason is above the key field. Add a key, or pick a local provider |
| Model list is empty | No working key for the current provider yet; a rejected key never fills it. *Refresh* re-reads it after you fix the key |
| Hotkey does nothing | The global hook may be blocked — run as Administrator, or change the hotkey |
| "API key required!" | No key added for the selected provider, or *Add* rejected the key (the reason is shown under the input) |
| "Could not read the returned result!" | The model did not answer with text/JSON — check the History tab, try another model |
| Chat or an action does nothing | The agent has no key for its provider (empty provider = follow the OCR one). `YUUKA_API_DEBUG=1` writes `user/api-debug.log` with the exact request and the provider's error |
| Pasted result is duplicated | Turn off *Process text in the clipboard* if you do not want the app to read its own output |
| Update check says *unavailable* | GitHub was unreachable or rate limited — nothing else changes; press *Check for updates* again later |
| Update has no install button | The latest release has no `win-x64` `.zip` asset. In a checkout, *Update with git* does the same job with `git pull --ff-only` (local changes are stashed and restored) |

---

## License & credits

Rewritten from `img-to-text` by the same author. The "Yuuka/senpai" tone in messages and comments is
kept from the original.
