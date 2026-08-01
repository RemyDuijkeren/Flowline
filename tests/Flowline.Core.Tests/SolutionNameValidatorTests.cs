using Flowline.Core.Services;
using FluentAssertions;

namespace Flowline.Core.Tests;

public class SolutionNameValidatorTests
{
    // --- Solution unique name ---

    [Theory]
    [InlineData("MySolution")]
    [InlineData("_MySolution")]
    [InlineData("DWE_Base")]
    [InlineData("Event2")]
    public void ValidateSolutionUniqueName_ValidName_ReturnsNull(string name)
    {
        SolutionNameValidator.ValidateSolutionUniqueName(name).Should().BeNull();
    }

    [Fact]
    public void ValidateSolutionUniqueName_66Chars_ReturnsError()
    {
        var name = new string('a', 66);

        SolutionNameValidator.ValidateSolutionUniqueName(name).Should().NotBeNull();
    }

    [Fact]
    public void ValidateSolutionUniqueName_65Chars_ReturnsNull()
    {
        var name = new string('a', 65);

        SolutionNameValidator.ValidateSolutionUniqueName(name).Should().BeNull();
    }

    [Fact]
    public void ValidateSolutionUniqueName_ContainsSpace_ReturnsError()
    {
        SolutionNameValidator.ValidateSolutionUniqueName("My Solution").Should().NotBeNull();
    }

    [Fact]
    public void ValidateSolutionUniqueName_ContainsHyphen_ReturnsError()
    {
        SolutionNameValidator.ValidateSolutionUniqueName("My-Solution").Should().NotBeNull();
    }

    [Fact]
    public void ValidateSolutionUniqueName_StartsWithDigit_ReturnsError()
    {
        SolutionNameValidator.ValidateSolutionUniqueName("1Solution").Should().NotBeNull();
    }

    [Theory]
    [InlineData("event")]
    [InlineData("class")]
    public void ValidateSolutionUniqueName_CSharpKeyword_ReturnsError(string name)
    {
        var error = SolutionNameValidator.ValidateSolutionUniqueName(name);

        error.Should().NotBeNull();
        error.Should().Contain("keyword");
    }

    // --- Solution display name ---

    [Fact]
    public void ValidateSolutionDisplayName_ValidName_ReturnsNull()
    {
        SolutionNameValidator.ValidateSolutionDisplayName("My Solution! (v2)").Should().BeNull();
    }

    [Fact]
    public void ValidateSolutionDisplayName_257Chars_ReturnsError()
    {
        var name = new string('a', 257);

        SolutionNameValidator.ValidateSolutionDisplayName(name).Should().NotBeNull();
    }

    [Fact]
    public void ValidateSolutionDisplayName_256Chars_ReturnsNull()
    {
        var name = new string('a', 256);

        SolutionNameValidator.ValidateSolutionDisplayName(name).Should().BeNull();
    }

    // --- Publisher prefix ---

    [Theory]
    [InlineData("dwe")]
    [InlineData("ab")]
    [InlineData("abcdefgh")]
    public void ValidatePublisherPrefix_ValidPrefix_ReturnsNull(string prefix)
    {
        SolutionNameValidator.ValidatePublisherPrefix(prefix).Should().BeNull();
    }

    [Fact]
    public void ValidatePublisherPrefix_MscrmPrefix_ReturnsError()
    {
        var error = SolutionNameValidator.ValidatePublisherPrefix("mscrmx");

        error.Should().NotBeNull();
        error.Should().Contain("mscrm");
    }

    [Fact]
    public void ValidatePublisherPrefix_TooShort_ReturnsError()
    {
        SolutionNameValidator.ValidatePublisherPrefix("a").Should().NotBeNull();
    }

    [Fact]
    public void ValidatePublisherPrefix_NineChars_ReturnsError()
    {
        SolutionNameValidator.ValidatePublisherPrefix("abcdefghi").Should().NotBeNull();
    }

    [Fact]
    public void ValidatePublisherPrefix_NonAlphanumeric_ReturnsError()
    {
        SolutionNameValidator.ValidatePublisherPrefix("dw-e").Should().NotBeNull();
    }

    [Fact]
    public void ValidatePublisherPrefix_StartsWithDigit_ReturnsError()
    {
        SolutionNameValidator.ValidatePublisherPrefix("1dwe").Should().NotBeNull();
    }

    // --- Publisher unique name ---

    [Theory]
    [InlineData("dwe")]
    [InlineData("_dwe")]
    [InlineData("DWE_Publisher")]
    public void ValidatePublisherUniqueName_ValidName_ReturnsNull(string name)
    {
        SolutionNameValidator.ValidatePublisherUniqueName(name).Should().BeNull();
    }

    [Fact]
    public void ValidatePublisherUniqueName_ContainsSpace_ReturnsError()
    {
        SolutionNameValidator.ValidatePublisherUniqueName("dwe publisher").Should().NotBeNull();
    }

    [Fact]
    public void ValidatePublisherUniqueName_StartsWithDigit_ReturnsError()
    {
        SolutionNameValidator.ValidatePublisherUniqueName("1dwe").Should().NotBeNull();
    }

    // --- Throwing convenience wrappers ---

    [Fact]
    public void EnsureSolutionUniqueName_Invalid_ThrowsFlowlineExceptionWithValidationFailed()
    {
        var act = () => SolutionNameValidator.EnsureSolutionUniqueName("event");

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.ValidationFailed);
    }

    [Fact]
    public void EnsureSolutionUniqueName_Valid_DoesNotThrow()
    {
        var act = () => SolutionNameValidator.EnsureSolutionUniqueName("MySolution");

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsurePublisherPrefix_Invalid_ThrowsFlowlineExceptionWithValidationFailed()
    {
        var act = () => SolutionNameValidator.EnsurePublisherPrefix("mscrmx");

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.ValidationFailed);
    }
}
