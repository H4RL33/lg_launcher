using Avalonia;
using Avalonia.Input;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private sealed record ArticleView(Guid FeedId, string FeedTitle, RssArticle Article);
    private sealed record ViewLocation(Page Page, GameCollection? Collection = null, Game? Game = null, ArticleView? Article = null);
    private sealed record HistoryEntry(ViewLocation View, int Offset = 0, Vector Scroll = default);

    private readonly List<HistoryEntry> _history = [new(new ViewLocation(Page.Home))];
    private int _historyIndex;

    private void Navigate(Page page, GameCollection? collection = null) => Navigate(new ViewLocation(page, Collection: collection));

    private void SelectGame(Game game)
    {
        if (_page != Page.Game || _game != game) Navigate(new ViewLocation(Page.Game, Game: game));
    }

    private void Navigate(ViewLocation view, int offset = 0)
    {
        var next = new HistoryEntry(view, Math.Max(offset, 0));
        var changed = view != _history[_historyIndex].View || next.Offset != _offset;
        if (changed)
        {
            RememberHistoryPosition();
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(next);
            _historyIndex++;
        }
        ShowHistoryEntry(next, changed);
    }

    private void HandleNavigationPointer(object? sender, PointerReleasedEventArgs args)
    {
        if (args.Pointer.Type != PointerType.Mouse) return;
        var direction = args.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.XButton1Released => -1,
            PointerUpdateKind.XButton2Released => 1,
            _ => 0
        };
        if (direction == 0) return;
        args.Handled = true;
        if (!_updateHandoff && !DialogHost.HasModal) MoveInHistory(direction);
    }

    private void MoveInHistory(int direction)
    {
        for (var index = _historyIndex + direction; index >= 0 && index < _history.Count; index += direction)
        {
            if (!IsAvailable(_history[index].View)) continue;
            RememberHistoryPosition();
            _historyIndex = index;
            ShowHistoryEntry(_history[index], animate: true);
            return;
        }
    }

    private bool IsAvailable(ViewLocation view) => view.Page switch
    {
        Page.Game => view.Game != null && _library.Games.Contains(view.Game),
        Page.Collections => view.Collection == null || _library.Collections.Contains(view.Collection),
        Page.RssArticle => view.Article != null && RssSubscriptions.Any(feed => feed.Enabled && feed.Id == view.Article.FeedId),
        _ => true
    };

    private void RememberHistoryPosition() =>
        _history[_historyIndex] = _history[_historyIndex] with { Offset = _offset, Scroll = ContentScroll.Offset };

    private void ShowHistoryEntry(HistoryEntry entry, bool animate)
    {
        var view = entry.View;
        if (_editingArtwork) FinishArtworkMove(false);
        if (view.Page != Page.Game)
        {
            ResetArtworkParallax(immediate: true);
            CancelMotion(GameInfo);
            CancelMotion(_artwork);
            if (_detailsRequest != null) { _detailsRequest.Cancel(); _artworkGame = null; }
        }
        else CancelMotion(ContentScroll);

        _page = view.Page;
        _collection = view.Collection;
        _game = view.Game;
        _selectedModAssessment = null;
        _rssOpenArticle = view.Article;
        _offset = entry.Offset;
        SearchBox.Clear();
        CloseSearch();
        ContentScroll.Offset = default;
        Render();
        _ = RefreshSelectedModStatus();
        if (entry.Scroll != default && ContentScroll.IsVisible)
        {
            ContentScroll.UpdateLayout();
            ContentScroll.Offset = entry.Scroll;
        }
        if (animate) AnimateView(_page == Page.Game ? GameInfo : ContentScroll);
    }
}
