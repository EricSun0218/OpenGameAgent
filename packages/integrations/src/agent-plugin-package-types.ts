import type { KeyObject } from "node:crypto";

export const AGENT_PLUGIN_PACKAGE_SCHEMA_V1 = "urn:opengameagent:agent-plugin-package:1" as const;
export const AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY = "io.opengameagent.package" as const;
export const AGENT_PLUGIN_PACKAGE_MANIFEST_FILE = "manifest.json" as const;
export const AGENT_PLUGIN_PACKAGE_SIGNATURES_FILE = "signatures.json" as const;
export const AGENT_PLUGIN_PACKAGE_PUBLICATION_PREFIX = ".oga-plugin-publication-" as const;
export const AGENT_PLUGIN_PACKAGE_SIGNATURE_DOMAIN_V1 =
	"OpenGameAgent Agent Plugin Package Signature v1\u0000" as const;

export interface AgentPluginPackageLimits {
	readonly maxTraversalDepth: number;
	readonly maxDirectories: number;
	readonly maxEntriesPerDirectory: number;
	readonly maxVisitedEntries: number;
	readonly maxFiles: number;
	readonly maxPortablePathBytes: number;
	readonly maxPortablePathSegmentBytes: number;
	readonly maxAddressedPathBytes: number;
	readonly maxFileBytes: number;
	readonly maxTotalFileBytes: number;
	readonly maxPluginManifestBytes: number;
	readonly maxPackageManifestBytes: number;
	readonly maxSignaturesBytes: number;
	readonly maxJsonDepth: number;
	readonly maxSigners: number;
	readonly maxTrustedKeys: number;
	readonly maxSignatures: number;
	readonly maxKeyIdBytes: number;
	readonly maxReleaseSequenceBytes: number;
	readonly maxPluginVersionBytes: number;
}

export interface AgentPluginPackageSigner {
	readonly keyId: string;
	readonly sign: (message: Uint8Array, signal: AbortSignal) => PromiseLike<Uint8Array> | Uint8Array;
}

export interface AgentPluginPackageTrustedKey {
	readonly keyId: string;
	readonly publicKey: KeyObject;
}

export interface AgentPluginPackageFile {
	readonly path: string;
	readonly bytes: number;
	readonly sha256: string;
	readonly executable: boolean;
}

export interface AgentPluginPackageManifestV1 {
	readonly schema: typeof AGENT_PLUGIN_PACKAGE_SCHEMA_V1;
	readonly plugin: {
		readonly name: string;
		readonly version: string;
	};
	readonly releaseSequence: string;
	readonly files: readonly AgentPluginPackageFile[];
}

export interface BuildSignedAgentPluginPackageOptions {
	readonly cwd: string;
	readonly sourceRoot: string;
	readonly packageRoot: string;
	readonly releaseSequence: string;
	readonly executablePaths: readonly string[];
	readonly signers: readonly AgentPluginPackageSigner[];
	readonly limits: AgentPluginPackageLimits;
}

export interface AgentPluginPackageBuildResult {
	readonly pluginName: string;
	readonly pluginVersion: string;
	readonly releaseSequence: string;
	readonly packageDigest: string;
	readonly packageRoot: string;
	readonly manifest: AgentPluginPackageManifestV1;
	readonly files: readonly AgentPluginPackageFile[];
}

export interface AgentPluginPackageInstalledState {
	readonly pluginName: string;
	readonly pluginVersion: string;
	readonly releaseSequence: string;
	readonly packageDigest: string;
	readonly rootPath: string;
}

export interface InstallSignedAgentPluginPackageOptions {
	readonly cwd: string;
	readonly packageRoot: string;
	readonly storeRoot: string;
	readonly trustedKeys: readonly AgentPluginPackageTrustedKey[];
	readonly requiredSignatures: number;
	readonly currentInstallation?: AgentPluginPackageInstalledState;
	readonly limits: AgentPluginPackageLimits;
}
