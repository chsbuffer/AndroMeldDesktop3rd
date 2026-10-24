using Andromeld.Desktop.Services;
using Andromeld.Protocol;

using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private IObservable<bool> _canRunSearchAction = null!;
    private AndroidIntentUri? _searchIntent;

    [Reactive]
    public partial string SearchFeedback { get; private set; } = "";
    public string SearchActionText => Search.Trim();
    public int SearchActionRow => Apps.Count == 0 ? 0 : 2;
    public string SearchActionLabel => _searchIntent?.Scheme switch
    {
        "tel" => "在 Android 上拨打电话",
        "mailto" => "在 Android 上撰写邮件",
        null => "复制到 Android 剪贴板",
        _ => "在 Android 中打开"
    };
    public bool ShowSearchPhone => IsConnected && _supportsMirrors && _supportsPhone && MatchesTool("手机屏幕", "Phone Screen");
    public bool ShowSearchFiles => IsConnected && _supportsFiles && MatchesTool("文件", "Files");
    public bool ShowSearchTerminal => IsConnected && _supportsTerminal && MatchesTool("终端", "Terminal");
    public bool HasSearchTools => ShowSearchPhone || ShowSearchFiles || ShowSearchTerminal;
    public bool ShowSearchAction => IsConnected && !AppsLoading && SearchActionText.Length > 0 &&
                                    (_searchIntent is not null || (Apps.Count == 0 && !HasSearchTools));
    public bool CanRunSearchAction =>
        ShowSearchAction && (_searchIntent is not null ? _supportsMirrors : _client?.Capabilities.Contains("clipboard") == true);
    public bool ShowAppsEmptyState => AppsEmpty && !HasSearchTools && !ShowSearchAction;

    private bool MatchesTool(string name, string english)
    {
        return SearchActionText.Length > 0 &&
               (AppSearchMatcher.Matches(name, Search) || english.Contains(SearchActionText, StringComparison.OrdinalIgnoreCase));
    }

    [ReactiveCommand(CanExecute = nameof(_canOpenTerminal))]
    private async Task OpenSearchTerminalAsync()
    {
        TerminalViewModel? running = Terminals.FirstOrDefault(static t => !t.IsDisposed && (t.IsRunning || t.IsOpening));
        if (running is not null && TerminalRequested is { } focus)
        {
            await focus(running).ConfigureAwait(true);
        }
        else
        {
            await OpenTerminalAsync().ConfigureAwait(true);
        }
    }

    private void InitializeSearch()
    {
        _canRunSearchAction = this.WhenAnyValue(x => x.CanRunSearchAction);
        this.WhenAnyValue(x => x.IsConnected, x => x.AppsLoading).Subscribe(_ => UpdateSearchActions()).DisposeWith(_commands);
    }

    private void UpdateSearchActions()
    {
        _searchIntent = AndroidIntentUri.TryParse(Search, out AndroidIntentUri? intent) ? intent : null;
        SearchFeedback = "";
        foreach (string property in new[]
                 {
                     nameof(SearchActionText), nameof(SearchActionLabel), nameof(SearchActionRow), nameof(ShowSearchPhone),
                     nameof(ShowSearchFiles), nameof(ShowSearchTerminal), nameof(HasSearchTools), nameof(ShowSearchAction),
                     nameof(CanRunSearchAction), nameof(ShowAppsEmptyState)
                 })
        {
            this.RaisePropertyChanged(property);
        }
    }

    [ReactiveCommand(CanExecute = nameof(_canRunSearchAction))]
    private async Task RunSearchActionAsync()
    {
        if (!CanRunSearchAction || _client is not { } client)
        {
            return;
        }

        string text = SearchActionText;
        AndroidIntentUri? intent = _searchIntent;
        CancellationToken token = _connection.Token;
        if (intent is not null)
        {
            await OpenMirrorAsync(null, intent).ConfigureAwait(true);
        }
        else
        {
            await client.SetClipboardTextAsync(text, token).ConfigureAwait(true);
            if (!_disposed && _client == client && IsConnected && !token.IsCancellationRequested &&
                string.Equals(SearchActionText, text, StringComparison.Ordinal))
            {
                SearchFeedback = "已发送到 Android 剪贴板";
            }
        }
    }

    private void UpdateRunningApps()
    {
        foreach (AppTile tile in _allApps)
        {
            tile.IsRunning = Mirrors.Any(m =>
                !m.IsDisposed && m.CanControl && string.Equals(m.App?.PackageName, tile.Package, StringComparison.Ordinal) &&
                m.App!.UserId == tile.App.UserId);
        }
    }
}