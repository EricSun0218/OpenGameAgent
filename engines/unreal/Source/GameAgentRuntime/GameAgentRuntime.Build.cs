using UnrealBuildTool;

public class GameAgentRuntime : ModuleRules
{
    public GameAgentRuntime(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        CppStandard = CppStandardVersion.Cpp17;

        PublicDependencyModuleNames.AddRange(
            new[]
            {
                "Core"
            });
    }
}
