using System;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// Who is calling a request route, and what they may do: the rules every request controller
/// (albums, songs, SoulSync's probe) shares.
/// </summary>
internal static class RequestCallers
{
    /// <summary>
    /// The Jellyfin user behind the call, or null.
    /// </summary>
    /// <param name="controller">The controller handling the call.</param>
    /// <returns>The user, or null.</returns>
    public static User? Resolve(ControllerBase controller)
    {
        var claim = controller.User.FindFirstValue("Jellyfin-UserId")
            ?? controller.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (Guid.TryParse(claim, out var userId))
        {
            var userManager = controller.HttpContext.RequestServices.GetRequiredService<IUserManager>();
            return userManager.GetUserById(userId);
        }

        return null;
    }

    /// <summary>
    /// Whether the user is a Jellyfin administrator.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>True for an administrator.</returns>
    public static bool IsAdministrator(User user)
        => user.Permissions
            .FirstOrDefault(p => p.Kind == PermissionKind.IsAdministrator)?.Value ?? false;

    /// <summary>
    /// Whether the user may make requests: anyone, unless the admin restricted requests to
    /// administrators (<see cref="Configuration.PluginConfiguration.LidarrRequireAdmin"/>).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>True when they may.</returns>
    public static bool MayRequest(User user)
        => !(Plugin.Instance?.Configuration?.LidarrRequireAdmin ?? true) || IsAdministrator(user);
}
