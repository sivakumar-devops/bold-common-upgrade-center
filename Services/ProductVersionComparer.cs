namespace Bold.UpgradeCenter.Services;

public static class ProductVersionComparer
{
    public static bool TryParse(string? value, out ProductVersionParts parts)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parts = new ProductVersionParts(Array.Empty<int>());
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var parsedParts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var number) ? number : (int?)null)
            .ToArray();

        if (parsedParts.Length == 0 || parsedParts.Any(part => part is null))
        {
            parts = new ProductVersionParts(Array.Empty<int>());
            return false;
        }

        parts = new ProductVersionParts(parsedParts.Select(part => part!.Value).ToArray());
        return true;
    }

    public static ProductVersionParts Parse(string value)
    {
        return TryParse(value, out var parts) ? parts : new ProductVersionParts(Array.Empty<int>());
    }

    public static int Compare(ProductVersionParts? leftParts, ProductVersionParts? rightParts)
    {
        if (leftParts is null && rightParts is null)
        {
            return 0;
        }

        if (leftParts is null)
        {
            return -1;
        }

        if (rightParts is null)
        {
            return 1;
        }

        var maxLength = Math.Max(leftParts.Parts.Count, rightParts.Parts.Count);
        for (var index = 0; index < maxLength; index++)
        {
            var left = index < leftParts.Parts.Count ? leftParts.Parts[index] : 0;
            var right = index < rightParts.Parts.Count ? rightParts.Parts[index] : 0;
            var comparison = left.CompareTo(right);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }
}

public sealed record ProductVersionParts(IReadOnlyList<int> Parts);

public sealed class ProductVersionPartsComparer : IComparer<ProductVersionParts>
{
    public static readonly ProductVersionPartsComparer Instance = new();

    public int Compare(ProductVersionParts? x, ProductVersionParts? y)
    {
        return ProductVersionComparer.Compare(x, y);
    }
}
