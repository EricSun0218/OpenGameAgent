import { Entry } from "@napi-rs/keyring";
import type { GameProviderCredentialSource } from "@opengameagent/kernel-pi";

export interface GameProviderCredential {
	key?: string;
	environment?: Readonly<Record<string, string>>;
}

export interface StoredGameProviderCredential {
	providerId: string;
	revision: number;
	credential: GameProviderCredential;
}

export interface GameProviderCredentialWriteResult {
	changed: boolean;
	revision: number;
}

export interface GameKeyringEntry {
	getPassword(): string | null;
	setPassword(password: string): void;
}

export interface KeyringGameProviderCredentialStoreOptions {
	service?: string;
	maximumCredentialCharacters?: number;
	entryFactory?: (service: string, account: string) => GameKeyringEntry;
}

interface ActiveCredentialEnvelope {
	version: 1;
	providerId: string;
	revision: number;
	credential: GameProviderCredential;
	deleted?: false;
}

interface DeletedCredentialEnvelope {
	version: 1;
	providerId: string;
	revision: number;
	deleted: true;
}

type CredentialEnvelope = ActiveCredentialEnvelope | DeletedCredentialEnvelope;

function validateIdentifier(value: string, name: string, maximum = 128): void {
	if (!/^[a-z0-9][a-z0-9._-]*$/iu.test(value) || value.length > maximum) throw new TypeError(`${name} is invalid.`);
}

function normalizeCredential(value: GameProviderCredential, maximumCharacters: number): GameProviderCredential {
	if (value.key === undefined && value.environment === undefined) throw new TypeError("Credential is empty.");
	if (value.key !== undefined && (value.key.length < 1 || value.key.length > maximumCharacters)) {
		throw new RangeError("Credential key is invalid.");
	}
	const environmentEntries = Object.entries(value.environment ?? {});
	if (environmentEntries.length > 32) throw new RangeError("Credential environment has too many fields.");
	for (const [name, item] of environmentEntries) {
		if (!/^[A-Z_][A-Z0-9_]{0,127}$/u.test(name) || item.length < 1 || item.length > maximumCharacters) {
			throw new TypeError("Credential environment is invalid.");
		}
	}
	const credential: GameProviderCredential = {
		...(value.key === undefined ? {} : { key: value.key }),
		...(environmentEntries.length === 0
			? {}
			: { environment: Object.fromEntries(environmentEntries.sort(([left], [right]) => left.localeCompare(right))) }),
	};
	if (JSON.stringify(credential).length > maximumCharacters) throw new RangeError("Credential is too large.");
	return credential;
}

function sameCredential(left: GameProviderCredential, right: GameProviderCredential): boolean {
	return JSON.stringify(left) === JSON.stringify(right);
}

class KeyedQueue {
	private readonly tails = new Map<string, Promise<void>>();

	async run<T>(key: string, operation: () => Promise<T>): Promise<T> {
		const previous = this.tails.get(key) ?? Promise.resolve();
		let release: () => void = () => {};
		const current = new Promise<void>((resolve) => {
			release = resolve;
		});
		const tail = previous.catch(() => undefined).then(() => current);
		this.tails.set(key, tail);
		await previous.catch(() => undefined);
		try {
			return await operation();
		} finally {
			release();
			if (this.tails.get(key) === tail) this.tails.delete(key);
		}
	}
}

export class KeyringGameProviderCredentialStore implements GameProviderCredentialSource {
	private readonly service: string;
	private readonly maximumCredentialCharacters: number;
	private readonly entryFactory: (service: string, account: string) => GameKeyringEntry;
	private readonly queue = new KeyedQueue();

	constructor(options: KeyringGameProviderCredentialStoreOptions = {}) {
		this.service = options.service ?? "OpenGameAgent";
		validateIdentifier(this.service, "Credential service", 128);
		this.maximumCredentialCharacters = options.maximumCredentialCharacters ?? 64 * 1024;
		if (
			!Number.isInteger(this.maximumCredentialCharacters) ||
			this.maximumCredentialCharacters < 1024 ||
			this.maximumCredentialCharacters > 1024 * 1024
		) {
			throw new RangeError("maximumCredentialCharacters is invalid.");
		}
		this.entryFactory = options.entryFactory ?? ((service, account) => new Entry(service, account));
	}

	async read(providerId: string, signal?: AbortSignal): Promise<GameProviderCredential | undefined> {
		return (await this.readRecord(providerId, signal))?.credential;
	}

	async readRecord(providerId: string, signal?: AbortSignal): Promise<StoredGameProviderCredential | undefined> {
		validateIdentifier(providerId, "Provider id");
		signal?.throwIfAborted();
		return await this.queue.run(providerId, async () => {
			signal?.throwIfAborted();
			const envelope = this.readEnvelope(providerId);
			return envelope === undefined || envelope.deleted === true
				? undefined
				: { providerId, revision: envelope.revision, credential: structuredClone(envelope.credential) };
		});
	}

	async set(
		providerId: string,
		credentialValue: GameProviderCredential,
		expectedRevision?: number,
		signal?: AbortSignal,
	): Promise<GameProviderCredentialWriteResult> {
		validateIdentifier(providerId, "Provider id");
		const credential = normalizeCredential(credentialValue, this.maximumCredentialCharacters);
		validateRevision(expectedRevision);
		signal?.throwIfAborted();
		return await this.queue.run(providerId, async () => {
			signal?.throwIfAborted();
			const current = this.readEnvelope(providerId);
			checkExpectedRevision(current?.revision ?? 0, expectedRevision);
			if (current && current.deleted !== true && sameCredential(current.credential, credential)) {
				return { changed: false, revision: current.revision };
			}
			const revision = (current?.revision ?? 0) + 1;
			const envelope: CredentialEnvelope = { version: 1, providerId, revision, credential };
			const serialized = JSON.stringify(envelope);
			if (serialized.length > this.maximumCredentialCharacters) throw new RangeError("Credential is too large.");
			try {
				this.entry(providerId).setPassword(serialized);
			} catch {
				throw new Error("The operating-system credential store rejected the write.");
			}
			return { changed: true, revision };
		});
	}

	async remove(
		providerId: string,
		expectedRevision?: number,
		signal?: AbortSignal,
	): Promise<GameProviderCredentialWriteResult> {
		validateIdentifier(providerId, "Provider id");
		validateRevision(expectedRevision);
		signal?.throwIfAborted();
		return await this.queue.run(providerId, async () => {
			signal?.throwIfAborted();
			const current = this.readEnvelope(providerId);
			checkExpectedRevision(current?.revision ?? 0, expectedRevision);
			if (!current) return { changed: false, revision: 0 };
			if (current.deleted === true) return { changed: false, revision: current.revision };
			const revision = current.revision + 1;
			const envelope: DeletedCredentialEnvelope = { version: 1, providerId, revision, deleted: true };
			try {
				this.entry(providerId).setPassword(JSON.stringify(envelope));
			} catch {
				throw new Error("The operating-system credential store rejected the removal.");
			}
			return { changed: true, revision };
		});
	}

	private entry(providerId: string): GameKeyringEntry {
		return this.entryFactory(this.service, providerId);
	}

	private readEnvelope(providerId: string): CredentialEnvelope | undefined {
		let serialized: string | null;
		try {
			serialized = this.entry(providerId).getPassword();
		} catch {
			throw new Error("The operating-system credential store could not be read.");
		}
		if (serialized === null) return undefined;
		if (serialized.length < 1 || serialized.length > this.maximumCredentialCharacters) {
			throw new Error("Stored provider credential is corrupt.");
		}
		try {
			const value = JSON.parse(serialized) as {
				version?: unknown;
				providerId?: unknown;
				revision?: unknown;
				credential?: unknown;
				deleted?: unknown;
			};
			if (
				value.version !== 1 ||
				value.providerId !== providerId ||
				!Number.isSafeInteger(value.revision) ||
				(value.revision as number) < 1 ||
				(value.deleted === true) === (value.credential !== undefined)
			) {
				throw new Error();
			}
			if (value.deleted === true) {
				return { version: 1, providerId, revision: value.revision as number, deleted: true };
			}
			return {
				version: 1,
				providerId,
				revision: value.revision as number,
				credential: normalizeCredential(value.credential as GameProviderCredential, this.maximumCredentialCharacters),
			};
		} catch {
			throw new Error("Stored provider credential is corrupt.");
		}
	}
}

function validateRevision(value: number | undefined): void {
	if (value !== undefined && (!Number.isSafeInteger(value) || value < 0)) {
		throw new TypeError("Expected credential revision is invalid.");
	}
}

function checkExpectedRevision(current: number, expected: number | undefined): void {
	if (expected !== undefined && current !== expected) throw new Error("Credential revision conflict.");
}
