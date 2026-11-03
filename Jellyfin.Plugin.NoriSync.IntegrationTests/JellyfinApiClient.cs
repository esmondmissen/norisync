using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.NoriSync.IntegrationTests;

public sealed class JellyfinApiClient : IDisposable
{
    private readonly HttpClient _http;

    public JellyfinApiClient(Uri baseAddress)
    {
        _http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add(
            "Authorization",
            "MediaBrowser Client=\"IntegrationTest\", Device=\"CI\", DeviceId=\"ci-1\", Version=\"1.0\"");
    }

    public async Task CompleteStartupWizardAsync(string adminUser, string adminPass, CancellationToken ct)
    {
        // GET /Startup/User lazily materializes the default admin by invoking
        // UserManager.InitializeAsync. POST /Startup/User then renames that user —
        // without the GET, the POST throws "Sequence contains no elements".
        using (var first = await _http.GetAsync("/Startup/User", ct))
        {
            first.EnsureSuccessStatusCode();
        }

        await PostJsonAsync("/Startup/Configuration", new
        {
            UICulture = "en-US",
            MetadataCountryCode = "US",
            PreferredMetadataLanguage = "en",
        }, ct);

        await PostJsonAsync("/Startup/User", new
        {
            Name = adminUser,
            Password = adminPass,
        }, ct);

        await PostJsonAsync("/Startup/RemoteAccess", new
        {
            EnableRemoteAccess = true,
        }, ct);

        using var complete = await _http.PostAsync("/Startup/Complete", content: null, ct);
        complete.EnsureSuccessStatusCode();

        var auth = await _http.PostAsJsonAsync("/Users/AuthenticateByName", new
        {
            Username = adminUser,
            Pw = adminPass,
        }, ct);
        auth.EnsureSuccessStatusCode();
        var payload = await auth.Content.ReadFromJsonAsync<AuthResponse>(ct)
            ?? throw new InvalidOperationException("Empty auth response");
        var token = payload.AccessToken ?? throw new InvalidOperationException("No AccessToken in auth response");

        _http.DefaultRequestHeaders.Remove("Authorization");
        _http.DefaultRequestHeaders.Add("Authorization", $"MediaBrowser Token=\"{token}\"");
    }

    // POST /System/Restart disposes the IHost and re-initialises inside the same
    // OS process (Program.cs do/while loop). The server stops accepting connections
    // for a few seconds. This method fires the request, tolerates the connection
    // drop, then blocks until the HTTP surface is back up.
    public async Task<int> RestartServerAsync(TimeSpan readyTimeout, CancellationToken ct)
    {
        int status;
        try
        {
            using var resp = await _http.PostAsync("/System/Restart", content: null, ct);
            status = (int)resp.StatusCode;
            if (status >= 400)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"POST /System/Restart returned {status}: {body}");
            }
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("/System/Restart"))
        {
            throw;
        }
        catch (HttpRequestException)
        {
            // Server tore down the connection while completing the request — fine.
            status = -1;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            status = -2;
        }

        await WaitUntilUnavailableAsync(TimeSpan.FromSeconds(20), ct);
        await WaitForHttpReadyAsync(readyTimeout, ct);
        return status;
    }

    private async Task WaitUntilUnavailableAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                using var resp = await _http.GetAsync("/System/Info/Public", cts.Token);
                // Still up — keep waiting.
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
            {
                break; // outer timeout — fall through
            }
            catch
            {
                return;
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token);
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
            {
                break;
            }
        }
        // Server never went down within the window. Not fatal — the readiness
        // probe will still catch a hung server.
    }

    public async Task UpdatePluginConfigurationAsync(Guid pluginId, object configuration, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/Plugins/{pluginId:D}/Configuration", configuration, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"POST /Plugins/{pluginId}/Configuration returned {(int)resp.StatusCode}: {body}");
        }
    }

    public async Task<int> GetTrackedChangesCountAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("/api/norisync/status", ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<NoriSyncStatusResponse>(ct)
            ?? throw new InvalidOperationException("Empty /api/norisync/status response");
        return payload.TotalTrackedChanges;
    }

    private sealed record NoriSyncStatusResponse(
        [property: JsonPropertyName("TotalTrackedChanges")] int TotalTrackedChanges);

    public async Task AddMusicLibraryAsync(string containerPath, CancellationToken ct)
    {
        var url = "/Library/VirtualFolders?"
            + "name=" + Uri.EscapeDataString("Music")
            + "&collectionType=music"
            + "&paths=" + Uri.EscapeDataString(containerPath)
            + "&refreshLibrary=true";
        using var resp = await _http.PostAsync(url, content: null, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"POST {url} returned {(int)resp.StatusCode}: {body}");
        }
    }

    public async Task WaitForAudioItemsAsync(int minimum, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (!cts.Token.IsCancellationRequested)
        {
            using var resp = await _http.GetAsync(
                "/Items?Recursive=true&IncludeItemTypes=Audio&Limit=5", cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                var payload = await resp.Content.ReadFromJsonAsync<ItemsResponse>(cts.Token);
                if (payload?.TotalRecordCount >= minimum)
                {
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
        }
        throw new TimeoutException($"Library never reached {minimum} audio items within {timeout}");
    }

    private sealed record ItemsResponse(
        [property: JsonPropertyName("TotalRecordCount")] int TotalRecordCount);

    public async Task<PluginInfo[]> ListPluginsAsync(CancellationToken ct)
    {
        var resp = await _http.GetAsync("/Plugins", ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PluginInfo[]>(cancellationToken: ct)) ?? Array.Empty<PluginInfo>();
    }

    public async Task<TaskInfo[]> ListScheduledTasksAsync(CancellationToken ct)
    {
        var resp = await _http.GetAsync("/ScheduledTasks", ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TaskInfo[]>(cancellationToken: ct)) ?? Array.Empty<TaskInfo>();
    }

    public async Task TriggerTaskAsync(string taskId, CancellationToken ct)
    {
        var resp = await _http.PostAsync($"/ScheduledTasks/Running/{taskId}", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<TaskInfo> GetScheduledTaskAsync(string taskId, CancellationToken ct)
    {
        var resp = await _http.GetAsync($"/ScheduledTasks/{taskId}", ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TaskInfo>(cancellationToken: ct))
            ?? throw new InvalidOperationException($"No task with id {taskId}");
    }

    // Waits for LastExecutionResult to advance past `baseline`. Passing `null` means
    // "wait for the first ever run to complete". The trigger endpoint returns 204 before
    // the task has transitioned to Running, so polling State alone can miss the whole
    // execution.
    public async Task<TaskInfo> WaitForTaskCompletionAsync(
        string taskId,
        DateTime? baselineEndTime,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (true)
        {
            cts.Token.ThrowIfCancellationRequested();
            var info = await GetScheduledTaskAsync(taskId, cts.Token);
            var endTime = info.LastExecutionResult?.EndTimeUtc;
            var running = string.Equals(info.State, "Running", StringComparison.OrdinalIgnoreCase);
            if (!running && endTime.HasValue && endTime != baselineEndTime)
            {
                return info;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
        }
    }

    private async Task PostJsonAsync(string path, object body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(path, body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"POST {path} returned {(int)resp.StatusCode}: {respBody}");
        }
    }

    public async Task WaitForHttpReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        // During startup/restart Jellyfin's HTTP server starts accepting requests
        // before the full middleware pipeline is wired up — /System/Info/Public
        // can return 200 while /ScheduledTasks still 503s. If we're already
        // authenticated, also probe an admin endpoint; otherwise fall back to
        // the public one (used before login).
        var probePath = _http.DefaultRequestHeaders.Authorization?.ToString()?.Contains("Token=", StringComparison.Ordinal) == true
            || _http.DefaultRequestHeaders.TryGetValues("Authorization", out var values) && values.Any(v => v.Contains("Token=", StringComparison.Ordinal))
                ? "/ScheduledTasks"
                : "/System/Info/Public";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        Exception? last = null;
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                using var resp = await _http.GetAsync(probePath, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    return;
                }
                last = new HttpRequestException($"{probePath} returned {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
        }
        throw new TimeoutException("Jellyfin HTTP server did not become ready in time", last);
    }

    public void Dispose() => _http.Dispose();

    private sealed record AuthResponse(
        [property: JsonPropertyName("AccessToken")] string? AccessToken);

    public sealed record PluginInfo(
        [property: JsonPropertyName("Id")] string Id,
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("Status")] string Status,
        [property: JsonPropertyName("Version")] string Version);

    public sealed record TaskInfo(
        [property: JsonPropertyName("Id")] string Id,
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("Key")] string Key,
        [property: JsonPropertyName("State")] string State,
        [property: JsonPropertyName("LastExecutionResult")] TaskResult? LastExecutionResult);

    public sealed record TaskResult(
        [property: JsonPropertyName("StartTimeUtc")] DateTime? StartTimeUtc,
        [property: JsonPropertyName("EndTimeUtc")] DateTime? EndTimeUtc,
        [property: JsonPropertyName("Status")] string Status,
        [property: JsonPropertyName("ErrorMessage")] string? ErrorMessage,
        [property: JsonPropertyName("LongErrorMessage")] string? LongErrorMessage);
}
