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
dotnet run --project tests/YuukaOCR.Tests        # 42 tests
pwsh ./build.ps1                                 # publish into publish/
pwsh ./build.ps1 -SelfContained                  # bundle the runtime, no .NET install needed
```

Publish output:

```
publish/
├─ YuukaOCR.exe          ← double-click this
├─ YuukaOCR.dll, YuukaOCR.Core.dll, *.json
├─ Assets/ui.png
├─ config/app_configs.json
└─ user/                 ← created on first run
```

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

### The result bubble

* **The text is selectable.** Select any part of it and a small accent-filled copy button appears
  next to the selection — that copies exactly what you picked. The icon-only **copy** button in the
  bottom-right corner (under **close**) copies the whole result.
* **The action bar** along the bottom holds your registered action buttons (icon-only, up to 10) and
  a chat box. Chat and actions use either the **whole result** or **just the selected text** — a
  selection switches the scope automatically, and the little label next to the input shows which one
  is active.
* Actions are predefined quick-chat turns: each one sends the global *Agent instruction* plus its own
  *Action instruction* plus the context, through the **same conversation** as the chat box, so "and
  in Vietnamese?" works right after "summarize this".
* The conversation is shared with every bubble and survives a restart (`chat_history` in
  `user/log.json`); *Settings → History → Chat history* shows it in order.
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
| **System** | Provider, base URL and model on one row (wraps when the window is narrow) — the model field is the searchable picker described above, the list fills itself and *Refresh* re-reads it; multiple API keys with per-row *copy* / *remove* icons, hotkey, start with Windows, auto-update, clipboard flags. This is also the tab the startup key check opens. |
| **Appearance** | UI language (10 languages: English, Tiếng Việt, 日本語, 한국어, 简体中文, Español, Français, Deutsch, Русский, Português), 8 theme presets, frame + background opacity, font, font size, custom bar image, UI scale |
| **Layout** | Result bubble direction (D-pad + Auto), **Test** — shows the bubble at the current position so the placement can be checked without running an OCR — spacing, minimum width, clear saved positions |
| **Hooking** | Hook mode (Outside / Inside), top/bottom snap zones, vertical offset, release distance, spring stiffness, damping, bounce |
| **Prompt/Agent** | **OCR** — the OCR prompt (moved here from System and renamed from *Custom prompt*; same `custom_prompt` key) and its enable toggle. **Agent** — instruction/persona, provider + model on one row (empty = follow the OCR provider), temperature + context-token budget on one row. **Actions** — a browser-style tab strip with a `+` button; each tab picks an icon and holds the action instruction (max 10) |
| **History** | *Recent results* and *Prompts used* side by side, *Chat history* below. Every row is selectable text with a floating copy button plus icon-only **copy / expand / remove** buttons at the bottom right; expand grows the row to its full content and flips to collapse |
| **Status** | API calls per model, uptime, per-source statistics |
| **Console** | Live log lines |

Changing the language in **Appearance → Language** applies immediately: the settings window reopens
on the same tab, and the bar, tooltips, dialogs and log messages follow. The choice is stored as
`language` in `user/settings.json`. English is the default — a settings file without that key starts
in English, and an unknown code falls back to it too. Every table must hold exactly the English key
set; the test runner fails when one drifts, and it also fails when a language is offered in the menu
without a table behind it.

### Themes

**Appearance → Theme** offers 8 presets — Sakura (default), Midnight, Ocean, Forest, Sunset, Lavender,
Graphite and Daylight (the only light one). Picking a card repaints **the whole app at once**: the
floating bar, the result bubble and the settings window itself. A preset is not a set of loose
colours: it authors the bar, the accent, the close button, the background and the text, and the panel,
hover, border and on-accent colours are derived from them, so a theme can never drift out of sync.

The two sliders under **Transparency** control the floating frame and the window backgrounds
independently (`frame_opacity` / `background_opacity`, 20–100%). They are stored in
`user/settings.json` as plain numbers, so the file stays readable and hand-editable.

Every slider in the settings window is **discrete**: it snaps to fixed steps and prints the step
values under the track, so a value is always a number you can name instead of a position you have to
guess. Range, step and default all come from one table (`Core/Settings/SliderSpecs.cs`), so a
default can never sit between two ticks — the test runner checks that.

The slider chrome is deliberately chunky: an 8 DIP track you can actually hit, a 20 DIP thumb row
(the ellipse is 18 DIP — a shorter row used to clip its top and bottom), and 3 DIP tick marks that
start below the thumb instead of behind it. On a crowded slider (0–32000 by 4000, 0–600 by 50 …)
every second mark is drawn at 38 % so the steps stay countable; the *Window edge snapping* sliders in
**Hooking** ask for doubled marks (6 DIP) through `controls:SliderTicks.Thickness`. A slider's height
is never fixed: it measures its own thumb row plus tick row, which is also what keeps the labels from
clipping at 125 % or 150 %.

Appearance changes reach **every window immediately**, including the result bubble: all windows
repaint through one central subscription (`Ui/AppearanceSync.cs`), so a new window only has to
implement `IThemedWindow` and register itself.

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
| Update fails | The app uses `git pull --ff-only` and keeps local changes; see the message in the System tab |

---

## License & credits

Rewritten from `img-to-text` by the same author. The "Yuuka/senpai" tone in messages and comments is
kept from the original.
