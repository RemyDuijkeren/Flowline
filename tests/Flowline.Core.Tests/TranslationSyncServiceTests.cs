using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Flowline.Core.Services;
using Flowline.Core;
using Moq;
using Microsoft.PowerPlatform.Dataverse.Client;
using Xunit;

namespace Flowline.Core.Tests;

public class TranslationSyncServiceTests
{
    private readonly Mock<IOrganizationServiceAsync2> _serviceMock;
    private readonly TranslationSyncService _service;

    public TranslationSyncServiceTests()
    {
        _serviceMock = new Mock<IOrganizationServiceAsync2>();
        _service = new TranslationSyncService(new NullFlowlineOutput());
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

        _serviceMock.Setup(x => x.ExecuteAsync(It.Is<OrganizationRequest>(r => 
            r.RequestName == "ExportTranslation" && 
            (string)r["SolutionName"] == solutionName)))
            .ReturnsAsync(response);

        // Act
        await _service.ExportAsync(_serviceMock.Object, solutionName, exportPath);

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

        _serviceMock.Setup(x => x.ExecuteAsync(It.Is<OrganizationRequest>(r => 
            r.RequestName == "ImportTranslation" && 
            (string)r["TranslationXml"] == Convert.ToBase64String(expectedBytes))))
            .ReturnsAsync(new OrganizationResponse());

        _serviceMock.Setup(x => x.ExecuteAsync(It.Is<OrganizationRequest>(r => 
            r.RequestName == "PublishAllXml")))
            .ReturnsAsync(new OrganizationResponse());

        // Act
        await _service.ImportAsync(_serviceMock.Object, importPath);

        // Assert
        _serviceMock.Verify(x => x.ExecuteAsync(It.Is<OrganizationRequest>(r => r.RequestName == "ImportTranslation")), Times.Once);
        _serviceMock.Verify(x => x.ExecuteAsync(It.Is<OrganizationRequest>(r => r.RequestName == "PublishAllXml")), Times.Once);

        // Cleanup
        if (File.Exists(importPath)) File.Delete(importPath);
    }
}
