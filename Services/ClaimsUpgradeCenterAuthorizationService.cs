using System.Security.Claims;
using Bold.UpgradeCenter.Models;
using Bold.UpgradeCenter.Security;

namespace Bold.UpgradeCenter.Services;

public sealed class ClaimsUpgradeCenterAuthorizationService : IUpgradeCenterAuthorizationService
{
    public Task<UpgradeCenterUser> GetCurrentUserAsync(
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        var isAdmin = string.Equals(
                          principal.FindFirst(UpgradeCenterClaimTypes.IsAdmin)?.Value,
                          "true",
                          StringComparison.OrdinalIgnoreCase) ||
                      principal.IsInRole("Admin");

        var email = principal.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;
        var displayName = ResolveUserName(
            principal.FindFirst(UpgradeCenterClaimTypes.UserName)?.Value,
            principal.FindFirst(ClaimTypes.Name)?.Value,
            principal.Identity?.Name,
            email);

        var user = new UpgradeCenterUser
        {
            DisplayName = displayName,
            Initials = GetInitials(displayName),
            Email = email,
            Role = isAdmin ? "UMS Admin" : "User",
            IsAuthorized = principal.Identity?.IsAuthenticated == true && isAdmin
        };

        return Task.FromResult(user);
    }

    private static string ResolveUserName(params string?[] values)
    {
        foreach (var value in values)
        {
            var userName = NormalizeUserName(value);
            if (!string.IsNullOrWhiteSpace(userName) && !Guid.TryParse(userName, out _))
            {
                return userName;
            }
        }

        return "Bold BI user";
    }

    private static string? NormalizeUserName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var userName = value.Trim();
        var slashIndex = userName.LastIndexOf(" / ", StringComparison.Ordinal);
        if (slashIndex >= 0 && slashIndex + 3 < userName.Length)
        {
            userName = userName[(slashIndex + 3)..].Trim();
        }

        var emailSeparator = userName.IndexOf('@', StringComparison.Ordinal);
        if (emailSeparator > 0)
        {
            userName = userName[..emailSeparator].Trim();
        }

        return string.IsNullOrWhiteSpace(userName) ? null : userName;
    }

    private static string GetInitials(string displayName)
    {
        var parts = displayName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .ToArray();

        if (parts.Length == 0)
        {
            return "U";
        }

        if (parts.Length == 1)
        {
            return parts[0][0].ToString().ToUpperInvariant();
        }

        return string.Concat(parts[0][0], parts[^1][0]).ToUpperInvariant();
    }
}
