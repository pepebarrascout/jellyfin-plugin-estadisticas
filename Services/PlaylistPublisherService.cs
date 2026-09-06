using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Estadisticas.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas.Services;

/// <summary>
/// Creates or updates Jellyfin playlists from a list of ItemIds.
/// The playlist content is REPLACED on each run (same playlist id, new items),
/// following the same pattern as jellyfin-smartlists-plugin and jellyfin-plugin-podcast:
/// - Find existing playlist by stored Guid, update its LinkedChildren in place.
/// - If no stored Guid, create a new playlist and persist its Guid.
/// - Never delete + recreate (avoids duplicate "Playlist1", "Playlist11" issues).
/// </summary>
public sealed class PlaylistPublisherService
{
    private readonly ILogger<PlaylistPublisherService> _logger;
    private readonly IPlaylistManager _playlistManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public PlaylistPublisherService(
        ILogger<PlaylistPublisherService> logger,
        IPlaylistManager playlistManager,
        ILibraryManager libraryManager,
        IUserManager userManager)
    {
        _logger = logger;
        _playlistManager = playlistManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Create or update a Jellyfin playlist with the given item ids.
    /// </summary>
    /// <param name="playlistName">Display name for the playlist.</param>
    /// <param name="existingPlaylistId">Existing playlist Guid (string) or null to create a new one.</param>
    /// <param name="itemIds">Jellyfin ItemIds (as strings) to put in the playlist.</param>
    /// <param name="adminUserId">Jellyfin user id to own the playlist (typically the admin).</param>
    /// <returns>The playlist Guid (string) that was created or updated.</returns>
    public async Task<string> PublishAsync(
        string playlistName,
        string? existingPlaylistId,
        IReadOnlyList<string> itemIds,
        Guid adminUserId)
    {
        if (string.IsNullOrWhiteSpace(playlistName))
            throw new ArgumentException("Playlist name required", nameof(playlistName));

        var user = _userManager.GetUserById(adminUserId);
        if (user == null)
            throw new InvalidOperationException($"Admin user {adminUserId} not found");

        // Resolve item ids to BaseItems
        var items = new List<BaseItem>();
        foreach (var idStr in itemIds)
        {
            if (Guid.TryParse(idStr, out var id))
            {
                var item = _libraryManager.GetItemById(id);
                if (item != null) items.Add(item);
            }
        }

        // Try to find an existing playlist by stored Guid
        Playlist? playlist = null;
        if (!string.IsNullOrWhiteSpace(existingPlaylistId) && Guid.TryParse(existingPlaylistId, out var existingId))
        {
            var existing = _libraryManager.GetItemById(existingId) as Playlist;
            if (existing != null)
            {
                playlist = existing;
                _logger.LogDebug("Found existing playlist {Id} for name '{Name}'", existingId, playlistName);
            }
        }

        if (playlist == null)
        {
            // Create new playlist via IPlaylistManager
            var createOptions = new PlaylistCreationRequest
            {
                Name = playlistName,
                ItemIdList = items.Select(i => i.Id).ToList(),
                UserId = user.Id,
                MediaType = MediaType.Audio
            };
            var result = await _playlistManager.CreatePlaylist(createOptions).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.Id))
            {
                throw new InvalidOperationException($"Failed to create playlist '{playlistName}'");
            }
            var newId = Guid.Parse(result.Id);
            playlist = _libraryManager.GetItemById(newId) as Playlist;
            if (playlist == null)
                throw new InvalidOperationException("Created playlist but could not retrieve it");

            _logger.LogInformation("Created playlist '{Name}' with {Count} items (id={Id})", playlistName, items.Count, newId);
            return result.Id;
        }

        // Update existing playlist: replace LinkedChildren
        var linkedChildren = items
            .Select(i => new LinkedChild { Path = i.Path, ItemId = i.Id })
            .ToArray();

        playlist.LinkedChildren = linkedChildren;
        await playlist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation("Updated playlist '{Name}' with {Count} items (id={Id})", playlistName, items.Count, playlist.Id);
        return playlist.Id.ToString();
    }
}
