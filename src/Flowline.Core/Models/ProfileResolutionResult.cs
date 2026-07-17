namespace Flowline.Core.Models;

public abstract record ProfileResolutionResult;
public record ProfileFound(PacProfile Profile) : ProfileResolutionResult;
public record ProfileAmbiguous(IReadOnlyList<PacProfile> Candidates) : ProfileResolutionResult;
public record ProfileNotFound(string EnvironmentUrl) : ProfileResolutionResult;
