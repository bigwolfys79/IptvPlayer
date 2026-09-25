using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace IptvPlayer.Models;


public class PlaylistCache
{
    // 6: m3u VOD catalogs now fill Description/Year/Genre
    public const int CurrentFormatVersion = 6;

    public int FormatVersion { get; set; }


    public DateTime SavedAtUtc { get; set; }


    public string? PortalKeyHash { get; set; }

    public List<CachedChannel> Channels { get; set; } = new();


    // Source snapshot for local-file playlists: detects file changes between runs
    public DateTime? SourceLastWriteTimeUtc { get; set; }

    public long SourceLength { get; set; }

    public string? ContentHash { get; set; }


    // Fast path compares mtime+size; SHA-256 confirms so a content-preserving
    // touch does not force a full reparse
    public bool IsSourceChanged(string playlistPath)
    {
        if (SourceLastWriteTimeUtc == null || !File.Exists(playlistPath))
        {
            return false;
        }

        var info = new FileInfo(playlistPath);
        if (info.LastWriteTimeUtc == SourceLastWriteTimeUtc.Value && info.Length == SourceLength)
        {
            return false;
        }

        return !string.Equals(ComputeContentHash(playlistPath), ContentHash, StringComparison.OrdinalIgnoreCase);
    }


    public void UpdateSourceState(string playlistPath)
    {
        if (!File.Exists(playlistPath))
        {
            return;
        }

        var info = new FileInfo(playlistPath);
        SourceLastWriteTimeUtc = info.LastWriteTimeUtc;
        SourceLength = info.Length;
        ContentHash = ComputeContentHash(playlistPath);
    }


    private static string? ComputeContentHash(string path)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (IOException)
        {
            return null;
        }
    }
}


public class CachedChannel
{
    public string Name { get; set; } = string.Empty;
    public string? StreamUrl { get; set; }
    public string? LogoUrl { get; set; }
    public string? Group { get; set; }
    public string? TvgId { get; set; }


    public int CatchupDays { get; set; }


    public string? PortalRequest { get; set; }


    public string? Description { get; set; }

    public int Year { get; set; }


    public string? Genre { get; set; }
}
