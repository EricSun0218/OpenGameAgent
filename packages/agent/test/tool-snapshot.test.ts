import type { AgentTool } from "@earendil-works/pi-agent-core";
import { type TSchema, Type } from "typebox";
import { describe, expect, it } from "vitest";
import {
	createToolDescriptorFingerprint,
	extendPreparedToolSnapshot,
	InvalidToolPolicyError,
	InvalidToolSnapshotError,
	prepareToolCatalogSnapshot,
	prepareToolSnapshot,
	selectVisibleToolSnapshot,
	type ToolSnapshotLimitError,
	validateToolSnapshot,
} from "../src/tool-snapshot.js";
import { DURABLE_TOOL_EXECUTION, type DurableExecutionAgentTool } from "../src/durable-tool-execution.js";

const LIMITS = {
	maxTools: 4,
	maxCatalogBytes: 4_096,
	maxSchemaDepth: 8,
	maxRequestSources: 4,
	maxPolicyRevisionBytes: 128,
	maxArgumentsBytes: 1_024,
	maxArgumentsDepth: 8,
	maxResultContentParts: 8,
	maxResultBytes: 4_096,
	maxResultDepth: 8,
	maxResultUpdates: 8,
} as const;

const CATALOG_LIMITS = {
	maxTools: LIMITS.maxTools,
	maxCatalogBytes: LIMITS.maxCatalogBytes,
	maxSchemaDepth: LIMITS.maxSchemaDepth,
} as const;

function tool(name: string, parameters: TSchema = Type.Object({ value: Type.String() })): AgentTool {
	return {
		name,
		label: name,
		description: `${name} tool`,
		parameters,
		execute: async () => ({ content: [{ type: "text", text: "ok" }], details: {} }),
	};
}

describe("tool snapshot preflight", () => {
	it("accepts a complete valid TypeBox tool snapshot", () => {
		const tools = [tool("inspect"), tool("act")];

		expect(validateToolSnapshot(tools, CATALOG_LIMITS)).toBe(tools);
	});

	it("fails closed an omitted execution mode to sequential", () => {
		const omitted = tool("omitted");
		const explicitSequential = { ...tool("sequential"), executionMode: "sequential" as const };
		const explicitParallel = { ...tool("parallel"), executionMode: "parallel" as const };
		const prepared = prepareToolSnapshot([omitted, explicitSequential, explicitParallel], LIMITS);

		expect(prepared.descriptors.map((descriptor) => descriptor.executionMode)).toEqual([
			"sequential",
			"sequential",
			"parallel",
		]);
		expect(prepared.tools.map((candidate) => candidate.executionMode)).toEqual([
			"sequential",
			"sequential",
			"parallel",
		]);
		expect(createToolDescriptorFingerprint(omitted, CATALOG_LIMITS)).toBe(
			createToolDescriptorFingerprint({ ...omitted, executionMode: "sequential" }, CATALOG_LIMITS),
		);
		expect(createToolDescriptorFingerprint(omitted, CATALOG_LIMITS)).not.toBe(
			createToolDescriptorFingerprint({ ...omitted, executionMode: "parallel" }, CATALOG_LIMITS),
		);
	});

	it("rejects an explicitly parallel durable Tool before publication", () => {
		const candidate = tool("durable");
		const durableParallel = {
			...candidate,
			executionMode: "parallel" as const,
			[DURABLE_TOOL_EXECUTION]: async () => ({ execute: async () => candidate.execute("call", {}) }),
		} satisfies DurableExecutionAgentTool;

		expect(() => prepareToolSnapshot([durableParallel], LIMITS)).toThrow(/durable.*parallel/u);
	});

	it("accepts omitted and explicitly sequential durable Tools as sequential", () => {
		const omittedCandidate = tool("durable-omitted");
		const sequentialCandidate = tool("durable-sequential");
		const durableOmitted = {
			...omittedCandidate,
			[DURABLE_TOOL_EXECUTION]: async () => ({ execute: async () => omittedCandidate.execute("call", {}) }),
		} satisfies DurableExecutionAgentTool;
		const durableSequential = {
			...sequentialCandidate,
			executionMode: "sequential" as const,
			[DURABLE_TOOL_EXECUTION]: async () => ({ execute: async () => sequentialCandidate.execute("call", {}) }),
		} satisfies DurableExecutionAgentTool;

		const prepared = prepareToolSnapshot([durableOmitted, durableSequential], LIMITS);

		expect(prepared.descriptors.map((descriptor) => descriptor.executionMode)).toEqual(["sequential", "sequential"]);
		expect(prepared.tools.map((candidate) => candidate.executionMode)).toEqual(["sequential", "sequential"]);
		expect(
			prepared.tools.map((candidate) => typeof (candidate as DurableExecutionAgentTool)[DURABLE_TOOL_EXECUTION]),
		).toEqual(["function", "function"]);
	});

	it("accepts shared TypeBox sub-schemas without retaining their identity", () => {
		const shared = Type.String();
		const prepared = prepareToolCatalogSnapshot(
			[tool("inspect", Type.Object({ first: shared, second: shared }))],
			CATALOG_LIMITS,
		);
		const descriptor = prepared.descriptors[0];
		if (!descriptor) throw new Error("Expected a prepared descriptor.");
		const properties = (descriptor.parameters as { properties: Record<string, unknown> }).properties;

		expect(properties.first).toEqual(properties.second);
		expect(properties.first).not.toBe(properties.second);
	});

	it("rejects duplicate names before provider dispatch", () => {
		expect(() => validateToolSnapshot([tool("act"), tool("act")], CATALOG_LIMITS)).toThrow(InvalidToolSnapshotError);
	});

	it("recursively rejects malformed JSON Schema", () => {
		const malformed = tool("broken", {
			type: "object",
			properties: { value: { type: 42 } },
		} as never);

		expect(() => validateToolSnapshot([malformed], CATALOG_LIMITS)).toThrow(/invalid parameter schema/);
	});

	it("rejects schemas whose provider representation would lose data", () => {
		const malformed = tool("broken", {
			type: "object",
			properties: { value: { type: "string", transform: () => "changed" } },
		} as never);

		expect(() => validateToolSnapshot([malformed], CATALOG_LIMITS)).toThrow(/non-JSON parameter schema/);
	});

	it("captures accessor-backed, class, and proxied Tool contracts exactly once", async () => {
		let nameReads = 0;
		class ClassTool {
			readonly label = "inspect";
			readonly description = "inspect tool";
			readonly parameters = Type.Object({ value: Type.String() });
			readonly marker = "bound";

			get name() {
				nameReads += 1;
				return "inspect";
			}

			async execute() {
				return { content: [{ type: "text" as const, text: this.marker }], details: {} };
			}
		}
		const propertyReads = new Map<PropertyKey, number>();
		const proxied = new Proxy(new ClassTool(), {
			get(target, property, receiver) {
				propertyReads.set(property, (propertyReads.get(property) ?? 0) + 1);
				return Reflect.get(target, property, receiver);
			},
		});
		let unrelatedGetterCalls = 0;
		Object.defineProperty(proxied, "unrelated", {
			enumerable: true,
			get() {
				unrelatedGetterCalls += 1;
				return "ignored";
			},
		});
		let elementReads = 0;
		let lengthReads = 0;
		const tools = [tool("placeholder")];
		Object.defineProperty(tools, 0, {
			enumerable: true,
			get() {
				elementReads += 1;
				return proxied as unknown as AgentTool;
			},
		});
		const source = new Proxy(tools, {
			get(target, property, receiver) {
				if (property === "length") {
					lengthReads += 1;
					return lengthReads === 1 ? 1 : LIMITS.maxTools + 1;
				}
				return Reflect.get(target, property, receiver);
			},
		});
		const prepared = prepareToolCatalogSnapshot(source, CATALOG_LIMITS);
		expect(prepared.descriptors[0]?.name).toBe("inspect");
		expect(prepared.tools[0]?.name).toBe("inspect");
		expect(nameReads).toBe(1);
		expect(lengthReads).toBe(1);
		expect(elementReads).toBe(1);
		for (const property of [
			"name",
			"label",
			"description",
			"parameters",
			"executionMode",
			"prepareArguments",
			"execute",
		]) {
			expect(propertyReads.get(property)).toBe(1);
		}
		expect(unrelatedGetterCalls).toBe(0);
		expect(await prepared.tools[0]?.execute("call", {})).toMatchObject({ content: [{ text: "bound" }] });
	});

	it("requires an object argument root", () => {
		expect(() => validateToolSnapshot([tool("broken", Type.String())], CATALOG_LIMITS)).toThrow(
			/object parameter schema/,
		);
	});

	it("fingerprints the complete model-visible Tool descriptor deterministically", () => {
		const original = tool("inspect", Type.Object({ area: Type.String() }, { additionalProperties: false }));
		const schemaChanged = tool("inspect", Type.Object({ region: Type.String() }, { additionalProperties: false }));

		expect(createToolDescriptorFingerprint(original, CATALOG_LIMITS)).toMatch(/^[a-f0-9]{64}$/u);
		expect(createToolDescriptorFingerprint(original, CATALOG_LIMITS)).toBe(
			createToolDescriptorFingerprint(original, CATALOG_LIMITS),
		);
		expect(createToolDescriptorFingerprint(schemaChanged, CATALOG_LIMITS)).not.toBe(
			createToolDescriptorFingerprint(original, CATALOG_LIMITS),
		);
	});

	it("publishes only detached frozen descriptors and construction-bound callbacks", async () => {
		const original = tool("inspect", Type.Object({ area: Type.String() }, { additionalProperties: false }));
		const initialExecute = original.execute;
		const prepared = prepareToolSnapshot([original], LIMITS);
		original.name = "mutated";
		original.description = "mutated";
		original.execute = async () => ({ content: [{ type: "text", text: "mutated" }], details: {} });
		(original.parameters as { properties: { area: { type: string } } }).properties.area.type = "number";

		expect(prepared.tools[0]).not.toBe(original);
		expect(prepared.tools[0]?.name).toBe("inspect");
		expect(prepared.tools[0]?.description).toBe("inspect tool");
		expect(prepared.tools[0]?.execute).not.toBe(original.execute);
		expect(Object.isFrozen(prepared.tools)).toBe(true);
		expect(Object.isFrozen(prepared.tools[0])).toBe(true);
		expect(Object.isFrozen(prepared.descriptors[0]?.parameters)).toBe(true);
		const descriptor = prepared.descriptors[0];
		if (!descriptor) throw new Error("Expected a prepared descriptor.");
		expect((descriptor.parameters as { properties: { area: { type: string } } }).properties.area.type).toBe("string");
		expect(await prepared.tools[0]?.execute("call", { area: "camp" })).toEqual(await initialExecute("call", {}));
	});

	it("extends prepared catalogs without wrapping or re-reading earlier Tools", () => {
		const base = prepareToolSnapshot([tool("inspect")], LIMITS);
		const extended = extendPreparedToolSnapshot(base, [tool("act")], LIMITS);

		expect(extended.tools[0]).toBe(base.tools[0]);
		expect(extended.descriptors[0]).toBe(base.descriptors[0]);
		expect(extended.tools.map((candidate) => candidate.name)).toEqual(["inspect", "act"]);
		expect(() =>
			extendPreparedToolSnapshot(base, [], { ...LIMITS, maxCatalogBytes: LIMITS.maxCatalogBytes - 1 }),
		).toThrow(/original catalog limits/u);
	});

	it("preserves dangerous schema keys without allowing prototype mutation", () => {
		const parameters = JSON.parse(
			'{"type":"object","properties":{"__proto__":{"type":"string"},"constructor":{"type":"string"}},"additionalProperties":false}',
		) as TSchema;
		const prepared = prepareToolSnapshot([tool("inspect", parameters)], LIMITS);
		const schema = prepared.descriptors[0]?.parameters as { properties: Record<string, unknown> };

		expect(Object.getPrototypeOf(schema.properties)).toBe(Object.prototype);
		expect(Object.hasOwn(schema.properties, "__proto__")).toBe(true);
		expect(Object.keys(schema.properties)).toEqual(["__proto__", "constructor"]);
		expect((schema.properties as { type?: unknown }).type).toBeUndefined();
	});

	it("enforces independent host count, catalog-byte, and schema-depth budgets", () => {
		expect(() => prepareToolSnapshot([tool("one"), tool("two")], { ...LIMITS, maxTools: 1 })).toThrowError(
			expect.objectContaining({ code: "tool_count_exceeded" }) as ToolSnapshotLimitError,
		);
		expect(() => prepareToolSnapshot([tool("one")], { ...LIMITS, maxCatalogBytes: 1 })).toThrowError(
			expect.objectContaining({ code: "tool_catalog_too_large" }) as ToolSnapshotLimitError,
		);
		expect(() => prepareToolSnapshot([tool("one")], { ...LIMITS, maxSchemaDepth: 1 })).toThrowError(
			expect.objectContaining({ code: "tool_schema_too_deep" }) as ToolSnapshotLimitError,
		);
		let elementReads = 0;
		const oversized = [tool("one"), tool("two")];
		Object.defineProperty(oversized, 0, {
			enumerable: true,
			get() {
				elementReads += 1;
				return tool("hidden");
			},
		});
		expect(() => prepareToolSnapshot(oversized, { ...LIMITS, maxTools: 1 })).toThrowError(
			expect.objectContaining({ code: "tool_count_exceeded" }) as ToolSnapshotLimitError,
		);
		expect(elementReads).toBe(0);
	});

	it("requires positive known host limits while ignoring unrelated fields", () => {
		expect(() => prepareToolSnapshot([], { ...LIMITS, maxTools: 0 })).toThrow(/maxTools/u);
		expect(() =>
			prepareToolSnapshot([], {
				maxTools: 1,
				maxCatalogBytes: 1,
				maxSchemaDepth: 1,
				maxArgumentsBytes: 1,
				maxResultContentParts: 1,
				maxResultBytes: 128,
				maxResultDepth: 4,
				maxResultUpdates: 1,
			} as never),
		).toThrow(/maxRequestSources/u);
		const { maxArgumentsDepth: _omitted, ...withoutArgumentsDepth } = LIMITS;
		expect(() => prepareToolSnapshot([], { ...withoutArgumentsDepth, maxArgumentDepth: 1 } as never)).toThrow(
			/maxArgumentsDepth/u,
		);
		expect(() => prepareToolSnapshot([], { ...LIMITS, unrelated: 1 } as never)).not.toThrow();
		let maxToolsReads = 0;
		const accessorLimits = Object.create(LIMITS) as typeof LIMITS;
		Object.defineProperty(accessorLimits, "maxTools", {
			get() {
				maxToolsReads += 1;
				return LIMITS.maxTools;
			},
		});
		expect(() => prepareToolSnapshot([], new Proxy(accessorLimits, {}))).not.toThrow();
		expect(maxToolsReads).toBe(1);
	});

	it("normalizes visibility to candidate order and binds the policy revision into the catalog revision", () => {
		const prepared = prepareToolSnapshot([tool("inspect"), tool("act")], LIMITS);
		const visible = selectVisibleToolSnapshot(
			prepared,
			{
				revision: "policy-7",
				visibleToolNames: ["act", "inspect"],
			},
			LIMITS.maxPolicyRevisionBytes,
		);

		expect(visible.visibleToolNames).toEqual(["inspect", "act"]);
		expect(visible.revision).toMatch(/^[a-f0-9]{64}$/u);
		expect(Object.isFrozen(visible)).toBe(true);
		expect(
			selectVisibleToolSnapshot(
				prepared,
				{ revision: "policy-8", visibleToolNames: ["act", "inspect"] },
				LIMITS.maxPolicyRevisionBytes,
			).revision,
		).not.toBe(visible.revision);
	});

	it("rejects unknown policy selections and treats duplicates as an idempotent set", () => {
		const prepared = prepareToolSnapshot([tool("inspect")], LIMITS);
		expect(() =>
			selectVisibleToolSnapshot(
				prepared,
				{ revision: "policy", visibleToolNames: ["unknown"] },
				LIMITS.maxPolicyRevisionBytes,
			),
		).toThrow(InvalidToolPolicyError);
		expect(
			selectVisibleToolSnapshot(
				prepared,
				{ revision: "policy", visibleToolNames: ["inspect", "inspect"] },
				LIMITS.maxPolicyRevisionBytes,
			).visibleToolNames,
		).toEqual(["inspect"]);
	});

	it("captures accessor-backed and proxied policy decisions exactly once", () => {
		const prepared = prepareToolSnapshot([tool("inspect")], LIMITS);
		let revisionReads = 0;
		let visibleNamesReads = 0;
		let unrelatedReads = 0;
		let nameReads = 0;
		let lengthReads = 0;
		const names = new Proxy(["inspect"], {
			get(target, property, receiver) {
				if (property === "length") {
					lengthReads += 1;
					return lengthReads === 1 ? 1 : LIMITS.maxTools + 1;
				}
				if (property === "0") nameReads += 1;
				return Reflect.get(target, property, receiver);
			},
		});
		const decision = {} as Record<string, unknown>;
		Object.defineProperty(decision, "revision", {
			enumerable: true,
			get() {
				revisionReads += 1;
				return "policy";
			},
		});
		Object.defineProperty(decision, "visibleToolNames", {
			enumerable: true,
			get() {
				visibleNamesReads += 1;
				return names;
			},
		});
		Object.defineProperty(decision, "unrelated", {
			get() {
				unrelatedReads += 1;
				return true;
			},
		});
		const visible = selectVisibleToolSnapshot(prepared, new Proxy(decision, {}) as never, 128);
		expect(visible.visibleToolNames).toEqual(["inspect"]);
		expect(revisionReads).toBe(1);
		expect(visibleNamesReads).toBe(1);
		expect(lengthReads).toBe(1);
		expect(nameReads).toBe(1);
		expect(unrelatedReads).toBe(0);
	});

	it("bounds the opaque policy revision before hashing the visible snapshot", () => {
		const prepared = prepareToolSnapshot([tool("inspect")], LIMITS);
		expect(() =>
			selectVisibleToolSnapshot(prepared, { revision: "策略", visibleToolNames: ["inspect"] }, 5),
		).toThrowError(expect.objectContaining({ code: "tool_policy_revision_too_large" }) as ToolSnapshotLimitError);
		expect(
			selectVisibleToolSnapshot(prepared, { revision: "策略", visibleToolNames: ["inspect"] }, 6).policyRevision,
		).toBe("策略");
	});
});
