import {
	type AuthOperationOptions,
	type Credential,
	type CredentialInfo,
	type CredentialStore,
	InMemoryCredentialStore,
} from "@earendil-works/pi-ai";

export type ProviderCredentialBinding =
	| Readonly<{ kind: "ambient" }>
	| Readonly<{ kind: "reference"; reference: string }>
	| Readonly<{ kind: "none" }>;

export type ProviderCredentialStoreErrorCode =
	| "missing"
	| "locked"
	| "permission_denied"
	| "unavailable"
	| "ambiguous"
	| "corrupt"
	| "too_long"
	| "backend";

const PROVIDER_CREDENTIAL_STORE_ERROR_CODES = new Set<ProviderCredentialStoreErrorCode>([
	"missing",
	"locked",
	"permission_denied",
	"unavailable",
	"ambiguous",
	"corrupt",
	"too_long",
	"backend",
]);

export class ProviderCredentialStoreError extends Error {
	constructor(readonly code: ProviderCredentialStoreErrorCode) {
		super(code === "missing" ? "Referenced credential is not configured." : "Credential backend operation failed.");
		this.name = "ProviderCredentialStoreError";
	}
}

export function requireReferencedCredential<T extends Credential>(credential: T | undefined): T {
	if (credential === undefined) throw new ProviderCredentialStoreError("missing");
	return credential;
}

export interface CreateProviderCredentialStoreOptions {
	bindings: ReadonlyMap<string, ProviderCredentialBinding>;
	references?: CredentialStore;
}

async function callReferenceBackend<T>(operation: () => Promise<T>, options?: AuthOperationOptions): Promise<T> {
	options?.signal?.throwIfAborted();
	try {
		return await operation();
	} catch (error) {
		options?.signal?.throwIfAborted();
		throw new ProviderCredentialStoreError(safeBackendErrorCode(error));
	}
}

async function modifyReferencedCredential(
	store: CredentialStore,
	reference: string,
	update: (current: Credential | undefined) => Promise<Credential | undefined>,
	options?: AuthOperationOptions,
): Promise<Credential | undefined> {
	let callbackFailed = false;
	let callbackFailure: unknown;
	options?.signal?.throwIfAborted();
	try {
		return await store.modify(
			reference,
			async (current) => {
				try {
					return await update(current);
				} catch (error) {
					callbackFailed = true;
					callbackFailure = error;
					throw error;
				}
			},
			options,
		);
	} catch (error) {
		if (callbackFailed && Object.is(error, callbackFailure)) throw error;
		options?.signal?.throwIfAborted();
		throw new ProviderCredentialStoreError(safeBackendErrorCode(error));
	}
}

function safeBackendErrorCode(error: unknown): ProviderCredentialStoreErrorCode {
	return error instanceof ProviderCredentialStoreError && PROVIDER_CREDENTIAL_STORE_ERROR_CODES.has(error.code)
		? error.code
		: "backend";
}

export function createProviderCredentialStore(options: CreateProviderCredentialStoreOptions): CredentialStore {
	const bindings = new Map<string, ProviderCredentialBinding>();
	const providersByReference = new Map<string, string[]>();
	for (const [providerId, binding] of options.bindings) {
		const captured = Object.freeze({ ...binding }) as ProviderCredentialBinding;
		bindings.set(providerId, captured);
		if (captured.kind === "reference") {
			const providers = providersByReference.get(captured.reference);
			if (providers === undefined) providersByReference.set(captured.reference, [providerId]);
			else providers.push(providerId);
		}
	}
	if (providersByReference.size > 0 && options.references === undefined) {
		throw new ProviderCredentialStoreError("unavailable");
	}
	const transient = new InMemoryCredentialStore();
	const references = options.references;
	const referenceFor = (providerId: string): string | undefined => {
		const binding = bindings.get(providerId);
		return binding?.kind === "reference" ? binding.reference : undefined;
	};

	return {
		read(providerId: string, operation?: AuthOperationOptions): Promise<Credential | undefined> {
			const reference = referenceFor(providerId);
			return reference === undefined || references === undefined
				? transient.read(providerId, operation)
				: callReferenceBackend(() => references.read(reference, operation), operation);
		},
		async list(operation?: AuthOperationOptions): Promise<readonly CredentialInfo[]> {
			const listed = [...(await transient.list(operation))];
			if (references === undefined || providersByReference.size === 0) return listed;

			const referenced = await callReferenceBackend(() => references.list(operation), operation);
			for (const credential of referenced) {
				for (const providerId of providersByReference.get(credential.providerId) ?? []) {
					listed.push({ providerId, type: credential.type });
				}
			}
			return listed;
		},
		modify(
			providerId: string,
			update: (current: Credential | undefined) => Promise<Credential | undefined>,
			operation?: AuthOperationOptions,
		): Promise<Credential | undefined> {
			const reference = referenceFor(providerId);
			return reference === undefined || references === undefined
				? transient.modify(providerId, update, operation)
				: modifyReferencedCredential(references, reference, update, operation);
		},
		delete(providerId: string, operation?: AuthOperationOptions): Promise<void> {
			const reference = referenceFor(providerId);
			return reference === undefined || references === undefined
				? transient.delete(providerId, operation)
				: callReferenceBackend(() => references.delete(reference, operation), operation);
		},
	};
}
