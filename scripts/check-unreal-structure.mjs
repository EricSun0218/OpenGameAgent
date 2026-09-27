import { readFileSync } from "node:fs";

const required = [
	"engines/unreal/OpenGameAgent/OpenGameAgent.uplugin",
	"engines/unreal/OpenGameAgent/Source/OpenGameAgent/OpenGameAgent.Build.cs",
	"engines/unreal/OpenGameAgent/Source/OpenGameAgent/Public/OpenGameAgentRuntimeClient.h",
	"engines/unreal/OpenGameAgent/Source/OpenGameAgent/Public/OpenGameAgentModule.h",
	"engines/unreal/OpenGameAgent/Source/OpenGameAgent/Public/OpenGameAgentActionPump.h",
	"engines/unreal/OpenGameAgent/Source/OpenGameAgent/Private/OpenGameAgentRuntimeClient.cpp",
	"engines/unreal/OpenGameAgent/Source/OpenGameAgent/Private/OpenGameAgentActionPump.cpp",
];

const contents = new Map(required.map((path) => [path, readFileSync(path, "utf8")]));
const build = contents.get(required[1]);
for (const moduleName of ["Core", "Json"]) {
	if (!build.includes(`"${moduleName}"`)) throw new Error(`Unreal plugin is missing the ${moduleName} module.`);
}
for (const buildInvariant of ["CppStandardVersion.Cpp20", "OGA_WITH_OPENSSL", '"OpenSSL"']) {
	if (!build.includes(buildInvariant)) {
		throw new Error(`Unreal plugin build rules are missing invariant: ${buildInvariant}`);
	}
}

const runtimeSources = [...contents.entries()]
	.filter(([path]) => !path.includes("/Generated/"))
	.map(([, content]) => content)
	.join("\n");
for (const forbidden of [
	"@earendil-works",
	"pi-agent",
	"ModelProvider",
	"AgentLoop",
	"VectorMemory",
	"WorkflowRouter",
]) {
	if (runtimeSources.includes(forbidden))
		throw new Error(`Unreal thin client contains forbidden framework surface: ${forbidden}`);
}

for (const requiredInvariant of [
	"CURLOPT_FOLLOWLOCATION",
	"CURLOPT_HEADERFUNCTION",
	"CURLU_DISALLOW_USER",
	"CURLINFO_EFFECTIVE_URL",
	"MaxAttachmentResponseBytes",
	"MaxResponseHeaderBytes",
	"MaxConcurrentRequests",
	"MaxQueuedRequests",
	"FQueuedThreadPool",
	"VerifySslCertificates",
	"ENamedThreads::GameThread",
	"ReadAttachment",
	"Content-Digest",
	"IOpenGameAgentAuthoritativeActionAdapter",
	"IsInGameThread",
	"RecordActionReceipt",
]) {
	if (!runtimeSources.includes(requiredInvariant)) {
		throw new Error(`Unreal thin client is missing invariant: ${requiredInvariant}`);
	}
}

console.log(`Unreal thin-client structure passed (${required.length} required files).`);
