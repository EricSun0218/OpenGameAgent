import { describe, expect, it } from "vitest";
import { type GameKeyringEntry, KeyringGameProviderCredentialStore } from "./keyring-credentials.js";

function memoryEntries() {
	const values = new Map<string, string>();
	return {
		values,
		factory(service: string, account: string): GameKeyringEntry {
			const key = `${service}\0${account}`;
			return {
				getPassword: () => values.get(key) ?? null,
				setPassword: (password) => {
					values.set(key, password);
				},
			};
		},
	};
}

describe("keyring provider credentials", () => {
	it("stores provider credentials behind the system entry and exposes no secret metadata API", async () => {
		const memory = memoryEntries();
		const store = new KeyringGameProviderCredentialStore({ service: "oga-test", entryFactory: memory.factory });
		expect(await store.read("deepseek")).toBeUndefined();
		expect(await store.set("deepseek", { key: "secret-key" }, 0)).toEqual({ changed: true, revision: 1 });
		expect(await store.readRecord("deepseek")).toEqual({
			providerId: "deepseek",
			revision: 1,
			credential: { key: "secret-key" },
		});
		expect(memory.values.size).toBe(1);
		expect([...memory.values.values()][0]).not.toBe("secret-key");
		expect(await store.set("deepseek", { key: "secret-key" }, 1)).toEqual({ changed: false, revision: 1 });
	});

	it("uses revision checks, serializes concurrent writes, and removes idempotently", async () => {
		const memory = memoryEntries();
		const store = new KeyringGameProviderCredentialStore({ entryFactory: memory.factory });
		await store.set("provider", { key: "one" }, 0);
		const results = await Promise.allSettled([
			store.set("provider", { key: "two" }, 1),
			store.set("provider", { key: "three" }, 1),
		]);
		expect(results.filter((value) => value.status === "fulfilled")).toHaveLength(1);
		expect(results.filter((value) => value.status === "rejected")).toHaveLength(1);
		const current = await store.readRecord("provider");
		expect(current?.revision).toBe(2);
		expect(await store.remove("provider", 2)).toEqual({ changed: true, revision: 3 });
		expect(await store.read("provider")).toBeUndefined();
		expect(await store.remove("provider", 3)).toEqual({ changed: false, revision: 3 });
		expect(await store.set("provider", { key: "four" }, 3)).toEqual({ changed: true, revision: 4 });
	});

	it("fails closed for corruption, cancellation, oversized values, and stale revisions", async () => {
		const memory = memoryEntries();
		const store = new KeyringGameProviderCredentialStore({
			service: "oga-corrupt",
			entryFactory: memory.factory,
			maximumCredentialCharacters: 1024,
		});
		memory.values.set("oga-corrupt\0provider", "not-json");
		await expect(store.read("provider")).rejects.toThrow("corrupt");
		memory.values.clear();
		await expect(store.set("provider", { key: "x".repeat(2000) })).rejects.toThrow("invalid");
		await expect(store.set("provider", { key: "one" }, 2)).rejects.toThrow("revision conflict");
		const controller = new AbortController();
		controller.abort(new Error("cancelled"));
		await expect(store.read("provider", controller.signal)).rejects.toThrow("cancelled");
	});

	it("never copies a rejected store error or a secret into its public error", async () => {
		const store = new KeyringGameProviderCredentialStore({
			entryFactory: () => ({
				getPassword: () => null,
				setPassword: () => {
					throw new Error("secret-key from native backend");
				},
			}),
		});
		await expect(store.set("provider", { key: "secret-key" })).rejects.toThrow(
			"operating-system credential store rejected the write",
		);
		try {
			await store.set("provider", { key: "secret-key" });
		} catch (error) {
			expect(String(error)).not.toContain("secret-key");
		}
	});
});
