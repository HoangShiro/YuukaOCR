using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using YuukaOCR.App.Services;
using YuukaOCR.App.Ui.Controls;
using YuukaOCR.Core.Agent;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Providers;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.App.Ui.Tabs;

/// <summary>
/// Prompt/Agent tab: the OCR prompt (moved here from System and renamed), the agent's persona and
/// sampling settings, and the browser-style editor for the result window's action buttons.
///
/// The three sections mirror the request: OCR first, Agent second, Actions last.
/// </summary>
public partial class PromptAgentTab : UserControl
{
    private AppServices _services = null!;
    private string? _selectedId;

    /// <summary>
    /// The action the two editors currently hold text for. Not always <see cref="_selectedId"/>: a
    /// commit can run in the middle of a selection change, and the text in the boxes then belongs to the
    /// action it was typed for — never to the one that is about to be selected.
    /// </summary>
    private string? _editorActionId;

    private bool _loading = true;

    // Drag-to-reorder state for the action tabs. A drag only starts after the pointer really moved,
    // so a plain click still selects the tab.
    private Point? _tabDragStart;
    private string? _tabDragId;
    private bool _suppressTabClick;

    public PromptAgentTab()
    {
        InitializeComponent();
        ActionTabs.DragOver += OnTabDragOver;
        ActionTabs.Drop += OnTabDrop;
    }

    public void Initialize(AppServices services)
    {
        _services = services;
        _loading = true;

        var settings = services.Settings.Settings;

        PromptEnabledBox.IsChecked = settings.PromptEnabled;
        PromptBox.Text = settings.CustomPrompt;
        InstructionBox.Text = settings.Agent.Instruction;

        BuildProviders();
        RefreshModelList();
        AgentModelPicker.TextCommitted += (_, _) => CommitModel();

        BindSlider(TemperatureSlider, SliderSpecs.Get(SliderSpecs.AgentTemperature), settings.Agent.Temperature);
        BindSlider(TokensSlider, SliderSpecs.Get(SliderSpecs.AgentContextTokens), settings.Agent.ContextTokens);
        UpdateTemperatureLabel();
        UpdateTokensLabel();

        _selectedId = settings.Actions.Count > 0 ? settings.Actions[0].Id : null;

        // One reusable expand/reset pair per prompt field. The default provider is a function so the
        // action instruction always answers for whichever tab is selected right now.
        OcrPromptTools.Initialize(services, PromptBox, "agent.ocr.prompt", () => AppConfig.DefaultPromptText);
        OcrPromptTools.Committed += (_, _) => SavePrompt();

        InstructionTools.Initialize(services, InstructionBox, "agent.instruction",
            () => AgentSettings.DefaultInstruction);
        InstructionTools.Committed += (_, _) => SaveInstruction();

        ActionInstructionTools.Initialize(services, ActionInstructionBox, "agent.action.instruction",
            () => AgentActions.DefaultInstruction(_selectedId));
        ActionInstructionTools.Committed += (_, _) => SaveActionInstruction();

        BuildActionTabs();
        _loading = false;
    }

    // -------------------------------------------------------------------- OCR

    /// <summary>
    /// Writes every editor on this tab into settings.
    ///
    /// The tab used to persist on <c>LostFocus</c> only, which is not enough: the icon picker, the action
    /// chips and the expand/reset buttons are <c>Focusable=False</c> on purpose (they must not steal the
    /// caret), so clicking one moves no focus, fires no LostFocus — and the handler that rebuilds the tab
    /// from settings then overwrote the field the user had just typed into. The reported case was
    /// "type an action instruction, click an icon, the instruction is back to its default". Every path
    /// that rebuilds anything therefore commits first, and so does closing the window.
    /// </summary>
    public void CommitEdits()
    {
        if (_loading) return;

        SavePrompt();
        SaveInstruction();
        SaveActionName(rebuild: false);
        SaveActionInstruction();
    }

    private void OnPromptEnabled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = PromptEnabledBox.IsChecked == true;
        _services.Settings.Update(s => s.PromptEnabled = enabled);
    }

    private void OnPromptLostFocus(object sender, RoutedEventArgs e) => SavePrompt();

    private void SavePrompt()
    {
        if (_loading) return;

        var prompt = PromptBox.Text ?? "";
        // No write when nothing changed: every Update repaints every open window (AppearanceSync), and
        // LostFocus fires on every click into and out of the field.
        if (prompt == _services.Settings.Settings.CustomPrompt) return;

        _services.Settings.Update(s => s.CustomPrompt = prompt);
    }

    // ------------------------------------------------------------------ Agent

    private void OnInstructionLostFocus(object sender, RoutedEventArgs e) => SaveInstruction();

    private void SaveInstruction()
    {
        if (_loading) return;

        var instruction = InstructionBox.Text ?? "";
        if (instruction == _services.Settings.Settings.Agent.Instruction) return;

        _services.Settings.Update(s => s.Agent.Instruction = instruction);
    }

    private void BuildProviders()
    {
        var settings = _services.Settings.Settings;

        ProviderBox.Items.Clear();

        // Empty id = follow the OCR provider, which is what a user who never opened this tab expects.
        ProviderBox.Items.Add(new ComboItem("", L.T("agent.follow")));

        foreach (var info in ProviderRegistry.All.OrderBy(p => p.DisplayName, StringComparer.CurrentCulture))
            ProviderBox.Items.Add(new ComboItem(info.Id, info.DisplayName));

        var current = settings.Agent.Provider;
        ProviderBox.SelectedIndex = 0;
        for (var i = 0; i < ProviderBox.Items.Count; i++)
        {
            if (ProviderBox.Items[i] is ComboItem item && item.Id == current)
            {
                ProviderBox.SelectedIndex = i;
                break;
            }
        }
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderBox.SelectedItem is not ComboItem item) return;

        _services.Settings.Update(s => s.Agent.Provider = item.Id);
        RefreshModelList();
    }

    /// <summary>
    /// Fills the model picker with what is known without a network round-trip: the provider's suggested
    /// models, the ones this user picked before, and whatever is currently saved. Anything else can be
    /// typed — <see cref="ModelPicker"/> commits free text.
    /// </summary>
    private void RefreshModelList()
    {
        var settings = _services.Settings.Settings;
        var providerId = string.IsNullOrWhiteSpace(settings.Agent.Provider)
            ? settings.ApiProvider
            : settings.Agent.Provider;

        var models = new List<string>(ProviderRegistry.Get(providerId).SuggestedModels);

        if (settings.RecentModels.TryGetValue(providerId, out var recent)) models.AddRange(recent);
        if (!string.IsNullOrWhiteSpace(settings.Agent.Model)) models.Add(settings.Agent.Model);

        var ranked = ModelRanking.Rank(models.Distinct(StringComparer.OrdinalIgnoreCase));
        AgentModelPicker.SetModels(ranked);
        AgentModelPicker.Text = settings.Agent.Model;
    }

    private void BindSlider(Slider slider, SliderSpec spec, double value)
    {
        SliderSetup.Apply(slider, spec.Key);
        slider.Value = spec.Snap(value);
    }

    private void OnTemperatureChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTemperatureLabel();
        if (_loading) return;

        var value = SliderSpecs.Get(SliderSpecs.AgentTemperature).Snap(e.NewValue);
        _services.Settings.Update(s => s.Agent.Temperature = value);
    }

    private void OnTokensChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTokensLabel();
        if (_loading) return;

        var value = (int)SliderSpecs.Get(SliderSpecs.AgentContextTokens).Snap(e.NewValue);
        _services.Settings.Update(s => s.Agent.ContextTokens = value);
    }

    private void UpdateTemperatureLabel() =>
        TemperatureValue.Text = SliderSpecs.Get(SliderSpecs.AgentTemperature).Format(TemperatureSlider.Value);

    private void UpdateTokensLabel() =>
        TokensValue.Text = SliderSpecs.Get(SliderSpecs.AgentContextTokens).Format(TokensSlider.Value);

    // ---------------------------------------------------------------- Actions

    private void BuildActionTabs()
    {
        // Whatever is in the editors belongs to the selection that is still current — commit it before
        // reading settings back into the fields, or the rebuild overwrites it (see CommitEdits).
        CommitEdits();

        var actions = _services.Settings.Settings.Actions;

        ActionTabs.Children.Clear();
        foreach (var action in actions)
        {
            var selected = action.Id == _selectedId;

            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(action.Name) ? L.T("agent.action.unnamed") : action.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 11
                },
                Style = (Style)FindResource("YuukaChipButton"),
                Margin = new Thickness(0, 0, 4, 4),
                Padding = new Thickness(9, 5, 9, 5),
                MinWidth = 62,
                MaxWidth = 130,
                Tag = action.Id,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            // Selected = accent fill with on-accent label, referenced rather than captured so the chip
            // follows a theme switch while the tab is open.
            if (selected)
            {
                button.SetResourceReference(Control.BackgroundProperty, "YuukaAccent");
                button.SetResourceReference(Control.ForegroundProperty, "YuukaOnAccent");
            }

            button.Click += OnActionTabClick;
            button.PreviewMouseLeftButtonDown += OnTabMouseDown;
            button.PreviewMouseMove += OnTabMouseMove;
            ActionTabs.Children.Add(button);
        }

        NewActionButton.IsEnabled = actions.Count < AgentActions.Max;
        ActionCount.Text = $"{actions.Count}/{AgentActions.Max}";
        ExportPresetButton.IsEnabled = actions.Count > 0;
        // The default instruction belongs to the *selected* action, so it changes with the selection.
        ActionInstructionTools.Refresh();

        var current = actions.FirstOrDefault(a => a.Id == _selectedId);
        ActionEditor.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (current is not null)
        {
            ActionNameBox.Text = current.Name;
            ActionInstructionBox.Text = current.Instruction;
            BuildIconPicker(current.Icon);
        }

        // The editors now hold this action's text, so a later commit must write it back to this action.
        _editorActionId = current?.Id;
    }

    private void CommitModel()
    {
        var model = (AgentModelPicker.Text ?? "").Trim();
        _services.Settings.Update(s => s.Agent.Model = model);
    }

    private void BuildIconPicker(string currentIcon)
    {
        IconPicker.Children.Clear();

        foreach (var key in ActionIcons.Keys)
        {
            var selected = string.Equals(key, currentIcon, StringComparison.OrdinalIgnoreCase);

            var button = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 4, 4),
                Style = (Style)FindResource("YuukaIconButton"),
                Tag = key,

                // The glyph names a theme resource instead of capturing a brush: a captured instance keeps
                // the colour it was built with, so every icon here used to stay in the old theme after a
                // light/dark switch until the tab was rebuilt.
                Content = ActionIcons.Glyph(key, 14, selected ? "YuukaOnAccent" : "YuukaText"),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            if (selected) button.SetResourceReference(Control.BackgroundProperty, "YuukaAccent");
            button.Click += OnIconClick;
            IconPicker.Children.Add(button);
        }
    }

    private void OnActionTabClick(object sender, RoutedEventArgs e)
    {
        // A click that ends a drag must not also select the tab it was dropped on.
        if (_suppressTabClick)
        {
            _suppressTabClick = false;
            return;
        }

        if (sender is not Button { Tag: string id }) return;
        _selectedId = id;
        BuildActionTabs();
    }

    private void OnNewAction(object sender, RoutedEventArgs e)
    {
        var actions = _services.Settings.Settings.Actions;
        if (actions.Count >= AgentActions.Max) return;

        var action = new AgentAction
        {
            Id = AgentActions.NewId(),
            Name = L.T("agent.action.new"),
            Icon = AgentActions.DefaultIcon,
            // Starts empty on purpose: the tab stays selected so the user writes the instruction, and the
            // result window only shows buttons that have one.
            Instruction = ""
        };

        _selectedId = action.Id;
        _services.Settings.Update(s => s.Actions.Add(action));
        BuildActionTabs();
    }

    private void OnDeleteAction(object sender, RoutedEventArgs e)
    {
        var id = _selectedId;
        if (id is null) return;

        _services.Settings.Update(s => s.Actions.RemoveAll(a => a.Id == id));
        _selectedId = _services.Settings.Settings.Actions.FirstOrDefault()?.Id;
        BuildActionTabs();
    }

    private void OnActionNameLostFocus(object sender, RoutedEventArgs e) => SaveActionName(rebuild: true);

    /// <summary>
    /// Writes the action name. <paramref name="rebuild"/> is false while committing pending edits, because
    /// the caller is about to rebuild anyway (and rebuilding from inside a rebuild would loop).
    /// </summary>
    private bool SaveActionName(bool rebuild)
    {
        var id = _editorActionId;
        if (_loading || id is null) return false;

        var name = (ActionNameBox.Text ?? "").Trim();
        var action = _services.Settings.Settings.Actions.FirstOrDefault(a => a.Id == id);
        if (action is null) return false;

        if (name != action.Name)
        {
            _services.Settings.Update(s =>
            {
                var target = s.Actions.FirstOrDefault(a => a.Id == id);
                if (target is not null) target.Name = name;
            });
        }

        if (rebuild) BuildActionTabs();
        return true;
    }

    private void OnActionInstructionLostFocus(object sender, RoutedEventArgs e) => SaveActionInstruction();

    private void SaveActionInstruction()
    {
        var id = _editorActionId;
        if (_loading || id is null) return;

        var instruction = ActionInstructionBox.Text ?? "";
        var action = _services.Settings.Settings.Actions.FirstOrDefault(a => a.Id == id);
        if (action is null || instruction == action.Instruction) return;

        _services.Settings.Update(s =>
        {
            var target = s.Actions.FirstOrDefault(a => a.Id == id);
            if (target is not null) target.Instruction = instruction;
        });
    }

    private void OnIconClick(object sender, RoutedEventArgs e)
    {
        var id = _selectedId;
        if (id is null || sender is not Button { Tag: string icon }) return;

        _services.Settings.Update(s =>
        {
            var action = s.Actions.FirstOrDefault(a => a.Id == id);
            if (action is not null) action.Icon = AgentActions.NormalizeIcon(icon);
        });
        BuildActionTabs();
    }

    // ------------------------------------------------- tab order / presets

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        _suppressTabClick = false;
        if (sender is not Button { Tag: string id }) return;
        _tabDragStart = e.GetPosition(ActionTabs);
        _tabDragId = id;
    }

    /// <summary>
    /// Starts a reorder drag once the pointer has actually moved. A click never moves, so selecting a
    /// tab keeps working; the flag only exists to stop the click that ends a drag from selecting.
    /// </summary>
    private void OnTabMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragStart is null || _tabDragId is null) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _tabDragStart = null;
            _tabDragId = null;
            return;
        }

        var point = e.GetPosition(ActionTabs);
        if (Math.Abs(point.X - _tabDragStart.Value.X) < 6 && Math.Abs(point.Y - _tabDragStart.Value.Y) < 6)
            return;

        var id = _tabDragId;
        _tabDragStart = null;
        _tabDragId = null;
        _suppressTabClick = true;

        if (sender is DependencyObject source) DragDrop.DoDragDrop(source, id, DragDropEffects.Move);
    }

    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(string)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTabDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(string)) is not string id) return;
        e.Handled = true;
        MoveAction(id, IndexAt(e.GetPosition(ActionTabs)));
    }

    /// <summary>Insertion index for a horizontal drop: the number of tab centres left of the pointer.</summary>
    private int IndexAt(Point point)
    {
        var index = 0;
        foreach (var child in ActionTabs.Children.OfType<FrameworkElement>())
        {
            var centre = child.TranslatePoint(new Point(child.ActualWidth / 2, 0), ActionTabs).X;
            if (point.X > centre) index++;
        }
        return index;
    }

    /// <summary>
    /// Moves one action to <paramref name="targetIndex"/> in the settings list. That list *is* the
    /// button order in the result window (and the JSON order on disk), so the bar follows immediately
    /// through the appearance fan-out and the order survives a restart.
    /// </summary>
    private void MoveAction(string id, int targetIndex)
    {
        var actions = _services.Settings.Settings.Actions;
        var from = actions.FindIndex(a => a.Id == id);
        if (from < 0) return;

        var to = Math.Clamp(targetIndex, 0, actions.Count);
        if (to > from) to--;                      // the removal shifts everything after it left
        if (to == from) return;

        _services.Settings.Update(s =>
        {
            var action = s.Actions[from];
            s.Actions.RemoveAt(from);
            s.Actions.Insert(Math.Clamp(to, 0, s.Actions.Count), action);
        });

        BuildActionTabs();
        ActionStatus.Text = L.T("agent.reordered");
    }

    private void OnExportPreset(object sender, RoutedEventArgs e)
    {
        var actions = _services.Settings.Settings.Actions;
        if (actions.Count == 0)
        {
            ActionStatus.Text = L.T("agent.exportEmpty");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = L.T("agent.export"),
            Filter = L.T("agent.presetFilter"),
            DefaultExt = ".json",
            AddExtension = true,
            FileName = AgentPreset.SuggestedFileName(DateTime.Now)
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            // UTF-8 without BOM: the file is meant to be read by humans and by other machines.
            File.WriteAllText(dialog.FileName,
                AgentPreset.Export(actions, DateTime.Now.ToString("o")),
                new UTF8Encoding(false));
            ActionStatus.Text = L.F("agent.exportDone", Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            ActionStatus.Text = L.F("agent.exportFailed", ex.Message);
        }
    }

    /// <summary>
    /// Loads a preset over the current tabs. The confirmation comes first, the file is validated before
    /// anything is touched, and a rejected file leaves the loaded configuration exactly as it was.
    /// </summary>
    private void OnImportPreset(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("agent.import"),
            Filter = L.T("agent.presetFilter"),
            DefaultExt = ".json",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        string text;
        try
        {
            text = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            ActionStatus.Text = L.F("agent.importFailed", ex.Message);
            return;
        }

        var preset = AgentPreset.Import(text);
        if (!preset.Success)
        {
            ActionStatus.Text = L.F("agent.importFailed", L.F(preset.ErrorKey!, preset.ErrorArg));
            return;
        }

        var answer = MessageBox.Show(L.F("agent.importConfirm", preset.Actions.Count), "YuukaOCR",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            ActionStatus.Text = L.T("agent.importCancelled");
            return;
        }

        _selectedId = preset.Actions[0].Id;
        _services.Settings.Update(s =>
        {
            s.Actions.Clear();
            s.Actions.AddRange(preset.Actions);
        });

        BuildActionTabs();
        ActionStatus.Text = L.F("agent.importDone", preset.Actions.Count);
    }

    /// <summary>One row in the provider combo: the id that gets stored, and the label that is shown.</summary>
    private sealed record ComboItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
