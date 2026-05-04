using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Flowline.Core.Services;
using Flowline.Core;
using NSubstitute;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Core.Tests;

public class TranslationServiceTests
{
    private readonly IOrganizationServiceAsync2 _serviceMock;
    private readonly TranslationService _service;

    public TranslationServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _service = new TranslationService(new NullFlowlineOutput());
    }

    [Fact]
    public async Task ExportAsync_ShouldExecuteExportTranslationRequest()
    {
        // Arrange
        var solutionName = "TestSolution";
        var exportPath = "translations.zip";
        var expectedBytes = new byte[] { 1, 2, 3 };
        var response = new OrganizationResponse();
        response["ExportTranslationXml"] = Convert.ToBase64String(expectedBytes);

        _serviceMock.ExecuteAsync(Arg.Is<OrganizationRequest>(r =>
            r.RequestName == "ExportTranslation" &&
            (string)r["SolutionName"] == solutionName))
            .Returns(Task.FromResult<OrganizationResponse>(response));

        // Act
        await _service.ExportAsync(_serviceMock, solutionName, exportPath);

        // Assert
        Assert.True(File.Exists(exportPath));
        var actualBytes = await File.ReadAllBytesAsync(exportPath);
        Assert.Equal(expectedBytes, actualBytes);

        // Cleanup
        if (File.Exists(exportPath)) File.Delete(exportPath);
    }

    [Fact]
    public async Task ImportAsync_ShouldExecuteImportTranslationRequest()
    {
        // Arrange
        var importPath = "import.zip";
        var expectedBytes = new byte[] { 4, 5, 6 };
        await File.WriteAllBytesAsync(importPath, expectedBytes);

        _serviceMock.ExecuteAsync(Arg.Is<OrganizationRequest>(r =>
            r.RequestName == "ImportTranslation" &&
            (string)r["TranslationXml"] == Convert.ToBase64String(expectedBytes)))
            .Returns(Task.FromResult(new OrganizationResponse()));

        _serviceMock.ExecuteAsync(Arg.Is<OrganizationRequest>(r =>
            r.RequestName == "PublishAllXml"))
            .Returns(Task.FromResult(new OrganizationResponse()));

        // Act
        await _service.ImportAsync(_serviceMock, importPath);

        // Assert
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<OrganizationRequest>(r => r.RequestName == "ImportTranslation"));
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishAllXml"));

        // Cleanup
        if (File.Exists(importPath)) File.Delete(importPath);
    }
}
