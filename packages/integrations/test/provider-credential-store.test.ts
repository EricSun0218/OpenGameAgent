import { type CredentialStore, InMemoryCredentialStore } from "@earendil-works/pi-ai";
import { describe, expect, it } from "vitest";
import {
	createProviderCredentialStore,
	ProviderCredentialStoreError,
	requireReferencedCredential,
} from "../src/provider-credential-store.js";

describe("provider credential store", () => {
	it("reads a referenced credential through its safe provider id", async () => {
		const references: CredentialStore = {
			read: async (reference) =>
				reference === "private-reference" ? { type: "api_key", key: "private-secret" } : undefined,
			list: async () => [],
			modify: async (_reference, update) => update(undefined),
			delete: async () => {},
		};
		const store = createProviderCredentialStore({
			bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
			references,
		});

		await expect(store.read("game-provider")).resolves.toEqual({
			type: "api_key",
			key: "private-secret",
		});
	});

	it("lists safe provider metadata without reading referenced credentials", async () => {
		let reads = 0;
		let lists = 0;
		const references: CredentialStore = {
			read: async () => {
				reads += 1;
				return { type: "oauth", access: "private-secret", refresh: "private-refresh", expires: 0 };
			},
			list: async () => {
				lists += 1;
				return [{ providerId: "private-reference", type: "oauth" }];
			},
			modify: async (_reference, update) => update(undefined),
			delete: async () => {},
		};
		const store = createProviderCredentialStore({
			bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
			references,
		});

		const listed = await store.list();

		expect({ listed, reads, lists, json: JSON.stringify(listed) }).toEqual({
			listed: [{ providerId: "game-provider", type: "oauth" }],
			reads: 0,
			lists: 1,
			json: '[{"providerId":"game-provider","type":"oauth"}]',
		});
	});

	it("does not consult the reference backend when the request has no reference binding", async () => {
		let lists = 0;
		const references: CredentialStore = {
			read: async () => undefined,
			list: async () => {
				lists += 1;
				return [];
			},
			modify: async (_reference, update) => update(undefined),
			delete: async () => {},
		};
		const store = createProviderCredentialStore({
			bindings: new Map([
				["ambient-provider", { kind: "ambient" as const }],
				["keyless-provider", { kind: "none" as const }],
			]),
			references,
		});

		const listed = await store.list();

		expect({ listed, referenceLists: lists }).toEqual({ listed: [], referenceLists: 0 });
	});

	it("fails safely when a reference binding has no host credential store", () => {
		let failure: unknown;
		try {
			createProviderCredentialStore({
				bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
			});
		} catch (error) {
			failure = error;
		}

		expect({
			isTyped: failure instanceof ProviderCredentialStoreError,
			code: failure instanceof ProviderCredentialStoreError ? failure.code : undefined,
			message: failure instanceof Error ? failure.message : undefined,
			hasCause: failure instanceof Error && Object.hasOwn(failure, "cause"),
			jsonHasPrivateData: JSON.stringify(failure).includes("private-reference"),
		}).toEqual({
			isTyped: true,
			code: "unavailable",
			message: "Credential backend operation failed.",
			hasCause: false,
			jsonHasPrivateData: false,
		});
	});

	it("sanitizes an unknown referenced-read failure", async () => {
		const references: CredentialStore = {
			read: async (reference) => {
				throw new Error(`backend exposed ${reference} and private-secret`);
			},
			list: async () => [],
			modify: async (_reference, update) => update(undefined),
			delete: async () => {},
		};
		const store = createProviderCredentialStore({
			bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
			references,
		});

		let failure: unknown;
		try {
			await store.read("game-provider");
		} catch (error) {
			failure = error;
		}
		const publicProjection = `${String(failure)}\n${JSON.stringify(failure)}\n${
			failure instanceof Error ? failure.stack : ""
		}`;

		expect({
			isTyped: failure instanceof ProviderCredentialStoreError,
			code: failure instanceof ProviderCredentialStoreError ? failure.code : undefined,
			message: failure instanceof Error ? failure.message : undefined,
			hasCause: failure instanceof Error && Object.hasOwn(failure, "cause"),
			leaksReference: publicProjection.includes("private-reference"),
			leaksSecret: publicProjection.includes("private-secret"),
		}).toEqual({
			isTyped: true,
			code: "backend",
			message: "Credential backend operation failed.",
			hasCause: false,
			leaksReference: false,
			leaksSecret: false,
		});
	});

	it.each(["list", "modify", "delete"] as const)("sanitizes an unknown referenced-%s failure", async (operation) => {
		const fail = async () => {
			throw new Error("backend exposed private-reference and private-secret");
		};
		const references: CredentialStore = {
			read: async () => undefined,
			list: fail,
			modify: fail,
			delete: fail,
		};
		const store = createProviderCredentialStore({
			bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
			references,
		});

		let failure: unknown;
		try {
			if (operation === "list") await store.list();
			else if (operation === "modify") await store.modify("game-provider", async () => undefined);
			else await store.delete("game-provider");
		} catch (error) {
			failure = error;
		}
		const publicProjection = `${String(failure)}\n${JSON.stringify(failure)}\n${
			failure instanceof Error ? failure.stack : ""
		}`;

		expect({
			isTyped: failure instanceof ProviderCredentialStoreError,
			code: failure instanceof ProviderCredentialStoreError ? failure.code : undefined,
			hasCause: failure instanceof Error && Object.hasOwn(failure, "cause"),
			leaksReference: publicProjection.includes("private-reference"),
			leaksSecret: publicProjection.includes("private-secret"),
		}).toEqual({
			isTyped: true,
			code: "backend",
			hasCause: false,
			leaksReference: false,
			leaksSecret: false,
		});
	});

	it("propagates a referenced modify callback failure unchanged", async () => {
		const callbackFailure = new Error("callback failed");
		const references: CredentialStore = {
			read: async () => undefined,
			list: async () => [],
			modify: async (_reference, update) => update({ type: "api_key", key: "private-secret" }),
			delete: async () => {},
		};
		const store = createProviderCredentialStore({
			bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
			references,
		});

		let failure: unknown;
		try {
			await store.modify("game-provider", async () => {
				throw callbackFailure;
			});
		} catch (error) {
			failure = error;
		}

		expect(failure).toBe(callbackFailure);
	});

	it("fails closed when a referenced credential is missing", () => {
		let failure: unknown;
		try {
			requireReferencedCredential(undefined);
		} catch (error) {
			failure = error;
		}

		expect({
			isTyped: failure instanceof ProviderCredentialStoreError,
			code: failure instanceof ProviderCredentialStoreError ? failure.code : undefined,
			message: failure instanceof Error ? failure.message : undefined,
			hasCause: failure instanceof Error && Object.hasOwn(failure, "cause"),
		}).toEqual({
			isTyped: true,
			code: "missing",
			message: "Referenced credential is not configured.",
			hasCause: false,
		});
	});

	it("returns the referenced credential unchanged when it exists", () => {
		const credential = { type: "api_key" as const, key: "private-secret" };

		expect(requireReferencedCredential(credential)).toBe(credential);
	});

	it("preserves Pi read, modify, list, and delete semantics for a reference", async () => {
		const references = new InMemoryCredentialStore();
		await references.modify("private-reference", async () => ({ type: "api_key", key: "first-secret" }));
		const store = createProviderCredentialStore({
			bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
			references,
		});
		let callbackCurrent: unknown;

		const updated = await store.modify("game-provider", async (current) => {
			callbackCurrent = current;
			return { type: "api_key", key: "second-secret" };
		});
		const unchanged = await store.modify("game-provider", async () => undefined);
		const read = await store.read("game-provider");
		const listed = await store.list();
		await store.delete("game-provider");
		const missing = await store.read("game-provider");

		expect({ callbackCurrent, updated, unchanged, read, listed, missing }).toEqual({
			callbackCurrent: { type: "api_key", key: "first-secret" },
			updated: { type: "api_key", key: "second-secret" },
			unchanged: { type: "api_key", key: "second-secret" },
			read: { type: "api_key", key: "second-secret" },
			listed: [{ providerId: "game-provider", type: "api_key" }],
			missing: undefined,
		});
	});

	it("retains the delegate's serialized modify boundary", async () => {
		const references = new InMemoryCredentialStore();
		await references.modify("private-reference", async () => ({ type: "api_key", key: "first-secret" }));
		const store = createProviderCredentialStore({
			bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
			references,
		});
		let releaseFirst: (() => void) | undefined;
		const firstMayFinish = new Promise<void>((resolve) => {
			releaseFirst = resolve;
		});
		let markFirstEntered: (() => void) | undefined;
		const firstEntered = new Promise<void>((resolve) => {
			markFirstEntered = resolve;
		});
		let secondEntered = false;

		const first = store.modify("game-provider", async () => {
			markFirstEntered?.();
			await firstMayFinish;
			return { type: "api_key", key: "second-secret" };
		});
		await firstEntered;
		const second = store.modify("game-provider", async (current) => {
			secondEntered = true;
			return current?.type === "api_key"
				? { ...current, key: `${current.key}-third` }
				: { type: "api_key", key: "unexpected" };
		});
		await Promise.resolve();
		const enteredBeforeRelease = secondEntered;
		releaseFirst?.();

		const results = await Promise.all([first, second]);

		expect({ enteredBeforeRelease, results }).toEqual({
			enteredBeforeRelease: false,
			results: [
				{ type: "api_key", key: "second-secret" },
				{ type: "api_key", key: "second-secret-third" },
			],
		});
	});

	it("captures credential bindings when the request store is created", async () => {
		const references = new InMemoryCredentialStore();
		await references.modify("first-reference", async () => ({ type: "api_key", key: "first-secret" }));
		await references.modify("second-reference", async () => ({ type: "api_key", key: "second-secret" }));
		const bindings = new Map([["game-provider", { kind: "reference" as const, reference: "first-reference" }]]);
		const store = createProviderCredentialStore({ bindings, references });

		bindings.set("game-provider", { kind: "reference", reference: "second-reference" });

		await expect(store.read("game-provider")).resolves.toEqual({ type: "api_key", key: "first-secret" });
	});

	it.each(["ambient", "none"] as const)("uses Pi's transient store for a %s binding", async (kind) => {
		let backendCalls = 0;
		const backend = async () => {
			backendCalls += 1;
			throw new Error("reference backend should not run");
		};
		const references: CredentialStore = {
			read: backend,
			list: backend,
			modify: backend,
			delete: backend,
		};
		const store = createProviderCredentialStore({
			bindings: new Map([["game-provider", { kind }]]),
			references,
		});

		const updated = await store.modify("game-provider", async () => ({
			type: "api_key",
			key: "request-secret",
		}));
		const read = await store.read("game-provider");
		const listed = await store.list();
		await store.delete("game-provider");
		const missing = await store.read("game-provider");

		expect({ updated, read, listed, missing, backendCalls }).toEqual({
			updated: { type: "api_key", key: "request-secret" },
			read: { type: "api_key", key: "request-secret" },
			listed: [{ providerId: "game-provider", type: "api_key" }],
			missing: undefined,
			backendCalls: 0,
		});
	});

	it("preserves a known safe backend failure code without its original error", async () => {
		const backendFailure = new ProviderCredentialStoreError("locked");
		const references: CredentialStore = {
			read: async () => {
				throw backendFailure;
			},
			list: async () => [],
			modify: async (_reference, update) => update(undefined),
			delete: async () => {},
		};
		const store = createProviderCredentialStore({
			bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
			references,
		});

		let failure: unknown;
		try {
			await store.read("game-provider");
		} catch (error) {
			failure = error;
		}

		expect({
			isFresh: failure !== backendFailure,
			isTyped: failure instanceof ProviderCredentialStoreError,
			code: failure instanceof ProviderCredentialStoreError ? failure.code : undefined,
			hasCause: failure instanceof Error && Object.hasOwn(failure, "cause"),
		}).toEqual({ isFresh: true, isTyped: true, code: "locked", hasCause: false });
	});

	it("does not trust an untyped backend error code", async () => {
		const references: CredentialStore = {
			read: async () => {
				throw { code: "locked", detail: "private-secret" };
			},
			list: async () => [],
			modify: async (_reference, update) => update(undefined),
			delete: async () => {},
		};
		const store = createProviderCredentialStore({
			bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
			references,
		});

		await expect(store.read("game-provider")).rejects.toMatchObject({
			name: "ProviderCredentialStoreError",
			code: "backend",
			message: "Credential backend operation failed.",
		});
	});

	it.each(["read", "list", "modify", "delete"] as const)(
		"does not trust an out-of-contract typed error code from referenced %s",
		async (operation) => {
			const fail = async () => {
				throw new ProviderCredentialStoreError("private-secret" as never);
			};
			const references: CredentialStore = {
				read: fail,
				list: fail,
				modify: fail,
				delete: fail,
			};
			const store = createProviderCredentialStore({
				bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
				references,
			});

			let failure: unknown;
			try {
				if (operation === "read") await store.read("game-provider");
				else if (operation === "list") await store.list();
				else if (operation === "modify") await store.modify("game-provider", async () => undefined);
				else await store.delete("game-provider");
			} catch (error) {
				failure = error;
			}

			expect({
				code: failure instanceof ProviderCredentialStoreError ? failure.code : undefined,
				leaksPrivateCode: JSON.stringify(failure).includes("private-secret"),
			}).toEqual({ code: "backend", leaksPrivateCode: false });
		},
	);

	it("keeps an in-flight caller abort reason instead of a backend failure", async () => {
		const controller = new AbortController();
		const reason = Object.freeze({ kind: "caller-abort" });
		const references: CredentialStore = {
			read: async () => {
				controller.abort(reason);
				throw new Error("backend exposed private-reference and private-secret");
			},
			list: async () => [],
			modify: async (_reference, update) => update(undefined),
			delete: async () => {},
		};
		const store = createProviderCredentialStore({
			bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
			references,
		});

		let failure: unknown;
		try {
			await store.read("game-provider", { signal: controller.signal });
		} catch (error) {
			failure = error;
		}

		expect(failure).toBe(reason);
	});

	it.each(["read", "list", "modify", "delete"] as const)(
		"preserves the exact caller abort reason for referenced %s",
		async (operation) => {
			let backendCalls = 0;
			const backend = async () => {
				backendCalls += 1;
				throw new Error("backend should not run");
			};
			const references: CredentialStore = {
				read: backend,
				list: backend,
				modify: backend,
				delete: backend,
			};
			const store = createProviderCredentialStore({
				bindings: new Map([["game-provider", { kind: "reference" as const, reference: "private-reference" }]]),
				references,
			});
			const controller = new AbortController();
			const reason = Object.freeze({ kind: "caller-abort", operation });
			controller.abort(reason);

			let failure: unknown;
			try {
				if (operation === "read") await store.read("game-provider", { signal: controller.signal });
				else if (operation === "list") await store.list({ signal: controller.signal });
				else if (operation === "modify") {
					await store.modify("game-provider", async () => undefined, { signal: controller.signal });
				} else await store.delete("game-provider", { signal: controller.signal });
			} catch (error) {
				failure = error;
			}

			expect({ sameReason: failure === reason, backendCalls }).toEqual({ sameReason: true, backendCalls: 0 });
		},
	);
});
