const { SqliteAuthority } = await import("../../src/sqlite-authority.ts");

const authority = new SqliteAuthority({
	path: process.argv[2],
	busyTimeoutMs: 0,
	verifyOnOpen: "full",
	ownership: "exclusive",
});

process.stdout.write("ready\n");
process.stdin.resume();
process.stdin.on("end", () => authority.close());
