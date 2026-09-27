export type BoundedFetchFailureKind = "http-response" | "sse-event";

export class BoundedFetchResponseLimitError extends Error {
	constructor(public readonly kind: BoundedFetchFailureKind) {
		super(`The ${kind} exceeded its configured byte limit.`);
		this.name = "BoundedFetchResponseLimitError";
	}
}

export class BoundedFetchRedirectError extends Error {
	constructor() {
		super("The HTTP response was redirected.");
		this.name = "BoundedFetchRedirectError";
	}
}

export type BoundedFetchErrorFactory = (kind: BoundedFetchFailureKind) => Error;
export type BoundedFetchHttpLimitSelector = (requestUrl: string, response: Readonly<Response>) => number;
export type BoundedFetchSseLineValidator = (line: Uint8Array, isFirstLine: boolean) => void;

/** Selects response framing from the normalized media type, or forces every response through the total-body path. */
export type BoundedFetchResponseMode = "auto" | "total-body";

/**
 * Bounds complete non-streaming bodies and individual SSE events.
 *
 * In `auto` mode, only a normalized bare `Content-Type` of `text/event-stream` selects per-event limits and
 * optional line validation. `total-body` ignores the response's SSE claim, `maxSseEventBytes`, and
 * `validateSseLine`; it always applies the selected HTTP body limit.
 */
export function createBoundedFetch(
	upstream: typeof fetch,
	maxHttpBytes: number,
	maxSseEventBytes: number,
	createError: BoundedFetchErrorFactory = (kind) => new BoundedFetchResponseLimitError(kind),
	selectHttpLimit?: BoundedFetchHttpLimitSelector,
	validateSseLine?: BoundedFetchSseLineValidator,
	responseMode: BoundedFetchResponseMode = "auto",
): typeof fetch {
	assertPositiveSafeInteger(maxHttpBytes, "maxHttpBytes");
	assertPositiveSafeInteger(maxSseEventBytes, "maxSseEventBytes");
	if (responseMode !== "auto" && responseMode !== "total-body") {
		throw new TypeError('responseMode must be "auto" or "total-body".');
	}
	if (selectHttpLimit !== undefined && typeof selectHttpLimit !== "function") {
		throw new TypeError("selectHttpLimit must be a function when provided.");
	}
	if (
		typeof upstream !== "function" ||
		typeof createError !== "function" ||
		(validateSseLine !== undefined && typeof validateSseLine !== "function")
	) {
		throw new TypeError("Bounded fetch requires an upstream fetch and error factory.");
	}
	return async (input, init) => {
		const request = new Request(input, { ...init, redirect: "manual" });
		const response = await upstream(request);
		if (
			response.redirected ||
			response.type === "opaqueredirect" ||
			isRedirectStatus(response.status) ||
			(response.url !== "" && response.url !== request.url)
		) {
			rejectResponse(response, new BoundedFetchRedirectError());
		}
		if (!response.body) return response;
		const contentType = response.headers.get("content-type")?.toLowerCase() ?? "";
		const mediaType = contentType.split(";", 1)[0]?.trim() ?? "";
		const isSse = responseMode === "auto" && mediaType === "text/event-stream";
		const httpLimit =
			isSse || selectHttpLimit === undefined
				? maxHttpBytes
				: callResponseHook(response, () => selectHttpLimit(request.url, response));
		if (!Number.isSafeInteger(httpLimit) || httpLimit <= 0) {
			rejectResponse(response, new TypeError("The selected HTTP response byte limit must be a positive safe integer."));
		}
		if (!isSse) {
			const contentLength = response.headers.get("content-length");
			if (contentLength !== null) {
				const declared = Number(contentLength);
				if (!Number.isSafeInteger(declared) || declared < 0 || declared > httpLimit) {
					const error = callResponseHook(response, () => createError("http-response"));
					rejectResponse(response, error);
				}
			}
		}
		const body = isSse
			? response.body.pipeThrough(boundSseEvents(maxSseEventBytes, createError, validateSseLine))
			: response.body.pipeThrough(boundTotalBytes(httpLimit, createError));
		return new Response(body, {
			status: response.status,
			statusText: response.statusText,
			headers: response.headers,
		});
	};
}

function isRedirectStatus(status: number): boolean {
	return status === 301 || status === 302 || status === 303 || status === 307 || status === 308;
}

function callResponseHook<T>(response: Response, hook: () => T): T {
	try {
		return hook();
	} catch (error) {
		rejectResponse(response, error);
	}
}

function rejectResponse(response: Response, error: unknown): never {
	try {
		const body = response.body;
		if (body) void Promise.resolve(body.cancel()).catch(() => undefined);
	} catch {
		// Cleanup is best-effort and cannot replace or delay the admission failure.
	}
	throw error;
}

function boundTotalBytes(
	maxBytes: number,
	createError: BoundedFetchErrorFactory,
): TransformStream<Uint8Array, Uint8Array> {
	let total = 0;
	return new TransformStream({
		transform(chunk, controller) {
			total += chunk.byteLength;
			if (total > maxBytes) throw createError("http-response");
			controller.enqueue(chunk);
		},
	});
}

function boundSseEvents(
	maxBytes: number,
	createError: BoundedFetchErrorFactory,
	validateLine?: BoundedFetchSseLineValidator,
): TransformStream<Uint8Array, Uint8Array> {
	let eventBytes = 0;
	let lineHasContent = false;
	let pendingCarriageReturn = false;
	let pendingLineIsBlank = false;
	let isFirstLine = true;
	let line: number[] | undefined = validateLine === undefined ? undefined : [];

	const count = (byte: number): void => {
		eventBytes += 1;
		line?.push(byte);
		if (eventBytes > maxBytes) throw createError("sse-event");
	};
	const finishLine = (isBlank: boolean, admitLine?: (bytes: readonly number[]) => void): void => {
		if (line !== undefined) {
			admitLine?.(line);
			line = [];
		}
		isFirstLine = false;
		lineHasContent = false;
		pendingCarriageReturn = false;
		if (isBlank) eventBytes = 0;
	};

	return new TransformStream({
		transform(chunk, controller) {
			const maxOutputBytes = (line?.length ?? 0) + chunk.byteLength;
			let output: Uint8Array | undefined;
			let outputBytes = 0;
			const admitLine =
				validateLine === undefined
					? undefined
					: (bytes: readonly number[]) => {
							output ??= new Uint8Array(maxOutputBytes);
							const start = outputBytes;
							const end = start + bytes.length;
							output.set(bytes, start);
							validateLine(output.subarray(start, end), isFirstLine);
							outputBytes = end;
						};
			const releaseOutput = (copyExact = false): void => {
				if (output !== undefined && outputBytes > 0) {
					controller.enqueue(copyExact ? output.slice(0, outputBytes) : output.subarray(0, outputBytes));
				}
			};
			try {
				for (const byte of chunk) {
					if (pendingCarriageReturn) {
						if (byte === 10) {
							count(byte);
							finishLine(pendingLineIsBlank, admitLine);
							continue;
						}
						finishLine(pendingLineIsBlank, admitLine);
					}

					count(byte);
					if (byte === 13) {
						pendingCarriageReturn = true;
						pendingLineIsBlank = !lineHasContent;
					} else if (byte === 10) {
						finishLine(!lineHasContent, admitLine);
					} else {
						lineHasContent = true;
					}
				}
			} catch (error) {
				try {
					releaseOutput(true);
				} catch {
					// A release failure cannot replace the validator or capacity error.
				}
				throw error;
			}
			if (validateLine === undefined) {
				controller.enqueue(chunk);
			} else {
				releaseOutput();
			}
		},
		flush(controller) {
			if (line === undefined || line.length === 0) return;
			controller.enqueue(Uint8Array.from(line));
		},
	});
}

function assertPositiveSafeInteger(value: number, name: string): void {
	if (!Number.isSafeInteger(value) || value <= 0) throw new TypeError(`${name} must be a positive safe integer.`);
}
