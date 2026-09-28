using System.Text.Json.Serialization;

namespace Bold.UpgradeCenter.Services;

public sealed class CustomPatchValidationRequest
{
    public string? TargetVersion { get; set; }

    public string? ImageRepository { get; set; }

    public List<CustomPatchImageRequest> Images { get; set; } = [];
}

public sealed class CustomPatchImageRequest
{
    public string? DeploymentName { get; set; }

    public string? ContainerName { get; set; }

    public string? Image { get; set; }
}

public sealed record CustomPatchValidationResponse(
    bool IsValid,
    string TargetVersion,
    IReadOnlyList<CustomPatchImageValidationResult> Results,
    IReadOnlyList<string> Errors);

public sealed record CustomPatchImageValidationResult(
    string DeploymentName,
    string? ContainerName,
    string Image,
    bool IsValid,
    string Message);

public interface ICustomPatchValidationService
{
    Task<string?> GetCommonImageRepositoryAsync(
        CancellationToken cancellationToken = default);

    Task<CustomPatchValidationResponse> ValidateAsync(
        CustomPatchValidationRequest request,
        string currentVersion,
        CancellationToken cancellationToken = default);
}
