using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class CustomPatchValidationService : ICustomPatchValidationService
{
    private static readonly Regex ImageReferencePattern = new(
        @"^(?=.{1,255}$)([a-zA-Z0-9.-]+(?::[0-9]+)?/)?[a-z0-9]+([._/-][a-z0-9]+)*:[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$",
        RegexOptions.Compiled);
    private static readonly Regex ImageTagPattern = new(
        @"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$",
        RegexOptions.Compiled);
    private static readonly Regex ProductVersionTagPattern = new(
        @"^v?(?<version>\d+(?:\.\d+){2,})(?:[_.-].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IKubernetesUpgradeService kubernetesUpgradeService;
    private readonly IContainerImageRegistryValidator imageRegistryValidator;
    private readonly KubernetesUpgradeOptions kubernetesOptions;
    private readonly IUpgradeProductContext productContext;

    public CustomPatchValidationService(
        IKubernetesUpgradeService kubernetesUpgradeService,
        IContainerImageRegistryValidator imageRegistryValidator,
        IOptions<KubernetesUpgradeOptions> kubernetesOptions,
        IUpgradeProductContext productContext)
    {
        this.kubernetesUpgradeService = kubernetesUpgradeService;
        this.imageRegistryValidator = imageRegistryValidator;
        this.kubernetesOptions = kubernetesOptions.Value;
        this.productContext = productContext;
    }

    public async Task<string?> GetCommonImageRepositoryAsync(
        CancellationToken cancellationToken = default)
    {
        var validation = await kubernetesUpgradeService.ValidateNamespaceAsync(cancellationToken);
        if (!validation.Succeeded)
        {
            return null;
        }

        var deployments = validation.DeploymentStatuses
            .Where(deployment => deployment.IsExpected && deployment.IsAvailable)
            .ToList();
        if (deployments.Count == 0)
        {
            return null;
        }

        var repositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var deployment in deployments)
        {
            var container = SelectPrimaryContainer(deployment);
            if (container is null ||
                !TryGetImageRepositoryAndName(container.Image, out var repositoryWithName, out _) ||
                string.IsNullOrWhiteSpace(GetRepositoryRoot(repositoryWithName)))
            {
                return null;
            }

            repositories.Add(GetRepositoryRoot(repositoryWithName)!);
        }

        return repositories.Count == 1 ? repositories.Single() : null;
    }

    public async Task<CustomPatchValidationResponse> ValidateAsync(
        CustomPatchValidationRequest request,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var results = new List<CustomPatchImageValidationResult>();
        var imageTag = request.TargetVersion?.Trim() ?? string.Empty;
        var targetVersion = ExtractTargetVersion(imageTag, errors);

        ValidateTargetTag(imageTag, currentVersion, targetVersion, errors);

        var validation = await kubernetesUpgradeService.ValidateNamespaceAsync(cancellationToken);
        if (!validation.Succeeded)
        {
            errors.Add($"Kubernetes namespace validation failed: {validation.Message}");
        }

        var discoveredRequired = validation.DeploymentStatuses
            .Where(deployment => deployment.IsExpected && deployment.IsRequired && deployment.IsAvailable)
            .Select(deployment => deployment.DeploymentName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var discoveredOptional = validation.DeploymentStatuses
            .Where(deployment => deployment.IsExpected && deployment.IsOptional && deployment.IsAvailable)
            .Select(deployment => deployment.DeploymentName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var discoveredTargets = discoveredRequired
            .Concat(discoveredOptional)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var images = ResolveRequestedImages(request, validation, errors);
        if (!string.IsNullOrWhiteSpace(request.ImageRepository))
        {
            ValidateRepository(request.ImageRepository!, kubernetesOptions.AllowedImageRepositories, errors);
        }

        images = images
            .Where(image => !string.IsNullOrWhiteSpace(image.DeploymentName) || !string.IsNullOrWhiteSpace(image.Image))
            .ToList();
        if (images.Count == 0)
        {
            errors.Add("At least one custom deployment image is required.");
        }

        var duplicateDeployments = images
            .Where(image => !string.IsNullOrWhiteSpace(image.DeploymentName))
            .GroupBy(image => image.DeploymentName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var duplicate in duplicateDeployments)
        {
            errors.Add($"Duplicate image mapping found for deployment '{duplicate}'.");
        }

        var mappedDeployments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images)
        {
            var deploymentName = image.DeploymentName?.Trim() ?? string.Empty;
            var imageReference = image.Image?.Trim() ?? string.Empty;
            var itemErrors = new List<string>();

            if (string.IsNullOrWhiteSpace(deploymentName))
            {
                itemErrors.Add("Deployment name is required.");
            }
            else if (!discoveredTargets.Contains(deploymentName))
            {
                itemErrors.Add($"Deployment is not an available {productContext.Current.DisplayName} upgrade target in the current namespace.");
            }
            else
            {
                mappedDeployments.Add(deploymentName);
            }

            ValidateImageReference(imageReference, itemErrors);

            if (itemErrors.Count > 0)
            {
                errors.AddRange(itemErrors.Select(error => $"{deploymentName}: {error}"));
                results.Add(new CustomPatchImageValidationResult(deploymentName, image.ContainerName, imageReference, false, string.Join(" ", itemErrors)));
                continue;
            }

            var registryValidation = await imageRegistryValidator.ValidateAsync(imageReference, cancellationToken);
            if (!registryValidation.IsValid)
            {
                itemErrors.Add(registryValidation.Message);
                errors.AddRange(itemErrors.Select(error => $"{deploymentName}: {error}"));
                results.Add(new CustomPatchImageValidationResult(deploymentName, image.ContainerName, imageReference, false, string.Join(" ", itemErrors)));
                continue;
            }

            results.Add(new CustomPatchImageValidationResult(
                deploymentName,
                image.ContainerName,
                imageReference,
                true,
                "Image format, repository policy, deployment mapping, and registry manifest validation succeeded."));
        }

        var missingRequired = discoveredRequired
            .Where(deployment => !mappedDeployments.Contains(deployment))
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingRequired.Count > 0)
        {
            errors.Add($"Missing custom image mapping for required deployment(s): {string.Join(", ", missingRequired)}.");
        }

        return new CustomPatchValidationResponse(
            errors.Count == 0 && results.Count > 0 && results.All(result => result.IsValid),
            targetVersion ?? imageTag,
            results,
            errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static List<CustomPatchImageRequest> ResolveRequestedImages(
        CustomPatchValidationRequest request,
        KubernetesNamespaceValidationResult validation,
        List<string> errors)
    {
        if (request.Images.Count > 0)
        {
            return request.Images;
        }

        var targetVersion = request.TargetVersion?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            return [];
        }

        var customRepository = request.ImageRepository?.Trim().TrimEnd('/');
        var generatedImages = new List<CustomPatchImageRequest>();
        foreach (var deployment in validation.DeploymentStatuses
                     .Where(deployment => deployment.IsExpected && deployment.IsAvailable)
                     .OrderBy(deployment => deployment.DeploymentName, StringComparer.OrdinalIgnoreCase))
        {
            var container = SelectPrimaryContainer(deployment);
            if (container is null)
            {
                if (deployment.IsRequired)
                {
                    errors.Add($"{deployment.DeploymentName}: Current deployment image could not be resolved.");
                }

                continue;
            }

            if (!TryGetImageRepositoryAndName(container.Image, out var currentRepositoryWithName, out var imageName))
            {
                errors.Add($"{deployment.DeploymentName}: Current deployment image is not a supported tagged container image.");
                continue;
            }

            var targetRepositoryWithName = string.IsNullOrWhiteSpace(customRepository)
                ? currentRepositoryWithName
                : $"{customRepository}/{imageName}";

            generatedImages.Add(new CustomPatchImageRequest
            {
                DeploymentName = deployment.DeploymentName,
                ContainerName = container.Name,
                Image = $"{targetRepositoryWithName}:{targetVersion}"
            });
        }

        return generatedImages;
    }

    private static string? ExtractTargetVersion(string imageTag, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(imageTag))
        {
            return null;
        }

        var match = ProductVersionTagPattern.Match(imageTag);
        if (!match.Success)
        {
            errors.Add("Custom patch version must begin with a valid product version, for example 17.1.11 or 17.1.11_patch.");
            return null;
        }

        return match.Groups["version"].Value;
    }

    private static void ValidateTargetTag(string imageTag, string currentVersion, string? targetVersion, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(imageTag))
        {
            errors.Add("Custom patch version is required.");
            return;
        }

        if (!ImageTagPattern.IsMatch(imageTag))
        {
            errors.Add("Custom patch version must be a valid container image tag.");
        }

        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            return;
        }

        if (!ProductVersionComparer.TryParse(currentVersion, out var currentParts))
        {
            errors.Add("Installed product version is not in a supported version format.");
            return;
        }

        if (!ProductVersionComparer.TryParse(targetVersion, out var targetParts))
        {
            errors.Add("Custom patch target version is not in a supported version format.");
            return;
        }

        if (ProductVersionComparer.Compare(targetParts, currentParts) <= 0)
        {
            errors.Add($"Custom patch target version v{targetVersion} must be greater than the currently installed version v{currentVersion.TrimStart('v', 'V')}.");
        }
    }

    private void ValidateImageReference(string imageReference, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            errors.Add("Custom image reference is required.");
            return;
        }

        if (!ImageReferencePattern.IsMatch(imageReference) ||
            !ContainerImageRegistryValidator.TryParseImageReference(imageReference, out var registry, out var repository, out _))
        {
            errors.Add("Image reference must include a valid repository/name and tag, for example registry.example.com/boldbi/bi-api:16.1.90-custom.");
            return;
        }

        if (!ContainerImageRegistryValidator.TryValidateRegistryAuthorityFormat(registry, out var registryError))
        {
            errors.Add(registryError);
            return;
        }

        var allowedRepositories = kubernetesOptions.AllowedImageRepositories
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Select(prefix => prefix.Trim())
            .ToList();
        if (allowedRepositories.Count == 0)
        {
            errors.Add("Allowed image repository list is not configured.");
            return;
        }

        if (!ContainerImageRegistryValidator.IsRepositoryAllowed($"{registry}/{repository}", allowedRepositories))
        {
            errors.Add("Image repository is not in the configured allowed repository list.");
        }
    }

    private static void ValidateRepository(
        string imageRepository,
        IEnumerable<string> allowedRepositories,
        List<string> errors)
    {
        var trimmed = imageRepository.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Contains(':', StringComparison.Ordinal) && trimmed.LastIndexOf(':') > trimmed.LastIndexOf('/'))
        {
            errors.Add("Image repository should not include an image tag. Enter only the registry and repository path.");
            return;
        }

        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex <= 0)
        {
            errors.Add("Image repository must include a registry host and repository path.");
            return;
        }

        var registry = trimmed[..slashIndex];
        if (!ContainerImageRegistryValidator.TryValidateRegistryAuthorityFormat(registry, out var registryError))
        {
            errors.Add(registryError);
            return;
        }

        var allowed = allowedRepositories
            .Where(repository => !string.IsNullOrWhiteSpace(repository))
            .ToList();
        if (allowed.Count == 0)
        {
            errors.Add("Allowed image repository list is not configured.");
            return;
        }

        if (!ContainerImageRegistryValidator.IsRepositoryAllowed(trimmed, allowed))
        {
            errors.Add("Image repository is not in the configured allowed repository list.");
        }
    }

    private static KubernetesDeploymentContainerInfo? SelectPrimaryContainer(KubernetesDeploymentDiscoveryItem deployment)
    {
        return deployment.Containers.FirstOrDefault(container =>
            !string.IsNullOrWhiteSpace(container.Image) &&
            !container.Image.Equals("Image not reported", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetImageRepositoryAndName(
        string image,
        out string repositoryWithName,
        out string imageName)
    {
        repositoryWithName = string.Empty;
        imageName = string.Empty;

        var trimmed = image.Trim();
        var digestIndex = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (digestIndex >= 0)
        {
            trimmed = trimmed[..digestIndex];
        }

        var lastSlashIndex = trimmed.LastIndexOf('/');
        var lastColonIndex = trimmed.LastIndexOf(':');
        if (lastColonIndex > lastSlashIndex)
        {
            trimmed = trimmed[..lastColonIndex];
        }

        lastSlashIndex = trimmed.LastIndexOf('/');
        if (lastSlashIndex < 0 || lastSlashIndex == trimmed.Length - 1)
        {
            return false;
        }

        repositoryWithName = trimmed;
        imageName = trimmed[(lastSlashIndex + 1)..];
        return !string.IsNullOrWhiteSpace(repositoryWithName) &&
               !string.IsNullOrWhiteSpace(imageName);
    }

    private static string? GetRepositoryRoot(string repositoryWithName)
    {
        var lastSlashIndex = repositoryWithName.LastIndexOf('/');
        return lastSlashIndex > 0 ? repositoryWithName[..lastSlashIndex] : null;
    }
}
