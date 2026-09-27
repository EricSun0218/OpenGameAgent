import type { AssistantMessage, DeferredHandle, Models } from "@earendil-works/pi-ai";

export interface GameDeferredResponseAdapter {
	fetch(handle: Readonly<DeferredHandle>, signal: AbortSignal): Promise<AssistantMessage>;
	cancel(handle: Readonly<DeferredHandle>, signal: AbortSignal): Promise<void>;
}

export function createPiModelsDeferredResponseAdapter(models: Models): GameDeferredResponseAdapter {
	const getModel = models.getModel.bind(models);
	const fetchDeferred = models.fetchDeferred.bind(models);
	const cancelDeferred = models.cancelDeferred.bind(models);
	const resolveModel = (handle: Readonly<DeferredHandle>) => {
		const model = getModel(handle.provider, handle.modelId);
		if (!model || model.api !== handle.api) {
			throw new Error("The deferred provider model is unavailable.");
		}
		return model;
	};
	return Object.freeze({
		fetch: (handle: Readonly<DeferredHandle>, signal: AbortSignal) =>
			fetchDeferred(resolveModel(handle), handle, { signal, wait: 0 }),
		cancel: (handle: Readonly<DeferredHandle>, signal: AbortSignal) =>
			cancelDeferred(resolveModel(handle), handle, { signal }),
	});
}
