using System.Text.Json.Serialization;

namespace Flowline.Config;

public class ProjectSolution
{
    public static IEqualityComparer<ProjectSolution> NameComparer { get; } = new ProjectSolutionNameComparer();

    public string Name { get; init; } = null!;
    public bool IncludeManaged { get; set; } = false;
    public bool ForceClassicPluginAssembly { get; set; } = false;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerateConfig? Generate { get; set; }

    private sealed class ProjectSolutionNameComparer : IEqualityComparer<ProjectSolution>
    {
        public bool Equals(ProjectSolution? x, ProjectSolution? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);
        }

        public int GetHashCode(ProjectSolution obj)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
        }
    }
}
