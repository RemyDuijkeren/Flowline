using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Core.Services;

public interface ITranslationSyncService
{
    Task ExportAsync(IOrganizationServiceAsync2 service, string solutionName, string exportPath);
    Task ImportAsync(IOrganizationServiceAsync2 service, string importPath);
}

public class TranslationSyncService : ITranslationSyncService
{
    private readonly ILogger<TranslationSyncService> _logger;

    public TranslationSyncService(ILogger<TranslationSyncService> logger)
    {
        _logger = logger;
    }

    public async Task ExportAsync(IOrganizationServiceAsync2 service, string solutionName, string exportPath)
    {
        _logger.LogInformation("Exporting translations for solution {SolutionName}...", solutionName);

        var request = new OrganizationRequest("ExportTranslation")
        {
            ["SolutionName"] = solutionName
        };

        var response = await service.ExecuteAsync(request);
        var exportTranslationXml = (string)response["ExportTranslationXml"];
        var compressedTranslations = Convert.FromBase64String(exportTranslationXml);

        var directory = Path.GetDirectoryName(exportPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(exportPath, compressedTranslations);
        _logger.LogInformation("Translations exported to {ExportPath}", exportPath);
    }

    public async Task ImportAsync(IOrganizationServiceAsync2 service, string importPath)
    {
        _logger.LogInformation("Importing translations from {ImportPath}...", importPath);

        if (!File.Exists(importPath))
        {
            throw new FileNotFoundException("Translation file not found.", importPath);
        }

        var compressedTranslations = await File.ReadAllBytesAsync(importPath);
        var translationXml = Convert.ToBase64String(compressedTranslations);

        var request = new OrganizationRequest("ImportTranslation")
        {
            ["TranslationXml"] = translationXml
        };

        await service.ExecuteAsync(request);
        _logger.LogInformation("Translations imported successfully.");

        _logger.LogInformation("Publishing all changes...");
        await service.ExecuteAsync(new OrganizationRequest("PublishAllXml"));
        _logger.LogInformation("Changes published.");
    }
}
