/** Add host-approved MCP resource headers only to the exact configured resource endpoint. */
type FetchInput = Parameters<typeof fetch>[0];
type HeaderInput = ConstructorParameters<typeof Headers>[0];

export function createMcpResourceScopedFetch(
	upstream: typeof fetch,
	resourceUrl: URL,
	resourceHeaders: Headers,
): typeof fetch {
	const expectedHref = resourceUrl.href;
	const approvedHeaders = new Headers(resourceHeaders);
	return async (input, init) => {
		if (resolveInputHref(input) !== expectedHref) return await upstream(input, init);

		const headers = new Headers(headersFromInput(input));
		new Headers(init?.headers).forEach((value, key) => {
			headers.set(key, value);
		});
		approvedHeaders.forEach((value, key) => {
			headers.set(key, value);
		});
		return await upstream(input, { ...init, headers });
	};
}

function resolveInputHref(input: FetchInput): string | undefined {
	try {
		if (typeof input === "string") return new URL(input).href;
		if (input instanceof URL) return input.href;
		return new URL(input.url).href;
	} catch {
		return undefined;
	}
}

function headersFromInput(input: FetchInput): HeaderInput | undefined {
	if (typeof input === "string" || input instanceof URL) return undefined;
	return input.headers;
}
