using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class DeployCommandCacheMessagingTests
{
    private const string OldSha = "abc123def456";
    private const string NewSha = "999999999999";
    private const string CiNoteSnippet = "On CI, use --path";

    [Fact]
    public void Hit_NotCi_NoTestOrUat_ReturnsTerseReuseMessage()
    {
        var message = DeployCommand.BuildCacheStatusMessage(DeployCommand.CacheOutcome.Hit, "Contoso", OldSha, OldSha,
            cachedManaged: true, wantManaged: true, isCi: false, hasTestOrUat: false);

        message.Should().Contain("Reusing cached artifact for 'Contoso'").And.Contain(OldSha[..7]);
        message.Should().NotContain("promotion stage");
        message.Should().NotContain(CiNoteSnippet);
    }

    [Fact]
    public void Hit_NotCi_HasTestOrUat_ReturnsReuseMessagePlusPipelineFraming()
    {
        var message = DeployCommand.BuildCacheStatusMessage(DeployCommand.CacheOutcome.Hit, "Contoso", OldSha, OldSha,
            cachedManaged: true, wantManaged: true, isCi: false, hasTestOrUat: true);

        message.Should().Contain("Reusing cached artifact for 'Contoso'");
        message.Should().Contain("promotion stage");
    }

    [Fact]
    public void NoEntry_NotCi_HasTestOrUat_ReturnsPackMessageWithWillBeReusedFraming()
    {
        var message = DeployCommand.BuildCacheStatusMessage(DeployCommand.CacheOutcome.NoEntry, "Contoso", null, NewSha,
            cachedManaged: false, wantManaged: true, isCi: false, hasTestOrUat: true);

        message.Should().Contain("No cached build yet for 'Contoso'");
        message.Should().Contain("will be reused across later promotion stages");
    }

    [Fact]
    public void CommitChanged_NotCi_NoTestOrUat_NamesOldAndNewCommit_NoFraming()
    {
        var message = DeployCommand.BuildCacheStatusMessage(DeployCommand.CacheOutcome.CommitChanged, "Contoso", OldSha, NewSha,
            cachedManaged: true, wantManaged: true, isCi: false, hasTestOrUat: false);

        message.Should().Contain(OldSha[..7]).And.Contain(NewSha[..7]);
        message.Should().NotContain("promotion stage");
    }

    [Fact]
    public void ManagedMismatch_NotCi_NamesCachedModeAndRequestedMode()
    {
        var message = DeployCommand.BuildCacheStatusMessage(DeployCommand.CacheOutcome.ManagedMismatch, "Contoso", OldSha, NewSha,
            cachedManaged: false, wantManaged: true, isCi: false, hasTestOrUat: false);

        message.Should().Contain("cached build was unmanaged").And.Contain("wants managed");
    }

    [Fact]
    public void NoCacheFlag_NotCi_NamesNoCacheForcedFreshPack()
    {
        var message = DeployCommand.BuildCacheStatusMessage(DeployCommand.CacheOutcome.NoCacheFlag, "Contoso", OldSha, NewSha,
            cachedManaged: true, wantManaged: true, isCi: false, hasTestOrUat: false);

        message.Should().Contain("--no-cache forced a fresh pack");
    }

    [Fact]
    public void ArtifactFileMissing_NotCi_NamesManifestExistsButFileGone()
    {
        var message = DeployCommand.BuildCacheStatusMessage(DeployCommand.CacheOutcome.ArtifactFileMissing, "Contoso", OldSha, NewSha,
            cachedManaged: true, wantManaged: true, isCi: false, hasTestOrUat: false);

        message.Should().Contain("cached manifest exists but the artifact file is missing");
    }

    [Fact]
    public void NoCurrentCommit_NotCi_NamesCommitCouldNotBeResolved()
    {
        var message = DeployCommand.BuildCacheStatusMessage(DeployCommand.CacheOutcome.NoCurrentCommit, "Contoso", OldSha, null,
            cachedManaged: true, wantManaged: true, isCi: false, hasTestOrUat: false);

        message.Should().Contain("couldn't resolve the current commit");
    }

    [Fact]
    public void Hit_IsCi_StillReportsRealReuseOutcome_WithCiNoteAppended()
    {
        var message = DeployCommand.BuildCacheStatusMessage(DeployCommand.CacheOutcome.Hit, "Contoso", OldSha, OldSha,
            cachedManaged: true, wantManaged: true, isCi: true, hasTestOrUat: false);

        message.Should().Contain("Reusing cached artifact for 'Contoso'");
        message.Should().Contain(CiNoteSnippet);
    }

    [Fact]
    public void AnyMiss_IsCi_StillReportsRealMissAndReason_WithSameCiNoteAppended()
    {
        var message = DeployCommand.BuildCacheStatusMessage(DeployCommand.CacheOutcome.CommitChanged, "Contoso", OldSha, NewSha,
            cachedManaged: true, wantManaged: true, isCi: true, hasTestOrUat: false);

        message.Should().Contain(OldSha[..7]).And.Contain(NewSha[..7]);
        message.Should().Contain(CiNoteSnippet);
    }

    [Fact]
    public void AnyOutcome_IsCi_HasTestOrUat_SuppressesPipelineFraming_ButStillAppendsCiNote()
    {
        var message = DeployCommand.BuildCacheStatusMessage(DeployCommand.CacheOutcome.Hit, "Contoso", OldSha, OldSha,
            cachedManaged: true, wantManaged: true, isCi: true, hasTestOrUat: true);

        message.Should().NotContain("promotion stage");
        message.Should().Contain(CiNoteSnippet);
    }
}
