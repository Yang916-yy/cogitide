using L = Workbench.Desktop.Localization;
using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Workbench.Protocol;

namespace Workbench.Desktop;

public partial class MainWindow : Window
{
    private readonly HostClient _host = new();
    private readonly ObservableCollection<TimelineEntry> _entries = [];
    private readonly ObservableCollection<SessionInfo> _sessions = [];
    private readonly ObservableCollection<ImageAttachment> _draftImages = [];
    private readonly Dictionary<string, TimelineEntry> _toolEntries = [];
    private readonly Dictionary<string, List<(string Text, TimelineEntry Entry)>> _steeringEntries = [];
    private readonly List<WorkbenchTerminalWindow> _terminals = [];
    private DesktopSettings _settings;
    private readonly bool _persistSettings;
    private ModelInfo[] _accountModels = [];
    private string? _accountBinding;
    private bool _accountKnown;
    private bool _loadingModels;
    private bool _modelsLoaded;
    private bool _newSessionBeforeSend;
    private AgentSession? _session;
    private string _workspace = "";
    private string[] _files = [];
    private TimelineEntry? _assistantEntry;
    private string? _pendingApproval;
    private bool _running;
    private bool _submitting;
    private bool _stopping;
    private bool _choosingImages;
    private bool _loadingSelection;
    private bool _ready;
    private int _loadGeneration;
    private double _inspectorWidth = 420;

    public MainWindow() : this(DesktopSettings.Load(), connectHost: true) { }

    // The same compiled window can be exercised headlessly without starting a Host or touching user preferences.
    internal MainWindow(DesktopSettings settings, bool connectHost)
    {
        _settings = settings.NormalizeBackend().RememberProvider(settings.Provider);
        _persistSettings = connectHost;
        InitializeComponent();
        L.Changed += RefreshLocalization;
        UiMotion.Changed += RefreshMotion;
        RefreshMotion(null, EventArgs.Empty);
        // Handle the shortcut before TextBox consumes Enter to insert a newline.
        PromptBox.AddHandler(KeyDownEvent, PromptKeyDown, RoutingStrategies.Tunnel);
        Timeline.ItemsSource = _entries;
        AttachmentList.ItemsSource = _draftImages;
        SessionList.ItemsSource = _sessions;
        _workspace = Program.WorkspacePath is { } explicitWorkspace && Directory.Exists(explicitWorkspace)
            ? explicitWorkspace : _settings.LastWorkspace is { } path && Directory.Exists(path) ? path : "";
        UpdateWorkspaceLabels();
        UpdateModelLabel();
        SetRunState(false);
        SizeChanged += (_, _) => ResizeInspector();
        _host.Notification += (method, value) => Dispatcher.UIThread.Post(() => OnNotification(method, value));
        _host.Disconnected += message => Dispatcher.UIThread.Post(() => { _ready = false; SetRunState(false); StatusLabel.Text = message; AddEntry("CONNECTION", message, "#F0B5A7"); });
        Opened += async (_, _) => { RefreshMotion(null, EventArgs.Empty); if (connectHost) await InitializeAsync(); };
        KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Escape || !_running) return;
            args.Handled = true;
            await GuardAsync(StopAsync);
        };
        Closed += (_, _) =>
        {
            L.Changed -= RefreshLocalization;
            UiMotion.Changed -= RefreshMotion;
            foreach (var terminal in _terminals.ToArray()) terminal.Close();
            SaveSettings();
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _host.StartAsync();
            _ready = true;
            await RefreshSessionsAsync();
            if (_settings.LastSessionId is { } id && _sessions.FirstOrDefault(x => x.Id == id) is { } saved)
            {
                _loadingSelection = true;
                SessionList.SelectedItem = saved;
                _loadingSelection = false;
                await LoadSessionAsync(saved.Id);
            }
            else if (!string.IsNullOrWhiteSpace(_workspace)) await RefreshWorkspaceAsync();
            if (_session is null) LocalizedControl.Text(StatusLabel, string.IsNullOrWhiteSpace(_workspace) ? "ReadyNoProject" : "ReadyProject");
            else ShowLoadedStatus(_session.Info.Status);
        }
        catch (Exception ex) { ShowError(ex); if (Program.SmokeTest) Environment.ExitCode = 1; }
        if (Program.ScreenshotPath is { } screenshot)
        {
            await Task.Delay(700);
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            bitmap.Render(this);
            bitmap.Save(screenshot, PngBitmapEncoderOptions.Default);
        }
        if (Program.SmokeTest)
        {
            await Task.Delay(1200);
            Close();
        }
    }

    private async void OpenProjectClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.Text("ChooseFolder"), AllowMultiple = false });
        var selected = folders.FirstOrDefault()?.TryGetLocalPath();
        if (selected is null) return;
        _workspace = Path.GetFullPath(selected);
        _session = null;
        _loadingSelection = true;
        SessionList.SelectedItem = null;
        _loadingSelection = false;
        _entries.Clear();
        _toolEntries.Clear();
        _steeringEntries.Clear();
        _assistantEntry = null;
        EmptyState.IsVisible = true;
        LocalizedControl.Text(SessionTitle, "NewSession");
        LocalizedControl.Text(RunStateLabel, "Ready");
        LocalizedControl.Text(StatusLabel, "ProjectOpened");
        UpdateWorkspaceLabels();
        SaveSettings();
        await RefreshWorkspaceAsync();
        PromptBox.Focus();
    });

    private async void NewSessionClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () => await CreateSessionAsync());

    private async Task CreateSessionAsync(string? title = null)
    {
        RequireWorkspace();
        _session = await _host.RequestAsync<AgentSession>("session.create", new { workspacePath = _workspace, title = title ?? L.Text("NewSession"), backend = SelectedBackend });
        _newSessionBeforeSend = false;
        _entries.Clear();
        _toolEntries.Clear();
        _steeringEntries.Clear();
        _assistantEntry = null;
        EmptyState.IsVisible = true;
        SessionTitle.Text = _session.Info.Title;
        LocalizedControl.Text(RunStateLabel, "Ready");
        LocalizedControl.Text(StatusLabel, "SessionReady");
        await RefreshSessionsAsync(_session.Info.Id);
        SaveSettings();
    }

    private async void SessionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingSelection || !_ready || _running || SessionList.SelectedItem is not SessionInfo info) return;
        await GuardAsync(async () => await LoadSessionAsync(info.Id));
    }

    private async Task LoadSessionAsync(string id)
    {
        var generation = ++_loadGeneration;
        var sessionTask = _host.RequestAsync<AgentSession>("session.load", new { sessionId = id });
        var eventsTask = _host.RequestAsync<AgentEvent[]>("session.events", new { sessionId = id });
        await Task.WhenAll(sessionTask, eventsTask);
        var loaded = await sessionTask;
        var events = await eventsTask;
        if (generation != _loadGeneration) return;
        RestoreSession(loaded, events);
        await RefreshWorkspaceAsync();
    }

    internal void RestoreSession(AgentSession loaded, IReadOnlyList<AgentEvent> events)
    {
        _session = loaded;
        _newSessionBeforeSend = false;
        _workspace = loaded.Info.WorkspacePath;
        _settings = _settings with { Backend = ComposerChoices.NormalizeBackend(loaded.Info.Backend) };
        SessionTitle.Text = loaded.Info.Title;
        _entries.Clear();
        _toolEntries.Clear();
        _steeringEntries.Clear();
        _assistantEntry = null;
        foreach (var message in loaded.Messages.Where(x => x.Role != "system"))
        {
            if (message.Role == "tool" && message.ToolCallId is { } callId && _toolEntries.TryGetValue(callId, out var toolEntry))
            {
                var failure = message.ToolIsError == true || events.LastOrDefault(evt => evt.Type == "tool.completed" && evt.Call?.Id == callId)?.Result?.IsError == true;
                toolEntry.Label = (failure ? "FAILED · " : "DONE · ") + toolEntry.Label.Split('·').Last().Trim();
                toolEntry.Text += "\n\n" + Limit(DisplayToolResult(new(message.Content)), 24000);
            }
            else if (!string.IsNullOrWhiteSpace(message.Content) || message.Images is { Count: > 0 })
            {
                if (message.Role == "user") AddUserEntry(Limit(message.Content, 24000), message.Images);
                else AddEntry(message.Role.ToUpperInvariant(), Limit(message.Role == "tool" ? DisplayToolResult(new(message.Content)) : message.Content, 24000), "#8CCFC4");
            }
            if (message.ToolCalls is { Count: > 0 })
                foreach (var call in message.ToolCalls) _toolEntries[call.Id] = AddEntry("TOOL · " + call.Name, Pretty(call.ArgumentsJson), "#A4AECA");
        }
        foreach (var pending in DesktopEventPresentation.UnconfirmedInstructions(events))
            AddEntry("RecoveredInput", "", "#E4B499").LocalizeText(() => L.Format("RecoveredInputText", pending.Text));
        if (loaded.Info.Backend == "codex")
        {
            _newSessionBeforeSend = true;
            AddEntry("NOTICE", "", "#E4B499").LocalizeText(() => L.Text("LegacyCodexSession"));
        }
        ShowLoadedStatus(loaded.Info.Status);
        EmptyState.IsVisible = _entries.Count == 0;
        UpdateWorkspaceLabels();
        UpdateModelLabel();
        SaveSettings();
    }

    private async Task RefreshSessionsAsync(string? selectId = null)
    {
        var sessions = await _host.RequestAsync<SessionInfo[]>("session.list");
        _loadingSelection = true;
        try
        {
            _sessions.Clear();
            foreach (var info in sessions.OrderByDescending(x => x.UpdatedAt)) _sessions.Add(info);
            if (selectId is not null) SessionList.SelectedItem = _sessions.FirstOrDefault(x => x.Id == selectId);
        }
        finally { _loadingSelection = false; }
    }

    private async void SendClick(object? sender, RoutedEventArgs e) => await GuardAsync(_running ? StopAsync : SubmitAsync);
    private async Task SubmitAsync()
    {
        if (_submitting || _choosingImages || _stopping) return;
        _submitting = true;
        UpdateInteractionState();
        try { await SendAsync(); }
        finally { _submitting = false; UpdateInteractionState(); }
    }
    private async Task SendAsync()
    {
        var draft = PromptBox.Text;
        var prompt = draft?.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;
        RequireWorkspace();
        var images = _draftImages.ToArray();
        if (images.Length > 0 && _running) throw new InvalidOperationException(L.Text("ImagesNextTurn"));
        // Release the submitted draft before any await so subsequent typing belongs to the next input.
        PromptBox.Text = "";
        _draftImages.Clear();
        RefreshAttachments();
        try
        {
            if (_running && _session is not null)
            {
                await _host.RequestAsync<JsonElement>("run.steer", new { sessionId = _session.Info.Id, message = prompt });
                LocalizedControl.Text(StatusLabel, "SteerSubmitted");
                return;
            }
            if (SelectedBackend == "chatgpt")
                ApplyAccountState(await _host.RequestAsync<AccountState>("account.status"));
            if (NeedsFreshSession)
                await CreateSessionAsync(prompt.Length > 48 ? prompt[..48] + "…" : prompt);
            var provider = _settings.Provider;
            if (_session!.Info.Backend == "chatgpt") provider = provider with
            {
                Endpoint = "https://api.openai.com/v1", Provider = ProviderKind.OpenAiResponses,
                Model = _settings.CodexModel, ReasoningEffort = _settings.CodexReasoningEffort,
                ThinkingBudgetTokens = null,
                SupportsImages = _accountModels.FirstOrDefault(model => model.Id == _settings.CodexModel)?.SupportsImages == true
            };
            _assistantEntry = null;
            AddUserEntry(prompt, images);
            SetRunState(true);
            try
            {
                await _host.RequestAsync<JsonElement>("run.start", new { sessionId = _session.Info.Id, prompt, images, provider,
                    options = new RunOptions(ExecutionMode: _settings.ExecutionMode, Research: _settings.Research) });
                LocalizedControl.Text(StatusLabel, "RunSubmitted");
            }
            catch { SetRunState(false); throw; }
        }
        catch
        {
            foreach (var image in images) _draftImages.Add(image);
            RefreshAttachments();
            if (string.IsNullOrEmpty(PromptBox.Text)) PromptBox.Text = draft;
            else AddEntry("UnsentInput", "", "#E4B499").LocalizeText(() => L.Format("UnsentInputText", prompt));
            throw;
        }
    }

    private async void AttachImagesClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_choosingImages || _submitting || _running) return;
        _choosingImages = true;
        UpdateInteractionState();
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.Text("ChooseImages"), AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("PNG, JPEG, GIF, WebP") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp"], MimeTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"] }]
            });
            if (files.Count + _draftImages.Count > ImageInputs.MaxCount) throw new InvalidOperationException(L.Text("ImageLimit"));
            List<ImageAttachment> added = [];
            foreach (var file in files)
            {
                await using var source = await file.OpenReadAsync();
                using var buffer = new MemoryStream();
                // Read at most the bounded payload plus one byte even for non-seekable storage providers.
                var chunk = new byte[64 * 1024];
                while (buffer.Length <= ImageInputs.MaxImageBytes)
                {
                    var count = await source.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, ImageInputs.MaxImageBytes + 1L - buffer.Length)));
                    if (count == 0) break;
                    buffer.Write(chunk, 0, count);
                }
                if (buffer.Length > ImageInputs.MaxImageBytes) throw new InvalidOperationException(L.Text("ImageLimit"));
                var bytes = buffer.ToArray();
                var mediaType = ImageInputs.DetectMediaType(bytes) ?? throw new InvalidOperationException(L.Text("UnsupportedImage"));
                added.Add(new ImageAttachment(mediaType, Convert.ToBase64String(bytes), file.Name.Length <= 256 ? file.Name : file.Name[..256]));
            }
            ImageInputs.Validate(_draftImages.Concat(added).ToArray());
            foreach (var image in added) _draftImages.Add(image);
            RefreshAttachments();
            if (added.Count > 0) UiMotion.Reveal(AttachmentList);
        }
        finally { _choosingImages = false; UpdateInteractionState(); }
    });

    private void RemoveImageClick(object? sender, RoutedEventArgs e)
    {
        if (_submitting || _choosingImages) return;
        if ((sender as Control)?.DataContext is ImageAttachment image) _draftImages.Remove(image);
        RefreshAttachments();
    }

    private void RefreshAttachments() => AttachmentList.IsVisible = _draftImages.Count > 0;

    private void AddUserEntry(string text, IReadOnlyList<ImageAttachment>? images)
    {
        var entry = AddEntry("YOU", text, "#AAA4FF");
        if (images is { Count: > 0 })
        {
            var names = string.Join(", ", images.Select((image, index) => image.Name ?? $"#{index + 1}"));
            entry.LocalizeText(() => text + "\n\n" + L.Format("AttachedImages", names));
        }
    }

    private async Task StopAsync()
    {
        if (!_running || _session is null || _stopping || _submitting) return;
        _stopping = true;
        UpdateInteractionState();
        LocalizedControl.Text(RunStateLabel, "Stopping");
        try
        {
            await _host.RequestAsync<JsonElement>("run.cancel", new { sessionId = _session.Info.Id });
            LocalizedControl.Text(StatusLabel, "StopRequested");
        }
        catch { _stopping = false; UpdateInteractionState(); throw; }
    }

    private async void PromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; await GuardAsync(SubmitAsync); }
        else if (e.Key == Key.Escape && _running) { e.Handled = true; await GuardAsync(StopAsync); }
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        try
        {
            if (method == "approval.requested")
            {
                var approval = parameters.Deserialize<ApprovalRequest>(Wire.Json);
                if (approval is null || approval.SessionId != _session?.Info.Id) return;
                _pendingApproval = approval.Id;
                ApprovalLabel.Text = ApprovalDetails(approval);
                ApprovalPanel.IsVisible = true;
                UiMotion.Reveal(ApprovalPanel);
                ApproveButton.IsEnabled = DeclineButton.IsEnabled = true;
                LocalizedControl.Text(RunStateLabel, "AwaitingApproval");
                return;
            }
            if (method == "account.changed")
            {
                var account = parameters.Deserialize<AccountState>(Wire.Json);
                if (account is not null) ApplyAccountState(account);
                if (account is { LoggedIn: true }) LocalizedControl.Text(StatusLabel, "AccountConnected", (Func<string>)(() => account.Email ?? account.Plan ?? L.Text("Account")));
                else LocalizedControl.Text(StatusLabel, "AccountUpdated");
                return;
            }
            if (method != "agent.event") return;
            var evt = parameters.Deserialize<AgentEvent>(Wire.Json);
            if (evt is null || _session?.Info.Id != evt.SessionId) return;
            switch (evt.Type)
            {
                case "run.started": SetRunState(true); break;
                case "message.delta":
                    _assistantEntry ??= AddEntry("ASSISTANT", "", "#80D1C3");
                    _assistantEntry.Text += evt.Text;
                    break;
                case "message.completed":
                    if (!string.IsNullOrWhiteSpace(evt.Text))
                    {
                        _assistantEntry ??= AddEntry("ASSISTANT", "", "#80D1C3");
                        _assistantEntry.Text = evt.Text;
                    }
                    _assistantEntry = null;
                    break;
                case "tool.started":
                    _assistantEntry = null;
                    if (evt.Call is { } started) _toolEntries[started.Id] = AddEntry("TOOL · " + started.Name, Pretty(started.ArgumentsJson), "#9CA9C8");
                    if (evt.Call?.Name is { } name) LocalizedControl.Text(RunStateLabel, "Passthrough", (Func<string>)(() => TimelineEntry.ToolTitle(name)));
                    else LocalizedControl.Text(RunStateLabel, "ToolRunning");
                    break;
                case "tool.output":
                    AppendOutput(evt.Text ?? "");
                    break;
                case "tool.completed":
                    if (evt.Call is { } completed && _toolEntries.TryGetValue(completed.Id, out var card))
                    {
                        card.Label = (evt.Result?.IsError == true ? "FAILED · " : "DONE · ") + completed.Name;
                        card.Text = Pretty(completed.ArgumentsJson) + "\n\n" + Limit(evt.Result is null ? evt.Text ?? L.Text("Completed") : DisplayToolResult(evt.Result), 16000);
                    }
                    else if (evt.Result is { } result) AddEntry(result.IsError ? "TOOL ERROR" : "TOOL RESULT", Limit(DisplayToolResult(result), 16000), "#9CA9C8");
                    break;
                case "approval.requested":
                    LocalizedControl.Text(RunStateLabel, "AwaitingApproval");
                    break;
                case "context.trimmed":
                    if (evt.Text is not null) AddEntry("CONTEXT", evt.Text, "#ABB3C9");
                    else AddEntry("CONTEXT", "", "#ABB3C9").LocalizeText(() => L.Text("ContextTrimmed"));
                    break;
                case "context.compacting":
                    LocalizedControl.Text(RunStateLabel, "CompactingContext");
                    LocalizedControl.Text(StatusLabel, "CompactingContext");
                    break;
                case "context.compacted":
                    AddEntry("CONTEXT", "", "#ABB3C9").LocalizeText(() => L.Text("ContextCompacted"));
                    LocalizedControl.Text(RunStateLabel, "Running");
                    break;
                case "context.compaction_failed":
                    AddEntry("CONTEXT", "", "#E4B499").LocalizeText(() => L.Text("ContextCompactionFailed"));
                    break;
                case "input.queued":
                    if (evt.Text is { } queued)
                    {
                        if (!_steeringEntries.TryGetValue(evt.RunId, out var queue)) _steeringEntries[evt.RunId] = queue = [];
                        queue.Add((queued, AddEntry("YOU · SteerQueued", queued, "#AAA4FF")));
                    }
                    break;
                case "input.steered":
                    if (evt.Text is { } applied)
                    {
                        var queue = _steeringEntries.GetValueOrDefault(evt.RunId);
                        var index = queue?.FindIndex(x => string.Equals(x.Text, applied, StringComparison.Ordinal)) ?? -1;
                        if (index >= 0)
                        {
                            queue![index].Entry.Label = "YOU · SteerApplied";
                            queue.RemoveAt(index);
                        }
                        else AddEntry("YOU · SteerApplied", applied, "#AAA4FF");
                    }
                    break;
                case "run.steered":
                    if (evt.Text is { } accepted) AddEntry("YOU · SteerAccepted", accepted, "#AAA4FF");
                    break;
                case "model.usage":
                    AddEntry("TOKENS", "", "#8F98AD").LocalizeText(() => DesktopEventPresentation.Usage(evt.Text));
                    break;
                case "run.completed":
                case "run.cancelled":
                case "run.failed":
                    SetRunState(false);
                    ApprovalPanel.IsVisible = false;
                    _pendingApproval = null;
                    _assistantEntry = null;
                    if (_steeringEntries.Remove(evt.RunId, out var remaining))
                        foreach (var pending in remaining)
                        {
                            pending.Entry.Label = "UnconfirmedInput";
                            pending.Entry.LocalizeText(() => L.Format("UnconfirmedInputText", pending.Text));
                        }
                    var outcome = evt.Type switch { "run.failed" => "Failed", "run.cancelled" => "Stopped", _ => "Completed" };
                    LocalizedControl.Text(RunStateLabel, outcome);
                    LocalizedControl.Text(StatusLabel, outcome);
                    if (evt.Type != "run.completed") AddEntry(evt.Type == "run.failed" ? "ERROR" : "STOPPED", evt.Text ?? RunStateLabel.Text!, "#E8B391");
                    _ = GuardAsync(async () => { await RefreshSessionsAsync(_session?.Info.Id); await RefreshWorkspaceAsync(); });
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(evt.Text)) AddEntry(evt.Type.ToUpperInvariant(), Limit(evt.Text, 12000));
                    break;
            }
        }
        catch (JsonException) { LocalizedControl.Text(StatusLabel, "UnknownEvent"); }
    }

    private async void ApproveClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () => await ResolveApprovalAsync(true));
    private async void DeclineClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () => await ResolveApprovalAsync(false));
    private async Task ResolveApprovalAsync(bool approved)
    {
        if (_pendingApproval is null) return;
        ApproveButton.IsEnabled = DeclineButton.IsEnabled = false;
        try
        {
            await _host.RequestAsync<JsonElement>("approval.respond", new { approvalId = _pendingApproval, approved });
            _pendingApproval = null;
            ApprovalPanel.IsVisible = false;
            LocalizedControl.Text(RunStateLabel, approved ? "Resuming" : "OperationDeclined");
        }
        finally { ApproveButton.IsEnabled = DeclineButton.IsEnabled = _pendingApproval is not null; }
    }

    private async void RefreshWorkspaceClick(object? sender, RoutedEventArgs e) => await GuardAsync(RefreshWorkspaceAsync);
    private void FilesClick(object? sender, RoutedEventArgs e) => SetInspector(!InspectorPanel.IsVisible || InspectorTabs.SelectedIndex != 0, 0);
    private void ChangesClick(object? sender, RoutedEventArgs e) => SetInspector(!InspectorPanel.IsVisible || InspectorTabs.SelectedIndex != 1, 1);
    private void CloseInspectorClick(object? sender, RoutedEventArgs e) => SetInspector(false);
    private void InspectorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (InspectorPanel is not null && FilesButton is not null) UpdateInspectorButtons();
    }
    private void SetInspector(bool visible, int? selectedTab = null)
    {
        var wasVisible = InspectorPanel.IsVisible;
        if (!visible && InspectorPanel.IsVisible) _inspectorWidth = Math.Clamp(InspectorPanel.Bounds.Width, 300, 620);
        WorkspaceLayout.ColumnDefinitions[2].Width = new GridLength(visible ? 1 : 0);
        WorkspaceLayout.ColumnDefinitions[3].MinWidth = visible ? 280 : 0;
        if (!visible) WorkspaceLayout.ColumnDefinitions[3].Width = new GridLength(0);
        InspectorPanel.IsVisible = InspectorSplitter.IsVisible = visible;
        ResizeInspector();
        if (selectedTab is { } tab) InspectorTabs.SelectedIndex = tab;
        UpdateInspectorButtons();
        if (visible && !wasVisible) UiMotion.Reveal(InspectorPanel);
    }
    private void ResizeInspector()
    {
        if (!InspectorPanel.IsVisible) return;
        var maximum = Math.Clamp(Bounds.Width - 700, 280, 620);
        WorkspaceLayout.ColumnDefinitions[3].MaxWidth = maximum;
        WorkspaceLayout.ColumnDefinitions[3].Width = new GridLength(Math.Clamp(_inspectorWidth, 280, maximum));
    }
    private void UpdateInspectorButtons()
    {
        FilesButton.Classes.Set("active", InspectorPanel.IsVisible && InspectorTabs.SelectedIndex == 0);
        ChangesButton.Classes.Set("active", InspectorPanel.IsVisible && InspectorTabs.SelectedIndex == 1);
    }
    private async Task RefreshWorkspaceAsync()
    {
        if (!_ready || string.IsNullOrWhiteSpace(_workspace)) return;
        var workspace = _workspace;
        var filesTask = _host.RequestAsync<string[]>("workspace.files", new { workspacePath = workspace });
        var diffTask = _host.RequestAsync<ToolResult>("workspace.diff", new { workspacePath = workspace });
        await Task.WhenAll(filesTask, diffTask);
        if (workspace != _workspace) return;
        _files = await filesTask;
        FilterFiles();
        var diff = await diffTask;
        _diffEmpty = string.IsNullOrWhiteSpace(diff.Content);
        DiffPreview.Text = _diffEmpty ? L.Text("NoChanges") : diff.Content;
    }

    private void FileFilterChanged(object? sender, TextChangedEventArgs e) => FilterFiles();
    private void FilterFiles()
    {
        if (FileList is null) return;
        var filter = FileFilter.Text?.Trim() ?? "";
        FileList.ItemsSource = _files.Where(x => x.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(3000).ToArray();
    }

    private async void FileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not string path || !_ready) return;
        await GuardAsync(async () =>
        {
            var workspace = _workspace;
            var result = await _host.RequestAsync<ToolResult>("workspace.read", new { workspacePath = workspace, path });
            if (_workspace != workspace || FileList.SelectedItem as string != path) return;
            PreviewPath.Text = path;
            FilePreview.Text = DisplayToolResult(result);
            var highlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
            if (highlighting is not null)
            {
                foreach (var color in highlighting.NamedHighlightingColors)
                {
                    var name = color.Name ?? "";
                    color.Foreground = new SimpleHighlightingBrush(Color.Parse(name.Contains("Comment", StringComparison.OrdinalIgnoreCase) ? "#7D8B9F" :
                        name.Contains("String", StringComparison.OrdinalIgnoreCase) || name.Contains("Char", StringComparison.OrdinalIgnoreCase) ? "#C5B587" :
                        name.Contains("Number", StringComparison.OrdinalIgnoreCase) || name.Contains("Digit", StringComparison.OrdinalIgnoreCase) ? "#8ED7CB" : "#ADA7EC"));
                }
            }
            FilePreview.SyntaxHighlighting = highlighting;
        });
    }

    private async void RunCommandClick(object? sender, RoutedEventArgs e) => await GuardAsync(RunCommandAsync);
    private async void CommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await GuardAsync(RunCommandAsync); }
    }
    private async Task RunCommandAsync()
    {
        if (RunCommandButton.IsEnabled == false || _running) return;
        RequireWorkspace();
        var command = CommandBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(command)) return;
        RunCommandButton.IsEnabled = false;
        CommandBox.IsEnabled = false;
        SetInspector(true, 2);
        AppendOutput("\n> " + command + "\n");
        LocalizedControl.Text(StatusLabel, "CommandRunning");
        try
        {
            var result = await _host.RequestAsync<ToolResult>("workspace.command", new { workspacePath = _workspace, command, timeoutSeconds = 120 });
            AppendOutput(DisplayToolResult(result) + "\n");
            LocalizedControl.Text(StatusLabel, result.IsError ? "CommandFailed" : "CommandCompleted");
            await RefreshWorkspaceAsync();
        }
        finally { RunCommandButton.IsEnabled = true; CommandBox.IsEnabled = true; }
    }

    private async void ConnectionsClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var connections = new ConnectionsWindow(_host, _settings, preference =>
        {
            _settings = _settings with { Language = preference.Language, ReducedMotion = preference.ReducedMotion };
            SaveSettings();
        });
        var settings = await connections.ShowDialog<DesktopSettings?>(this);
        if (settings is null) return;
        _modelsLoaded = false;
        await ApplyComposerSettingsAsync(settings.RememberProvider(settings.Provider));
    });

    private void OpenTerminalClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            RequireWorkspace();
            var terminal = new WorkbenchTerminalWindow(_workspace);
            _terminals.Add(terminal);
            terminal.Closed += (_, _) => _terminals.Remove(terminal);
            terminal.Show(this);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private string SelectedBackend => ComposerChoices.NormalizeBackend(_settings.Backend);
    internal bool NeedsFreshSession => _session is null || ComposerChoices.RequiresFreshSession(_session.Info, _settings, _accountBinding) || _newSessionBeforeSend;
    internal void ApplyAccountState(AccountState state)
    {
        var changed = _accountKnown ? _accountBinding != state.AccountBinding :
            _session?.Info is { Backend: "chatgpt", AccountBinding: { } savedBinding } && savedBinding != state.AccountBinding;
        _accountBinding = state.AccountBinding;
        _accountKnown = true;
        if (!state.LoggedIn) { _modelsLoaded = false; _accountModels = []; }
        if (!changed) return;
        _modelsLoaded = false;
        _accountModels = [];
        _settings = _settings with { CodexModel = "", CodexReasoningEffort = "none" };
        if (_session?.Info.Backend == "chatgpt")
        {
            _newSessionBeforeSend = true;
            AddEntry("NOTICE", "", "#E4B499").LocalizeText(() => L.Text("AccountChangedNewChat"));
        }
        UpdateModelLabel();
        SaveSettings();
    }
    private void UpdateModelLabel()
    {
        ModelLabel.Text = SelectedBackend == "chatgpt" ? (string.IsNullOrWhiteSpace(_settings.CodexModel) ? "ChatGPT" : _settings.CodexModel) : _settings.Provider.Model;
        ExecutionModeLabel.Text = L.Text(ComposerChoices.ModeKey(_settings.ExecutionMode));
        ToolTip.SetTip(ExecutionModeButton, L.Text("ApiExecutionHint"));
        AutomationProperties.SetName(ExecutionModeButton, L.Text("ExecutionMode"));
        var effort = SelectedBackend == "chatgpt" ? _settings.CodexReasoningEffort : _settings.Provider.ReasoningEffort;
        ReasoningLabel.Text = SelectedBackend != "chatgpt" && _settings.Provider.ThinkingBudgetTokens is not null
            ? L.Text("ReasoningManual") : L.Text(ComposerChoices.EffortKey(effort, SelectedBackend == "chatgpt" || ComposerChoices.ResolveProvider(_settings.Provider) is ProviderKind.OpenAiCompatible or ProviderKind.OpenAiResponses));
    }

    private void ExecutionModeClick(object? sender, RoutedEventArgs e)
    {
        if (_running || _submitting || _choosingImages) return;
        List<Control> items = [new MenuItem { Header = L.Text("ExecutionMode"), IsEnabled = false }];
        foreach (var mode in Enum.GetValues<ExecutionMode>())
        {
            var item = new MenuItem { Header = SelectedMark(_settings.ExecutionMode == mode) + L.Text(ComposerChoices.ModeKey(mode)), Tag = mode };
            item.Click += (_, _) =>
            {
                if (_running || _submitting) return;
                _settings = _settings with { ExecutionMode = mode };
                UpdateModelLabel();
                SaveSettings();
            };
            items.Add(item);
        }
        OpenMenu(ExecutionModeButton, items);
    }

    private async void ModelClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_running || _submitting || _choosingImages) return;
        var menu = OpenMenu(ModelButton, ModelMenuItems());
        if (!_ready || _modelsLoaded || _loadingModels) return;
        _loadingModels = true;
        try
        {
            ApplyAccountState(await _host.RequestAsync<AccountState>("account.status"));
            var binding = _accountBinding;
            var models = await _host.RequestAsync<ModelInfo[]>("model.list");
            if (binding != _accountBinding) return;
            ApplyAccountModels(models);
            if (menu.IsOpen) menu.ItemsSource = ModelMenuItems();
        }
        catch (Exception)
        {
            // Manual/current choices remain usable; connection diagnostics belong in Settings.
            if (menu.IsOpen) menu.ItemsSource = ModelMenuItems(accountUnavailable: true);
        }
        finally { _loadingModels = false; }
    });

    internal void ApplyAccountModels(ModelInfo[] models)
    {
        _accountModels = models;
        _modelsLoaded = true;
        NormalizeAccountEffort();
    }

    private void NormalizeAccountEffort()
    {
        var effort = ComposerChoices.AccountEffort(_accountModels.FirstOrDefault(model => model.Id == _settings.CodexModel), _settings.CodexReasoningEffort);
        if (effort == _settings.CodexReasoningEffort) return;
        _settings = _settings with { CodexReasoningEffort = effort };
        UpdateModelLabel();
        SaveSettings();
    }

    private IReadOnlyList<Control> ModelMenuItems(bool accountUnavailable = false)
    {
        List<Control> items = [new MenuItem { Header = L.Text("ConfiguredApiModels"), IsEnabled = false }];
        foreach (var provider in new[] { _settings.Provider }.Concat(_settings.RecentProviders ?? [])
            .DistinctBy(item => (item.Endpoint.TrimEnd('/'), item.Model, ComposerChoices.ResolveProvider(item))).Take(8))
        {
            var active = SelectedBackend == "api" && ComposerChoices.SameModel(_settings.Provider, provider);
            var item = new MenuItem { Header = SelectedMark(active) + provider.Model, Tag = provider };
            ToolTip.SetTip(item, provider.Endpoint + " · " + ComposerChoices.ResolveProvider(provider));
            item.Click += async (_, _) => await GuardAsync(() => SelectModelAsync("api", provider, ""));
            items.Add(item);
        }
        items.Add(new Separator());
        items.Add(new MenuItem { Header = "ChatGPT", IsEnabled = false });
        var defaultItem = new MenuItem { Header = SelectedMark(SelectedBackend == "chatgpt" && string.IsNullOrEmpty(_settings.CodexModel)) + L.Text("AccountDefaultModel"), Tag = "" };
        defaultItem.Click += async (_, _) => await GuardAsync(() => SelectModelAsync("chatgpt", null, ""));
        items.Add(defaultItem);
        var models = _accountModels.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_settings.CodexModel) && !_accountModels.Any(model => model.Id == _settings.CodexModel))
            models = models.Prepend(new ModelInfo(_settings.CodexModel, _settings.CodexModel));
        foreach (var model in models.Take(40))
        {
            var item = new MenuItem { Header = SelectedMark(SelectedBackend == "chatgpt" && model.Id == _settings.CodexModel) + model.Name, Tag = model.Id };
            if (model.SupportsImages == true) ToolTip.SetTip(item, L.Text("ModelSupportsImages"));
            item.Click += async (_, _) => await GuardAsync(() => SelectModelAsync("chatgpt", null, model.Id));
            items.Add(item);
        }
        if (accountUnavailable) items.Add(new MenuItem { Header = L.Text("AccountModelsUnavailable"), IsEnabled = false });
        items.Add(new Separator());
        var configure = new MenuItem { Header = L.Text("ConfigureModels") };
        configure.Click += ConnectionsClick;
        items.Add(configure);
        return items;
    }

    private async Task SelectModelAsync(string backend, ProviderSettings? provider, string accountModel)
    {
        if (_running || _submitting || _choosingImages) return;
        var settings = backend == "api" ? _settings.RememberProvider(provider!) with { Backend = "api" }
            : _settings with { Backend = "chatgpt", CodexModel = accountModel,
                CodexReasoningEffort = accountModel == _settings.CodexModel
                    ? ComposerChoices.AccountEffort(_accountModels.FirstOrDefault(model => model.Id == accountModel), _settings.CodexReasoningEffort) : "none" };
        await ApplyComposerSettingsAsync(settings);
    }

    private async Task ApplyComposerSettingsAsync(DesktopSettings settings)
    {
        var previous = _settings;
        var changed = ComposerChoices.RequiresNewSession(previous, settings);
        _settings = settings;
        try
        {
            if (changed && _session is not null && _entries.Count > 0)
            {
                // Settings remain accessible during a run. Keep its session attached until it ends,
                // then start the new model's session at the next submission.
                if (_running || _submitting) _newSessionBeforeSend = true;
                else
                {
                    await CreateSessionAsync();
                    LocalizedControl.Text(StatusLabel, "ModelChangedNewChat");
                }
            }
            SaveSettings();
        }
        catch { _settings = previous; throw; }
        finally { UpdateModelLabel(); }
    }

    private void ReasoningClick(object? sender, RoutedEventArgs e)
    {
        if (_running || _submitting || _choosingImages) return;
        var chatgpt = SelectedBackend == "chatgpt";
        if (chatgpt) NormalizeAccountEffort();
        var selected = chatgpt ? _settings.CodexReasoningEffort : _settings.Provider.ReasoningEffort;
        var efforts = chatgpt ? ComposerChoices.AccountEfforts(_accountModels.FirstOrDefault(model => model.Id == _settings.CodexModel))
            : ComposerChoices.ApiEfforts(_settings.Provider);
        List<Control> items = [new MenuItem { Header = L.Text("ReasoningEffort"), IsEnabled = false }];
        foreach (var effort in efforts)
        {
            var key = ComposerChoices.EffortKey(effort, chatgpt || ComposerChoices.ResolveProvider(_settings.Provider) is ProviderKind.OpenAiCompatible or ProviderKind.OpenAiResponses);
            var item = new MenuItem { Header = SelectedMark(selected == effort && (chatgpt || _settings.Provider.ThinkingBudgetTokens is null)) + L.Text(key), Tag = effort };
            item.Click += (_, _) =>
            {
                if (_running || _submitting) return;
                _settings = chatgpt ? _settings with { CodexReasoningEffort = effort }
                    : _settings.RememberProvider(_settings.Provider with { ReasoningEffort = effort, ThinkingBudgetTokens = null });
                UpdateModelLabel();
                SaveSettings();
            };
            items.Add(item);
        }
        if (!chatgpt && _settings.Provider.ThinkingBudgetTokens is { } budget)
            items.Add(new MenuItem { Header = "✓ " + L.Format("ReasoningManualBudget", budget), IsEnabled = false });
        OpenMenu(ReasoningButton, items);
    }

    private static string SelectedMark(bool selected) => selected ? "✓ " : "";
    private static ContextMenu OpenMenu(Control owner, IReadOnlyList<Control> items)
    {
        owner.ContextMenu?.Close();
        var menu = new ContextMenu { ItemsSource = items, MaxHeight = 420, MinWidth = 210 };
        owner.ContextMenu = menu;
        menu.Open(owner);
        return menu;
    }
    private void UpdateWorkspaceLabels()
    {
        WorkspaceLabel.Text = string.IsNullOrWhiteSpace(_workspace) ? L.Text("ChooseLocalFolder") : new DirectoryInfo(_workspace).Name;
        ToolTip.SetTip(WorkspaceLabel, _workspace);
        HeaderWorkspace.Text = string.IsNullOrWhiteSpace(_workspace) ? L.Text("ChooseProject") : new DirectoryInfo(_workspace).Name;
    }

    private void RequireWorkspace()
    {
        if (!_ready) throw new InvalidOperationException(L.Text("HostNotConnected"));
        if (string.IsNullOrWhiteSpace(_workspace) || !Directory.Exists(_workspace)) throw new InvalidOperationException(L.Text("WorkspaceRequired"));
    }

    internal void SetRunState(bool running)
    {
        if (!running || !_running) _stopping = false;
        _running = running;
        UpdateInteractionState();
        LocalizedControl.Text(RunStateLabel, _stopping ? "Stopping" : running ? "Running" : "Ready");
    }

    private void UpdateInteractionState()
    {
        SendButton.IsEnabled = !_submitting && !_choosingImages && !_stopping;
        SendGlyph.Symbol = _running ? "stop" : "send";
        var actionKey = _running ? "StopTooltip" : "SendTooltip";
        ToolTip.SetTip(SendButton, L.Text(actionKey));
        AutomationProperties.SetName(SendButton, L.Text(actionKey));
        LocalizedControl.Text(ComposerHint, _running ? "SteerShortcut" : "SendShortcut");
        var canSwitch = !_running && !_submitting && !_choosingImages;
        AttachButton.IsEnabled = canSwitch;
        ExecutionModeButton.IsEnabled = canSwitch;
        ModelButton.IsEnabled = canSwitch;
        ReasoningButton.IsEnabled = canSwitch;
        OpenProjectButton.IsEnabled = canSwitch;
        NewSessionButton.IsEnabled = canSwitch;
        SessionList.IsEnabled = canSwitch;
        RunCommandButton.IsEnabled = canSwitch;
    }

    private TimelineEntry AddEntry(string label, string text, string accent = "#929DB6")
    {
        var entry = new TimelineEntry(label, text, accent);
        _entries.Add(entry);
        EmptyState.IsVisible = false;
        Dispatcher.UIThread.Post(() => Timeline.ScrollIntoView(entry), DispatcherPriority.Background);
        return entry;
    }
    private void AppendOutput(string text)
    {
        CommandOutput.Text = Limit((CommandOutput.Text ?? "") + text, 160000);
        CommandOutput.CaretIndex = CommandOutput.Text.Length;
    }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ShowError(Exception ex)
    {
        StatusLabel.Text = ex is OperationCanceledException ? L.Text("CancelledOrTimeout") : ex.Message;
        AddEntry("NOTICE", StatusLabel.Text ?? L.Text("OperationFailed"), "#E4B499");
    }
    private void SaveSettings()
    {
        _settings = _settings with { LastWorkspace = _workspace, LastSessionId = _session?.Info.Id, Backend = SelectedBackend };
        if (!_persistSettings) return;
        try { _settings.Save(); } catch (IOException) { LocalizedControl.Text(StatusLabel, "SettingsSaveFailed"); }
    }
    private static string Pretty(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }); }
        catch (JsonException) { return json; }
    }
    private static string ApprovalDetails(ApprovalRequest approval)
    {
        var text = new System.Text.StringBuilder().AppendLine(approval.ToolName).AppendLine(approval.Reason);
        try
        {
            using var document = JsonDocument.Parse(approval.ArgumentsJson);
            foreach (var property in document.RootElement.EnumerateObject())
                text.AppendLine().Append(property.Name).AppendLine(":")
                    .AppendLine(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText());
        }
        catch (JsonException) { text.AppendLine(approval.ArgumentsJson); }
        return text.ToString();
    }
    private static string DisplayToolResult(ToolResult result)
    {
        try
        {
            using var doc = JsonDocument.Parse(result.Content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result.Content;
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String)
            {
                var status = root.TryGetProperty("exitCode", out var exit) ? "\n[" + L.Format("ExitCode", exit.GetRawText()) + "]" : "";
                if (root.TryGetProperty("timedOut", out var timeout) && timeout.ValueKind == JsonValueKind.True) status += " [" + L.Text("Timeout") + "]";
                if (root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True) status += " [" + L.Text("OutputTruncated") + "]";
                return output.GetString() + status;
            }
            return Pretty(result.Content);
        }
        catch (JsonException) { return result.Content; }
    }
    private static string Limit(string text, int limit) => text.Length <= limit ? text : L.Format("LastCharacters", limit) + text[^limit..];

    private bool _diffEmpty;
    private void ShowLoadedStatus(string status)
    {
        LocalizedControl.Text(RunStateLabel, "Passthrough", (Func<string>)(() => DesktopEventPresentation.SessionStatus(status)));
        LocalizedControl.Text(StatusLabel, "SessionRestored", (Func<string>)(() => DesktopEventPresentation.SessionStatus(status)));
    }
    private void RefreshLocalization()
    {
        Title = ProductIdentity.WindowTitle;
        if (_session is null) LocalizedControl.Text(SessionTitle, "NewSession");
        UpdateWorkspaceLabels();
        UpdateModelLabel();
        UpdateInteractionState();
        if (_diffEmpty) DiffPreview.Text = L.Text("NoChanges");
        foreach (var entry in _entries) entry.RefreshLocalization();
        LocalizedControl.RemeasureTabHeaders(InspectorTabs);
    }
    private void RefreshMotion(object? sender, EventArgs e)
    {
        foreach (var control in this.GetVisualDescendants().OfType<TemplatedControl>().Where(control => control is Button or TextBox or ComboBox))
            control.Transitions = UiMotion.CreateFeedbackTransitions();
    }
}
