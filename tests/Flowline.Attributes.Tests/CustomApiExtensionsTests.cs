using Microsoft.Xrm.Sdk;
using Moq;
using Flowline.Attributes;
using FluentAssertions;

namespace Flowline.Attributes.Tests;

public class CustomApiExtensionsTests
{
    private class TestCustomApi
    {
        [RequestParameter]
        public string? StringParam { get; set; }

        [RequestParameter]
        public int IntParam { get; set; }

        [ResponseProperty]
        public string? StringResponse { get; set; }

        [ResponseProperty]
        public int IntResponse { get; set; }

        public string? NoAttribute { get; set; }
    }

    [Fact]
    public void LoadRequestParameters_ShouldPopulateProperties()
    {
        // Arrange
        var contextMock = new Mock<IPluginExecutionContext>();
        var inputParameters = new ParameterCollection
        {
            ["stringParam"] = "hello",
            ["intParam"] = 42,
            ["noAttribute"] = "should not be set"
        };
        contextMock.Setup(c => c.InputParameters).Returns(inputParameters);

        var target = new TestCustomApi();

        // Act
        contextMock.Object.LoadRequestParameters(target);

        // Assert
        target.StringParam.Should().Be("hello");
        target.IntParam.Should().Be(42);
        target.NoAttribute.Should().BeNull();
    }

    [Fact]
    public void StoreResponseProperties_ShouldPopulateOutputParameters()
    {
        // Arrange
        var contextMock = new Mock<IPluginExecutionContext>();
        var outputParameters = new ParameterCollection();
        contextMock.Setup(c => c.OutputParameters).Returns(outputParameters);

        var target = new TestCustomApi
        {
            StringResponse = "world",
            IntResponse = 123,
            NoAttribute = "secret"
        };

        // Act
        contextMock.Object.StoreResponseProperties(target);

        // Assert
        outputParameters.Should().ContainKey("stringResponse");
        outputParameters["stringResponse"].Should().Be("world");
        outputParameters.Should().ContainKey("intResponse");
        outputParameters["intResponse"].Should().Be(123);
        outputParameters.Should().NotContainKey("noAttribute");
    }

    [Fact]
    public void LoadRequestParameters_ShouldThrow_WhenContextIsNull()
    {
        // Arrange
        IPluginExecutionContext context = null!;
        var target = new TestCustomApi();

        // Act
        var act = () => context.LoadRequestParameters(target);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("context");
    }

    [Fact]
    public void LoadRequestParameters_ShouldThrow_WhenTargetIsNull()
    {
        // Arrange
        var contextMock = new Mock<IPluginExecutionContext>();
        object target = null!;

        // Act
        var act = () => contextMock.Object.LoadRequestParameters(target);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("target");
    }
}
