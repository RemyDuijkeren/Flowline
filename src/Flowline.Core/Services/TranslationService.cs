using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Core.Services;

public class TranslationService(IFlowlineOutput output)
{
    public async Task ExportAsync(IOrganizationServiceAsync2 service, string solutionName, string exportPath)
    {
        output.Verbose($"Exporting translations for solution {solutionName}...");

        var request = new OrganizationRequest("ExportTranslation")
        {
            ["SolutionName"] = solutionName
        };

        var response = await service.ExecuteAsync(request).ConfigureAwait(false);
        var exportTranslationXml = (string)response["ExportTranslationXml"];
        var compressedTranslations = Convert.FromBase64String(exportTranslationXml);

        var directory = Path.GetDirectoryName(exportPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllBytesAsync(exportPath, compressedTranslations).ConfigureAwait(false);
        output.Info($"[green]Translations exported to [bold]{exportPath}[/][/]");
    }

    public async Task ImportAsync(IOrganizationServiceAsync2 service, string importPath)
    {
        output.Verbose($"Importing translations from {importPath}...");

        if (!File.Exists(importPath))
            throw new FileNotFoundException("Translation file not found.", importPath);

        var compressedTranslations = await File.ReadAllBytesAsync(importPath).ConfigureAwait(false);
        var translationXml = Convert.ToBase64String(compressedTranslations);

        var request = new OrganizationRequest("ImportTranslation")
        {
            ["TranslationXml"] = translationXml
        };

        await service.ExecuteAsync(request).ConfigureAwait(false);
        output.Info("[green]Translations imported[/]");

        output.Verbose("Publishing all changes...");
        await service.ExecuteAsync(new OrganizationRequest("PublishAllXml")).ConfigureAwait(false);
        output.Verbose("Changes published");
    }
}
