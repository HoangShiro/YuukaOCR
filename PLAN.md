# YuukaOCR — Implementation plan (Result Window, Action Bar, Agent, Settings, Hooking)

> Status legend: `[ ]` not started · `[x]` done and verified (build + tests + CLI run).
> This file is the working plan for the feature set below; it is updated as work lands.

---

## 0. Scope in one paragraph

The result bubble becomes a small interactive panel: selectable text, an icon-only copy button, and a
borderless action bar (registered action buttons + a chat box that talks to the configured LLM with
either the whole OCR result or just the selected text as context). Appearance settings must reach
every window the moment they change, through one shared mechanism. The settings window gains a
`Prompt/Agent` tab (OCR prompt, agent persona/provider/model/temperature/token budget, and a
browser-style action editor) and a reworked History tab (two tables side by side, a chat history
table, per-item copy/expand/remove with text selection). Hooking gains an `Inside` mode, safer
targets for fullscreen windows, and recalibrated defaults. Slider ticks/tracks get thicker and every
default is snapped onto its tick grid.

---

## 1. Architecture decisions

| # | Decision | Why |
| --- | --- | --- |
| 1 | **`Ui/AppearanceSync.cs`** — `IThemedWindow.ApplyAppearance()` + one settings subscription; windows call `AppearanceSync.Register(this)` and are held through weak references. | Requirement: appearance sync must be central and reusable, not per-window. The result window used to paint itself once and keep stale colours, which is exactly the bug this removes. A new window needs one line. |
| 2 | **`Core/Settings/SliderSpecs.cs`** — a single table of `(key, min, max, step, default, decimals)` for every slider; `Ui/Controls/SliderSetup.Apply(slider, key)` binds a control to it, and `Normalize()` *snaps* stored values onto the grid. | Kills the "default between two ticks" class of bug permanently: the range on screen and the range the clamp accepts cannot disagree. Also gives the test runner something to assert. |
| 3 | **`Core/Agent/`** — `AgentService` (chat + actions + shared history + token budget), `AgentPrompts` (pure composition/trimming helpers), `AgentAction` (one registered action). | Actions are just pre-composed chat turns, so both must go through one service with one history. Composition lives in pure static helpers so it is testable without WPF. |
| 4 | **`LlmRequest` grows `History`, `SystemPrompt`, `Temperature`, `JsonMode`** (all optional, appended). | Multi-turn chat and plain-text answers are needed; OCR keeps its current behaviour (JSON mode, 0.2). Positional call sites keep compiling. |
| 5 | **`Ui/SelectionCopy.cs`** — `Attach(TextBox)` adds the floating icon-only copy button next to the selection. Used by every History row; the result bubble keeps its own richer popup because a selection there also switches the chat scope. | History 2 and Result Window 4 are the same interaction; the bubble needs one extra behaviour (scope), so it owns its popup and everything else shares the helper. |
| 6 | **`Ui/Controls/ActionIcons.cs`** — one table of vector path data by key (`chat`, `translate`, `summarize`, …), plus the window-chrome glyphs (`copy`, `send`, `expand`, `collapse`, `remove`, `plus`). Core stores only the key. | Core must not reference WPF; the icon set still lives in exactly one place. |
| 7 | **`Core/Interop/WindowTracker`** gains `HookMode { Outside, Inside }`, mode-aware target math and clamping to the monitor work area. | Hooking math becomes testable and can no longer place the bar off the work area of a fullscreen window. |
| 8 | **`Core/Logging/ApiTrace.cs`** — request/response trace to `user/api-debug.log`, enabled only by `YUUKA_API_DEBUG=1`; `build.ps1` deletes stray trace files before publishing. | Requirement: API logs for debugging, gone in the production build. |
| 9 | **History rows are built in code** inside `LogTab`, not a `DataTemplate`. | A row needs a live selection-copy `TextBox`, a per-item expand state and three icon buttons; a template would need code-behind hooks for all of it. |
| 10 | **JSON keys are preserved** (`custom_prompt`); only labels/placement change. New keys: `agent`, `actions`, `hook_mode`. | Existing `user/settings.json` files must keep working; the rename is a UI change. |
| 11 | **A half-written action is kept, not deleted.** `AppSettings.NormalizeActions` caps the list at 10 and repairs duplicate ids, but keeps an action with an empty instruction; the result window skips those. | A user who adds a tab and has not typed the instruction yet must not lose the tab on the next save. |

---

## 2. Files (as built)

### New — Core

| File | Contents |
| --- | --- |
| `Settings/SliderSpecs.cs` | `SliderSpec` record + `SliderSpecs.All` / `Get` / `Snap` / `Default`. |
| `Settings/AgentSettings.cs` | `Instruction`, `Provider`, `Model`, `Temperature`, `ContextTokens` + `Normalize()`. |
| `Agent/AgentAction.cs` | `AgentAction` (`Id`, `Name`, `Icon`, `Instruction`), `AgentActions.Max = 10`, icon whitelist, `Defaults()`. |
| `Agent/AgentPrompts.cs` | `ChatTurn`, `AgentReply`, `TokensOf`, `TrimHistory`, `SystemPrompt`, `ComposeAction` — pure, unit-testable. |
| `Agent/AgentService.cs` | Shared conversation, chat + action execution, provider/model resolution, persistence through `LogService`. |
| `Logging/ApiTrace.cs` | Opt-in request/response trace. |

### Modified — Core

| File | Change |
| --- | --- |
| `Settings/AppSettings.cs` | `Agent`, `Actions`, `HookMode`; hooking defaults from `SliderSpecs`; `Normalize()` snaps to `SliderSpecs`, normalizes hook mode and the action table. |
| `Settings/ThemeSettings.cs` | Opacity / font size snap to their slider grid. |
| `Providers/LlmTypes.cs` | `LlmTurn`; `LlmRequest.History/SystemPrompt/Temperature/JsonMode`. |
| `Providers/OpenAiCompatibleProvider.cs` | system + multi-turn messages, temperature, JSON mode only when asked; API trace. |
| `Providers/GeminiProvider.cs` | `systemInstruction`, multi-turn `contents`, temperature, JSON only when asked. |
| `Providers/AnthropicProvider.cs` | top-level `system`, multi-turn `messages`, temperature. |
| `Interop/WindowTracker.cs` | `HookMode`, `ParseMode`/`SerializeMode`/`NormalizeMode`, mode-aware target math, on-screen clamping. |
| `Logging/ActivityLog.cs` | `ChatHistory` (`ChatEntry`: stamp, role, text), `MaxChatEntries = 200`. |
| `Logging/LogService.cs` | `AddChatTurn`, `GetChatHistory`, `ClearChatHistory`, `RemoveChatTurn`, `RemoveOutput`, `RemovePrompt`. |
| `Localization/Strings*.cs` (10 files) | New keys for the tab, action bar, chat, history and hooking. |

### New — App

| File | Contents |
| --- | --- |
| `Ui/AppearanceSync.cs` | `IThemedWindow` + central appearance subscription. |
| `Ui/SelectionCopy.cs` | Floating copy button attached to any `TextBox`. |
| `Ui/Controls/ActionIcons.cs` | Icon geometry table + glyph factory. |
| `Ui/Controls/SliderSetup.cs` | Binds a `Slider` to its `SliderSpec`. |
| `Ui/Tabs/PromptAgentTab.xaml(.cs)` | `Prompt/Agent` tab: OCR · Agent · Actions (browser tabs + `+`). |

### Modified — App

| File | Change |
| --- | --- |
| `Ui/ResultWindow.xaml(.cs)` | Selectable text, copy button bottom-right, transcript pane, borderless action bar, agent wiring, `AppearanceSync`. |
| `Ui/MainWindow.cs` | `AppearanceSync`; mode-aware hooking; clamped targets. |
| `Ui/ConfigWindow.xaml(.cs)` | `Prompt/Agent` tab added (8 tabs), `AppearanceSync`, tab indices for History/Status refresh. |
| `Ui/Tabs/SystemTab.xaml(.cs)` | Custom-prompt block removed (moved to Prompt/Agent as `OCR prompt`). |
| `Ui/Tabs/HookingTab.xaml(.cs)` | Hook mode selector (Outside/Inside), ranges from `SliderSpecs`. |
| `Ui/Tabs/ThemeTab.xaml(.cs)` | Slider ranges from `SliderSpecs`. |
| `Ui/Tabs/LayoutTab.xaml(.cs)` | Slider ranges from `SliderSpecs`. |
| `Ui/Tabs/LogTab.xaml(.cs)` | Rebuilt: two tables in one row, chat history below, per-row copy/expand/remove, selection copy. |
| `Ui/Styles.xaml` | Thicker slider track/ticks, icon-button style. |
| `Ui/Controls/TickMarks.cs` | Tick length 4 → 6, pen 1.5 → 3.0 (double thickness). |
| `Services/AppServices.cs` | Owns `AgentService`. |
| `App.xaml.cs` | `AppearanceSync.Initialize(Services.Settings)` before the first window. |

### Tests / build / docs

| File | Change |
| --- | --- |
| `tests/YuukaOCR.Tests/Program.cs` | 36 → 42 tests: slider grid/snapping/unique keys, action table normalization, action prompt composition, token trimming. |
| `build.ps1` | Deletes `user/api-debug.log` (and a stray copy next to the exe) before publishing. |
| `README.md`, `PROJECT_STRUCTURE.md` | New tab, action bar, agent, history, hooking mode, file map. |
| `PLAN.md` | This file, kept current. |

---

## 3. Behaviour spec (what "done" means)

### 3.1 Appearance synchronisation
- Any window that implements `IThemedWindow` and calls `AppearanceSync.Register(this)` repaints
  **immediately** on a preset / opacity / font / scale change: colours, font family, font size,
  corner radius, min/max width where applicable.
- `ResultWindow` re-applies scale-derived values (`MinWidth`, `MaxWidth`, `CornerRadius`, font size,
  action-button size) on every change.
- New windows need the interface plus one `Register` line — no per-window settings subscription.

### 3.2 Result Window
- Text is selectable; click-to-copy is gone.
- Selecting text shows a floating icon-only copy button next to the selection; clicking it copies
  exactly the selected text and suppresses the clipboard watcher.
- Icon-only `Copy` button in the bottom-right corner, directly below `Close`.
- Borderless action bar at the bottom **inside** the window: `[action buttons] [context toggle | chat box | Send]`,
  one horizontal row, never wrapping (actions scroll horizontally, chat keeps a minimum width).
- Action buttons: one per registered action, icon-only, max 10, tooltip = action name.
- Chat context toggle: whole result vs selected text (defaults to the selection when there is one).
- Chat transcript: the turns of this bubble's slice of the shared conversation.

### 3.3 Agent
- One shared conversation for the chat box and every action; persisted in `user/log.json`
  (`chat_history`) and shown in History → Chat history.
- `Agent instruction` is the system prompt; provider/model/temperature come from `Settings → Prompt/Agent → Agent`.
  An empty agent provider follows the OCR provider; an empty agent model follows the OCR **model**
  while the provider is the same, otherwise the provider's default model.
- `Context Token` is the history budget; oldest turns are dropped first, a single oversized turn is
  truncated, the current message and the system prompt are never dropped.
- Chat turns put the OCR context in the system prompt; an action turn puts the `Action instruction`
  and the context in the user turn (`ComposeAction`), so the text is never sent twice.
- An action turn = `Agent instruction` + `Action instruction` + context, sent through the same
  conversation.

### 3.4 Settings
- Slider ticks are twice as thick, tracks twice as thick.
- Every slider's default lies exactly on a tick (`SliderSpecs` + test).
- `Prompt/Agent` tab: `OCR` (prompt + enable toggle, moved from System) → `Agent` (instruction;
  provider+model on one row; temperature+context token on one row) → `Actions` (browser tabs, `+`,
  per-tab icon picker and action instruction, `×` to remove, max 10).
- History tab: `Recent results` and `Prompts used` side by side, `Chat history` below; each row has
  icon-only copy/expand/remove bottom-right, selectable text with the floating copy button, and
  expand grows the row to its full content (the button flips to collapse).

### 3.5 Hooking
- `Outside` (existing) and `Inside` (new) modes; the bar sits on the inner edge in `Inside`.
- Targets are clamped to the monitor work area, so a fullscreen app cannot push the bar off-screen.
- Defaults: proximity 100/100, offsets 0/0, release 100, spring 0.10, damping 0.40, bounce 0.30.

---

## 4. Phases

### Phase 1 — Core foundations
- [x] `SliderSpecs` table + snapping helpers
- [x] `AgentSettings`, `AgentAction`, `AgentPrompts`
- [x] `LlmTypes` extension + three providers (multi-turn, temperature, JSON mode, trace)
- [x] `AgentService`
- [x] `ActivityLog` / `LogService` chat history
- [x] `WindowTracker` hook mode + clamping
- [x] `ApiTrace`
- [x] `AppSettings` wiring + recalibrated defaults

### Phase 2 — Core tests (CLI)
- [x] slider defaults on grid, snapping, unique keys
- [x] action table normalization (cap 10, unique ids, icon fallback)
- [x] prompt composition + token trimming
- [x] chat history persistence/removal (covered by the CLI probe below)
- [ ] multi-turn payload against a local `HttpListener` (covered end-to-end by the live CLI probe instead)

### Phase 3 — App infrastructure
- [x] `AppearanceSync` + adopt in `MainWindow`, `ConfigWindow`, `ResultWindow`
- [x] `ActionIcons`, `SelectionCopy`, `SliderSetup`
- [x] `Styles.xaml`: thicker slider, icon-button style
- [x] `TickMarks`: double tick thickness

### Phase 4 — Result Window
- [x] XAML rebuild (selectable text, right rail with Close/Copy, transcript, action bar)
- [x] selection copy, copy button, close behaviour
- [x] action buttons + chat box + context toggle + send
- [x] agent wiring, busy state, error display

### Phase 5 — Settings UI
- [x] `PromptAgentTab` (OCR / Agent / Actions)
- [x] `SystemTab`: prompt block removed
- [x] `HookingTab`: mode selector + new ranges/defaults
- [x] `ThemeTab` / `LayoutTab`: ranges from `SliderSpecs`
- [x] `LogTab` rebuild
- [x] `ConfigWindow`: new tab + indices

### Phase 6 — Localization
- [x] new keys in all 10 tables, every table in sync (parity test green)

### Phase 7 — Verification
- [x] `dotnet build -c Release` clean
- [x] test runner green (42/42)
- [x] CLI/API-log run against the configured provider (see §6)
- [x] `build.ps1` cleans API trace files before publish
- [x] `README.md` / `PROJECT_STRUCTURE.md` updated
- [x] `PLAN.md` checkboxes closed

---

## 5. Risks / mitigations

| Risk | Mitigation |
| --- | --- |
| Text selection needs focus, but the bubble is `ShowActivated=False` (preview). | Selection works as soon as the window is active; the preview path stays non-activating on purpose. The copy button stays available without selection. |
| The action bar could force the bubble wider than `MaxWidth`. | Actions live in a horizontal `ScrollViewer`; the chat box takes the remaining space with a minimum, so nothing wraps. |
| Chat history growth. | `ActivityLog.MaxChatEntries` cap + `Context Token` budget on every request. |
| Changing slider steps moves stored values. | `Normalize()` snaps to the grid — a value can only land on a tick, never between two. |
| Snapping the hook target could fight the spring. | The target is clamped once per tick before `SetTarget`; the spring still owns the motion. |
| New localization keys drifting from English. | The existing table test fails on any missing/extra key; every new key gets a real translation (the 80 % difference test would fail on English copies). |
| A fresh action with no instruction could look broken. | The settings tab keeps the empty action; the result window hides buttons without an instruction. |

---

## 6. Verification log (CLI + API logs, no GUI)

Run from `G:\9 - Private\YuukaOCR`:

```
dotnet build YuukaOCR.slnx -c Release            -> Build succeeded, 0 warnings
dotnet run --project tests/YuukaOCR.Tests -c Release
                                                 -> 42/42 passed
YUUKA_API_DEBUG=1 dotnet run --project tools/probe/probe.csproj -c Release
```

The probe (temporary, deleted afterwards) built `SettingsStore` + `LogService` + `AgentService` from
the real `user/` files and made one chat call and one action call. Output:

```
provider   = openrouter  model='@preset/code'
agent      = provider='' model='' temp=0,75 tokens=8000
actions    = 3  hook_mode=outside
chat       = ok=True text='OK'
action     = ok=True text='Reads screen text, answers questions.'
history    = turns=4 logged=4
trace file = ...\user\api-debug.log exists=True
```

`user/api-debug.log` confirmed the request shape:

- chat: OCR context inside the **system** message, temperature 0.75, model `@preset/code`;
- action: system prompt without context, shared history (`user`/`assistant` turns) followed by the
  composed user turn `"<action instruction>\n\n<context label>:\n<context>"`.

The same run also proved that the older `user/settings.json` (with `agent`, `actions` and
`hook_mode` all `null`) loads with defaults instead of crashing.

### XAML smoke test (`tools/xamlcheck`)

`dotnet build` cannot see a duplicate resource key or a missing `StaticResource` — those are runtime
errors. This first smoke run of `YuukaOCR.exe --config` died on
`ArgumentException: Item has already been added. Key in dictionary: 'YuukaIconButton'` (two styles
with the same key in `Styles.xaml`), so the tool was added:

```
dotnet run --project tools/xamlcheck -c Release
OK    SystemTab / ThemeTab / LayoutTab / HookingTab / PromptAgentTab / LogTab / StatusTab /
      ConsoleTab / SnipOverlay / ResultWindow
      history rows: outputs=2 prompts=0 chat=0
      chat rows after two turns: 2   (then 0 after clearing)
      agent tab action tabs: 3 (settings has 3 actions)
all XAML parsed
```

It constructs every tab through the real `App.xaml` resources, builds the result bubble with real
`AppServices`, and renders the History rows from the live `user/log.json` — all from the command
line, no GUI interaction.

---

## 7. Polish pass 2 — the reported defects

Six reports, each traced to a concrete cause rather than a style preference.

### 7.1 The thumb was clipped by its own row

The template put an 18 DIP thumb in a 15 DIP track row, inside a 34 DIP slider: the ellipse hung
1.5 DIP above the control (clipped by the window edge) and 1.5 DIP into the tick row. The tick row
itself was `*` of 19 DIP while `TickMarks` measured 21.25 DIP, so the labels were squeezed too.

Fixed in `Ui/Styles.xaml`: row 0 is 20 DIP, the tick row is `Auto`, the slider has no fixed `Height`
(a `MinHeight` of 38 remains) and `TickMarks` is top-aligned with a 1 DIP gap. `TickMarks` now
reserves the stroke's overhang in `MeasureOverride`, so a 6 DIP mark cannot poke out of its band.

### 7.2 Dense sliders read as a comb

`TickMarks.OnRender` measures the gap between marks; under 22 DIP every second mark is drawn at 38 %
opacity (the last mark always stays full so the top of the range is never faded).

### 7.3 Doubled marks on the snapping sliders

`SliderTicks.Thickness` is an attached property (default 3.0) that the slider template forwards into
its `TickMarks`. The five *Window edge snapping* sliders set it to 6.

### 7.4 The action *Name* field and the chat placeholder were cut off

Both had a fixed height (26 and 24 DIP) with `Padding` on top of it. Once the theme font or the UI
scale grows, the line box needs more than `Height − Padding` and the descenders are clipped — the
reported 125 % case. Both are now content-sized (`MinHeight` + padding, no `Height`), which also
survives 150 % and a larger theme font.

### 7.5 Buttons disappeared into the bubble on dark themes

The send button, the floating copy button and the action buttons were transparent with **on-accent**
ink — dark ink on a dark bubble. They now use `YuukaAccentIconButton`: accent fill, on-accent ink
(derived from the accent's luminance, so it flips with the preset), with hover at 82 % and pressed at
62 % of the fill. `SelectionCopy` (used by the History rows) uses the same style. The rail's
copy-all button stays a subtle icon: it is permanent chrome, not a contextual action.

### 7.6 A new action did not appear until the bubble was reopened

`ApplyAppearance` repainted colours but never rebuilt the action bar, so the bar only changed on
`ShowText`/`ApplyLocalization`. It now compares an action signature (id + name + icon of every action
that has an instruction) and rebuilds only when it changed — so a rename updates the tooltip, an add
appends a button and a remove drops it, while a slider drag costs nothing.

### Verification (CLI only, no GUI interaction)

```
dotnet build YuukaOCR.slnx -c Release                     Build succeeded, 0 warnings
dotnet run --project tests/YuukaOCR.Tests -c Release       42/42 passed
dotnet run --project tools/xamlcheck -c Release            all XAML parsed + all layout probes passed
```

The probes are new and measure the fixes instead of trusting them:

```
OK  ThemeTab/LayoutTab/HookingTab/PromptAgentTab: every thumb fully inside, ticks start below it
OK  HookingTab: 8 sliders, 5 with doubled marks
OK  ChatBox at 100/125/150 %: 30.4 / 34.4 / 37.6 DIP for a 23.6 / 27.5 / 31.4 DIP line
OK  ActionNameBox: 34.0 DIP for a 25.0 DIP line
OK  dense ticks alternate: major 255 ink vs minor 97; sparse ticks stay uniform (5 marks)
OK  accent buttons readable in all 8 presets (worst glyph contrast 5.9:1)
OK  send, floating copy and 3 action buttons use the accent style
OK  action bar sync: 3 -> 4 -> Renamed action -> 3 buttons
```

One harness bug was found on the way: `new YuukaOCR.App.App()` posts `OnStartup` onto the dispatcher,
so the first probe that pumped the queue took the single-instance mutex, created the real main window
and rebound `AppearanceSync` to the app's own settings store — which made the action-bar probe fail
for a reason that does not exist in the app. The harness now builds a plain `Application` and merges
`Styles.xaml` itself.
