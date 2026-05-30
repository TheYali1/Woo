namespace Woo_.Models;

public sealed class BuildRequest
{
    public BuildRequest(BuildConfiguration configuration, OutputConflictChoice conflictChoice)
    {
        Configuration = configuration;
        ConflictChoice = conflictChoice;
    }

    public BuildConfiguration Configuration { get; }
    public OutputConflictChoice ConflictChoice { get; }
}
