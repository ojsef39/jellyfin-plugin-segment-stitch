using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SegmentStitch;

/// <summary>
/// Looks items up when they are added, after a metadata download and on playback, instead of on
/// the next media segment scan, and writes the caches on shutdown. Gelato runs the providers for
/// its items before playback, so they are left out of the add and update hooks: adding a long
/// series would otherwise look up every episode. The playback hook refreshes local files that are due.
/// </summary>
internal sealed class OnDemandFetchService : IHostedService, IDisposable
{
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<OnDemandFetchService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Task> _pending = new();

    public OnDemandFetchService(
        IMediaSegmentManager mediaSegmentManager,
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        ILogger<OnDemandFetchService> logger)
    {
        _mediaSegmentManager = mediaSegmentManager;
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _sessionManager.PlaybackStart += OnPlaybackStart;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_pending.Values.ToArray()).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Shutting down: unfinished lookups are asked again on the next run.
        }

        Plugin.Instance?.SaveCaches();
    }

    public void Dispose() => _cts.Dispose();

    /// <summary>
    /// Whether a newly added item is looked up right away. An episode is added before its
    /// metadata download, often with only an IMDb id while its numbers come from TVDB or TMDB:
    /// it waits for the download (<see cref="ShouldFetchOnItemUpdated"/>), as does one whose
    /// series is not known yet.
    /// </summary>
    /// <param name="item">The added item.</param>
    /// <param name="series">The episode's series, if found.</param>
    /// <returns>Whether to fetch now.</returns>
    internal static bool ShouldFetchOnItemAdded(BaseItem? item, BaseItem? series)
        => !IsGelato(item)
            && (item is Movie
            || (item is Episode
                && series is not null
                && (series.HasProviderId(MetadataProvider.Tvdb) || series.HasProviderId(MetadataProvider.Tmdb))));

    /// <summary>
    /// Whether an updated item is looked up: after a metadata download, which is when an item
    /// added without its ids gets them.
    /// </summary>
    /// <param name="e">The update.</param>
    /// <returns>Whether to fetch now.</returns>
    internal static bool ShouldFetchOnItemUpdated([NotNullWhen(true)] ItemChangeEventArgs? e)
        => e?.Item is Episode or Movie && !IsGelato(e.Item) && e.UpdateReason.HasFlag(ItemUpdateType.MetadataDownload);

    /// <summary>
    /// Whether an item is one of Gelato's: those carry the Stremio id they are streamed by.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether Gelato manages it.</returns>
    internal static bool IsGelato(BaseItem? item) => item?.HasProviderId("Stremio") == true;

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var item = e?.Item;

        // GetItemById rejects an empty id, which an episode added before its series has.
        var series = item is Episode episode && episode.SeriesId != Guid.Empty
            ? _libraryManager.GetItemById(episode.SeriesId)
            : null;
        if (ShouldFetchOnItemAdded(item, series))
        {
            Track(FetchAsync(item!, "item added"));
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (ShouldFetchOnItemUpdated(e))
        {
            Track(FetchAsync(e.Item, "metadata download"));
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e?.Item is Episode or Movie or Video)
        {
            Track(FetchAsync(e.Item, "playback"));
        }
    }

    private async Task FetchAsync(BaseItem item, string trigger)
    {
        try
        {
            if (Plugin.Instance?.Configuration.EnableOnDemandFetch != true)
            {
                return;
            }

            // The provider decides whether to fetch, so segments not due are kept.
            await _mediaSegmentManager
                .RunSegmentPluginProviders(item, _libraryManager.GetLibraryOptions(item), false, _cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Segment lookup ({Trigger}) failed for {Name}", trigger, item.Name);
        }
    }

    private void Track(Task task)
    {
        _pending[task.Id] = task;
        task.ContinueWith(t => _pending.TryRemove(t.Id, out _), TaskScheduler.Default);
    }
}
