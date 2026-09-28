using System.Security.Claims;
using Bold.UpgradeCenter.Security;

namespace Bold.UpgradeCenter.Services;

public sealed record UpgradeUserInfo(
    string UserId,
    string DisplayName,
    string? Email,
    string Role,
    bool IsAdmin)
{
    public static UpgradeUserInfo System(string displayName = "Upgrade Center")
    {
        return new UpgradeUserInfo("system", displayName, null, "System", true);
    }
}

public interface IUpgradeUserContextProvider
{
    UpgradeUserInfo GetCurrentUser();
}

public sealed class UpgradeUserContextProvider : IUpgradeUserContextProvider
{
    private readonly IHttpContextAccessor httpContextAccessor;

    public UpgradeUserContextProvider(IHttpContextAccessor httpContextAccessor)
    {
        this.httpContextAccessor = httpContextAccessor;
    }

    public UpgradeUserInfo GetCurrentUser()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return new UpgradeUserInfo("anonymous", "Unknown user", null, "Unknown", false);
        }

        var userId = FirstClaimValue(user, ClaimTypes.NameIdentifier, "sub", "user_id") ?? "unknown";
        var displayName = ResolveUserName(user);
        var email = FirstClaimValue(user, ClaimTypes.Email, "email");
        var isAdmin = string.Equals(user.FindFirst("upgrade_center:is_admin")?.Value, "true", StringComparison.OrdinalIgnoreCase) ||
                      user.IsInRole("Admin");
        var role = isAdmin ? "Admin" : "User";

        return new UpgradeUserInfo(userId, displayName, email, role, isAdmin);
    }

    private static string? FirstClaimValue(ClaimsPrincipal user, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string ResolveUserName(ClaimsPrincipal user)
    {
        var values = new[]
        {
            FirstClaimValue(user, UpgradeCenterClaimTypes.UserName),
            FirstClaimValue(user, ClaimTypes.Name, "name", "display_name"),
            FirstClaimValue(user, ClaimTypes.Email, "email"),
            user.Identity?.Name
        };

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
}
