using UnrealBuildTool;

public class OpenGameAgentUnreal : ModuleRules
{
    public OpenGameAgentUnreal(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[]
        {
            "Core",
            "CoreUObject",
            "Engine",
            "HTTP",
            "Json"
        });
    }
}
