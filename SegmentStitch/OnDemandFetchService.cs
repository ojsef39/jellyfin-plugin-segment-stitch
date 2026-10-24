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
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SegmentStitch;

/// <summary>
/// Looks new items up right away instead of on the next media segment scan, and writes the
/// caches on shutdown. Playback is left out: Gelato already runs the providers for its stream
/// rows on playback.
/// </summary>
internal sealed class OnDemandFetchService : IHostedService, IDisposable
{
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<OnDemandFetchService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Task> _pending = new();

    public OnDemandFetchService(
        IMediaSegmentManager mediaSegmentManager,
        ILibraryManager libraryManager,
        ILogger<OnDemandFetchService> logger)
    {
        _mediaSegmentManager = mediaSegmentManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
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
        => item is Movie
            || (item is Episode
                && series is not null
                && (series.HasProviderId(MetadataProvider.Tvdb) || series.HasProviderId(MetadataProvider.Tmdb)));

    /// <summary>
    /// Whether an updated item is looked up: after a metadata download, which is when an item
    /// added without its ids gets them.
    /// </summary>
    /// <param name="e">The update.</param>
    /// <returns>Whether to fetch now.</returns>
    internal static bool ShouldFetchOnItemUpdated([NotNullWhen(true)] ItemChangeEventArgs? e)
        => e?.Item is Episode or Movie && e.UpdateReason.HasFlag(ItemUpdateType.MetadataDownload);

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
