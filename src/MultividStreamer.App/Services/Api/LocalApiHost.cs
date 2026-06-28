using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultividStreamer.App.Models;
using MultividStreamer.App.Services;

namespace MultividStreamer.App.Services.Api;

public sealed record StreamDiagnosticsSnapshot(
    DateTime TimestampLocal,
    string Method,
    string Range,
    string AuthSource,
    int StatusCode,
    long BytesSent,
    double AverageMbps,
    string ClientState);

public sealed class LocalApiHost
{
    private const int StreamBufferSize = 512 * 1024;
    private const int SocketSendBufferSize = 4 * 1024 * 1024;
    // How long an idle keep-alive connection waits for the next request before
    // we close it, so persistent connections don't pile up forever.
    private static readonly TimeSpan KeepAliveIdleTimeout = TimeSpan.FromSeconds(15);

    private readonly CatalogStore catalogStore;
    private readonly LibrarySourceStore sourceStore;
    private readonly ApiSettings settings;
    private readonly TrustedDeviceStore trustedDeviceStore;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly object pairingLock = new();

    // Browse cache: ResolveBrowseFolder + LoadBrowseEntries are O(catalog) and were
    // re-run on every page (the client paginates folders at 200 entries/page), so a
    // 2000-item folder rebuilt the full entry list ~10x. Cache the resolved folder and
    // its token-less entries per folderId, invalidated whenever the catalog list
    // instance changes (CatalogStore returns a new list on reload, e.g. after Rescan).
    // The per-request stream token is applied after the cache, so one cached list is
    // reusable across devices/tokens.
    private readonly object browseCacheLock = new();
    private List<CatalogItem>? browseCacheCatalogRef;
    private readonly Dictionary<string, (BrowseFolder folder, List<BrowseEntry> entries)> browseCache = new(StringComparer.Ordinal);

    // Source-folder index, rebuilt once per catalog version. Resolving a source
    // folderId and listing a source's items were both O(catalog): the resolver even
    // hashed (SHA256) every directory of every source on every call, so the FIRST
    // open of any folder cost seconds with a large catalog. The index turns resolve
    // into an O(1) lookup and listing into O(items-in-source). Zip folders stay lazy
    // so we never open every .zip just to navigate the source tree.
    private readonly object sourceIndexLock = new();
    private List<CatalogItem>? sourceIndexCatalogRef;
    private Dictionary<string, BrowseFolder>? sourceFolderById;
    private Dictionary<string, List<CatalogItem>>? itemsBySourceId;

    // Zip subfolders discovered while browsing their parent zip (you always open a
    // parent before its child), so resolving them is an O(1) hit instead of the old
    // "open every preceding .zip" scan. Versioned with the source index above:
    // cleared whenever the catalog changes.
    private readonly ConcurrentDictionary<string, BrowseFolder> discoveredZipFolders = new(StringComparer.Ordinal);

    // ffprobe results (duration/resolution) for /media/{id}/info, keyed by absolute
    // path. ffprobe is ~50-150ms per file, so cache it: the headset asks once per
    // playback, and a folder of transcode files would otherwise re-probe repeatedly.
    private readonly ConcurrentDictionary<string, MediaInfo> mediaInfoCache = new(StringComparer.OrdinalIgnoreCase);

    private TcpListener? listener;
    private CancellationTokenSource? cancellationTokenSource;
    private DiscoveryResponder? discoveryResponder;
    private string? pairingCode;
    private DateTime pairingExpiresUtc;

    public LocalApiHost(CatalogStore catalogStore, LibrarySourceStore sourceStore, ApiSettings settings, TrustedDeviceStore trustedDeviceStore)
    {
        this.catalogStore = catalogStore;
        this.sourceStore = sourceStore;
        this.settings = settings;
        this.trustedDeviceStore = trustedDeviceStore;
    }

    public event EventHandler? TrustedDevicesChanged;

    public event EventHandler<StreamDiagnosticsSnapshot>? StreamDiagnosticsUpdated;

    // Rolling request log surfaced in the app window so you can see, live, whether
    // the headset actually reaches the server, what it asks for, and what we answer.
    // (Stream video requests are excluded from the "<-" received lines to avoid
    // flooding during playback; they still report through StreamDiagnosticsUpdated.)
    public event Action<string>? RequestLogged;

    // Carries the current request's method+path through the async response writers
    // so WriteJsonAsync can log the status it sends without threading it everywhere.
    // AsyncLocal flows per logical call, so it is safe across concurrent clients.
    private static readonly AsyncLocal<RequestLogContext?> currentRequestLog = new();

    private sealed record RequestLogContext(string Method, string Path, long StartTimestamp);

    public bool IsRunning { get; private set; }

    public bool AllowLan { get; set; }

    public string BaseUrl => AllowLan ? $"http://{GetLocalNetworkAddress()}:{settings.Port}" : settings.BaseUrl;

    public string CatalogUrl => $"{BaseUrl}/catalog";

    public string HealthUrl => $"{BaseUrl}/health";

    public string Token => settings.Token;

    public string? PairingCode
    {
        get
        {
            lock (pairingLock)
            {
                return IsPairingActiveLocked() ? pairingCode : null;
            }
        }
    }

    public DateTime? PairingExpiresUtc
    {
        get
        {
            lock (pairingLock)
            {
                return IsPairingActiveLocked() ? pairingExpiresUtc : null;
            }
        }
    }

    public string StartPairing()
    {
        lock (pairingLock)
        {
            pairingCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            pairingExpiresUtc = DateTime.UtcNow.AddMinutes(5);
            return pairingCode;
        }
    }

    public void CancelPairing()
    {
        lock (pairingLock)
        {
            pairingCode = null;
            pairingExpiresUtc = default;
        }
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        cancellationTokenSource = new CancellationTokenSource();
        IPAddress bindAddress = AllowLan ? IPAddress.Any : IPAddress.Loopback;
        listener = new TcpListener(bindAddress, settings.Port);
        listener.Start();
        IsRunning = true;
        _ = Task.Run(() => ListenAsync(cancellationTokenSource.Token));

        // LAN auto-discovery: advertise this streamer so the headset can find it by
        // machine name. Best-effort — never let it stop the HTTP API from serving.
        discoveryResponder = new DiscoveryResponder(() =>
            new DiscoveryInfo(settings.ServerId, Environment.MachineName, BaseUrl, settings.Port, 1));
        try
        {
            discoveryResponder.Start();
        }
        catch (Exception)
        {
            discoveryResponder = null;
        }
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        discoveryResponder?.Stop();
        discoveryResponder = null;
        cancellationTokenSource?.Cancel();
        listener?.Stop();
        listener = null;
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = null;
        IsRunning = false;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener != null)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        // Disable Nagle and keep the connection open across requests (HTTP
        // keep-alive). The headset issues successive Range requests while
        // playing; closing the socket after each one forced a fresh TCP
        // handshake + slow-start every time, creating delivery gaps that
        // starved the player's buffer (short, for 8K) and caused stutter.
        client.NoDelay = true;
        client.SendBufferSize = SocketSendBufferSize;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);

        try
        {
            while (true)
            {
                ApiRequest? request;
                using (CancellationTokenSource idleCts = new(KeepAliveIdleTimeout))
                {
                    try
                    {
                        request = await ReadRequestAsync(reader, idleCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break; // idle connection timed out waiting for a request
                    }
                }

                if (request == null)
                {
                    break; // client closed the connection
                }

                await RouteRequestAsync(stream, request);

                if (RequestsConnectionClose(request))
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            // Connection-level failure (e.g. the client reset the socket mid
            // transfer). Nothing to send; just drop the connection.
        }
        finally
        {
            client.Close();
        }
    }

    private static bool RequestsConnectionClose(ApiRequest request)
    {
        return request.Headers.TryGetValue("Connection", out string? value)
            && value.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private async Task RouteRequestAsync(NetworkStream stream, ApiRequest request)
    {
        try
        {
            string path = request.Path.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(path))
            {
                path = "/";
            }

            bool isStreamPath = path.StartsWith("/stream/", StringComparison.OrdinalIgnoreCase);
            bool isDiagnosticRangePath = string.Equals(path, "/diagnostics/range", StringComparison.OrdinalIgnoreCase);

            currentRequestLog.Value = new RequestLogContext(request.Method, path, System.Diagnostics.Stopwatch.GetTimestamp());
            if (!isStreamPath && !isDiagnosticRangePath)
            {
                // Connection / browse phase: log arrival so a request that reaches us
                // but then hangs (no following "->" line) is obvious.
                LogRequestReceived(request, path);
            }

            if (!IsGetOrHead(request) || (IsHead(request) && !isStreamPath && !isDiagnosticRangePath))
            {
                await WriteJsonAsync(stream, HttpStatusCode.MethodNotAllowed, new { error = "method_not_allowed" });
                return;
            }

            if (path == "/health")
            {
                await WriteJsonAsync(stream, HttpStatusCode.OK, new { status = "ok" });
                return;
            }

            if (path == "/server/info")
            {
                await WriteServerInfoAsync(stream);
                return;
            }

            if (path == "/pair/claim")
            {
                await WritePairClaimAsync(stream, request);
                return;
            }

            if (!IsAuthorized(request))
            {
                if (isStreamPath || isDiagnosticRangePath)
                {
                    NotifyStreamDiagnostics(request, HttpStatusCode.Unauthorized, 0, 0, "Unauthorized: token manquant ou invalide");
                }

                await WriteJsonAsync(stream, HttpStatusCode.Unauthorized, new { error = "unauthorized" });
                return;
            }

            if (path == "/catalog")
            {
                string? streamToken = GetRequestToken(request);
                List<PublicCatalogItem> items = catalogStore.Load()
                    .Select(item => ToPublicItem(item, streamToken))
                    .ToList();

                await WriteJsonAsync(stream, HttpStatusCode.OK, new
                {
                    generatedUtc = DateTime.UtcNow,
                    itemCount = items.Count,
                    items
                });
                return;
            }

            if (path == "/browse/root")
            {
                await WriteBrowseRootAsync(stream, request);
                return;
            }

            if (path == "/browse")
            {
                await WriteBrowseFolderAsync(stream, request);
                return;
            }

            if (isStreamPath)
            {
                string itemId = Uri.UnescapeDataString(path["/stream/".Length..]);
                await WriteStreamAsync(stream, request, itemId);
                return;
            }

            if (isDiagnosticRangePath)
            {
                await WriteDiagnosticRangeAsync(stream, request);
                return;
            }

            if (path.StartsWith("/zip/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/items", StringComparison.OrdinalIgnoreCase))
            {
                string zipItemId = Uri.UnescapeDataString(path["/zip/".Length..^"/items".Length].Trim('/'));
                await WriteZipItemsAsync(stream, request, zipItemId);
                return;
            }

            if (path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/info", StringComparison.OrdinalIgnoreCase))
            {
                string mediaItemId = Uri.UnescapeDataString(path["/media/".Length..^"/info".Length].Trim('/'));
                await WriteMediaInfoAsync(stream, request, mediaItemId);
                return;
            }

            await WriteJsonAsync(stream, HttpStatusCode.NotFound, new { error = "not_found" });
        }
        catch (Exception exception)
        {
            await WriteJsonAsync(stream, HttpStatusCode.InternalServerError, new { error = "server_error", message = exception.Message });
        }
    }

    private static async Task<ApiRequest?> ReadRequestAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return null;
        }

        string[] parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            int separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string name = line[..separatorIndex].Trim();
            string value = line[(separatorIndex + 1)..].Trim();
            headers[name] = value;
        }

        string target = parts[1];
        string path = target;
        Dictionary<string, string> query = new(StringComparer.OrdinalIgnoreCase);
        int queryIndex = target.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = target[..queryIndex];
            query = ParseQuery(target[(queryIndex + 1)..]);
        }

        return new ApiRequest(parts[0], path, headers, query);
    }

    private bool IsAuthorized(ApiRequest request)
    {
        string? token = GetRequestToken(request);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (string.Equals(token, settings.Token, StringComparison.Ordinal))
        {
            return true;
        }

        return trustedDeviceStore.TryAuthorizeToken(token, out _);
    }

    private static string? GetRequestToken(ApiRequest request)
    {
        request.Headers.TryGetValue("Authorization", out string? authorization);
        if (!string.IsNullOrWhiteSpace(authorization)
            && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        request.Query.TryGetValue("token", out string? queryToken);
        return queryToken;
    }

    private static string GetRequestTokenSource(ApiRequest request)
    {
        request.Headers.TryGetValue("Authorization", out string? authorization);
        if (!string.IsNullOrWhiteSpace(authorization)
            && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return "header";
        }

        return request.Query.TryGetValue("token", out string? queryToken) && !string.IsNullOrWhiteSpace(queryToken)
            ? "query"
            : "aucun";
    }

    private static bool IsGetOrHead(ApiRequest request)
    {
        return string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
            || IsHead(request);
    }

    private static bool IsHead(ApiRequest request)
    {
        return string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
    }

    private async Task WriteServerInfoAsync(NetworkStream stream)
    {
        await WriteJsonAsync(stream, HttpStatusCode.OK, new
        {
            name = "Multivid Streamer",
            machineName = Environment.MachineName,
            serverId = settings.ServerId,
            apiVersion = 1,
            baseUrl = BaseUrl,
            serverTimeUtc = DateTime.UtcNow,
            authentication = new
            {
                type = "bearer_or_query_token",
                tokenScope = "trusted_device",
                pairing = "short_code",
                pairingClaimUrl = "/pair/claim?code={code}&deviceName={deviceName}"
            },
            capabilities = new
            {
                browse = true,
                browsePagination = true,
                streamById = true,
                streamRange = true,
                diagnosticsRange = true,
                zipBrowse = true,
                lan = AllowLan,
                pairingCode = true,
                trustedDevices = true
            },
            endpoints = new
            {
                health = "/health",
                serverInfo = "/server/info",
                pairClaim = "/pair/claim?code={code}&deviceName={deviceName}",
                browseRoot = "/browse/root",
                browse = "/browse?folderId={folderId}&limit={limit}&cursor={cursor}",
                stream = "/stream/{itemId}",
                streamSeek = "/stream/{itemId}?t={seconds}",
                mediaInfo = "/media/{itemId}/info",
                diagnosticsRange = "/diagnostics/range",
                zipItems = "/zip/{zipItemId}/items"
            }
        });
    }

    private async Task WritePairClaimAsync(NetworkStream stream, ApiRequest request)
    {
        if (!request.Query.TryGetValue("code", out string? code) || string.IsNullOrWhiteSpace(code))
        {
            await WriteJsonAsync(stream, HttpStatusCode.BadRequest, new { error = "missing_code" });
            return;
        }

        string? error = null;
        lock (pairingLock)
        {
            if (!IsPairingActiveLocked())
            {
                error = "pairing_inactive";
            }
            else if (!string.Equals(pairingCode, code.Trim(), StringComparison.Ordinal))
            {
                error = "invalid_code";
            }
            else
            {
                pairingCode = null;
                pairingExpiresUtc = default;
            }
        }

        if (error != null)
        {
            await WriteJsonAsync(stream, HttpStatusCode.Unauthorized, new { error });
            return;
        }

        request.Query.TryGetValue("deviceName", out string? deviceName);
        string deviceToken = TrustedDeviceStore.CreateDeviceToken();
        TrustedDevice device = trustedDeviceStore.AddDevice(deviceName ?? string.Empty, deviceToken);
        TrustedDevicesChanged?.Invoke(this, EventArgs.Empty);

        await WriteJsonAsync(stream, HttpStatusCode.OK, new
        {
            status = "paired",
            baseUrl = BaseUrl,
            serverId = settings.ServerId,
            machineName = Environment.MachineName,
            token = deviceToken,
            tokenScope = "trusted_device",
            deviceId = device.Id,
            deviceName = device.Name
        });
    }

    private bool IsPairingActiveLocked()
    {
        if (string.IsNullOrWhiteSpace(pairingCode) || DateTime.UtcNow >= pairingExpiresUtc)
        {
            pairingCode = null;
            pairingExpiresUtc = default;
            return false;
        }

        return true;
    }

    private async Task WriteBrowseRootAsync(NetworkStream stream, ApiRequest request)
    {
        List<CatalogItem> catalogItems = catalogStore.Load();

        // Count items per source in a single pass. The previous Count(...) per
        // source was O(sources x catalog) — with ~150 sources over ~200k items that
        // is tens of millions of comparisons on every /browse/root call.
        Dictionary<string, int> itemCountBySource = new(StringComparer.Ordinal);
        foreach (CatalogItem item in catalogItems)
        {
            if (string.IsNullOrEmpty(item.SourceId))
            {
                continue;
            }

            itemCountBySource[item.SourceId] = itemCountBySource.GetValueOrDefault(item.SourceId) + 1;
        }

        List<BrowseEntry> entries = sourceStore.Load()
            .Select(source => ToSourceBrowseEntry(source, itemCountBySource.GetValueOrDefault(source.Id)))
            .ToList();

        // Root is the bounded list of library sources (small); return it whole and
        // never paginate, so nextCursor is always null here. This makes the client's
        // browse loop terminate on root no matter what — a defensive backstop against
        // a client that does not advance the cursor on /browse/root.
        await WriteJsonAsync(stream, HttpStatusCode.OK, new
        {
            folderId = "root",
            name = "Streamer PC",
            itemCount = entries.Count,
            cursor = 0,
            limit = entries.Count,
            nextCursor = (int?)null,
            entries
        });
    }

    private async Task WriteBrowseFolderAsync(NetworkStream stream, ApiRequest request)
    {
        if (!request.Query.TryGetValue("folderId", out string? folderId) || string.IsNullOrWhiteSpace(folderId))
        {
            await WriteJsonAsync(stream, HttpStatusCode.BadRequest, new { error = "missing_folder_id" });
            return;
        }

        (BrowseFolder folder, List<BrowseEntry> entries)? resolved;
        try
        {
            resolved = GetCachedBrowse(folderId);
        }
        catch (InvalidDataException)
        {
            await WriteJsonAsync(stream, HttpStatusCode.BadRequest, new { error = "invalid_zip" });
            return;
        }
        catch (IOException)
        {
            await WriteJsonAsync(stream, HttpStatusCode.BadRequest, new { error = "zip_unavailable" });
            return;
        }

        if (resolved == null)
        {
            await WriteJsonAsync(stream, HttpStatusCode.NotFound, new { error = "not_found" });
            return;
        }

        BrowseFolder folder = resolved.Value.folder;
        List<BrowseEntry> entries = AddTokenToStreamUrls(resolved.Value.entries, GetRequestToken(request));
        BrowsePage page = CreateBrowsePage(entries, request);
        NotifyStreamDiagnostics(request, HttpStatusCode.OK, 0, 0,
            $"BROWSE {(string.IsNullOrEmpty(folder.directory) ? folder.name : folder.directory)} -> {entries.Count} items");
        await WriteJsonAsync(stream, HttpStatusCode.OK, new
        {
            folderId = folder.Id,
            folder.name,
            folder.parentId,
            folder.sourceId,
            folder.zipId,
            folder.directory,
            itemCount = entries.Count,
            page.cursor,
            page.limit,
            page.nextCursor,
            entries = page.entries
        });
    }

    private (BrowseFolder folder, List<BrowseEntry> entries)? GetCachedBrowse(string folderId)
    {
        // CatalogStore returns the same list instance until the catalog file changes,
        // so reference identity is our cache-version key (cheap, no extra stat).
        List<CatalogItem> catalog = catalogStore.Load();

        lock (browseCacheLock)
        {
            if (!ReferenceEquals(browseCacheCatalogRef, catalog))
            {
                browseCache.Clear();
                browseCacheCatalogRef = catalog;
            }
            else if (browseCache.TryGetValue(folderId, out var hit))
            {
                return hit;
            }
        }

        // Build outside the lock: ResolveBrowseFolder/LoadBrowseEntries only read the
        // immutable catalog snapshot, so concurrent builds are safe and idempotent.
        BrowseFolder? folder = ResolveBrowseFolder(folderId);
        if (folder == null)
        {
            return null;
        }

        (BrowseFolder folder, List<BrowseEntry> entries) built = (folder, LoadBrowseEntries(folder));

        lock (browseCacheLock)
        {
            if (ReferenceEquals(browseCacheCatalogRef, catalog))
            {
                browseCache[folderId] = built;
            }
        }

        return built;
    }

    // Builds (once per catalog version) the maps used to resolve and list source
    // folders in O(1) / O(items-in-source) instead of O(catalog) per request.
    private (Dictionary<string, BrowseFolder> folders, Dictionary<string, List<CatalogItem>> itemsBySource) GetSourceIndex()
    {
        List<CatalogItem> catalog = catalogStore.Load();

        lock (sourceIndexLock)
        {
            if (ReferenceEquals(sourceIndexCatalogRef, catalog) &&
                sourceFolderById != null && itemsBySourceId != null)
            {
                return (sourceFolderById, itemsBySourceId);
            }
        }

        // Build outside the lock: only reads the immutable catalog/source snapshots.
        List<LibrarySource> sources = sourceStore.Load();

        Dictionary<string, List<CatalogItem>> itemsBySource = new(StringComparer.Ordinal);
        foreach (CatalogItem item in catalog)
        {
            if (string.IsNullOrEmpty(item.SourceId))
            {
                continue;
            }

            if (!itemsBySource.TryGetValue(item.SourceId, out List<CatalogItem>? list))
            {
                list = new List<CatalogItem>();
                itemsBySource[item.SourceId] = list;
            }

            list.Add(item);
        }

        Dictionary<string, BrowseFolder> folders = new(StringComparer.Ordinal);
        foreach (LibrarySource source in sources)
        {
            folders[source.Id] = new BrowseFolder(source.Id, source.Name, null, source.Id, null, string.Empty, BrowseFolderKind.Source);

            HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
            if (itemsBySource.TryGetValue(source.Id, out List<CatalogItem>? sourceItems))
            {
                foreach (CatalogItem item in sourceItems)
                {
                    AddDirectoryAndParents(directories, item.Directory);
                }
            }

            foreach (string directory in directories)
            {
                string id = CreateSourceFolderId(source.Id, directory);
                folders[id] = new BrowseFolder(
                    id,
                    GetDirectoryName(directory),
                    GetParentSourceFolderId(source.Id, directory),
                    source.Id,
                    null,
                    directory,
                    BrowseFolderKind.SourceDirectory);
            }
        }

        // Zip roots are resolvable without opening any archive (the id is just a hash
        // of the catalog zip-item id), so index them too. This alone kills the old
        // cost of opening every preceding .zip when entering a zip near the end of a
        // large library. Zip SUBfolders stay lazy (discovered on browse).
        foreach (CatalogItem zipItem in catalog.Where(IsExistingZipItem))
        {
            string zipRootId = CreateZipFolderId(zipItem.Id, string.Empty);
            folders[zipRootId] = new BrowseFolder(
                zipRootId, zipItem.FileName, null, zipItem.SourceId, zipItem.Id, string.Empty, BrowseFolderKind.ZipRoot);
        }

        lock (sourceIndexLock)
        {
            sourceIndexCatalogRef = catalog;
            sourceFolderById = folders;
            itemsBySourceId = itemsBySource;
            discoveredZipFolders.Clear(); // new catalog version: drop stale discoveries
        }

        return (folders, itemsBySource);
    }

    private BrowseFolder? ResolveBrowseFolder(string folderId)
    {
        (Dictionary<string, BrowseFolder> folders, _) = GetSourceIndex();
        if (folders.TryGetValue(folderId, out BrowseFolder? sourceFolder))
        {
            return sourceFolder; // source / source-dir / zip-root: O(1), no archive opened
        }

        // Zip subfolder reached through its parent (the normal path): O(1), no scan.
        if (discoveredZipFolders.TryGetValue(folderId, out BrowseFolder? discovered))
        {
            return discovered;
        }

        // Last resort only (e.g. a deep link to a zip subfolder never reached via its
        // parent). Normal navigation never gets here, so the per-zip open cost below
        // is no longer on the hot path.
        foreach (CatalogItem zipItem in catalogStore.Load().Where(IsExistingZipItem))
        {
            string zipRootId = CreateZipFolderId(zipItem.Id, string.Empty);
            if (string.Equals(zipRootId, folderId, StringComparison.Ordinal))
            {
                return new BrowseFolder(zipRootId, zipItem.FileName, null, zipItem.SourceId, zipItem.Id, string.Empty, BrowseFolderKind.ZipRoot);
            }

            foreach (string directory in LoadZipDirectories(zipItem))
            {
                string candidateId = CreateZipFolderId(zipItem.Id, directory);
                if (string.Equals(candidateId, folderId, StringComparison.Ordinal))
                {
                    return new BrowseFolder(
                        candidateId,
                        GetDirectoryName(directory),
                        GetParentZipFolderId(zipItem.Id, directory),
                        zipItem.SourceId,
                        zipItem.Id,
                        directory,
                        BrowseFolderKind.ZipDirectory);
                }
            }
        }

        return null;
    }

    private List<BrowseEntry> LoadBrowseEntries(BrowseFolder folder)
    {
        return folder.kind is BrowseFolderKind.ZipRoot or BrowseFolderKind.ZipDirectory
            ? LoadZipBrowseEntries(folder)
            : LoadSourceBrowseEntries(folder);
    }

    private List<BrowseEntry> LoadSourceBrowseEntries(BrowseFolder folder)
    {
        (_, Dictionary<string, List<CatalogItem>> itemsBySource) = GetSourceIndex();
        List<CatalogItem> items = itemsBySource.TryGetValue(folder.sourceId, out List<CatalogItem>? sourceItems)
            ? sourceItems
            : new List<CatalogItem>();
        List<BrowseEntry> entries = new();
        HashSet<string> childDirectories = new(StringComparer.OrdinalIgnoreCase);

        foreach (CatalogItem item in items)
        {
            string itemDirectory = NormalizeVirtualDirectory(item.Directory);
            if (TryGetImmediateChildDirectory(folder.directory, itemDirectory, out string childDirectory)
                && childDirectories.Add(childDirectory))
            {
                entries.Add(ToSourceFolderBrowseEntry(folder.sourceId, childDirectory, folder.Id));
                continue;
            }

            if (!string.Equals(itemDirectory, folder.directory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(string.Equals(item.Extension, ".zip", StringComparison.OrdinalIgnoreCase)
                ? ToZipBrowseEntry(item, folder.Id)
                : ToFileBrowseEntry(item));
        }

        return entries;
    }

    private List<BrowseEntry> LoadZipBrowseEntries(BrowseFolder folder)
    {
        CatalogItem? zipItem = FindCatalogItem(folder.zipId ?? string.Empty);
        if (!IsExistingZipItem(zipItem))
        {
            return new List<BrowseEntry>();
        }

        List<BrowseEntry> entries = new();
        HashSet<string> childDirectories = new(StringComparer.OrdinalIgnoreCase);

        using FileStream zipStream = new(
            zipItem!.AbsolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);

        foreach (ZipArchiveEntry entry in archive.Entries.Where(IsSupportedZipEntry))
        {
            string entryDirectory = GetZipEntryDirectory(entry.FullName);
            if (TryGetImmediateChildDirectory(folder.directory, entryDirectory, out string childDirectory)
                && childDirectories.Add(childDirectory))
            {
                // Remember this child so opening it later is an O(1) resolve instead
                // of a fresh scan over every .zip (its parent — this folder — is the
                // archive we already have open).
                string childId = CreateZipFolderId(zipItem.Id, childDirectory);
                discoveredZipFolders[childId] = new BrowseFolder(
                    childId,
                    GetDirectoryName(childDirectory),
                    folder.Id,
                    zipItem.SourceId,
                    zipItem.Id,
                    childDirectory,
                    BrowseFolderKind.ZipDirectory);
                entries.Add(ToZipFolderBrowseEntry(zipItem, childDirectory, folder.Id));
                continue;
            }

            if (!string.Equals(entryDirectory, folder.directory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(ToZipEntryBrowseEntry(ToPublicZipEntryItem(zipItem, entry), folder.Id));
        }

        return entries;
    }

    private static List<BrowseEntry> AddTokenToStreamUrls(IEnumerable<BrowseEntry> entries, string? token)
    {
        return entries
            .Select(entry => string.IsNullOrWhiteSpace(entry.streamUrl)
                ? entry
                : entry with { streamUrl = AddTokenToStreamUrl(entry.streamUrl, token) })
            .ToList();
    }

    private static List<PublicZipEntryItem> AddTokenToZipItemStreamUrls(IEnumerable<PublicZipEntryItem> items, string? token)
    {
        return items
            .Select(item => item with { StreamUrl = AddTokenToStreamUrl(item.StreamUrl, token) })
            .ToList();
    }

    private static string AddTokenToStreamUrl(string streamUrl, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || streamUrl.Contains("token=", StringComparison.OrdinalIgnoreCase))
        {
            return streamUrl;
        }

        char separator = streamUrl.Contains('?') ? '&' : '?';
        return $"{streamUrl}{separator}token={Uri.EscapeDataString(token)}";
    }

    private async Task WriteStreamAsync(NetworkStream stream, ApiRequest request, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || itemId.Contains('/'))
        {
            await WriteJsonAsync(stream, HttpStatusCode.NotFound, new { error = "not_found" });
            return;
        }

        if (itemId.StartsWith("zipentry_", StringComparison.Ordinal))
        {
            await WriteZipEntryStreamAsync(stream, request, itemId);
            return;
        }

        CatalogItem? item = FindCatalogItem(itemId);
        if (item == null || string.IsNullOrWhiteSpace(item.AbsolutePath) || !File.Exists(item.AbsolutePath))
        {
            await WriteJsonAsync(stream, HttpStatusCode.NotFound, new { error = "not_found" });
            return;
        }

        // Formats the headset can't decode (transcode-formats.json) are converted live
        // and piped through, instead of served as the raw (unplayable) file.
        if (SupportedMediaTypes.NeedsTranscode(item.AbsolutePath))
        {
            await WriteTranscodedStreamAsync(stream, request, item.AbsolutePath);
            return;
        }

        FileInfo file = new(item.AbsolutePath);
        await WriteFileStreamAsync(stream, request, item.AbsolutePath, file.Length, GetContentType(item.Extension));
    }

    // === Live transcode =======================================================
    // Formats the headset can't decode (wmv/flv/... per transcode-formats.json) are
    // converted on the fly: ffmpeg decodes the source and re-encodes to fragmented MP4,
    // whose bytes are streamed to the client as they are produced — no temp file, no
    // "transcode then send". The body uses chunked transfer-encoding because the final
    // size is unknown up front, which keeps HTTP keep-alive intact.
    //
    // Scope: plays from the start, or ?t=<seconds> for a best-effort seek (-ss tolerates
    // ~1-2s). Byte-range seeking is impossible on a live stream — the headset re-opens
    // the URL with ?t= to seek (separate, client-side change).

    // POC uses libx264 to isolate "does AVPro accept the piped stream?" from any Intel
    // QSV driver issue. Once the pipe is validated, switch to Arc hardware encode:
    //   "-c:v h264_qsv -global_quality 23 -preset veryfast"
    private const string TranscodeVideoArgs = "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p";
    private const string TranscodeAudioArgs = "-c:a aac -b:a 192k";

    private static string? ResolveFfmpegPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        return File.Exists(path) ? path : null;
    }

    private async Task WriteTranscodedStreamAsync(NetworkStream stream, ApiRequest request, string inputPath)
    {
        string? ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath == null)
        {
            await WriteJsonAsync(stream, HttpStatusCode.InternalServerError, new { error = "ffmpeg_missing" });
            NotifyStreamDiagnostics(request, HttpStatusCode.InternalServerError, 0, 0, "ffmpeg.exe introuvable dans tools\\");
            return;
        }

        double startSeconds = 0;
        if (request.Query.TryGetValue("t", out string? tValue)
            && double.TryParse(tValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            && parsed > 0)
        {
            startSeconds = parsed;
        }

        // -ss BEFORE -i = fast keyframe-accurate input seek (~1-2s tolerance).
        string seekArg = startSeconds > 0
            ? $"-ss {startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
            : string.Empty;

        string arguments =
            "-hide_banner -loglevel error " +
            seekArg +
            $"-i \"{inputPath}\" " +
            TranscodeVideoArgs + " " +
            TranscodeAudioArgs + " " +
            "-movflags frag_keyframe+empty_moov+default_base_moof " +
            "-f mp4 pipe:1";

        // HEAD: announce the response shape without launching ffmpeg.
        if (IsHead(request))
        {
            await WriteTranscodeHeadersAsync(stream);
            NotifyStreamDiagnostics(request, HttpStatusCode.OK, 0, 0, "HEAD transcode");
            return;
        }

        System.Diagnostics.ProcessStartInfo startInfo = new()
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        System.Diagnostics.Process? process = null;
        string? clientState = null;
        long bytesSent = 0;
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        System.Text.StringBuilder stderrTail = new();

        try
        {
            process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                await WriteJsonAsync(stream, HttpStatusCode.InternalServerError, new { error = "ffmpeg_start_failed" });
                return;
            }

            // Drain stderr async so ffmpeg never blocks on a full error pipe, and keep
            // the last lines for diagnostics (a codec/QSV failure shows up here).
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                {
                    return;
                }

                lock (stderrTail)
                {
                    stderrTail.AppendLine(e.Data);
                    if (stderrTail.Length > 4000)
                    {
                        stderrTail.Remove(0, stderrTail.Length - 4000);
                    }
                }
            };
            process.BeginErrorReadLine();

            await WriteTranscodeHeadersAsync(stream);

            Stream ffmpegOut = process.StandardOutput.BaseStream;
            byte[] buffer = new byte[StreamBufferSize];
            byte[] crlf = { (byte)'\r', (byte)'\n' };
            while (true)
            {
                int read = await ffmpegOut.ReadAsync(buffer);
                if (read == 0)
                {
                    break; // ffmpeg finished
                }

                // Chunked framing: "<hex size>\r\n<data>\r\n".
                byte[] sizeLine = Encoding.ASCII.GetBytes(read.ToString("X") + "\r\n");
                await stream.WriteAsync(sizeLine);
                await stream.WriteAsync(buffer.AsMemory(0, read));
                await stream.WriteAsync(crlf);
                bytesSent += read;
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n")); // terminating chunk
        }
        catch (IOException exception)
        {
            clientState = $"Client closed: {exception.Message}";
        }
        catch (ObjectDisposedException exception)
        {
            clientState = $"Client closed: {exception.Message}";
        }
        finally
        {
            stopwatch.Stop();
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception)
                {
                    // Best effort — the client is gone or ffmpeg already exited.
                }

                string tail;
                lock (stderrTail)
                {
                    tail = stderrTail.ToString().Trim();
                }

                if (clientState == null && !string.IsNullOrEmpty(tail))
                {
                    clientState = "ffmpeg: " + tail.Replace("\r", " ").Replace("\n", " ");
                }

                process.Dispose();
            }

            NotifyStreamDiagnostics(request, HttpStatusCode.OK, bytesSent,
                CalculateMbps(bytesSent, stopwatch.Elapsed), clientState ?? "transcode OK");
        }
    }

    private static async Task WriteTranscodeHeadersAsync(NetworkStream stream)
    {
        string header = string.Join("\r\n",
            "HTTP/1.1 200 OK",
            "Content-Type: video/mp4",
            "Accept-Ranges: none",
            "Cache-Control: no-store, no-transform",
            "Transfer-Encoding: chunked",
            "Connection: keep-alive",
            string.Empty,
            string.Empty);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
    }

    // Media metadata for the headset: duration is essential for transcoded items
    // because a live (fragmented, empty-moov) stream carries no total duration, so the
    // headset can't position its scrubber without it. Resolution is informational.
    private async Task WriteMediaInfoAsync(NetworkStream stream, ApiRequest request, string itemId)
    {
        CatalogItem? item = FindCatalogItem(itemId);
        if (item == null || string.IsNullOrWhiteSpace(item.AbsolutePath) || !File.Exists(item.AbsolutePath))
        {
            await WriteJsonAsync(stream, HttpStatusCode.NotFound, new { error = "not_found" });
            return;
        }

        MediaInfo? info = await ProbeMediaAsync(item.AbsolutePath);

        await WriteJsonAsync(stream, HttpStatusCode.OK, new
        {
            id = item.Id,
            needsTranscode = SupportedMediaTypes.NeedsTranscode(item.FileName),
            durationSeconds = info?.DurationSeconds,
            width = info?.Width,
            height = info?.Height
        });
    }

    private static string? ResolveFfprobePath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe");
        return File.Exists(path) ? path : null;
    }

    private async Task<MediaInfo?> ProbeMediaAsync(string inputPath)
    {
        if (mediaInfoCache.TryGetValue(inputPath, out MediaInfo? cached))
        {
            return cached;
        }

        string? ffprobePath = ResolveFfprobePath();
        if (ffprobePath == null)
        {
            return null;
        }

        System.Diagnostics.ProcessStartInfo startInfo = new()
        {
            FileName = ffprobePath,
            Arguments = "-v error -select_streams v:0 "
                + "-show_entries format=duration:stream=width,height "
                + $"-of json \"{inputPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                return null;
            }

            string json = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            MediaInfo? info = ParseFfprobeJson(json);
            if (info != null)
            {
                mediaInfoCache[inputPath] = info;
            }

            return info;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static MediaInfo? ParseFfprobeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            double? duration = null;
            if (root.TryGetProperty("format", out JsonElement format)
                && format.TryGetProperty("duration", out JsonElement durationElement)
                && double.TryParse(durationElement.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedDuration))
            {
                duration = parsedDuration;
            }

            int? width = null;
            int? height = null;
            if (root.TryGetProperty("streams", out JsonElement streams)
                && streams.ValueKind == JsonValueKind.Array
                && streams.GetArrayLength() > 0)
            {
                JsonElement firstStream = streams[0];
                if (firstStream.TryGetProperty("width", out JsonElement widthElement) && widthElement.TryGetInt32(out int parsedWidth))
                {
                    width = parsedWidth;
                }

                if (firstStream.TryGetProperty("height", out JsonElement heightElement) && heightElement.TryGetInt32(out int parsedHeight))
                {
                    height = parsedHeight;
                }
            }

            return new MediaInfo(duration, width, height);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteDiagnosticRangeAsync(NetworkStream stream, ApiRequest request)
    {
        byte[] bytes = new byte[4096];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        await using MemoryStream diagnosticStream = new(bytes, writable: false);
        await WriteSeekableStreamAsync(stream, request, diagnosticStream, bytes.Length, "application/octet-stream");
    }

    private async Task WriteZipItemsAsync(NetworkStream stream, ApiRequest request, string zipItemId)
    {
        CatalogItem? zipItem = FindCatalogItem(zipItemId);
        if (!IsExistingZipItem(zipItem))
        {
            await WriteJsonAsync(stream, HttpStatusCode.NotFound, new { error = "not_found" });
            return;
        }

        CatalogItem existingZipItem = zipItem!;
        List<PublicZipEntryItem> items;
        try
        {
            items = AddTokenToZipItemStreamUrls(LoadZipEntryItems(existingZipItem), GetRequestToken(request));
        }
        catch (InvalidDataException)
        {
            await WriteJsonAsync(stream, HttpStatusCode.BadRequest, new { error = "invalid_zip" });
            return;
        }
        catch (IOException)
        {
            await WriteJsonAsync(stream, HttpStatusCode.BadRequest, new { error = "zip_unavailable" });
            return;
        }

        await WriteJsonAsync(stream, HttpStatusCode.OK, new
        {
            zipId = existingZipItem.Id,
            zipFileName = existingZipItem.FileName,
            itemCount = items.Count,
            items
        });
    }

    private async Task WriteZipEntryStreamAsync(NetworkStream stream, ApiRequest request, string zipEntryId)
    {
        ZipEntryMatch? match;
        try
        {
            match = FindZipEntry(zipEntryId);
        }
        catch (InvalidDataException)
        {
            await WriteJsonAsync(stream, HttpStatusCode.BadRequest, new { error = "invalid_zip" });
            return;
        }
        catch (IOException)
        {
            await WriteJsonAsync(stream, HttpStatusCode.BadRequest, new { error = "zip_unavailable" });
            return;
        }

        if (match == null)
        {
            await WriteJsonAsync(stream, HttpStatusCode.NotFound, new { error = "not_found" });
            return;
        }

        await using FileStream zipStream = new(match.ZipPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = StreamBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry? entry = archive.GetEntry(match.EntryName);
        if (entry == null || !IsSupportedZipEntry(entry))
        {
            await WriteJsonAsync(stream, HttpStatusCode.NotFound, new { error = "not_found" });
            return;
        }

        await using Stream entryStream = entry.Open();
        await WriteSeeklessStreamAsync(stream, request, entryStream, entry.Length, GetContentType(Path.GetExtension(entry.Name)));
    }

    private async Task WriteFileStreamAsync(NetworkStream stream, ApiRequest request, string filePath, long fileLength, string contentType)
    {
        await using FileStream fileStream = new(filePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = StreamBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

        await WriteSeekableStreamAsync(stream, request, fileStream, fileLength, contentType);
    }

    private async Task WriteSeekableStreamAsync(NetworkStream stream, ApiRequest request, Stream source, long sourceLength, string contentType)
    {
        if (!TryCreateRange(request, sourceLength, out StreamRange range, out bool rangeRequested))
        {
            await WriteRangeNotSatisfiableAsync(stream, sourceLength);
            NotifyStreamDiagnostics(request, HttpStatusCode.RequestedRangeNotSatisfiable, 0, 0, null);
            return;
        }

        HttpStatusCode statusCode = await WriteStreamHeadersAsync(stream, sourceLength, contentType, range, rangeRequested);

        long contentLength = range.End - range.Start + 1;
        if (contentLength == 0 || IsHead(request))
        {
            NotifyStreamDiagnostics(request, statusCode, 0, 0, null);
            return;
        }

        string? clientState = null;
        long bytesSent = 0;
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            source.Seek(range.Start, SeekOrigin.Begin);
            StreamCopyResult copyResult = await CopyBytesAsync(source, stream, contentLength);
            bytesSent = copyResult.BytesSent;
            clientState = copyResult.ClientState;
        }
        catch (IOException exception)
        {
            clientState = $"IOException: {exception.Message}";
        }
        catch (ObjectDisposedException exception)
        {
            clientState = $"Closed: {exception.Message}";
        }
        finally
        {
            stopwatch.Stop();
            NotifyStreamDiagnostics(request, statusCode, bytesSent, CalculateMbps(bytesSent, stopwatch.Elapsed), clientState);
        }
    }

    private async Task WriteSeeklessStreamAsync(NetworkStream stream, ApiRequest request, Stream source, long sourceLength, string contentType)
    {
        if (!TryCreateRange(request, sourceLength, out StreamRange range, out bool rangeRequested))
        {
            await WriteRangeNotSatisfiableAsync(stream, sourceLength);
            NotifyStreamDiagnostics(request, HttpStatusCode.RequestedRangeNotSatisfiable, 0, 0, null);
            return;
        }

        HttpStatusCode statusCode = await WriteStreamHeadersAsync(stream, sourceLength, contentType, range, rangeRequested);

        long contentLength = range.End - range.Start + 1;
        if (contentLength == 0 || IsHead(request))
        {
            NotifyStreamDiagnostics(request, statusCode, 0, 0, null);
            return;
        }

        string? clientState = null;
        long bytesSent = 0;
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await SkipBytesAsync(source, range.Start);
            StreamCopyResult copyResult = await CopyBytesAsync(source, stream, contentLength);
            bytesSent = copyResult.BytesSent;
            clientState = copyResult.ClientState;
        }
        catch (IOException exception)
        {
            clientState = $"IOException: {exception.Message}";
        }
        catch (ObjectDisposedException exception)
        {
            clientState = $"Closed: {exception.Message}";
        }
        finally
        {
            stopwatch.Stop();
            NotifyStreamDiagnostics(request, statusCode, bytesSent, CalculateMbps(bytesSent, stopwatch.Elapsed), clientState);
        }
    }

    private static async Task<HttpStatusCode> WriteStreamHeadersAsync(NetworkStream stream, long sourceLength, string contentType, StreamRange range, bool rangeRequested)
    {
        HttpStatusCode statusCode = rangeRequested ? HttpStatusCode.PartialContent : HttpStatusCode.OK;
        long contentLength = range.End - range.Start + 1;
        List<string> headers = new()
        {
            $"HTTP/1.1 {(int)statusCode} {statusCode}",
            $"Content-Type: {contentType}",
            "Accept-Ranges: bytes",
            "Cache-Control: no-transform",
            $"Content-Length: {contentLength}",
            "Connection: keep-alive"
        };

        if (rangeRequested)
        {
            headers.Add($"Content-Range: bytes {range.Start}-{range.End}/{sourceLength}");
        }

        headers.Add(string.Empty);
        headers.Add(string.Empty);

        byte[] headerBytes = Encoding.ASCII.GetBytes(string.Join("\r\n", headers));
        await stream.WriteAsync(headerBytes);
        return statusCode;
    }

    private CatalogItem? FindCatalogItem(string itemId)
    {
        return catalogStore.Load()
            .FirstOrDefault(candidate => string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
    }

    private static bool IsExistingZipItem(CatalogItem? item)
    {
        return item != null
            && string.Equals(item.Extension, ".zip", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.AbsolutePath)
            && File.Exists(item.AbsolutePath);
    }

    private static List<PublicZipEntryItem> LoadZipEntryItems(CatalogItem zipItem)
    {
        using FileStream zipStream = new(
            zipItem.AbsolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);

        return archive.Entries
            .Where(IsSupportedZipEntry)
            .Select(entry => ToPublicZipEntryItem(zipItem, entry))
            .OrderBy(item => item.Directory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ZipEntryMatch? FindZipEntry(string zipEntryId)
    {
        foreach (CatalogItem zipItem in catalogStore.Load().Where(IsExistingZipItem))
        {
            using FileStream zipStream = new(
                zipItem.AbsolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);

            foreach (ZipArchiveEntry entry in archive.Entries.Where(IsSupportedZipEntry))
            {
                string candidateId = CreateZipEntryId(zipItem.Id, entry.FullName);
                if (string.Equals(candidateId, zipEntryId, StringComparison.Ordinal))
                {
                    return new ZipEntryMatch(zipItem.AbsolutePath, entry.FullName);
                }
            }
        }

        return null;
    }

    private static PublicZipEntryItem ToPublicZipEntryItem(CatalogItem zipItem, ZipArchiveEntry entry)
    {
        SupportedMediaTypes.TryGetCatalogType(entry.Name, out string kind, out string? formatGroup);
        string entryId = CreateZipEntryId(zipItem.Id, entry.FullName);
        return new PublicZipEntryItem(
            Id: entryId,
            SourceId: zipItem.SourceId,
            ZipId: zipItem.Id,
            Kind: kind,
            FormatGroup: formatGroup,
            FileName: entry.Name,
            Directory: GetZipEntryDirectory(entry.FullName),
            Extension: Path.GetExtension(entry.Name).ToLowerInvariant(),
            SizeBytes: entry.Length,
            ModifiedUtc: entry.LastWriteTime.UtcDateTime,
            StreamUrl: $"/stream/{entryId}");
    }

    private static bool IsSupportedZipEntry(ZipArchiveEntry entry)
    {
        return !string.IsNullOrWhiteSpace(entry.Name)
            && IsSafeZipEntryName(entry.FullName)
            && SupportedMediaTypes.IsSupportedZipImage(entry.Name);
    }

    private static bool IsSafeZipEntryName(string name)
    {
        string normalized = name.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains(':'))
        {
            return false;
        }

        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(part => part != "." && part != "..");
    }

    private static string GetZipEntryDirectory(string entryName)
    {
        string normalized = entryName.Replace('\\', '/');
        int separatorIndex = normalized.LastIndexOf('/');
        if (separatorIndex <= 0)
        {
            return string.Empty;
        }

        return normalized[..separatorIndex].Replace('/', '\\');
    }

    private static string CreateZipEntryId(string zipItemId, string entryName)
    {
        string key = $"{zipItemId}|{entryName}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string id = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        return $"zipentry_{id}";
    }

    private static BrowseEntry ToSourceBrowseEntry(LibrarySource source, int itemCount)
    {
        return new BrowseEntry(
            entryType: "folder",
            id: source.Id,
            name: source.Name,
            displayName: CreateSourceDisplayName(source),
            volumeHint: CreateVolumeHint(source.Path),
            kind: "source",
            formatGroup: null,
            sourceId: source.Id,
            zipId: null,
            parentId: "root",
            directory: string.Empty,
            extension: null,
            sizeBytes: null,
            modifiedUtc: null,
            browseUrl: CreateBrowseUrl(source.Id),
            streamUrl: null,
            itemCount: itemCount);
    }

    private static BrowseEntry ToSourceFolderBrowseEntry(string sourceId, string directory, string parentId)
    {
        string id = CreateSourceFolderId(sourceId, directory);
        return new BrowseEntry(
            entryType: "folder",
            id: id,
            name: GetDirectoryName(directory),
            displayName: null,
            volumeHint: null,
            kind: "folder",
            formatGroup: null,
            sourceId: sourceId,
            zipId: null,
            parentId: parentId,
            directory: directory,
            extension: null,
            sizeBytes: null,
            modifiedUtc: null,
            browseUrl: CreateBrowseUrl(id),
            streamUrl: null,
            itemCount: null);
    }

    private static BrowseEntry ToZipFolderBrowseEntry(CatalogItem zipItem, string directory, string parentId)
    {
        string id = CreateZipFolderId(zipItem.Id, directory);
        return new BrowseEntry(
            entryType: "folder",
            id: id,
            name: GetDirectoryName(directory),
            displayName: null,
            volumeHint: null,
            kind: "folder",
            formatGroup: null,
            sourceId: zipItem.SourceId,
            zipId: zipItem.Id,
            parentId: parentId,
            directory: directory,
            extension: null,
            sizeBytes: null,
            modifiedUtc: null,
            browseUrl: CreateBrowseUrl(id),
            streamUrl: null,
            itemCount: null);
    }

    private static BrowseEntry ToZipBrowseEntry(CatalogItem item, string parentId)
    {
        string folderId = CreateZipFolderId(item.Id, string.Empty);
        return new BrowseEntry(
            entryType: "zip",
            id: folderId,
            name: item.FileName,
            displayName: null,
            volumeHint: null,
            kind: item.Kind,
            formatGroup: item.FormatGroup,
            sourceId: item.SourceId,
            zipId: item.Id,
            parentId: parentId,
            directory: NormalizeVirtualDirectory(item.Directory),
            extension: item.Extension,
            sizeBytes: item.SizeBytes,
            modifiedUtc: item.ModifiedUtc,
            browseUrl: CreateBrowseUrl(folderId),
            streamUrl: item.StreamUrl,
            itemCount: null);
    }

    private static BrowseEntry ToFileBrowseEntry(CatalogItem item)
    {
        return new BrowseEntry(
            entryType: "file",
            id: item.Id,
            name: item.FileName,
            displayName: null,
            volumeHint: null,
            kind: item.Kind,
            formatGroup: item.FormatGroup,
            sourceId: item.SourceId,
            zipId: null,
            parentId: null,
            directory: NormalizeVirtualDirectory(item.Directory),
            extension: item.Extension,
            sizeBytes: item.SizeBytes,
            modifiedUtc: item.ModifiedUtc,
            browseUrl: null,
            streamUrl: item.StreamUrl,
            itemCount: null,
            needsTranscode: SupportedMediaTypes.NeedsTranscode(item.FileName));
    }

    private static BrowseEntry ToZipEntryBrowseEntry(PublicZipEntryItem item, string parentId)
    {
        return new BrowseEntry(
            entryType: "file",
            id: item.Id,
            name: item.FileName,
            displayName: null,
            volumeHint: null,
            kind: item.Kind,
            formatGroup: item.FormatGroup,
            sourceId: item.SourceId,
            zipId: item.ZipId,
            parentId: parentId,
            directory: NormalizeVirtualDirectory(item.Directory),
            extension: item.Extension,
            sizeBytes: item.SizeBytes,
            modifiedUtc: item.ModifiedUtc,
            browseUrl: null,
            streamUrl: item.StreamUrl,
            itemCount: null);
    }

    private static string CreateSourceDisplayName(LibrarySource source)
    {
        string? volumeHint = CreateVolumeHint(source.Path);
        return string.IsNullOrWhiteSpace(volumeHint)
            ? source.Name
            : $"{volumeHint} {source.Name}";
    }

    private static string? CreateVolumeHint(string path)
    {
        try
        {
            string root = Path.GetPathRoot(path) ?? string.Empty;
            if (root.Length >= 2 && root[1] == ':')
            {
                return root[..2].ToUpperInvariant();
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<string> LoadZipDirectories(CatalogItem zipItem)
    {
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        using FileStream zipStream = new(
            zipItem.AbsolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);

        foreach (ZipArchiveEntry entry in archive.Entries.Where(IsSupportedZipEntry))
        {
            AddDirectoryAndParents(directories, GetZipEntryDirectory(entry.FullName));
        }

        return directories;
    }

    private static void AddDirectoryAndParents(ISet<string> directories, string directory)
    {
        string normalized = NormalizeVirtualDirectory(directory);
        while (!string.IsNullOrWhiteSpace(normalized))
        {
            directories.Add(normalized);
            int separatorIndex = normalized.LastIndexOf('\\');
            normalized = separatorIndex <= 0 ? string.Empty : normalized[..separatorIndex];
        }
    }

    private static bool TryGetImmediateChildDirectory(string currentDirectory, string itemDirectory, out string childDirectory)
    {
        currentDirectory = NormalizeVirtualDirectory(currentDirectory);
        itemDirectory = NormalizeVirtualDirectory(itemDirectory);
        childDirectory = string.Empty;

        if (string.IsNullOrWhiteSpace(itemDirectory)
            || string.Equals(currentDirectory, itemDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string remaining;
        if (string.IsNullOrWhiteSpace(currentDirectory))
        {
            remaining = itemDirectory;
        }
        else
        {
            string prefix = currentDirectory + "\\";
            if (!itemDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            remaining = itemDirectory[prefix.Length..];
        }

        int separatorIndex = remaining.IndexOf('\\');
        string childName = separatorIndex < 0 ? remaining : remaining[..separatorIndex];
        childDirectory = string.IsNullOrWhiteSpace(currentDirectory) ? childName : $"{currentDirectory}\\{childName}";
        return !string.IsNullOrWhiteSpace(childName);
    }

    private static string NormalizeVirtualDirectory(string? directory)
    {
        return string.IsNullOrWhiteSpace(directory)
            ? string.Empty
            : directory.Replace('/', '\\').Trim('\\');
    }

    private static string GetDirectoryName(string directory)
    {
        string normalized = NormalizeVirtualDirectory(directory);
        int separatorIndex = normalized.LastIndexOf('\\');
        return separatorIndex < 0 ? normalized : normalized[(separatorIndex + 1)..];
    }

    private static string? GetParentSourceFolderId(string sourceId, string directory)
    {
        string parentDirectory = GetParentDirectory(directory);
        return string.IsNullOrWhiteSpace(parentDirectory) ? sourceId : CreateSourceFolderId(sourceId, parentDirectory);
    }

    private static string? GetParentZipFolderId(string zipItemId, string directory)
    {
        string parentDirectory = GetParentDirectory(directory);
        return CreateZipFolderId(zipItemId, parentDirectory);
    }

    private static string GetParentDirectory(string directory)
    {
        string normalized = NormalizeVirtualDirectory(directory);
        int separatorIndex = normalized.LastIndexOf('\\');
        return separatorIndex <= 0 ? string.Empty : normalized[..separatorIndex];
    }

    private static string CreateSourceFolderId(string sourceId, string directory)
    {
        return $"folder_{CreateHashId($"{sourceId}|{NormalizeVirtualDirectory(directory)}")}";
    }

    private static string CreateZipFolderId(string zipItemId, string directory)
    {
        return $"zipfolder_{CreateHashId($"{zipItemId}|{NormalizeVirtualDirectory(directory)}")}";
    }

    private static string CreateHashId(string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string CreateBrowseUrl(string folderId)
    {
        return $"/browse?folderId={Uri.EscapeDataString(folderId)}";
    }

    private static BrowsePage CreateBrowsePage(IReadOnlyList<BrowseEntry> entries, ApiRequest request)
    {
        int cursor = ParseQueryInt(request, "cursor", 0);
        int limit = Math.Clamp(ParseQueryInt(request, "limit", 100), 1, 500);
        cursor = Math.Clamp(cursor, 0, entries.Count);
        List<BrowseEntry> pageEntries = entries.Skip(cursor).Take(limit).ToList();
        int nextStart = cursor + pageEntries.Count;
        int? nextCursor = nextStart < entries.Count ? nextStart : null;
        return new BrowsePage(cursor, limit, nextCursor, pageEntries);
    }

    private static int ParseQueryInt(ApiRequest request, string key, int fallback)
    {
        return request.Query.TryGetValue(key, out string? value) && int.TryParse(value, out int parsed)
            ? parsed
            : fallback;
    }

    private static string GetLocalNetworkAddress()
    {
        try
        {
            List<IPAddress> addresses = Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .Where(candidate => candidate.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(candidate)
                    && IsPreferredLanAddress(candidate))
                .ToList();

            IPAddress? address = addresses
                .OrderBy(GetLanAddressPriority)
                .FirstOrDefault();

            return address?.ToString() ?? "127.0.0.1";
        }
        catch (Exception)
        {
            return "127.0.0.1";
        }
    }

    private static bool IsPreferredLanAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static int GetLanAddressPriority(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return 0;
        }

        if (bytes[0] == 10)
        {
            return 1;
        }

        return 2;
    }

    private static bool TryCreateRange(ApiRequest request, long fileLength, out StreamRange range, out bool rangeRequested)
    {
        rangeRequested = false;
        range = fileLength == 0 ? new StreamRange(0, -1) : new StreamRange(0, fileLength - 1);

        if (!request.Headers.TryGetValue("Range", out string? rangeHeader) || string.IsNullOrWhiteSpace(rangeHeader))
        {
            return true;
        }

        rangeRequested = true;
        if (fileLength <= 0 || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string value = rangeHeader["bytes=".Length..].Trim();
        if (value.Contains(','))
        {
            return false;
        }

        string[] parts = value.Split('-', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        string startText = parts[0].Trim();
        string endText = parts[1].Trim();

        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, out long suffixLength) || suffixLength <= 0)
            {
                return false;
            }

            long start = Math.Max(fileLength - suffixLength, 0);
            range = new StreamRange(start, fileLength - 1);
            return true;
        }

        if (!long.TryParse(startText, out long startByte) || startByte < 0 || startByte >= fileLength)
        {
            return false;
        }

        long endByte = fileLength - 1;
        if (endText.Length != 0 && (!long.TryParse(endText, out endByte) || endByte < startByte))
        {
            return false;
        }

        range = new StreamRange(startByte, Math.Min(endByte, fileLength - 1));
        return true;
    }

    private static async Task SkipBytesAsync(Stream source, long byteCount)
    {
        byte[] buffer = new byte[StreamBufferSize];
        long remaining = byteCount;

        while (remaining > 0)
        {
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, requested));
            if (read == 0)
            {
                return;
            }

            remaining -= read;
        }
    }

    private static async Task<StreamCopyResult> CopyBytesAsync(Stream source, Stream destination, long byteCount)
    {
        // Pipelined copy: read the NEXT block from disk while the CURRENT block is
        // being written to the socket. The previous serialized read-then-write left
        // a micro-gap in network delivery during every disk read, which a marginal
        // 8K60 decode pipeline turns into a visible hesitation. Double-buffering
        // keeps the stream continuously fed, like a proper media streamer.
        byte[] current = new byte[StreamBufferSize];
        byte[] next = new byte[StreamBufferSize];
        long remaining = byteCount;
        long copied = 0;

        int firstCount = (int)Math.Min(current.Length, remaining);
        Task<int> readTask = firstCount > 0
            ? source.ReadAsync(current, 0, firstCount)
            : Task.FromResult(0);

        while (remaining > 0)
        {
            int read = await readTask;
            if (read == 0)
            {
                return new StreamCopyResult(copied, "Source terminee avant la fin du segment");
            }

            remaining -= read;

            // Kick off the next disk read into the other buffer before we block on
            // the socket write, so the two overlap.
            Task<int>? nextRead = null;
            if (remaining > 0)
            {
                int nextCount = (int)Math.Min(next.Length, remaining);
                nextRead = source.ReadAsync(next, 0, nextCount);
            }

            try
            {
                await destination.WriteAsync(current.AsMemory(0, read));
            }
            catch (IOException exception)
            {
                return new StreamCopyResult(copied, $"Client closed: {exception.Message}");
            }
            catch (ObjectDisposedException exception)
            {
                return new StreamCopyResult(copied, $"Client closed: {exception.Message}");
            }

            copied += read;

            if (nextRead == null)
            {
                break;
            }

            readTask = nextRead;
            (current, next) = (next, current);
        }

        return new StreamCopyResult(copied, null);
    }

    private void NotifyStreamDiagnostics(ApiRequest request, HttpStatusCode statusCode, long bytesSent, double averageMbps, string? clientState)
    {
        request.Headers.TryGetValue("Range", out string? rangeHeader);
        StreamDiagnosticsUpdated?.Invoke(
            this,
            new StreamDiagnosticsSnapshot(
                DateTime.Now,
                request.Method.ToUpperInvariant(),
                string.IsNullOrWhiteSpace(rangeHeader) ? "aucun" : rangeHeader,
                GetRequestTokenSource(request),
                (int)statusCode,
                bytesSent,
                averageMbps,
                string.IsNullOrWhiteSpace(clientState) ? "OK" : clientState));
    }

    private static double CalculateMbps(long bytesSent, TimeSpan elapsed)
    {
        return bytesSent <= 0 || elapsed.TotalSeconds <= 0
            ? 0
            : bytesSent * 8d / elapsed.TotalSeconds / 1_000_000d;
    }

    private static async Task WriteRangeNotSatisfiableAsync(NetworkStream stream, long fileLength)
    {
        string header = string.Join("\r\n",
            $"HTTP/1.1 {(int)HttpStatusCode.RequestedRangeNotSatisfiable} {HttpStatusCode.RequestedRangeNotSatisfiable}",
            "Accept-Ranges: bytes",
            "Cache-Control: no-transform",
            $"Content-Range: bytes */{fileLength}",
            "Content-Length: 0",
            "Connection: keep-alive",
            string.Empty,
            string.Empty);

        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes);
    }

    private static string GetContentType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".m4v" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".webm" => "video/webm",
            ".ts" => "video/mp2t",
            ".mts" => "video/mp2t",
            ".m2ts" => "video/mp2t",
            ".wmv" => "video/x-ms-wmv",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    private static PublicCatalogItem ToPublicItem(CatalogItem item, string? token)
    {
        return new PublicCatalogItem
        {
            Id = item.Id,
            SourceId = item.SourceId,
            Kind = item.Kind,
            FormatGroup = item.FormatGroup,
            FileName = item.FileName,
            Directory = item.Directory,
            Extension = item.Extension,
            SizeBytes = item.SizeBytes,
            ModifiedUtc = item.ModifiedUtc,
            StreamUrl = AddTokenToStreamUrl(item.StreamUrl, token),
            NeedsTranscode = SupportedMediaTypes.NeedsTranscode(item.FileName)
        };
    }

    private async Task WriteJsonAsync(NetworkStream stream, HttpStatusCode statusCode, object body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, jsonOptions));
        string header = string.Join("\r\n",
            $"HTTP/1.1 {(int)statusCode} {statusCode}",
            "Content-Type: application/json; charset=utf-8",
            $"Content-Length: {bytes.Length}",
            "Connection: keep-alive",
            string.Empty,
            string.Empty);

        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(bytes);

        LogRequestSent((int)statusCode);
    }

    private void LogRequestReceived(ApiRequest request, string path)
    {
        RequestLogged?.Invoke(
            $"{DateTime.Now:HH:mm:ss}  <- {request.Method} {path}  [{GetRequestTokenSource(request)} {TokenPreview(request)}]");
    }

    private void LogRequestSent(int statusCode)
    {
        RequestLogContext? context = currentRequestLog.Value;
        if (context == null)
        {
            return;
        }

        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(context.StartTimestamp).TotalMilliseconds;
        RequestLogged?.Invoke($"{DateTime.Now:HH:mm:ss}  -> {statusCode} {context.Method} {context.Path}  {elapsedMs:F0}ms");
    }

    private static string TokenPreview(ApiRequest request)
    {
        string? token = GetRequestToken(request);
        if (string.IsNullOrWhiteSpace(token))
        {
            return "no-token";
        }

        return token.Length <= 8 ? token : token[..8] + "…";
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(parts[0].Replace("+", " "));
            string value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace("+", " ")) : string.Empty;
            values[key] = value;
        }

        return values;
    }

    private sealed record ApiRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        IReadOnlyDictionary<string, string> Query);

    private readonly record struct StreamRange(long Start, long End);

    private sealed record MediaInfo(double? DurationSeconds, int? Width, int? Height);

    private readonly record struct StreamCopyResult(long BytesSent, string? ClientState);

    private enum BrowseFolderKind
    {
        Source,
        SourceDirectory,
        ZipRoot,
        ZipDirectory
    }

    private sealed record BrowseFolder(
        string Id,
        string name,
        string? parentId,
        string sourceId,
        string? zipId,
        string directory,
        BrowseFolderKind kind);

    private sealed record BrowseEntry(
        string entryType,
        string id,
        string name,
        string? displayName,
        string? volumeHint,
        string? kind,
        string? formatGroup,
        string? sourceId,
        string? zipId,
        string? parentId,
        string? directory,
        string? extension,
        long? sizeBytes,
        DateTime? modifiedUtc,
        string? browseUrl,
        string? streamUrl,
        int? itemCount,
        bool needsTranscode = false);

    private sealed record BrowsePage(
        int cursor,
        int limit,
        int? nextCursor,
        List<BrowseEntry> entries);

    private sealed record PublicZipEntryItem(
        string Id,
        string SourceId,
        string ZipId,
        string Kind,
        string? FormatGroup,
        string FileName,
        string Directory,
        string Extension,
        long SizeBytes,
        DateTime ModifiedUtc,
        string StreamUrl);

    private sealed record ZipEntryMatch(string ZipPath, string EntryName);
}
