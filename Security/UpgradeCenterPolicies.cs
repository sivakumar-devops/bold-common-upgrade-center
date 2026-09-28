namespace Bold.UpgradeCenter.Security;

public static class UpgradeCenterPolicies
{
    public const string AdminOnly = "UpgradeCenterAdminOnly";
}

public static class UpgradeCenterAuthenticationDefaults
{
    public const string Scheme = "UpgradeCenterRemoteUms";
}

public static class UpgradeCenterClaimTypes
{
    public const string IsAdmin = "upgrade_center:is_admin";
    public const string UserName = "upgrade_center:user_name";
}
