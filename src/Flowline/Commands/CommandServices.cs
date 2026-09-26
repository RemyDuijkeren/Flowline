using Flowline.Core;
using Flowline.Core.Environments;
using Flowline.Core.Services;
using Flowline.Core.Validation;
using Flowline.Diagnostics;
using Flowline.Services;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Flowline.Commands;

/// <summary>
/// The infrastructure dependencies every <see cref="FlowlineCommand{TSettings}"/> needs — bundled so
/// each command constructor declares only its own dependencies plus this one, instead of re-declaring and
/// forwarding all of them to the base.
/// </summary>
public sealed class CommandServices(
    IAnsiConsole console,
    FlowlineRuntimeOptions runtimeOptions,
    ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory,
    SubprocessCapture capture,
    NuGetVersionClient nuGetVersionClient,
    FlowlineValidator validator)
{
    public IAnsiConsole Console { get; } = console;
    public FlowlineRuntimeOptions RuntimeOptions { get; } = runtimeOptions;
    public ProfileResolutionService ProfileResolutionService { get; } = profileResolutionService;
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
    public SubprocessCapture Capture { get; } = capture;
    public NuGetVersionClient NuGetVersionClient { get; } = nuGetVersionClient;
    public FlowlineValidator Validator { get; } = validator;
}
