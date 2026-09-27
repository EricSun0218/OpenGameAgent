export function isAllowedMcpHttpUrl(url: URL): boolean {
	if (url.username !== "" || url.password !== "" || url.hash !== "") return false;
	if (url.protocol === "https:") return true;
	return url.protocol === "http:" && isLoopbackHost(url.hostname);
}

function isLoopbackHost(hostname: string): boolean {
	const normalized = hostname.toLowerCase().replace(/^\[|\]$/gu, "");
	if (normalized === "localhost" || normalized === "::1") return true;
	const parts = normalized.split(".");
	return (
		parts.length === 4 && parts.every((part) => /^\d{1,3}$/u.test(part) && Number(part) <= 255) && parts[0] === "127"
	);
}
