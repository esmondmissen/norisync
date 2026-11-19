using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// Works out which user and device a request came from.
/// </summary>
internal static class RequestIdentity
{
    /// <summary>
    /// Reads the caller's user and device from the authenticated principal, falling back to the
    /// <c>MediaBrowser</c> authorization header for the device fields when the claims lack them.
    /// </summary>
    /// <param name="principal">The request's authenticated principal.</param>
    /// <param name="request">The request.</param>
    /// <returns>The identity; <c>UserId</c> is <see cref="Guid.Empty"/> and <c>DeviceId</c> <see langword="null"/> when unknown.</returns>
    public static (Guid UserId, string? DeviceId, string? DeviceName, string? ClientName) Resolve(ClaimsPrincipal principal, HttpRequest request)
    {
        var userIdClaim = principal.FindFirstValue("Jellyfin-UserId")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var userId = Guid.TryParse(userIdClaim, out var parsed) ? parsed : Guid.Empty;

        var deviceId = principal.FindFirstValue("Jellyfin-DeviceId");
        var deviceName = principal.FindFirstValue("Jellyfin-Device");
        var clientName = principal.FindFirstValue("Jellyfin-Client");

        if (string.IsNullOrEmpty(deviceId))
        {
            var authHeader = request.Headers["X-Emby-Authorization"].ToString();
            if (string.IsNullOrEmpty(authHeader))
            {
                authHeader = request.Headers.Authorization.ToString();
            }

            ParseAuthHeader(authHeader, ref deviceId, ref deviceName, ref clientName);
        }

        return (userId, string.IsNullOrWhiteSpace(deviceId) ? null : deviceId, deviceName, clientName);
    }

    private static void ParseAuthHeader(string header, ref string? deviceId, ref string? deviceName, ref string? clientName)
    {
        if (string.IsNullOrEmpty(header))
        {
            return;
        }

        var schemeIndex = header.IndexOf(' ', StringComparison.Ordinal);
        var payload = schemeIndex > 0 ? header[(schemeIndex + 1)..] : header;

        foreach (var raw in payload.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = raw.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = raw[..eq].Trim();
            var value = raw[(eq + 1)..].Trim().Trim('"');
            if (value.Length == 0)
            {
                continue;
            }

            if (string.IsNullOrEmpty(deviceId) && key.Equals("DeviceId", StringComparison.OrdinalIgnoreCase))
            {
                deviceId = value;
            }
            else if (string.IsNullOrEmpty(deviceName) && key.Equals("Device", StringComparison.OrdinalIgnoreCase))
            {
                deviceName = value;
            }
            else if (string.IsNullOrEmpty(clientName) && key.Equals("Client", StringComparison.OrdinalIgnoreCase))
            {
                clientName = value;
            }
        }
    }
}
