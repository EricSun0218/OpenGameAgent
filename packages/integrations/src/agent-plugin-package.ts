import { createHash, KeyObject, verify as verifyEd25519 } from "node:crypto";
import type { BigIntStats, Dirent, Stats } from "node:fs";
import type { FileHandle } from "node:fs/promises";
import { chmod, lstat, mkdir, open, opendir, realpath, rename } from "node:fs/promises";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { canonicalizeJsonValue, parseStrictJsonText, raceOperationWithAbort } from "@opengameagent/protocol";
import { AgentPluginPackageError, type AgentPluginPackageErrorCode } from "./agent-plugin-package-errors.js";
import {
	AGENT_PLUGIN_PACKAGE_PUBLICATION_STATE_MAX_BYTES,
	agentPluginPackagePublicationNextStatePath,
	agentPluginPackagePublicationStatePath,
} from "./agent-plugin-package-paths.js";
import {
	AGENT_PLUGIN_PACKAGE_MANIFEST_FILE,
	AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY,
	AGENT_PLUGIN_PACKAGE_SCHEMA_V1,
	AGENT_PLUGIN_PACKAGE_SIGNATURE_DOMAIN_V1,
	AGENT_PLUGIN_PACKAGE_SIGNATURES_FILE,
	type AgentPluginPackageBuildResult,
	type AgentPluginPackageFile,
	type AgentPluginPackageInstalledState,
	type AgentPluginPackageLimits,
	type AgentPluginPackageManifestV1,
	type AgentPluginPackageSigner,
	type AgentPluginPackageTrustedKey,
	type BuildSignedAgentPluginPackageOptions,
	type InstallSignedAgentPluginPackageOptions,
} from "./agent-plugin-package-types.js";
import { AGENT_PLUGIN_MANIFEST_SCHEMA_V1, isAgentPluginName } from "./agent-plugin-types.js";
import { BoundedFileReadError, readStableFile } from "./bounded-file.js";

const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });
const INCOMPLETE_STATE_BYTES = encoder.encode("incomplete");
const NEVER_ABORT_SIGNAL = new AbortController().signal;
const SHA256 = /^[0-9a-f]{64}$/u;
const POSITIVE_DECIMAL = /^[1-9][0-9]*$/u;
const BASE64URL_ED25519_SIGNATURE = /^[A-Za-z0-9_-]{86}$/u;
const WINDOWS_RESERVED_SEGMENT = /^(?:con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³])(?:\..*)?$/iu;
const TYPED_ARRAY_PROTOTYPE = Object.getPrototypeOf(Uint8Array.prototype) as object;
const TYPED_ARRAY_BUFFER = Object.getOwnPropertyDescriptor(TYPED_ARRAY_PROTOTYPE, "buffer")?.get as (
	this: Uint8Array,
) => ArrayBufferLike;
const TYPED_ARRAY_BYTE_LENGTH = Object.getOwnPropertyDescriptor(TYPED_ARRAY_PROTOTYPE, "byteLength")?.get as (
	this: Uint8Array,
) => number;
const TYPED_ARRAY_BYTE_OFFSET = Object.getOwnPropertyDescriptor(TYPED_ARRAY_PROTOTYPE, "byteOffset")?.get as (
	this: Uint8Array,
) => number;
const TYPED_ARRAY_SET = Uint8Array.prototype.set;

type ScanMode = "source" | "package-draft" | "installed-draft" | "package" | "installed";

interface CapturedBuildOptions {
	readonly sourceRoot: string;
	readonly packageRoot: string;
	readonly releaseSequence: string;
	readonly executablePaths: ReadonlySet<string>;
	readonly signers: readonly CapturedSigner[];
	readonly limits: AgentPluginPackageLimits;
}

interface CapturedSigner {
	readonly keyId: string;
	readonly sign: AgentPluginPackageSigner["sign"];
}

interface CapturedInstallOptions {
	readonly packageRoot: string;
	readonly storeRoot: string;
	readonly trustedKeys: ReadonlyMap<string, KeyObject>;
	readonly requiredSignatures: number;
	readonly currentInstallation?: AgentPluginPackageInstalledState;
	readonly limits: AgentPluginPackageLimits;
}

interface ScannedFile {
	readonly path: string;
	readonly addressedPath: string;
	readonly bytes: Uint8Array;
	readonly sha256: string;
	readonly metadata: boolean;
	readonly executable: boolean;
	readonly permissions: number;
}

interface ScannedTree {
	readonly files: readonly ScannedFile[];
	readonly pluginFiles: readonly ScannedFile[];
	readonly metadataFiles: ReadonlyMap<string, ScannedFile>;
}

interface DirectoryRecord {
	readonly addressedPath: string;
	readonly canonicalPath: string;
	readonly device: bigint;
	readonly inode: bigint;
	readonly relativeSegments: readonly string[];
	readonly depth: number;
}

interface DirectoryIdentity {
	readonly addressedPath: string;
	readonly canonicalPath: string;
	readonly device: bigint;
	readonly inode: bigint;
}

interface PublicationDraft extends DirectoryIdentity {
	readonly statePath: string;
	committed: boolean;
}

type PublicationRecord =
	| { readonly kind: "missing" }
	| { readonly kind: "incomplete"; readonly token: string }
	| {
			readonly kind: "complete";
			readonly token: string;
			readonly packageDigest: string;
			readonly device: string;
			readonly inode: string;
	  }
	| { readonly kind: "invalid" };

interface TreeBudget {
	directories: number;
	visitedEntries: number;
	pluginFiles: number;
	totalPluginBytes: number;
}

interface ParsedPluginIdentity {
	readonly name: string;
	readonly version: string;
}

interface ParsedSignature {
	readonly keyId: string;
	readonly bytes: Uint8Array;
}

/** Build one signed, inert Agent Plugins 1.0 directory without loading or executing package content. */
export function buildSignedAgentPluginPackage(
	options: BuildSignedAgentPluginPackageOptions,
	signal: AbortSignal,
): Promise<AgentPluginPackageBuildResult> {
	const captured = captureBuildOptions(options);
	assertSignal(signal);
	signal.throwIfAborted();
	return buildPackage(captured, signal);
}

/** Verify and install a signed package into a host-owned immutable content-addressed store. */
export function installSignedAgentPluginPackage(
	options: InstallSignedAgentPluginPackageOptions,
	signal: AbortSignal,
): Promise<AgentPluginPackageInstalledState> {
	const captured = captureInstallOptions(options);
	assertSignal(signal);
	signal.throwIfAborted();
	return installPackage(captured, signal);
}

async function buildPackage(
	options: CapturedBuildOptions,
	signal: AbortSignal,
): Promise<AgentPluginPackageBuildResult> {
	const tree = await scanTree(options.sourceRoot, "source", options.limits, signal);
	const pluginManifest = requiredPluginFile(tree.pluginFiles, "plugin.json", "source_invalid");
	const identity = parsePluginIdentity(pluginManifest.bytes, options.limits, "source_invalid");
	for (const executablePath of options.executablePaths) {
		if (!tree.pluginFiles.some((file) => file.path === executablePath)) {
			throw new AgentPluginPackageError("invalid_configuration");
		}
	}

	const files = tree.pluginFiles.map<AgentPluginPackageFile>((file) => ({
		path: file.path,
		bytes: file.bytes.byteLength,
		sha256: file.sha256,
		executable: options.executablePaths.has(file.path),
	}));
	const manifest: AgentPluginPackageManifestV1 = {
		schema: AGENT_PLUGIN_PACKAGE_SCHEMA_V1,
		plugin: identity,
		releaseSequence: options.releaseSequence,
		files,
	};
	const manifestText = canonicalizeJsonValue(manifest);
	if (encoder.encode(manifestText).byteLength > options.limits.maxPackageManifestBytes) {
		throw new AgentPluginPackageError("package_limit_exceeded");
	}
	const packageDigest = sha256(encoder.encode(manifestText));
	const signingPayload = concatenate(
		encoder.encode(AGENT_PLUGIN_PACKAGE_SIGNATURE_DOMAIN_V1),
		encoder.encode(manifestText),
	);
	const signatures: Array<{ keyId: string; signature: string }> = [];
	for (const signer of options.signers) {
		signal.throwIfAborted();
		let returned: Uint8Array;
		try {
			returned = await raceOperationWithAbort(() => signer.sign(signingPayload.slice(), signal), signal);
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof AgentPluginPackageError) throw error;
			throw new AgentPluginPackageError("operation_incomplete");
		}
		const signature = snapshotSignature(returned);
		signatures.push({ keyId: signer.keyId, signature: Buffer.from(signature).toString("base64url") });
	}
	const signaturesText = canonicalizeJsonValue({ schema: AGENT_PLUGIN_PACKAGE_SCHEMA_V1, signatures });
	if (encoder.encode(signaturesText).byteLength > options.limits.maxSignaturesBytes) {
		throw new AgentPluginPackageError("package_limit_exceeded");
	}

	const packageParent = await captureDirectoryIdentity(
		dirname(options.packageRoot),
		options.limits,
		"invalid_configuration",
		signal,
	);
	const stagingPaths = [
		...files.map((file) => file.path),
		`${AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY}/${AGENT_PLUGIN_PACKAGE_MANIFEST_FILE}`,
		`${AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY}/${AGENT_PLUGIN_PACKAGE_SIGNATURES_FILE}`,
	];
	assertPublicationPaths(options.packageRoot, stagingPaths, options.limits);
	let draft: PublicationDraft | undefined;
	try {
		const reservation = await reservePublicationDirectory(options.packageRoot, packageParent, options.limits, signal);
		if (reservation === "exists") throw new AgentPluginPackageError("installation_conflict");
		draft = reservation;
		await writePackageFiles(options.packageRoot, tree.pluginFiles, files, options.limits, signal);
		const metadataRoot = join(options.packageRoot, AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY);
		assertAddressedPath(metadataRoot, options.limits);
		await createDirectory(metadataRoot);
		const packageManifestPath = join(metadataRoot, AGENT_PLUGIN_PACKAGE_MANIFEST_FILE);
		const signaturesPath = join(metadataRoot, AGENT_PLUGIN_PACKAGE_SIGNATURES_FILE);
		assertAddressedPath(packageManifestPath, options.limits);
		assertAddressedPath(signaturesPath, options.limits);
		await writePublicationFile(packageManifestPath, encoder.encode(manifestText), false, signal);
		await writePublicationFile(signaturesPath, encoder.encode(signaturesText), false, signal);
		const draftTree = await scanTree(options.packageRoot, "package-draft", options.limits, signal);
		verifyCompleteFileSet(draftTree.pluginFiles, manifest, options.limits);
		assertMetadataBytes(draftTree, manifestText, signaturesText);
		await completePublicationDirectory(draft, packageParent, stagingPaths, packageDigest, options.limits, signal);
		const publishedTree = await scanTree(options.packageRoot, "package", options.limits, NEVER_ABORT_SIGNAL);
		verifyCompleteFileSet(publishedTree.pluginFiles, manifest, options.limits);
		assertMetadataBytes(publishedTree, manifestText, signaturesText);
	} catch (error) {
		const aborted = signal.aborted && draft?.committed !== true;
		const abortReason: unknown = signal.reason;
		if (aborted) throw abortReason;
		if (error instanceof AgentPluginPackageError) throw error;
		throw new AgentPluginPackageError("operation_incomplete");
	}

	return deepFreeze({
		pluginName: identity.name,
		pluginVersion: identity.version,
		releaseSequence: options.releaseSequence,
		packageDigest,
		packageRoot: options.packageRoot,
		manifest,
		files,
	});
}

async function installPackage(
	options: CapturedInstallOptions,
	signal: AbortSignal,
): Promise<AgentPluginPackageInstalledState> {
	const packageStateBefore = await readPublicationRecord(options.packageRoot, options.limits, signal);
	if (packageStateBefore.kind === "incomplete" || packageStateBefore.kind === "invalid") {
		throw new AgentPluginPackageError("package_invalid");
	}
	const tree = await scanTree(options.packageRoot, "package", options.limits, signal);
	const packageStateAfter = await readPublicationRecord(options.packageRoot, options.limits, signal);
	if (
		packageStateAfter.kind === "incomplete" ||
		packageStateAfter.kind === "invalid" ||
		publicationRecordToken(packageStateAfter) !== publicationRecordToken(packageStateBefore)
	) {
		throw new AgentPluginPackageError("package_invalid");
	}
	const manifestFile = requiredMetadataFile(tree, AGENT_PLUGIN_PACKAGE_MANIFEST_FILE);
	const signaturesFile = requiredMetadataFile(tree, AGENT_PLUGIN_PACKAGE_SIGNATURES_FILE);
	const manifestText = decodeCanonicalText(manifestFile.bytes, "package_invalid");
	const manifest = parsePackageManifest(manifestText, options.limits);
	const packageDigest = sha256(encoder.encode(manifestText));
	if (packageStateAfter.kind === "complete") {
		const packageIdentity = await captureDirectoryIdentity(
			options.packageRoot,
			options.limits,
			"package_invalid",
			signal,
		);
		assertCompletePublicationRecord(packageStateAfter, packageDigest, packageIdentity, "package_invalid");
	}
	const signaturesText = decodeCanonicalText(signaturesFile.bytes, "package_invalid");
	const signatures = parseSignatures(signaturesText, options.limits);
	verifyThreshold(manifestText, signatures, options);
	verifyCompleteFileSet(tree.pluginFiles, manifest, options.limits);
	const pluginFile = requiredPluginFile(tree.pluginFiles, "plugin.json", "package_invalid");
	const pluginIdentity = parsePluginIdentity(pluginFile.bytes, options.limits, "package_invalid");
	if (pluginIdentity.name !== manifest.plugin.name || pluginIdentity.version !== manifest.plugin.version) {
		throw new AgentPluginPackageError("package_invalid");
	}

	const rootPath = resolve(options.storeRoot, packageDigest);
	assertAddressedPath(rootPath, options.limits);
	const installed: AgentPluginPackageInstalledState = deepFreeze({
		pluginName: manifest.plugin.name,
		pluginVersion: manifest.plugin.version,
		releaseSequence: manifest.releaseSequence,
		packageDigest,
		rootPath,
	});
	validateUpdate(options.currentInstallation, installed);

	const storeIdentity = await ensureStoreRoot(options.storeRoot, options.limits, signal);
	const existingState = await readPublicationRecord(rootPath, options.limits, signal);
	const existing = await pathKind(rootPath, signal);
	let draft: PublicationDraft | undefined;
	if (existingState.kind === "invalid") throw new AgentPluginPackageError("installation_conflict");
	if (existingState.kind === "incomplete") {
		if (existing !== "missing" && existing !== "directory") {
			throw new AgentPluginPackageError("installation_conflict");
		}
		if (existing === "directory") {
			draft = await openPublicationDraft(rootPath, storeIdentity, options.limits, signal);
		}
	} else if (existing !== "missing") {
		if (existing !== "directory") throw new AgentPluginPackageError("installation_conflict");
		const identity = await captureDirectoryIdentity(rootPath, options.limits, "installation_conflict", signal);
		if (existingState.kind === "complete") {
			assertCompletePublicationRecord(existingState, packageDigest, identity, "installation_conflict");
		}
		await verifyInstalledRoot(rootPath, manifest, options.limits, signal);
		await assertDirectoryIdentity(storeIdentity, "installation_conflict", signal);
		await syncDirectory(options.storeRoot);
		await assertDirectoryIdentity(storeIdentity, "installation_conflict", signal);
		return installed;
	} else if (existingState.kind === "complete") {
		throw new AgentPluginPackageError("installation_conflict");
	}

	const publicationPaths = manifest.files.map((file) => file.path);
	assertPublicationPaths(rootPath, publicationPaths, options.limits);
	try {
		if (draft === undefined) {
			const reservation = await reservePublicationDirectory(rootPath, storeIdentity, options.limits, signal);
			if (reservation === "exists") {
				const concurrentState = await readPublicationRecord(rootPath, options.limits, signal);
				if (concurrentState.kind === "invalid" || concurrentState.kind === "incomplete") {
					throw new AgentPluginPackageError("installation_conflict");
				}
				const identity = await captureDirectoryIdentity(rootPath, options.limits, "installation_conflict", signal);
				if (concurrentState.kind === "complete") {
					assertCompletePublicationRecord(concurrentState, packageDigest, identity, "installation_conflict");
				}
				await verifyInstalledRoot(rootPath, manifest, options.limits, signal);
				await assertDirectoryIdentity(storeIdentity, "installation_conflict", signal);
				await syncDirectory(options.storeRoot);
				await assertDirectoryIdentity(storeIdentity, "installation_conflict", signal);
				return installed;
			}
			draft = reservation;
		}
		await writePackageFiles(rootPath, tree.pluginFiles, manifest.files, options.limits, signal);
		const draftTree = await scanTree(rootPath, "installed-draft", options.limits, signal);
		verifyCompleteFileSet(draftTree.pluginFiles, manifest, options.limits);
		await completePublicationDirectory(draft, storeIdentity, publicationPaths, packageDigest, options.limits, signal);
		await verifyInstalledRoot(rootPath, manifest, options.limits, NEVER_ABORT_SIGNAL);
		await assertDirectoryIdentity(storeIdentity, "installation_conflict", NEVER_ABORT_SIGNAL);
	} catch (error) {
		const aborted = signal.aborted && draft?.committed !== true;
		const abortReason: unknown = signal.reason;
		if (aborted) throw abortReason;
		if (error instanceof AgentPluginPackageError) throw error;
		throw new AgentPluginPackageError("operation_incomplete");
	}
	return installed;
}

function captureBuildOptions(options: BuildSignedAgentPluginPackageOptions): CapturedBuildOptions {
	let cwd: unknown;
	let sourceRoot: unknown;
	let packageRoot: unknown;
	let releaseSequence: unknown;
	let executablePathsInput: unknown;
	let signersInput: unknown;
	let limitsInput: unknown;
	try {
		cwd = options.cwd;
		sourceRoot = options.sourceRoot;
		packageRoot = options.packageRoot;
		releaseSequence = options.releaseSequence;
		executablePathsInput = options.executablePaths;
		signersInput = options.signers;
		limitsInput = options.limits;
	} catch {
		throw new AgentPluginPackageError("invalid_configuration");
	}
	if (
		typeof cwd !== "string" ||
		typeof sourceRoot !== "string" ||
		typeof packageRoot !== "string" ||
		typeof releaseSequence !== "string" ||
		!Array.isArray(executablePathsInput) ||
		!Array.isArray(signersInput)
	) {
		throw new AgentPluginPackageError("invalid_configuration");
	}
	const limits = captureLimits(limitsInput);
	if (executablePathsInput.length > limits.maxFiles) {
		throw new AgentPluginPackageError("invalid_configuration");
	}
	const source = resolve(cwd, sourceRoot);
	const destination = resolve(cwd, packageRoot);
	assertAddressedPath(source, limits);
	assertAddressedPath(destination, limits);
	if (overlaps(source, destination)) throw new AgentPluginPackageError("invalid_configuration");
	assertReleaseSequence(releaseSequence, limits);

	const executablePaths = new Set<string>();
	const portableKeys = new Set<string>();
	for (const value of executablePathsInput) {
		if (typeof value !== "string") throw new AgentPluginPackageError("invalid_configuration");
		assertPortablePath(value, limits, "invalid_configuration");
		const key = portableCollisionKey(value);
		if (portableKeys.has(key)) throw new AgentPluginPackageError("invalid_configuration");
		portableKeys.add(key);
		executablePaths.add(value);
	}
	if (
		signersInput.length < 1 ||
		signersInput.length > limits.maxSigners ||
		signersInput.length > limits.maxSignatures
	) {
		throw new AgentPluginPackageError("invalid_configuration");
	}
	const signerIds = new Set<string>();
	const signers: CapturedSigner[] = [];
	for (const value of signersInput) {
		let keyId: unknown;
		let sign: unknown;
		try {
			const signer = value as AgentPluginPackageSigner;
			keyId = signer.keyId;
			sign = signer.sign;
		} catch {
			throw new AgentPluginPackageError("invalid_configuration");
		}
		if (typeof keyId !== "string" || typeof sign !== "function") {
			throw new AgentPluginPackageError("invalid_configuration");
		}
		assertKeyId(keyId, limits, "invalid_configuration");
		if (signerIds.has(keyId)) throw new AgentPluginPackageError("invalid_configuration");
		signerIds.add(keyId);
		signers.push({ keyId, sign: sign.bind(value) as AgentPluginPackageSigner["sign"] });
	}
	signers.sort((left, right) => compareStrings(left.keyId, right.keyId));
	return { sourceRoot: source, packageRoot: destination, releaseSequence, executablePaths, signers, limits };
}

function captureInstallOptions(options: InstallSignedAgentPluginPackageOptions): CapturedInstallOptions {
	let cwd: unknown;
	let packageRoot: unknown;
	let storeRoot: unknown;
	let trustedKeysInput: unknown;
	let requiredSignatures: unknown;
	let currentInput: unknown;
	let limitsInput: unknown;
	try {
		cwd = options.cwd;
		packageRoot = options.packageRoot;
		storeRoot = options.storeRoot;
		trustedKeysInput = options.trustedKeys;
		requiredSignatures = options.requiredSignatures;
		currentInput = options.currentInstallation;
		limitsInput = options.limits;
	} catch {
		throw new AgentPluginPackageError("invalid_configuration");
	}
	if (
		typeof cwd !== "string" ||
		typeof packageRoot !== "string" ||
		typeof storeRoot !== "string" ||
		!Array.isArray(trustedKeysInput)
	) {
		throw new AgentPluginPackageError("invalid_configuration");
	}
	const limits = captureLimits(limitsInput);
	if (
		!Number.isSafeInteger(requiredSignatures) ||
		(requiredSignatures as number) < 1 ||
		(requiredSignatures as number) > limits.maxSignatures
	) {
		throw new AgentPluginPackageError("invalid_configuration");
	}
	if (trustedKeysInput.length < (requiredSignatures as number) || trustedKeysInput.length > limits.maxTrustedKeys) {
		throw new AgentPluginPackageError("invalid_configuration");
	}
	const trustedKeys = new Map<string, KeyObject>();
	const trustedKeyValues: KeyObject[] = [];
	for (const value of trustedKeysInput) {
		let keyId: unknown;
		let publicKey: unknown;
		try {
			const key = value as AgentPluginPackageTrustedKey;
			keyId = key.keyId;
			publicKey = key.publicKey;
		} catch {
			throw new AgentPluginPackageError("invalid_configuration");
		}
		if (typeof keyId !== "string" || !isEd25519PublicKey(publicKey)) {
			throw new AgentPluginPackageError("invalid_configuration");
		}
		assertKeyId(keyId, limits, "invalid_configuration");
		if (trustedKeys.has(keyId) || trustedKeyValues.some((existing) => samePublicKey(existing, publicKey))) {
			throw new AgentPluginPackageError("invalid_configuration");
		}
		trustedKeys.set(keyId, publicKey);
		trustedKeyValues.push(publicKey);
	}
	const packagePath = resolve(cwd, packageRoot);
	const store = resolve(cwd, storeRoot);
	assertAddressedPath(packagePath, limits);
	assertAddressedPath(store, limits);
	if (overlaps(packagePath, store)) throw new AgentPluginPackageError("invalid_configuration");
	const currentInstallation = captureCurrentInstallation(currentInput, cwd, limits);
	return {
		packageRoot: packagePath,
		storeRoot: store,
		trustedKeys,
		requiredSignatures: requiredSignatures as number,
		...(currentInstallation === undefined ? {} : { currentInstallation }),
		limits,
	};
}

function captureLimits(value: unknown): AgentPluginPackageLimits {
	if (!isRecord(value)) throw new AgentPluginPackageError("invalid_configuration");
	const names = [
		"maxTraversalDepth",
		"maxDirectories",
		"maxEntriesPerDirectory",
		"maxVisitedEntries",
		"maxFiles",
		"maxPortablePathBytes",
		"maxPortablePathSegmentBytes",
		"maxAddressedPathBytes",
		"maxFileBytes",
		"maxTotalFileBytes",
		"maxPluginManifestBytes",
		"maxPackageManifestBytes",
		"maxSignaturesBytes",
		"maxJsonDepth",
		"maxSigners",
		"maxTrustedKeys",
		"maxSignatures",
		"maxKeyIdBytes",
		"maxReleaseSequenceBytes",
		"maxPluginVersionBytes",
	] as const satisfies readonly (keyof AgentPluginPackageLimits)[];
	const captured = {} as Record<(typeof names)[number], number>;
	for (const name of names) {
		let current: unknown;
		try {
			current = value[name];
		} catch {
			throw new AgentPluginPackageError("invalid_configuration");
		}
		if (!Number.isSafeInteger(current) || (current as number) < 1) {
			throw new AgentPluginPackageError("invalid_configuration");
		}
		captured[name] = current as number;
	}
	return captured;
}

function captureCurrentInstallation(
	value: unknown,
	cwd: string,
	limits: AgentPluginPackageLimits,
): AgentPluginPackageInstalledState | undefined {
	if (value === undefined) return;
	if (!isRecord(value)) throw new AgentPluginPackageError("invalid_configuration");
	let pluginName: unknown;
	let pluginVersion: unknown;
	let releaseSequence: unknown;
	let packageDigest: unknown;
	let rootPath: unknown;
	try {
		pluginName = value.pluginName;
		pluginVersion = value.pluginVersion;
		releaseSequence = value.releaseSequence;
		packageDigest = value.packageDigest;
		rootPath = value.rootPath;
	} catch {
		throw new AgentPluginPackageError("invalid_configuration");
	}
	if (
		typeof pluginName !== "string" ||
		!isAgentPluginName(pluginName) ||
		typeof pluginVersion !== "string" ||
		!isSafeText(pluginVersion) ||
		encoder.encode(pluginVersion).byteLength > limits.maxPluginVersionBytes ||
		typeof releaseSequence !== "string" ||
		typeof packageDigest !== "string" ||
		!SHA256.test(packageDigest) ||
		typeof rootPath !== "string"
	) {
		throw new AgentPluginPackageError("invalid_configuration");
	}
	assertReleaseSequence(releaseSequence, limits);
	const resolvedRoot = resolve(cwd, rootPath);
	assertAddressedPath(resolvedRoot, limits);
	return { pluginName, pluginVersion, releaseSequence, packageDigest, rootPath: resolvedRoot };
}

async function scanTree(
	root: string,
	mode: ScanMode,
	limits: AgentPluginPackageLimits,
	signal: AbortSignal,
): Promise<ScannedTree> {
	const failureCode =
		mode === "source" ? "source_invalid" : isDraftMode(mode) ? "operation_incomplete" : "package_invalid";
	const rootRecord = await inspectRootDirectory(root, mode, limits, failureCode, signal);
	const directories: DirectoryRecord[] = [rootRecord];
	const pending: DirectoryRecord[] = [rootRecord];
	const files: ScannedFile[] = [];
	const metadataFiles = new Map<string, ScannedFile>();
	const portableKeys = new Set<string>();
	const budget: TreeBudget = { directories: 1, visitedEntries: 0, pluginFiles: 0, totalPluginBytes: 0 };

	while (pending.length > 0) {
		signal.throwIfAborted();
		const directory = pending.pop();
		if (!directory) break;
		await assertDirectoryRecord(directory, rootRecord.canonicalPath, mode, failureCode, signal);
		const entries = await readBoundedDirectoryEntries(
			directory.addressedPath,
			Math.min(limits.maxEntriesPerDirectory, limits.maxVisitedEntries - budget.visitedEntries),
			failureCode,
			signal,
		);
		budget.visitedEntries += entries.length;
		entries.sort((left, right) => compareStrings(left.name, right.name));
		for (const entry of entries) {
			signal.throwIfAborted();
			const relativeSegments = [...directory.relativeSegments, entry.name];
			const portablePath = relativeSegments.join("/");
			assertPortablePath(portablePath, limits, failureCode);
			const collisionKey = portableCollisionKey(portablePath);
			if (portableKeys.has(collisionKey)) throw new AgentPluginPackageError(failureCode);
			portableKeys.add(collisionKey);
			const addressedPath = join(directory.addressedPath, entry.name);
			assertAddressedPath(addressedPath, limits);
			const addressed = await safeLstatIdentity(addressedPath, failureCode, signal);
			if (addressed.isSymbolicLink()) throw new AgentPluginPackageError(failureCode);
			const isMetadata = relativeSegments[0]?.toLowerCase() === AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY;
			if (isMetadata && relativeSegments[0] !== AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY) {
				throw new AgentPluginPackageError(failureCode);
			}
			if (isMetadata && !isPackageMode(mode)) throw new AgentPluginPackageError(failureCode);
			if (addressed.isDirectory()) {
				assertSealedDirectoryMode(addressed, mode, failureCode);
				if (isMetadata && relativeSegments.length !== 1) throw new AgentPluginPackageError(failureCode);
				const depth = directory.depth + 1;
				if (depth > limits.maxTraversalDepth || budget.directories >= limits.maxDirectories) {
					throw new AgentPluginPackageError("package_limit_exceeded");
				}
				const canonicalPath = await safeRealpath(addressedPath, failureCode, signal);
				if (!isContained(rootRecord.canonicalPath, canonicalPath)) throw new AgentPluginPackageError(failureCode);
				budget.directories += 1;
				const child = {
					addressedPath,
					canonicalPath,
					device: addressed.dev,
					inode: addressed.ino,
					relativeSegments,
					depth,
				};
				directories.push(child);
				pending.push(child);
				continue;
			}
			if (!addressed.isFile()) throw new AgentPluginPackageError(failureCode);
			if (isMetadata) {
				if (relativeSegments.length !== 2) throw new AgentPluginPackageError(failureCode);
				const name = relativeSegments[1];
				if (name !== AGENT_PLUGIN_PACKAGE_MANIFEST_FILE && name !== AGENT_PLUGIN_PACKAGE_SIGNATURES_FILE) {
					throw new AgentPluginPackageError(failureCode);
				}
				const maximum =
					name === AGENT_PLUGIN_PACKAGE_MANIFEST_FILE ? limits.maxPackageManifestBytes : limits.maxSignaturesBytes;
				const file = await readScannedFile(addressedPath, portablePath, maximum, true, rootRecord, failureCode, signal);
				assertSealedFileMode(file, false, mode, failureCode);
				metadataFiles.set(name, file);
				files.push(file);
				continue;
			}
			if (budget.pluginFiles >= limits.maxFiles) throw new AgentPluginPackageError("package_limit_exceeded");
			const perFileMaximum =
				portablePath === "plugin.json"
					? Math.min(limits.maxFileBytes, limits.maxPluginManifestBytes)
					: limits.maxFileBytes;
			const remainingTotal = limits.maxTotalFileBytes - budget.totalPluginBytes;
			if (addressed.size > BigInt(perFileMaximum) || addressed.size > BigInt(remainingTotal)) {
				throw new AgentPluginPackageError("package_limit_exceeded");
			}
			const readMaximum = Math.min(perFileMaximum, Math.max(1, remainingTotal));
			const file = await readScannedFile(
				addressedPath,
				portablePath,
				readMaximum,
				false,
				rootRecord,
				failureCode,
				signal,
			);
			if (budget.totalPluginBytes > limits.maxTotalFileBytes - file.bytes.byteLength) {
				throw new AgentPluginPackageError("package_limit_exceeded");
			}
			budget.pluginFiles += 1;
			budget.totalPluginBytes += file.bytes.byteLength;
			files.push(file);
		}
	}
	for (const directory of directories) {
		await assertDirectoryRecord(directory, rootRecord.canonicalPath, mode, failureCode, signal);
	}
	files.sort((left, right) => compareStrings(left.path, right.path));
	const pluginFiles = files.filter((file) => !file.metadata);
	if (isPackageMode(mode) && metadataFiles.size !== 2) throw new AgentPluginPackageError(failureCode);
	return { files, pluginFiles, metadataFiles };
}

async function readBoundedDirectoryEntries(
	path: string,
	maximumEntries: number,
	failureCode: AgentPluginPackageErrorCode,
	signal: AbortSignal,
): Promise<Dirent[]> {
	signal.throwIfAborted();
	let directory: Awaited<ReturnType<typeof opendir>>;
	try {
		directory = await opendir(path);
		signal.throwIfAborted();
	} catch {
		signal.throwIfAborted();
		throw new AgentPluginPackageError(failureCode);
	}
	const entries: Dirent[] = [];
	try {
		for (;;) {
			signal.throwIfAborted();
			const entry = await directory.read();
			signal.throwIfAborted();
			if (entry === null) return entries;
			if (entries.length >= maximumEntries) {
				throw new AgentPluginPackageError("package_limit_exceeded");
			}
			entries.push(entry);
		}
	} catch (error) {
		signal.throwIfAborted();
		if (error instanceof AgentPluginPackageError) throw error;
		throw new AgentPluginPackageError(failureCode);
	} finally {
		await directory.close().catch(() => undefined);
	}
}

async function inspectRootDirectory(
	root: string,
	mode: ScanMode,
	limits: AgentPluginPackageLimits,
	failureCode: AgentPluginPackageErrorCode,
	signal: AbortSignal,
): Promise<DirectoryRecord> {
	assertAddressedPath(root, limits);
	const addressed = await safeLstatIdentity(root, failureCode, signal);
	if (!addressed.isDirectory() || addressed.isSymbolicLink()) throw new AgentPluginPackageError(failureCode);
	if (process.platform !== "win32" && isDraftMode(mode) && ![0o555, 0o755].includes(fileMode(addressed))) {
		throw new AgentPluginPackageError(failureCode);
	}
	assertSealedDirectoryMode(addressed, mode, failureCode);
	const canonicalPath = await safeRealpath(root, failureCode, signal);
	if (!sameAddressedPath(canonicalPath, root)) throw new AgentPluginPackageError(failureCode);
	return {
		addressedPath: root,
		canonicalPath,
		device: addressed.dev,
		inode: addressed.ino,
		relativeSegments: [],
		depth: 0,
	};
}

async function assertDirectoryRecord(
	directory: DirectoryRecord,
	canonicalRoot: string,
	mode: ScanMode,
	failureCode: AgentPluginPackageErrorCode,
	signal: AbortSignal,
): Promise<void> {
	const addressed = await safeLstatIdentity(directory.addressedPath, failureCode, signal);
	if (
		!addressed.isDirectory() ||
		addressed.isSymbolicLink() ||
		addressed.dev !== directory.device ||
		addressed.ino !== directory.inode
	) {
		throw new AgentPluginPackageError(failureCode);
	}
	assertSealedDirectoryMode(addressed, mode, failureCode);
	const canonical = await safeRealpath(directory.addressedPath, failureCode, signal);
	if (canonical !== directory.canonicalPath || !isContained(canonicalRoot, canonical)) {
		throw new AgentPluginPackageError(failureCode);
	}
}

async function readScannedFile(
	addressedPath: string,
	portablePath: string,
	maximum: number,
	metadata: boolean,
	root: DirectoryRecord,
	failureCode: AgentPluginPackageErrorCode,
	signal: AbortSignal,
): Promise<ScannedFile> {
	try {
		const read = await readStableFile(addressedPath, maximum, signal);
		if (!isContained(root.canonicalPath, read.canonicalPath)) throw new AgentPluginPackageError(failureCode);
		const addressedAfter = await safeLstatIdentity(addressedPath, failureCode, signal);
		if (!addressedAfter.isFile() || addressedAfter.isSymbolicLink()) throw new AgentPluginPackageError(failureCode);
		return {
			path: portablePath,
			addressedPath,
			bytes: read.bytes,
			sha256: sha256(read.bytes),
			metadata,
			executable: (addressedAfter.mode & 0o111n) !== 0n,
			permissions: Number(addressedAfter.mode & 0o777n),
		};
	} catch (error) {
		signal.throwIfAborted();
		if (error instanceof AgentPluginPackageError) throw error;
		if (error instanceof BoundedFileReadError) {
			if (error.code === "limit_exceeded") throw new AgentPluginPackageError("package_limit_exceeded");
			throw new AgentPluginPackageError(failureCode);
		}
		throw new AgentPluginPackageError(failureCode);
	}
}

function parsePluginIdentity(
	bytes: Uint8Array,
	limits: AgentPluginPackageLimits,
	failureCode: AgentPluginPackageErrorCode,
): ParsedPluginIdentity {
	let value: unknown;
	try {
		const text = decoder.decode(bytes);
		value = parseStrictJsonText(text, limits.maxJsonDepth);
	} catch {
		throw new AgentPluginPackageError(failureCode);
	}
	if (
		!isRecord(value) ||
		value.$schema !== AGENT_PLUGIN_MANIFEST_SCHEMA_V1 ||
		typeof value.name !== "string" ||
		!isAgentPluginName(value.name) ||
		typeof value.version !== "string" ||
		!isSafeText(value.version) ||
		encoder.encode(value.version).byteLength > limits.maxPluginVersionBytes
	) {
		throw new AgentPluginPackageError(failureCode);
	}
	return { name: value.name, version: value.version };
}

function parsePackageManifest(text: string, limits: AgentPluginPackageLimits): AgentPluginPackageManifestV1 {
	let value: unknown;
	try {
		value = parseStrictJsonText(text, limits.maxJsonDepth);
		if (canonicalizeJsonValue(value) !== text) throw new Error("not_canonical");
	} catch {
		throw new AgentPluginPackageError("package_invalid");
	}
	if (!hasExactKeys(value, ["files", "plugin", "releaseSequence", "schema"])) {
		throw new AgentPluginPackageError("package_invalid");
	}
	if (
		value.schema !== AGENT_PLUGIN_PACKAGE_SCHEMA_V1 ||
		!hasExactKeys(value.plugin, ["name", "version"]) ||
		typeof value.plugin.name !== "string" ||
		!isAgentPluginName(value.plugin.name) ||
		typeof value.plugin.version !== "string" ||
		!isSafeText(value.plugin.version) ||
		encoder.encode(value.plugin.version).byteLength > limits.maxPluginVersionBytes ||
		typeof value.releaseSequence !== "string" ||
		!Array.isArray(value.files)
	) {
		throw new AgentPluginPackageError("package_invalid");
	}
	assertReleaseSequence(value.releaseSequence, limits, "package_invalid");
	if (value.files.length > limits.maxFiles) throw new AgentPluginPackageError("package_limit_exceeded");
	const files: AgentPluginPackageFile[] = [];
	const keys = new Set<string>();
	let total = 0;
	let previous: string | undefined;
	for (const raw of value.files) {
		if (
			!hasExactKeys(raw, ["bytes", "executable", "path", "sha256"]) ||
			typeof raw.path !== "string" ||
			!Number.isSafeInteger(raw.bytes) ||
			(raw.bytes as number) < 0 ||
			typeof raw.sha256 !== "string" ||
			!SHA256.test(raw.sha256) ||
			typeof raw.executable !== "boolean"
		) {
			throw new AgentPluginPackageError("package_invalid");
		}
		assertPortablePath(raw.path, limits, "package_invalid");
		if (raw.path.split("/")[0]?.toLowerCase() === AGENT_PLUGIN_PACKAGE_METADATA_DIRECTORY) {
			throw new AgentPluginPackageError("package_invalid");
		}
		const key = portableCollisionKey(raw.path);
		if (keys.has(key) || (previous !== undefined && compareStrings(previous, raw.path) >= 0)) {
			throw new AgentPluginPackageError("package_invalid");
		}
		keys.add(key);
		previous = raw.path;
		const bytes = raw.bytes as number;
		if (bytes > limits.maxFileBytes || total > limits.maxTotalFileBytes - bytes) {
			throw new AgentPluginPackageError("package_limit_exceeded");
		}
		total += bytes;
		files.push({ path: raw.path, bytes, sha256: raw.sha256, executable: raw.executable });
	}
	return deepFreeze({
		schema: AGENT_PLUGIN_PACKAGE_SCHEMA_V1,
		plugin: { name: value.plugin.name, version: value.plugin.version },
		releaseSequence: value.releaseSequence,
		files,
	});
}

function parseSignatures(text: string, limits: AgentPluginPackageLimits): readonly ParsedSignature[] {
	let value: unknown;
	try {
		value = parseStrictJsonText(text, limits.maxJsonDepth);
		if (canonicalizeJsonValue(value) !== text) throw new Error("not_canonical");
	} catch {
		throw new AgentPluginPackageError("package_invalid");
	}
	if (
		!hasExactKeys(value, ["schema", "signatures"]) ||
		value.schema !== AGENT_PLUGIN_PACKAGE_SCHEMA_V1 ||
		!Array.isArray(value.signatures)
	) {
		throw new AgentPluginPackageError("package_invalid");
	}
	if (value.signatures.length < 1) throw new AgentPluginPackageError("signature_invalid");
	if (value.signatures.length > limits.maxSignatures) throw new AgentPluginPackageError("package_limit_exceeded");
	const ids = new Set<string>();
	const signatures: ParsedSignature[] = [];
	let previousKeyId: string | undefined;
	for (const raw of value.signatures) {
		if (
			!hasExactKeys(raw, ["keyId", "signature"]) ||
			typeof raw.keyId !== "string" ||
			typeof raw.signature !== "string"
		) {
			throw new AgentPluginPackageError("signature_invalid");
		}
		assertKeyId(raw.keyId, limits, "signature_invalid");
		if (
			ids.has(raw.keyId) ||
			(previousKeyId !== undefined && compareStrings(previousKeyId, raw.keyId) >= 0) ||
			!BASE64URL_ED25519_SIGNATURE.test(raw.signature)
		) {
			throw new AgentPluginPackageError("signature_invalid");
		}
		ids.add(raw.keyId);
		previousKeyId = raw.keyId;
		const bytes = new Uint8Array(Buffer.from(raw.signature, "base64url"));
		if (bytes.byteLength !== 64 || Buffer.from(bytes).toString("base64url") !== raw.signature) {
			throw new AgentPluginPackageError("signature_invalid");
		}
		signatures.push({ keyId: raw.keyId, bytes });
	}
	return signatures;
}

function verifyThreshold(
	manifestText: string,
	signatures: readonly ParsedSignature[],
	options: CapturedInstallOptions,
): void {
	const payload = concatenate(encoder.encode(AGENT_PLUGIN_PACKAGE_SIGNATURE_DOMAIN_V1), encoder.encode(manifestText));
	let valid = 0;
	for (const signature of signatures) {
		const key = options.trustedKeys.get(signature.keyId);
		if (key === undefined) continue;
		try {
			if (verifyEd25519(null, payload, key, signature.bytes)) valid += 1;
		} catch {
			throw new AgentPluginPackageError("signature_invalid");
		}
	}
	if (valid < options.requiredSignatures) throw new AgentPluginPackageError("signature_invalid");
}

function verifyCompleteFileSet(
	actual: readonly ScannedFile[],
	manifest: AgentPluginPackageManifestV1,
	limits: AgentPluginPackageLimits,
): void {
	if (actual.length !== manifest.files.length) throw new AgentPluginPackageError("package_invalid");
	let total = 0;
	for (let index = 0; index < manifest.files.length; index += 1) {
		const declared = manifest.files[index];
		const observed = actual[index];
		if (
			declared === undefined ||
			observed === undefined ||
			declared.path !== observed.path ||
			declared.bytes !== observed.bytes.byteLength ||
			declared.sha256 !== observed.sha256 ||
			(process.platform === "win32"
				? (observed.permissions & 0o222) !== 0
				: declared.executable !== observed.executable || observed.permissions !== (declared.executable ? 0o555 : 0o444))
		) {
			throw new AgentPluginPackageError("package_invalid");
		}
		if (total > limits.maxTotalFileBytes - observed.bytes.byteLength) {
			throw new AgentPluginPackageError("package_limit_exceeded");
		}
		total += observed.bytes.byteLength;
	}
}

function assertMetadataBytes(tree: ScannedTree, manifestText: string, signaturesText: string): void {
	const manifest = requiredMetadataFile(tree, AGENT_PLUGIN_PACKAGE_MANIFEST_FILE);
	const signatures = requiredMetadataFile(tree, AGENT_PLUGIN_PACKAGE_SIGNATURES_FILE);
	if (
		!sameBytes(manifest.bytes, encoder.encode(manifestText)) ||
		!sameBytes(signatures.bytes, encoder.encode(signaturesText))
	) {
		throw new AgentPluginPackageError("operation_incomplete");
	}
}

function validateUpdate(
	current: AgentPluginPackageInstalledState | undefined,
	next: AgentPluginPackageInstalledState,
): void {
	if (current === undefined) return;
	if (current.pluginName !== next.pluginName) throw new AgentPluginPackageError("rollback_rejected");
	if (current.packageDigest === next.packageDigest) {
		if (
			current.pluginVersion !== next.pluginVersion ||
			current.releaseSequence !== next.releaseSequence ||
			!sameAddressedPath(current.rootPath, next.rootPath)
		) {
			throw new AgentPluginPackageError("rollback_rejected");
		}
		return;
	}
	if (comparePositiveDecimals(next.releaseSequence, current.releaseSequence) <= 0) {
		throw new AgentPluginPackageError("rollback_rejected");
	}
}

async function verifyInstalledRoot(
	root: string,
	manifest: AgentPluginPackageManifestV1,
	limits: AgentPluginPackageLimits,
	signal: AbortSignal,
): Promise<void> {
	let tree: ScannedTree;
	try {
		tree = await scanTree(root, "installed", limits, signal);
		verifyCompleteFileSet(tree.pluginFiles, manifest, limits);
	} catch (error) {
		signal.throwIfAborted();
		if (error instanceof AgentPluginPackageError && error.code === "package_limit_exceeded") throw error;
		throw new AgentPluginPackageError("installation_conflict");
	}
}

async function writePackageFiles(
	root: string,
	sourceFiles: readonly ScannedFile[],
	declaredFiles: readonly AgentPluginPackageFile[],
	limits: AgentPluginPackageLimits,
	signal: AbortSignal,
): Promise<void> {
	const source = new Map(sourceFiles.map((file) => [file.path, file]));
	const createdDirectories = new Set<string>();
	for (const declared of declaredFiles) {
		signal.throwIfAborted();
		const file = source.get(declared.path);
		if (file === undefined) throw new AgentPluginPackageError("package_invalid");
		const target = join(root, ...declared.path.split("/"));
		assertAddressedPath(target, limits);
		const parent = dirname(target);
		if (!createdDirectories.has(parent)) {
			await createDirectoryTree(root, parent, createdDirectories);
		}
		await writePublicationFile(target, file.bytes, declared.executable, signal);
	}
}

async function createDirectoryTree(root: string, target: string, created: Set<string>): Promise<void> {
	const child = relative(root, target);
	if (child === "") return;
	let current = root;
	for (const segment of child.split(sep)) {
		current = join(current, segment);
		if (created.has(current)) continue;
		await createDirectory(current);
		created.add(current);
	}
}

async function readPublicationRecord(
	destination: string,
	limits: AgentPluginPackageLimits,
	signal: AbortSignal,
): Promise<PublicationRecord> {
	const statePath = agentPluginPackagePublicationStatePath(destination);
	assertAddressedPath(statePath, limits);
	assertAddressedPath(agentPluginPackagePublicationNextStatePath(destination), limits);
	signal.throwIfAborted();
	let info: Stats;
	try {
		info = await lstat(statePath);
		signal.throwIfAborted();
	} catch (error) {
		signal.throwIfAborted();
		return isNodeError(error, "ENOENT") ? { kind: "missing" } : { kind: "invalid" };
	}
	if (!info.isFile() || info.isSymbolicLink() || (process.platform !== "win32" && (info.mode & 0o777) !== 0o444)) {
		return { kind: "invalid" };
	}
	let bytes: Uint8Array;
	try {
		bytes = (await readStableFile(statePath, AGENT_PLUGIN_PACKAGE_PUBLICATION_STATE_MAX_BYTES, signal)).bytes;
	} catch {
		signal.throwIfAborted();
		return { kind: "invalid" };
	}
	try {
		info = await lstat(statePath);
		signal.throwIfAborted();
	} catch {
		signal.throwIfAborted();
		return { kind: "invalid" };
	}
	if (!info.isFile() || info.isSymbolicLink() || (process.platform !== "win32" && (info.mode & 0o777) !== 0o444)) {
		return { kind: "invalid" };
	}
	let token: string;
	try {
		token = decoder.decode(bytes);
		if (!sameBytes(bytes, encoder.encode(token))) return { kind: "invalid" };
	} catch {
		return { kind: "invalid" };
	}
	if (token === "incomplete") return { kind: "incomplete", token };
	const match = /^complete:([0-9a-f]{64}):([0-9]+):([0-9]+)$/u.exec(token);
	if (match === null) return { kind: "invalid" };
	const [, packageDigest, device, inode] = match;
	if (packageDigest === undefined || device === undefined || inode === undefined) return { kind: "invalid" };
	return { kind: "complete", token, packageDigest, device, inode };
}

function publicationRecordToken(record: PublicationRecord): string {
	if (record.kind === "missing" || record.kind === "invalid") return record.kind;
	return record.token;
}

function assertCompletePublicationRecord(
	record: PublicationRecord,
	packageDigest: string,
	identity: DirectoryIdentity,
	failureCode: AgentPluginPackageErrorCode,
): asserts record is Extract<PublicationRecord, { readonly kind: "complete" }> {
	if (
		record.kind !== "complete" ||
		record.packageDigest !== packageDigest ||
		record.device !== String(identity.device) ||
		record.inode !== String(identity.inode)
	) {
		throw new AgentPluginPackageError(failureCode);
	}
}

async function reservePublicationDirectory(
	destination: string,
	parent: DirectoryIdentity,
	limits: AgentPluginPackageLimits,
	signal: AbortSignal,
): Promise<PublicationDraft | "exists"> {
	assertAddressedPath(destination, limits);
	const statePath = agentPluginPackagePublicationStatePath(destination);
	assertAddressedPath(statePath, limits);
	assertAddressedPath(agentPluginPackagePublicationNextStatePath(destination), limits);
	if (!sameAddressedPath(dirname(destination), parent.addressedPath)) {
		throw new AgentPluginPackageError("invalid_configuration");
	}
	await assertDirectoryIdentity(parent, "installation_conflict", signal);
	let state = await readPublicationRecord(destination, limits, signal);
	let destinationKind = await pathKind(destination, signal);
	if (state.kind === "invalid" || state.kind === "complete") return "exists";
	if (state.kind === "missing") {
		if (destinationKind !== "missing") return "exists";
		try {
			await writePublicationFile(statePath, INCOMPLETE_STATE_BYTES, false, signal);
		} catch {
			signal.throwIfAborted();
			state = await readPublicationRecord(destination, limits, signal);
			if (state.kind !== "incomplete") return "exists";
		}
		state = await readPublicationRecord(destination, limits, signal);
		if (state.kind !== "incomplete") return "exists";
		destinationKind = await pathKind(destination, signal);
	}
	if (state.kind !== "incomplete") return "exists";
	if (destinationKind === "directory") {
		return await openPublicationDraft(destination, parent, limits, signal);
	}
	if (destinationKind !== "missing") return "exists";
	await assertDirectoryIdentity(parent, "installation_conflict", signal);
	try {
		await mkdir(destination, { mode: 0o700 });
	} catch (error) {
		signal.throwIfAborted();
		if (isNodeError(error, "EEXIST")) return await openPublicationDraft(destination, parent, limits, signal);
		throw new AgentPluginPackageError("operation_incomplete");
	}

	let draft: PublicationDraft | undefined;
	try {
		const identity = await captureDirectoryIdentityUninterruptible(destination, limits);
		draft = { ...identity, statePath, committed: false };
		if (process.platform !== "win32") await chmod(destination, 0o755);
		await syncDirectory(destination);
		signal.throwIfAborted();
		await assertDirectoryIdentity(draft, "operation_incomplete", signal);
		await assertDirectoryIdentity(parent, "installation_conflict", signal);
		return draft;
	} catch (error) {
		const aborted = signal.aborted;
		const abortReason: unknown = signal.reason;
		if (aborted) throw abortReason;
		if (error instanceof AgentPluginPackageError) throw error;
		throw new AgentPluginPackageError("operation_incomplete");
	}
}

async function openPublicationDraft(
	destination: string,
	parent: DirectoryIdentity,
	limits: AgentPluginPackageLimits,
	signal: AbortSignal,
): Promise<PublicationDraft> {
	if (!sameAddressedPath(dirname(destination), parent.addressedPath)) {
		throw new AgentPluginPackageError("installation_conflict");
	}
	await assertDirectoryIdentity(parent, "installation_conflict", signal);
	const identity = await captureDirectoryIdentity(destination, limits, "installation_conflict", signal);
	const rootInfo = await safeLstat(destination, "installation_conflict", signal);
	if (process.platform !== "win32" && ![0o555, 0o755].includes(rootInfo.mode & 0o777)) {
		throw new AgentPluginPackageError("installation_conflict");
	}
	const statePath = agentPluginPackagePublicationStatePath(destination);
	const state = await readPublicationRecord(destination, limits, signal);
	if (state.kind !== "incomplete") throw new AgentPluginPackageError("installation_conflict");
	await assertDirectoryIdentity(identity, "installation_conflict", signal);
	await assertDirectoryIdentity(parent, "installation_conflict", signal);
	return { ...identity, statePath, committed: false };
}

function assertPublicationPaths(
	destination: string,
	portableFilePaths: readonly string[],
	limits: AgentPluginPackageLimits,
): void {
	const statePath = agentPluginPackagePublicationStatePath(destination);
	assertAddressedPath(statePath, limits);
	assertAddressedPath(agentPluginPackagePublicationNextStatePath(destination), limits);
	for (const portablePath of portableFilePaths) {
		assertAddressedPath(join(destination, ...portablePath.split("/")), limits);
	}
}

async function completePublicationDirectory(
	draft: PublicationDraft,
	parent: DirectoryIdentity,
	portableFilePaths: readonly string[],
	packageDigest: string,
	limits: AgentPluginPackageLimits,
	signal: AbortSignal,
): Promise<void> {
	await assertDirectoryIdentity(draft, "operation_incomplete", signal);
	await assertDirectoryIdentity(parent, "installation_conflict", signal);
	const publicationDirectories = directoryPaths(draft.addressedPath, portableFilePaths).sort(
		(left, right) => right.split(sep).length - left.split(sep).length,
	);
	for (const directory of publicationDirectories) {
		signal.throwIfAborted();
		if (process.platform !== "win32") await chmod(directory, 0o555);
		await syncDirectory(directory);
	}
	await assertDirectoryIdentity(draft, "operation_incomplete", signal);
	await assertDirectoryIdentity(parent, "installation_conflict", signal);
	signal.throwIfAborted();
	const completeText = `complete:${packageDigest}:${String(draft.device)}:${String(draft.inode)}`;
	const completeBytes = encoder.encode(completeText);
	if (completeBytes.byteLength > AGENT_PLUGIN_PACKAGE_PUBLICATION_STATE_MAX_BYTES) {
		throw new AgentPluginPackageError("operation_incomplete");
	}
	const nextStatePath = agentPluginPackagePublicationNextStatePath(draft.addressedPath);
	await writePublicationFile(nextStatePath, completeBytes, false, signal);
	await assertDirectoryIdentity(draft, "operation_incomplete", signal);
	await assertDirectoryIdentity(parent, "installation_conflict", signal);
	signal.throwIfAborted();
	try {
		if (process.platform === "win32") await chmod(draft.statePath, 0o600);
		await rename(nextStatePath, draft.statePath);
	} catch {
		signal.throwIfAborted();
		const observed = await readPublicationRecord(draft.addressedPath, limits, signal);
		assertCompletePublicationRecord(observed, packageDigest, draft, "operation_incomplete");
	}
	await chmod(draft.statePath, 0o444).catch(() => undefined);
	draft.committed = true;
	await syncDirectory(parent.addressedPath);
	await assertDirectoryIdentity(draft, "operation_incomplete", NEVER_ABORT_SIGNAL);
	await assertDirectoryIdentity(parent, "installation_conflict", NEVER_ABORT_SIGNAL);
}

function directoryPaths(root: string, portableFilePaths: readonly string[]): string[] {
	const directories = new Set<string>([root]);
	for (const portablePath of portableFilePaths) {
		let current = root;
		const segments = portablePath.split("/");
		for (let index = 0; index < segments.length - 1; index += 1) {
			const segment = segments[index];
			if (segment === undefined) continue;
			current = join(current, segment);
			directories.add(current);
		}
	}
	return [...directories];
}

async function createDirectory(path: string): Promise<void> {
	try {
		await mkdir(path, { mode: 0o755 });
		if (process.platform !== "win32") await chmod(path, 0o755);
		await syncDirectory(dirname(path));
	} catch (error) {
		if (isNodeError(error, "EEXIST")) {
			try {
				const info = await lstat(path);
				if (
					!info.isDirectory() ||
					info.isSymbolicLink() ||
					(process.platform !== "win32" && ![0o555, 0o755].includes(info.mode & 0o777))
				) {
					throw new Error("invalid_existing_directory");
				}
				return;
			} catch {
				throw new AgentPluginPackageError("operation_incomplete");
			}
		}
		throw new AgentPluginPackageError("operation_incomplete");
	}
}

async function writePublicationFile(
	path: string,
	bytes: Uint8Array,
	executable: boolean,
	signal: AbortSignal,
): Promise<void> {
	signal.throwIfAborted();
	let handle: FileHandle;
	try {
		handle = await open(path, "wx", 0o600);
	} catch (error) {
		signal.throwIfAborted();
		if (isNodeError(error, "EEXIST")) {
			await verifyExistingPublicationFile(path, bytes, executable, signal);
			return;
		}
		throw new AgentPluginPackageError("operation_incomplete");
	}
	try {
		let offset = 0;
		while (offset < bytes.byteLength) {
			const result = await handle.write(bytes, offset, bytes.byteLength - offset, offset);
			if (result.bytesWritten < 1) throw new AgentPluginPackageError("operation_incomplete");
			offset += result.bytesWritten;
		}
		await handle.chmod(process.platform === "win32" ? 0o444 : executable ? 0o555 : 0o444);
		await handle.sync();
	} catch (error) {
		if (error instanceof AgentPluginPackageError) throw error;
		throw new AgentPluginPackageError("operation_incomplete");
	} finally {
		await handle.close().catch(() => undefined);
	}
	await syncDirectory(dirname(path));
	signal.throwIfAborted();
}

async function verifyExistingPublicationFile(
	path: string,
	expectedBytes: Uint8Array,
	executable: boolean,
	signal: AbortSignal,
): Promise<void> {
	try {
		const file = await readStableFile(path, Math.max(1, expectedBytes.byteLength), signal);
		let info = await safeLstat(path, "operation_incomplete", signal);
		if (!sameBytes(file.bytes, expectedBytes) || !info.isFile() || info.isSymbolicLink()) {
			throw new AgentPluginPackageError("operation_incomplete");
		}
		if (process.platform === "win32") {
			if ((info.mode & 0o222) !== 0) await chmod(path, 0o444);
			info = await safeLstat(path, "operation_incomplete", signal);
			if (!info.isFile() || info.isSymbolicLink() || (info.mode & 0o222) !== 0) {
				throw new AgentPluginPackageError("operation_incomplete");
			}
		} else if ((info.mode & 0o777) !== (executable ? 0o555 : 0o444)) {
			throw new AgentPluginPackageError("operation_incomplete");
		}
	} catch (error) {
		signal.throwIfAborted();
		if (error instanceof AgentPluginPackageError) throw error;
		throw new AgentPluginPackageError("operation_incomplete");
	}
}

async function ensureStoreRoot(
	root: string,
	limits: AgentPluginPackageLimits,
	signal: AbortSignal,
): Promise<DirectoryIdentity> {
	const parent = await captureDirectoryIdentity(dirname(root), limits, "invalid_configuration", signal);
	try {
		await mkdir(root, { mode: 0o755 });
		if (process.platform !== "win32") await chmod(root, 0o755);
	} catch (error) {
		if (!isNodeError(error, "EEXIST")) throw new AgentPluginPackageError("operation_incomplete");
	}
	await assertDirectoryIdentity(parent, "installation_conflict", signal);
	const identity = await captureDirectoryIdentity(root, limits, "installation_conflict", signal);
	await syncDirectory(parent.addressedPath);
	await assertDirectoryIdentity(parent, "installation_conflict", signal);
	await assertDirectoryIdentity(identity, "installation_conflict", signal);
	return identity;
}

async function captureDirectoryIdentity(
	path: string,
	limits: AgentPluginPackageLimits,
	failureCode: AgentPluginPackageErrorCode,
	signal: AbortSignal,
): Promise<DirectoryIdentity> {
	assertAddressedPath(path, limits);
	const addressed = await safeLstatIdentity(path, failureCode, signal);
	if (!addressed.isDirectory() || addressed.isSymbolicLink()) {
		throw new AgentPluginPackageError(failureCode);
	}
	const canonical = await safeRealpath(path, failureCode, signal);
	if (!sameAddressedPath(canonical, path)) throw new AgentPluginPackageError(failureCode);
	return {
		addressedPath: path,
		canonicalPath: canonical,
		device: addressed.dev,
		inode: addressed.ino,
	};
}

async function captureDirectoryIdentityUninterruptible(
	path: string,
	limits: AgentPluginPackageLimits,
): Promise<DirectoryIdentity> {
	assertAddressedPath(path, limits);
	try {
		const addressed = await lstat(path, { bigint: true });
		if (!addressed.isDirectory() || addressed.isSymbolicLink()) throw new Error("not_directory");
		const canonical = resolve(await realpath(path));
		if (!sameAddressedPath(canonical, path)) throw new Error("retargeted");
		return {
			addressedPath: path,
			canonicalPath: canonical,
			device: addressed.dev,
			inode: addressed.ino,
		};
	} catch (error) {
		if (error instanceof AgentPluginPackageError) throw error;
		throw new AgentPluginPackageError("operation_incomplete");
	}
}

async function assertDirectoryIdentity(
	identity: DirectoryIdentity,
	failureCode: AgentPluginPackageErrorCode,
	signal: AbortSignal,
): Promise<void> {
	const addressed = await safeLstatIdentity(identity.addressedPath, failureCode, signal);
	if (
		!addressed.isDirectory() ||
		addressed.isSymbolicLink() ||
		addressed.dev !== identity.device ||
		addressed.ino !== identity.inode
	) {
		throw new AgentPluginPackageError(failureCode);
	}
	const canonical = await safeRealpath(identity.addressedPath, failureCode, signal);
	if (canonical !== identity.canonicalPath) throw new AgentPluginPackageError(failureCode);
}

async function pathKind(path: string, signal: AbortSignal): Promise<"missing" | "directory" | "other"> {
	signal.throwIfAborted();
	try {
		const info = await lstat(path);
		signal.throwIfAborted();
		if (info.isDirectory() && !info.isSymbolicLink()) return "directory";
		return "other";
	} catch (error) {
		signal.throwIfAborted();
		if (isNodeError(error, "ENOENT")) return "missing";
		throw error;
	}
}

async function safeLstat(path: string, failureCode: AgentPluginPackageErrorCode, signal: AbortSignal): Promise<Stats> {
	signal.throwIfAborted();
	try {
		const result = await lstat(path);
		signal.throwIfAborted();
		return result;
	} catch {
		signal.throwIfAborted();
		throw new AgentPluginPackageError(failureCode);
	}
}

async function safeLstatIdentity(
	path: string,
	failureCode: AgentPluginPackageErrorCode,
	signal: AbortSignal,
): Promise<BigIntStats> {
	signal.throwIfAborted();
	try {
		const result = await lstat(path, { bigint: true });
		signal.throwIfAborted();
		return result;
	} catch {
		signal.throwIfAborted();
		throw new AgentPluginPackageError(failureCode);
	}
}

async function safeRealpath(
	path: string,
	failureCode: AgentPluginPackageErrorCode,
	signal: AbortSignal,
): Promise<string> {
	signal.throwIfAborted();
	try {
		const result = resolve(await realpath(path));
		signal.throwIfAborted();
		return result;
	} catch {
		signal.throwIfAborted();
		throw new AgentPluginPackageError(failureCode);
	}
}

function requiredPluginFile(
	files: readonly ScannedFile[],
	path: string,
	failureCode: AgentPluginPackageErrorCode,
): ScannedFile {
	const file = files.find((candidate) => candidate.path === path);
	if (file === undefined) throw new AgentPluginPackageError(failureCode);
	return file;
}

function requiredMetadataFile(tree: ScannedTree, name: string): ScannedFile {
	const file = tree.metadataFiles.get(name);
	if (file === undefined) throw new AgentPluginPackageError("package_invalid");
	return file;
}

function decodeCanonicalText(bytes: Uint8Array, failureCode: AgentPluginPackageErrorCode): string {
	try {
		const text = decoder.decode(bytes);
		if (!sameBytes(bytes, encoder.encode(text))) throw new Error("non_canonical_utf8");
		return text;
	} catch {
		throw new AgentPluginPackageError(failureCode);
	}
}

function snapshotSignature(value: unknown): Uint8Array {
	try {
		const bytes = value as Uint8Array;
		const buffer = TYPED_ARRAY_BUFFER.call(bytes);
		const byteLength = TYPED_ARRAY_BYTE_LENGTH.call(bytes);
		const byteOffset = TYPED_ARRAY_BYTE_OFFSET.call(bytes);
		if (byteLength !== 64) throw new Error("invalid_length");
		const snapshot = new Uint8Array(byteLength);
		TYPED_ARRAY_SET.call(snapshot, new Uint8Array(buffer, byteOffset, byteLength));
		return snapshot;
	} catch {
		throw new AgentPluginPackageError("signature_invalid");
	}
}

function concatenate(left: Uint8Array, right: Uint8Array): Uint8Array {
	const result = new Uint8Array(left.byteLength + right.byteLength);
	result.set(left, 0);
	result.set(right, left.byteLength);
	return result;
}

function sameBytes(left: Uint8Array, right: Uint8Array): boolean {
	if (left.byteLength !== right.byteLength) return false;
	for (let index = 0; index < left.byteLength; index += 1) {
		if (left[index] !== right[index]) return false;
	}
	return true;
}

function assertPortablePath(
	value: string,
	limits: AgentPluginPackageLimits,
	failureCode: AgentPluginPackageErrorCode,
): void {
	if (
		value.length === 0 ||
		value !== value.normalize("NFC") ||
		value.includes("\\") ||
		value.includes(":") ||
		/[<>"|?*]/u.test(value) ||
		value.startsWith("/") ||
		isAbsolute(value) ||
		hasControl(value) ||
		encoder.encode(value).byteLength > limits.maxPortablePathBytes
	) {
		throw new AgentPluginPackageError(failureCode);
	}
	const segments = value.split("/");
	if (
		segments.some(
			(segment) =>
				segment.length === 0 ||
				encoder.encode(segment).byteLength > limits.maxPortablePathSegmentBytes ||
				segment === "." ||
				segment === ".." ||
				segment.endsWith(".") ||
				segment.endsWith(" ") ||
				WINDOWS_RESERVED_SEGMENT.test(segment),
		)
	) {
		throw new AgentPluginPackageError(failureCode);
	}
}

function assertAddressedPath(path: string, limits: AgentPluginPackageLimits): void {
	if (encoder.encode(path).byteLength > limits.maxAddressedPathBytes) {
		throw new AgentPluginPackageError("package_limit_exceeded");
	}
}

function assertSealedDirectoryMode(
	info: Stats | BigIntStats,
	mode: ScanMode,
	failureCode: AgentPluginPackageErrorCode,
): void {
	if (process.platform === "win32" || mode === "source") return;
	const observed = fileMode(info);
	if (isDraftMode(mode)) {
		if (observed !== 0o555 && observed !== 0o755) throw new AgentPluginPackageError(failureCode);
		return;
	}
	if (observed !== 0o555) throw new AgentPluginPackageError(failureCode);
}

function fileMode(info: Stats | BigIntStats): number {
	return typeof info.mode === "bigint" ? Number(info.mode & 0o777n) : info.mode & 0o777;
}

function assertSealedFileMode(
	file: ScannedFile,
	executable: boolean,
	mode: ScanMode,
	failureCode: AgentPluginPackageErrorCode,
): void {
	if (mode === "source") return;
	if (
		process.platform === "win32" ? (file.permissions & 0o222) !== 0 : file.permissions !== (executable ? 0o555 : 0o444)
	) {
		throw new AgentPluginPackageError(failureCode);
	}
}

function isDraftMode(mode: ScanMode): mode is "package-draft" | "installed-draft" {
	return mode === "package-draft" || mode === "installed-draft";
}

function isPackageMode(mode: ScanMode): mode is "package-draft" | "package" {
	return mode === "package-draft" || mode === "package";
}

function assertReleaseSequence(
	value: string,
	limits: AgentPluginPackageLimits,
	failureCode: AgentPluginPackageErrorCode = "invalid_configuration",
): void {
	if (!POSITIVE_DECIMAL.test(value) || encoder.encode(value).byteLength > limits.maxReleaseSequenceBytes) {
		throw new AgentPluginPackageError(failureCode);
	}
}

function assertKeyId(value: string, limits: AgentPluginPackageLimits, failureCode: AgentPluginPackageErrorCode): void {
	if (!isSafeText(value) || encoder.encode(value).byteLength > limits.maxKeyIdBytes) {
		throw new AgentPluginPackageError(failureCode);
	}
}

function isSafeText(value: string): boolean {
	return value.length > 0 && value === value.normalize("NFC") && !hasControl(value);
}

function hasControl(value: string): boolean {
	return [...value].some((character) => {
		const code = character.codePointAt(0) ?? 0;
		return code <= 31 || (code >= 127 && code <= 159);
	});
}

function portableCollisionKey(value: string): string {
	return value.toLowerCase();
}

function comparePositiveDecimals(left: string, right: string): number {
	if (left.length !== right.length) return left.length < right.length ? -1 : 1;
	return compareStrings(left, right);
}

function compareStrings(left: string, right: string): number {
	return left < right ? -1 : left > right ? 1 : 0;
}

function sha256(bytes: Uint8Array): string {
	return createHash("sha256").update(bytes).digest("hex");
}

function overlaps(left: string, right: string): boolean {
	return isContained(left, right) || isContained(right, left);
}

function isContained(base: string, target: string): boolean {
	const child = relative(resolve(base), resolve(target));
	return child === "" || (child !== ".." && !child.startsWith(`..${sep}`) && !isAbsolute(child));
}

function hasExactKeys(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
	if (!isRecord(value)) return false;
	const observed = Object.keys(value).sort(compareStrings);
	const expected = [...keys].sort(compareStrings);
	return observed.length === expected.length && observed.every((key, index) => key === expected[index]);
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isEd25519PublicKey(value: unknown): value is KeyObject {
	try {
		return value instanceof KeyObject && value.type === "public" && value.asymmetricKeyType === "ed25519";
	} catch {
		return false;
	}
}

function samePublicKey(left: KeyObject, right: KeyObject): boolean {
	try {
		return left.equals(right);
	} catch {
		throw new AgentPluginPackageError("invalid_configuration");
	}
}

function sameAddressedPath(left: string, right: string): boolean {
	return process.platform === "win32" ? left.toLowerCase() === resolve(right).toLowerCase() : left === resolve(right);
}

async function syncDirectory(path: string): Promise<void> {
	// Pure Node cannot request directory-entry fsync on Windows; every file is still flushed before publication.
	if (process.platform === "win32") return;
	const handle = await open(path, "r");
	try {
		await handle.sync();
	} finally {
		await handle.close();
	}
}

function isNodeError(error: unknown, code: string): boolean {
	return isRecord(error) && error.code === code;
}

function assertSignal(signal: AbortSignal): void {
	if (!signal || typeof signal !== "object" || typeof signal.throwIfAborted !== "function") {
		throw new AgentPluginPackageError("invalid_configuration");
	}
}

function deepFreeze<T>(value: T): T {
	if (typeof value !== "object" || value === null || Object.isFrozen(value)) return value;
	Object.freeze(value);
	for (const child of Array.isArray(value) ? value : Object.values(value)) deepFreeze(child);
	return value;
}
