import { basename, isAbsolute, relative, resolve, sep } from "node:path";
import {
	BACKGROUND_CONTEXT,
	type ExecutionEnv,
	ExecutionError,
	FileError,
	type FileInfo,
	type Context as HarnessContext,
	type Result,
	type ShellExecOptions,
	type ShellExecResult,
	withAbortSignal,
} from "@earendil-works/pi-agent-core";
import { NodeExecutionEnv } from "@earendil-works/pi-agent-core/node";
import { BoundedFileReadError, readStableTextFile } from "./bounded-file.js";
import { DirectoryGameSkillError, type DirectoryGameSkillErrorCode } from "./errors.js";
import type { DirectoryGameSkillLimits, DirectoryGameSkillRoot } from "./types.js";

const encoder = new TextEncoder();
const ignoreFiles = new Set([".gitignore", ".ignore", ".fdignore"]);

export interface ResolvedDirectoryGameSkillRoot extends DirectoryGameSkillRoot {
	readonly addressedPath: string;
	readonly canonicalPath?: string;
}

interface SkillReadBudget {
	directories: number;
	visitedEntries: number;
	readFiles: number;
	readBytes: number;
}

export async function createBoundedSkillExecutionEnv(
	cwd: string,
	roots: readonly DirectoryGameSkillRoot[],
	limits: DirectoryGameSkillLimits,
	signal: AbortSignal,
): Promise<{
	readonly base: ExecutionEnv;
	readonly context: HarnessContext;
	readonly env: BoundedSkillExecutionEnv;
	readonly roots: readonly ResolvedDirectoryGameSkillRoot[];
}> {
	const nodeEnv = new NodeExecutionEnv({ cwd });
	const base: ExecutionEnv = nodeEnv;
	const context = withAbortSignal(signal, BACKGROUND_CONTEXT);
	const resolvedRoots: ResolvedDirectoryGameSkillRoot[] = [];
	for (const root of roots) {
		signal.throwIfAborted();
		const absoluteResult = await base.absolutePath(root.path, context);
		if (!absoluteResult.ok) throw new DirectoryGameSkillError("invalid_configuration");
		const addressedPath = resolve(absoluteResult.value);
		if (encoder.encode(addressedPath).byteLength > limits.maxAddressedPathBytes) {
			throw new DirectoryGameSkillError("catalog_limit_exceeded");
		}

		let canonicalPath: string | undefined;
		const info = await base.fileInfo(addressedPath, context);
		if (info.ok) {
			const canonical = await base.canonicalPath(addressedPath, context);
			if (canonical.ok) canonicalPath = resolve(canonical.value);
		}
		resolvedRoots.push({
			rootId: root.rootId,
			path: root.path,
			addressedPath,
			...(canonicalPath === undefined ? {} : { canonicalPath }),
		});
	}

	for (let index = 0; index < resolvedRoots.length; index += 1) {
		const left = resolvedRoots[index];
		if (!left) continue;
		for (let otherIndex = index + 1; otherIndex < resolvedRoots.length; otherIndex += 1) {
			const right = resolvedRoots[otherIndex];
			if (!right) continue;
			if (
				isContained(left.addressedPath, right.addressedPath) ||
				isContained(right.addressedPath, left.addressedPath) ||
				(left.canonicalPath !== undefined &&
					right.canonicalPath !== undefined &&
					(isContained(left.canonicalPath, right.canonicalPath) ||
						isContained(right.canonicalPath, left.canonicalPath)))
			) {
				throw new DirectoryGameSkillError("invalid_configuration");
			}
		}
	}

	return {
		base,
		context,
		roots: resolvedRoots,
		env: new BoundedSkillExecutionEnv(base, resolvedRoots, limits, signal),
	};
}

export class BoundedSkillExecutionEnv implements ExecutionEnv {
	readonly cwd: string;
	private readonly budget: SkillReadBudget = { directories: 0, visitedEntries: 0, readFiles: 0, readBytes: 0 };
	private readonly textByCanonicalPath = new Map<string, string>();
	private fatalError: DirectoryGameSkillError | undefined;

	constructor(
		private readonly base: ExecutionEnv,
		private readonly roots: readonly ResolvedDirectoryGameSkillRoot[],
		private readonly limits: DirectoryGameSkillLimits,
		private readonly signal: AbortSignal,
	) {
		this.cwd = base.cwd;
	}

	throwIfFatal(): void {
		if (this.fatalError) throw this.fatalError;
		this.signal.throwIfAborted();
	}

	readCachedText(canonicalPath: string): string | undefined {
		return this.textByCanonicalPath.get(resolve(canonicalPath));
	}

	absolutePath(path: string, context: HarnessContext): Promise<Result<string, FileError>> {
		const aborted = this.abortFailure(path, context);
		if (aborted) return Promise.resolve(aborted);
		const absolute = resolve(this.cwd, path);
		return Promise.resolve(this.admitAddressedPath(absolute, path));
	}

	joinPath(parts: string[], context: HarnessContext): Promise<Result<string, FileError>> {
		const addressed = parts[0] ?? this.cwd;
		const aborted = this.abortFailure(addressed, context);
		if (aborted) return Promise.resolve(aborted);
		const joined = resolve(addressed, ...parts.slice(1));
		return Promise.resolve(this.admitAddressedPath(joined, joined));
	}

	async readTextFile(path: string, context: HarnessContext): Promise<Result<string, FileError>> {
		const aborted = this.abortFailure(path, context);
		if (aborted) return aborted;
		const admitted = await this.admitCanonicalPath(path, context);
		if (!admitted.ok) return admitted;
		const info = await this.base.fileInfo(admitted.value, context);
		if (!info.ok) return info;
		if (info.value.kind !== "file") {
			return this.fileFailure("invalid", "Skill discovery can read only regular files.", path);
		}
		const fileLimit = ignoreFiles.has(basename(path)) ? this.limits.maxIgnoreFileBytes : this.limits.maxSkillFileBytes;
		if (info.value.size > fileLimit || this.budget.readFiles >= this.limits.maxReadFiles) {
			return this.fatalFileFailure("catalog_limit_exceeded", path);
		}
		this.budget.readFiles += 1;
		let content: string;
		let bytes: number;
		try {
			const stable = await readStableTextFile(admitted.value, fileLimit, this.effectiveSignal(context));
			if (!samePath(stable.canonicalPath, admitted.value)) {
				return this.fatalFileFailure("root_escape", path);
			}
			content = stable.text;
			bytes = stable.bytes;
		} catch (error) {
			if (error instanceof BoundedFileReadError) {
				if (error.code === "limit_exceeded") return this.fatalFileFailure("catalog_limit_exceeded", path);
				if (error.code === "changed") return this.fatalFileFailure("root_escape", path);
				return this.fileFailure("unknown", "Skill file could not be read.", path);
			}
			throw error;
		}
		const nextBytes = this.budget.readBytes + bytes;
		if (!Number.isSafeInteger(nextBytes) || bytes > fileLimit || nextBytes > this.limits.maxTotalReadBytes) {
			return this.fatalFileFailure("catalog_limit_exceeded", path);
		}
		this.budget.readBytes = nextBytes;
		this.textByCanonicalPath.set(resolve(admitted.value), content);
		return { ok: true, value: content };
	}

	readTextLines(
		path: string,
		_options: { maxLines?: number } | undefined,
		_context: HarnessContext,
	): Promise<Result<string[], FileError>> {
		return Promise.resolve(this.unsupportedFileOperation(path));
	}

	readBinaryFile(path: string, _context: HarnessContext): Promise<Result<Uint8Array, FileError>> {
		return Promise.resolve(this.unsupportedFileOperation(path));
	}

	writeFile(path: string, _content: string | Uint8Array, _context: HarnessContext): Promise<Result<void, FileError>> {
		return Promise.resolve(this.unsupportedFileOperation(path));
	}

	appendFile(path: string, _content: string | Uint8Array, _context: HarnessContext): Promise<Result<void, FileError>> {
		return Promise.resolve(this.unsupportedFileOperation(path));
	}

	renameFile(sourcePath: string, _destinationPath: string, _context: HarnessContext): Promise<Result<void, FileError>> {
		return Promise.resolve(this.unsupportedFileOperation(sourcePath));
	}

	async fileInfo(path: string, context: HarnessContext): Promise<Result<FileInfo, FileError>> {
		const aborted = this.abortFailure(path, context);
		if (aborted) return aborted;
		const admitted = this.admitAddressedPath(resolve(this.cwd, path), path);
		if (!admitted.ok) return admitted;
		return await this.base.fileInfo(admitted.value, context);
	}

	async listDir(path: string, context: HarnessContext): Promise<Result<FileInfo[], FileError>> {
		const aborted = this.abortFailure(path, context);
		if (aborted) return aborted;
		const addressed = resolve(this.cwd, path);
		const boundary = this.findBoundary(addressed);
		if (!boundary || !this.pathWithinBudget(addressed)) return this.fatalFileFailure("root_escape", path);
		const depth = pathDepth(boundary.addressedBase, addressed);
		if (depth === undefined || depth > this.limits.maxTraversalDepth) {
			return this.fatalFileFailure("catalog_limit_exceeded", path);
		}
		const admitted = await this.admitCanonicalPath(addressed, context);
		if (!admitted.ok) return admitted;
		if (this.budget.directories >= this.limits.maxDirectories) {
			return this.fatalFileFailure("catalog_limit_exceeded", path);
		}
		this.budget.directories += 1;
		const entries = await this.base.listDir(addressed, context);
		if (!entries.ok) return entries;
		const nextEntries = this.budget.visitedEntries + entries.value.length;
		if (
			entries.value.length > this.limits.maxEntriesPerDirectory ||
			!Number.isSafeInteger(nextEntries) ||
			nextEntries > this.limits.maxVisitedEntries
		) {
			return this.fatalFileFailure("catalog_limit_exceeded", path);
		}
		for (const entry of entries.value) {
			if (!this.pathWithinBudget(entry.path) || !isContained(addressed, entry.path)) {
				return this.fatalFileFailure("root_escape", path);
			}
		}
		this.budget.visitedEntries = nextEntries;
		return entries;
	}

	async canonicalPath(path: string, context: HarnessContext): Promise<Result<string, FileError>> {
		const aborted = this.abortFailure(path, context);
		if (aborted) return aborted;
		return await this.admitCanonicalPath(path, context);
	}

	async exists(path: string, context: HarnessContext): Promise<Result<boolean, FileError>> {
		const info = await this.fileInfo(path, context);
		if (info.ok) return { ok: true, value: true };
		if (info.error.code === "not_found") return { ok: true, value: false };
		return info;
	}

	createDir(
		path: string,
		_options: { recursive?: boolean } | undefined,
		_context: HarnessContext,
	): Promise<Result<void, FileError>> {
		return Promise.resolve(this.unsupportedFileOperation(path));
	}

	remove(
		path: string,
		_options: { recursive?: boolean; force?: boolean } | undefined,
		_context: HarnessContext,
	): Promise<Result<void, FileError>> {
		return Promise.resolve(this.unsupportedFileOperation(path));
	}

	createTempDir(_prefix: string | undefined, _context: HarnessContext): Promise<Result<string, FileError>> {
		return Promise.resolve(this.unsupportedFileOperation(this.cwd));
	}

	createTempFile(
		_options: { prefix?: string; suffix?: string } | undefined,
		_context: HarnessContext,
	): Promise<Result<string, FileError>> {
		return Promise.resolve(this.unsupportedFileOperation(this.cwd));
	}

	exec(
		_command: string,
		_options: ShellExecOptions | undefined,
		_context: HarnessContext,
	): Promise<Result<ShellExecResult, ExecutionError>> {
		this.setFatal("unsupported_discovery_operation");
		return Promise.resolve({
			ok: false,
			error: new ExecutionError("shell_unavailable", "Skill discovery cannot execute processes."),
		});
	}

	async cleanup(context: HarnessContext): Promise<void> {
		await this.base.cleanup(context);
	}

	private async admitCanonicalPath(path: string, context: HarnessContext): Promise<Result<string, FileError>> {
		const addressed = resolve(this.cwd, path);
		const boundary = this.findBoundary(addressed);
		if (!boundary || !this.pathWithinBudget(addressed)) return this.fatalFileFailure("root_escape", path);
		const canonical = await this.base.canonicalPath(addressed, context);
		if (!canonical.ok) return canonical;
		const canonicalPath = resolve(canonical.value);
		if (
			!this.pathWithinBudget(canonicalPath) ||
			boundary.root.canonicalPath === undefined ||
			!isContained(boundary.root.canonicalPath, canonicalPath)
		) {
			return this.fatalFileFailure("root_escape", path);
		}
		return { ok: true, value: canonicalPath };
	}

	private admitAddressedPath(absolute: string, original: string): Result<string, FileError> {
		if (!this.findBoundary(absolute) || !this.pathWithinBudget(absolute)) {
			return this.fatalFileFailure("root_escape", original);
		}
		return { ok: true, value: absolute };
	}

	private findBoundary(path: string): { root: ResolvedDirectoryGameSkillRoot; addressedBase: string } | undefined {
		let winner: { root: ResolvedDirectoryGameSkillRoot; addressedBase: string } | undefined;
		for (const root of this.roots) {
			for (const base of [root.addressedPath, root.canonicalPath]) {
				if (base === undefined || !isContained(base, path)) continue;
				if (!winner || base.length > winner.addressedBase.length) winner = { root, addressedBase: base };
			}
		}
		return winner;
	}

	private pathWithinBudget(path: string): boolean {
		return encoder.encode(path).byteLength <= this.limits.maxAddressedPathBytes;
	}

	private effectiveSignal(context: HarnessContext): AbortSignal {
		return context.abortSignal ?? this.signal;
	}

	private abortFailure(path: string, context: HarnessContext): Result<never, FileError> | undefined {
		const effective = this.effectiveSignal(context);
		if (!effective.aborted) return undefined;
		return this.fileFailure("aborted", "Skill discovery was aborted.", path);
	}

	private unsupportedFileOperation<T>(path: string): Result<T, FileError> {
		this.setFatal("unsupported_discovery_operation");
		return this.fileFailure("not_supported", "Skill discovery is read-only.", path);
	}

	private fatalFileFailure<T>(code: DirectoryGameSkillErrorCode, path: string): Result<T, FileError> {
		this.setFatal(code);
		return this.fileFailure(code === "root_escape" ? "permission_denied" : "invalid", code, path);
	}

	private fileFailure<T>(code: FileError["code"], message: string, path: string): Result<T, FileError> {
		return { ok: false, error: new FileError(code, message, path) };
	}

	private setFatal(code: DirectoryGameSkillErrorCode): void {
		this.fatalError ??= new DirectoryGameSkillError(code);
	}
}

function isContained(base: string, target: string): boolean {
	const child = relative(base, target);
	return child === "" || (child !== ".." && !child.startsWith(`..${sep}`) && !isAbsolute(child));
}

function samePath(left: string, right: string): boolean {
	return relative(left, right) === "" && relative(right, left) === "";
}

function pathDepth(base: string, target: string): number | undefined {
	const child = relative(base, target);
	if (child === "") return 0;
	if (child === ".." || child.startsWith(`..${sep}`) || isAbsolute(child)) return undefined;
	return child.split(sep).length;
}
