namespace Flowline.Core.Services;

public abstract record ProfileResolutionResult;
public record ProfileFound(PacProfile Profile) : ProfileResolutionResult;
public record ProfileAmbiguous(IReadOnlyList<PacProfile> Candidates) : ProfileResolutionResult;
public record ProfileNotFound(string EnvironmentUrl) : ProfileResolutionResult;
