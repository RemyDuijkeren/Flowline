using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.OrphanCleanup;
using Flowline.Core.OrphanCleanup.Handlers;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests.OrphanCleanup.Handlers;

public class EntityFamilyHandlerTests
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly EntityFamilyHandler _handler;

    public EntityFamilyHandlerTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _console.Profile.Width = 400; // avoid word-wrap splitting longer assertion substrings across lines
        _handler = new EntityFamilyHandler(_console);
    }

    DetectionContext Ctx(string packageSrcRoot = "irrelevant", IReadOnlyList<string>? entityLogicalNames = null) => new(
        PackageSrcRoot: packageSrcRoot,
        Service: _serviceMock,
        SolutionName: "TestSolution",
        EnvironmentUrl: "https://example.crm.dynamics.com",
        Mode: RunMode.Normal,
        EntityLogicalNames: entityLogicalNames ?? []);

    static void WriteEntityXml(string packageSrcRoot, string folderName, params string[] attributeLogicalNames)
    {
        var entityDir = Path.Combine(packageSrcRoot, "Entities", folderName);
        Directory.CreateDirectory(entityDir);
        var attributesXml = string.Concat(attributeLogicalNames.Select(n => $"<attribute PhysicalName=\"{n}\"><LogicalName>{n}</LogicalName></attribute>"));
        File.WriteAllText(Path.Combine(entityDir, "Entity.xml"),
            $"<Entity><EntityInfo><entity Name=\"{folderName}\"><attributes>{attributesXml}</attributes></entity></EntityInfo></Entity>");
    }

    void SetupAttributeMetadata(string entityLogicalName, params (Guid Id, string LogicalName)[] attributes)
    {
        var attrMetas = attributes.Select(a =>
        {
            var attr = new StringAttributeMetadata { LogicalName = a.LogicalName };
            typeof(AttributeMetadata).GetProperty("MetadataId")!.SetValue(attr, a.Id);
            return (AttributeMetadata)attr;
        }).ToArray();

        var entityMeta = new EntityMetadata { LogicalName = entityLogicalName };
        typeof(EntityMetadata).GetProperty("Attributes")!.SetValue(entityMeta, attrMetas);

        var collection = new EntityMetadataCollection();
        collection.Add(entityMeta);

        var response = new RetrieveMetadataChangesResponse
        {
            Results = new ParameterCollection { ["EntityMetadata"] = collection }
        };

        _serviceMock.ExecuteAsync(
                Arg.Is<OrganizationRequest>(r => r.RequestName == "RetrieveMetadataChanges"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(response));
    }

    [Fact]
    public void Status_IsActive()
    {
        Assert.Equal(HandlerStatus.Active, _handler.Status);
    }

    [Fact]
    public async Task DetectAsync_OrphanedEntity_ReturnsManualPrio3()
    {
        var id = Guid.NewGuid();

        var findings = (await _handler.DetectAsync(Ctx(), [(id, 1)], CancellationToken.None)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(1, finding.ComponentType);
        Assert.Equal(OrphanAction.Manual, finding.Action);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
        Assert.Equal($"Entity {id}", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_OrphanedAttributeNotInEntityXml_ReturnsManualPrio3WithResolvedName()
    {
        var attributeId = Guid.NewGuid();
        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteEntityXml(packageSrcRoot, "Account"); // no attributes declared locally
        SetupAttributeMetadata("account", (attributeId, "av_removedfield"));

        try
        {
            var findings = (await _handler.DetectAsync(
                Ctx(packageSrcRoot, entityLogicalNames: ["account"]), [(attributeId, 2)], CancellationToken.None)).Findings;

            var finding = Assert.Single(findings);
            Assert.Equal(2, finding.ComponentType);
            Assert.Equal(OrphanAction.Manual, finding.Action);
            Assert.Equal(OrphanPriority.Prio3, finding.Priority);
            Assert.Equal($"Attribute 'account.av_removedfield' ({attributeId})", finding.DisplayName);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }

    [Fact]
    public async Task DetectAsync_AttributeStillInEntityXml_Suppressed()
    {
        var attributeId = Guid.NewGuid();
        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteEntityXml(packageSrcRoot, "Account", "av_taxid");
        SetupAttributeMetadata("account", (attributeId, "av_taxid"));

        try
        {
            var result = await _handler.DetectAsync(
                Ctx(packageSrcRoot, entityLogicalNames: ["account"]), [(attributeId, 2)], CancellationToken.None);

            Assert.Empty(result.Findings);
            // ClaimedIds still includes the suppressed attribute — it's recognized as this handler's
            // own (componenttype 2), just not reported, so it must not fall through to generic
            // fallback as an unrecognized type.
            Assert.Contains(attributeId, result.ClaimedIds);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }

    [Fact]
    public async Task DetectAsync_AttributeWithNoEntityLogicalNamesContext_ReportedWithoutLocalCrossCheck()
    {
        var attributeId = Guid.NewGuid();

        var findings = (await _handler.DetectAsync(Ctx(entityLogicalNames: []), [(attributeId, 2)], CancellationToken.None)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(2, finding.ComponentType);
        Assert.Equal(OrphanAction.Manual, finding.Action);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
        Assert.Equal($"Attribute {attributeId}", finding.DisplayName);
        _ = _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_AttributeUnresolvedByMetadataQuery_ReportedBareGuid()
    {
        // Entity context exists but the attribute's MetadataId doesn't resolve (e.g. entity outside the
        // configured entityLogicalNames scope) — matches today's behavior of reporting bare rather than
        // suppressing, since ResolveAttributeInfoAsync never actually verified this one.
        var attributeId = Guid.NewGuid();
        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(packageSrcRoot);
        // Metadata query resolves the "account" entity but returns no attributes at all, so this
        // attribute's MetadataId never appears in attributeInfo — it stays unresolved.
        SetupAttributeMetadata("account"); // no attributes at all

        try
        {
            var findings = (await _handler.DetectAsync(
                Ctx(packageSrcRoot, entityLogicalNames: ["account"]), [(attributeId, 2)], CancellationToken.None)).Findings;

            var finding = Assert.Single(findings);
            Assert.Equal($"Attribute {attributeId}", finding.DisplayName);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }

    [Fact]
    public async Task DetectAsync_NoEntityOrAttributeCandidates_ReturnsEmpty()
    {
        var findings = (await _handler.DetectAsync(Ctx(), [(Guid.NewGuid(), 61)], CancellationToken.None)).Findings;

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_NoCandidates_ReturnsEmptyWithoutQuerying()
    {
        var findings = (await _handler.DetectAsync(Ctx(), [], CancellationToken.None)).Findings;

        Assert.Empty(findings);
        _ = _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_ClaimedIds_IncludesEntityAndSuppressedAttributeDespitePartialFindings()
    {
        // Entity is always claimed (no suppression path). The Attribute is claimed too even though
        // it's still declared in Entity.xml and suppressed out of Findings — both componenttype 1 and
        // 2 candidates are always recognized as this handler's own regardless of Findings membership.
        var entityId = Guid.NewGuid();
        var attributeId = Guid.NewGuid();
        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteEntityXml(packageSrcRoot, "Account", "av_taxid");
        SetupAttributeMetadata("account", (attributeId, "av_taxid"));

        try
        {
            var result = await _handler.DetectAsync(
                Ctx(packageSrcRoot, entityLogicalNames: ["account"]),
                [(entityId, 1), (attributeId, 2)],
                CancellationToken.None);

            var finding = Assert.Single(result.Findings);
            Assert.Equal(entityId, finding.ObjectId); // Attribute suppressed, Entity is not
            Assert.Equal(new HashSet<Guid> { entityId, attributeId }, result.ClaimedIds);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }
}
