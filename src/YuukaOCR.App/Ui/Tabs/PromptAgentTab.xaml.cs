using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private bool _loading = true;

    public PromptAgentTab() => InitializeComponent();

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
        BuildActionTabs();
        _loading = false;
    }

    // -------------------------------------------------------------------- OCR

    private void OnPromptEnabled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = PromptEnabledBox.IsChecked == true;
        _services.Settings.Update(s => s.PromptEnabled = enabled);
    }

    private void OnPromptLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var prompt = PromptBox.Text ?? "";
        _services.Settings.Update(s => s.CustomPrompt = prompt);
    }

    // ------------------------------------------------------------------ Agent

    private void OnInstructionLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var instruction = InstructionBox.Text ?? "";
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

            if (selected) button.Background = (Brush)FindResource("YuukaAccent");
            button.Click += OnActionTabClick;
            ActionTabs.Children.Add(button);
        }

        NewActionButton.IsEnabled = actions.Count < AgentActions.Max;
        ActionCount.Text = $"{actions.Count}/{AgentActions.Max}";

        var current = actions.FirstOrDefault(a => a.Id == _selectedId);
        ActionEditor.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (current is not null)
        {
            ActionNameBox.Text = current.Name;
            ActionInstructionBox.Text = current.Instruction;
            BuildIconPicker(current.Icon);
        }
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
            var brush = (Brush)FindResource(selected ? "YuukaOnAccent" : "YuukaText");

            var button = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 4, 4),
                Style = (Style)FindResource("YuukaIconButton"),
                Tag = key,
                Content = ActionIcons.Glyph(key, 14, brush),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            if (selected) button.Background = (Brush)FindResource("YuukaAccent");
            button.Click += OnIconClick;
            IconPicker.Children.Add(button);
        }
    }

    private void OnActionTabClick(object sender, RoutedEventArgs e)
    {
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

    private void OnActionNameLostFocus(object sender, RoutedEventArgs e)
    {
        var id = _selectedId;
        if (_loading || id is null) return;

        var name = (ActionNameBox.Text ?? "").Trim();
        _services.Settings.Update(s =>
        {
            var action = s.Actions.FirstOrDefault(a => a.Id == id);
            if (action is not null) action.Name = name;
        });
        BuildActionTabs();
    }

    private void OnActionInstructionLostFocus(object sender, RoutedEventArgs e)
    {
        var id = _selectedId;
        if (_loading || id is null) return;

        var instruction = ActionInstructionBox.Text ?? "";
        _services.Settings.Update(s =>
        {
            var action = s.Actions.FirstOrDefault(a => a.Id == id);
            if (action is not null) action.Instruction = instruction;
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

    /// <summary>One row in the provider combo: the id that gets stored, and the label that is shown.</summary>
    private sealed record ComboItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
