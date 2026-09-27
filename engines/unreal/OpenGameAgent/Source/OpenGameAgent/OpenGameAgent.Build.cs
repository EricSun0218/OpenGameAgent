using UnrealBuildTool;

public class OpenGameAgent : ModuleRules
{
    public OpenGameAgent(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        CppStandard = CppStandardVersion.Cpp20;

        PublicDependencyModuleNames.AddRange(
            new[]
            {
                "Core",
            });

        PrivateDependencyModuleNames.Add("Json");

        bool supportsLibCurl = Target.Platform == UnrealTargetPlatform.Win64 &&
            Target.Architecture == UnrealArch.X64 &&
            !Target.WindowsPlatform.bUseXCurl;
        PrivateDefinitions.Add("OGA_WITH_LIBCURL=" + (supportsLibCurl ? "1" : "0"));
        if (supportsLibCurl)
        {
            AddEngineThirdPartyPrivateStaticDependencies(Target, "libcurl", "nghttp2", "zlib");
        }

        bool supportsOpenSsl = supportsLibCurl;
        PrivateDefinitions.Add("OGA_WITH_OPENSSL=" + (supportsOpenSsl ? "1" : "0"));
        if (supportsOpenSsl)
        {
            PrivateDependencyModuleNames.Add("SSL");
            AddEngineThirdPartyPrivateStaticDependencies(Target, "OpenSSL");
        }
    }
}
