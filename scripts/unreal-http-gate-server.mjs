import { writeFileSync } from "node:fs";
import { createServer } from "node:net";

const stateArgument = process.argv.find((argument) => argument.startsWith("--state="));
if (!stateArgument) throw new Error("Pass --state=<gate state path>.");
const statePath = stateArgument.slice("--state=".length);
const fixture = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
const attachmentId = "img_4c4b6a3be1314ab86138bef4314dde022e600960d8689a2c8f8631802d20dab6";
const deferredPending = JSON.stringify({
	deferred: {
		operationId: "operation",
		inputId: "input",
		coordinate: { sessionId: "gate-session", actorId: "gate-actor", runId: "run", turn: "1" },
	},
});
const deferredAccepted = JSON.stringify({
	runId: "run",
	initialCoordinate: { sessionId: "gate-session", actorId: "gate-actor", runId: "run", turn: "1" },
});
const actionAuthority = { timelineId: "world", generation: "2", revision: "8" };
function actionRecord(actorId, operationId, attempt, claimToken, status) {
	return {
		sessionId: "gate-session",
		actorId,
		operationId,
		actionName: "world.write",
		attempt,
		status,
		claim: { claimToken, attempt, expiresAt: "9999-12-31T23:59:59.000Z" },
		payload: { value: 1 },
		expectedAuthority: actionAuthority,
	};
}
const dispatchedAction = actionRecord("gate-actor", "operation-network", "1", "claim-1", "claimed");
const currentAction = actionRecord("gate-actor", "operation-network", "2", "claim-2", "uncertain");
const exhaustAction = actionRecord("gate-exhaust-actor", "operation-exhaust", "1", "claim-exhaust", "claimed");
const invalidRecoveryAction = actionRecord(
	"gate-invalid-recovery-actor",
	"operation-invalid-recovery",
	"1",
	"claim-invalid-recovery",
	"claimed",
);
const state = {
	sourceRequests: [],
	targetConnections: 0,
	targetBytes: 0,
	serverError: null,
};

function persistState() {
	writeFileSync(statePath, `${JSON.stringify(state, null, "\t")}\n`, "utf8");
}

function recordServerError(error) {
	state.serverError = error instanceof Error ? error.message : String(error);
	persistState();
}

function handleSocketError(error) {
	if (error?.code !== "ECONNRESET" && error?.code !== "EPIPE") recordServerError(error);
}

function sendJson(socket, status, reason, body) {
	const encoded = Buffer.from(body, "utf8");
	socket.end(
		Buffer.concat([
			Buffer.from(
				`HTTP/1.1 ${status} ${reason}\r\nContent-Type: application/json\r\nContent-Length: ${encoded.length}\r\nConnection: close\r\n\r\n`,
				"ascii",
			),
			encoded,
		]),
	);
}

function listen(server) {
	return new Promise((resolve, reject) => {
		server.once("error", reject);
		server.listen({ host: "127.0.0.1", port: 0 }, () => {
			server.off("error", reject);
			resolve(server.address().port);
		});
	});
}

const target = createServer((socket) => {
	socket.on("error", handleSocketError);
	state.targetConnections += 1;
	persistState();
	socket.on("data", (data) => {
		state.targetBytes += data.length;
		persistState();
		socket.end("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
	});
});
const targetPort = await listen(target);
target.on("error", recordServerError);

const source = createServer((socket) => {
	socket.on("error", handleSocketError);
	let received = Buffer.alloc(0);
	let handled = false;
	socket.on("data", (data) => {
		if (handled) return;
		received = Buffer.concat([received, data]);
		const headerEnd = received.indexOf("\r\n\r\n");
		if (headerEnd < 0) return;
		const headerText = received.subarray(0, headerEnd).toString("latin1");
		const lines = headerText.split("\r\n");
		const [method, path] = lines[0].split(" ");
		const headers = Object.fromEntries(
			lines.slice(1).map((line) => {
				const separator = line.indexOf(":");
				return [line.slice(0, separator).toLowerCase(), line.slice(separator + 1).trim()];
			}),
		);
		const contentLength = Number.parseInt(headers["content-length"] ?? "0", 10);
		const bodyStart = headerEnd + 4;
		if (received.length < bodyStart + contentLength) return;
		handled = true;
		state.sourceRequests.push({
			method,
			path,
			authorization: headers.authorization ?? null,
			body: received.subarray(bodyStart, bodyStart + contentLength).toString("utf8"),
		});
		persistState();

		if (path === "/redirect") {
			socket.end(
				`HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:${targetPort}/followed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n`,
			);
			return;
		}
		if (path === "/attachment") {
			socket.end(
				Buffer.concat([
					Buffer.from(
						`HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: ${fixture.length}\r\nConnection: close\r\n\r\n`,
						"ascii",
					),
					fixture,
				]),
			);
			return;
		}
		if (path === "/runtime/v1/attachments/read") {
			socket.end(
				Buffer.concat([
					Buffer.from(
						`HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: ${fixture.length}\r\nETag: "${attachmentId}"\r\nCache-Control: private, no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Digest: sha-256=:TEtqO+ExSrhhOL70MU3eAi5gCWDYaJosj4YxgC0g2rY=:\r\nConnection: close\r\n\r\n`,
						"ascii",
					),
					fixture,
				]),
			);
			return;
		}
		if (path === "/runtime/v1/actions/next") {
			const { actorId } = JSON.parse(state.sourceRequests.at(-1).body);
			if (actorId === "gate-actor") {
				sendJson(socket, 200, "OK", JSON.stringify({ delivery: { kind: "dispatch", action: dispatchedAction } }));
				return;
			}
			if (actorId === "gate-exhaust-actor") {
				sendJson(socket, 200, "OK", JSON.stringify({ delivery: { kind: "dispatch", action: exhaustAction } }));
				return;
			}
			sendJson(socket, 200, "OK", '{"delivery":null}');
			return;
		}
		if (path === "/runtime/v1/actions/recovery") {
			const { actorId } = JSON.parse(state.sourceRequests.at(-1).body);
			if (actorId === "gate-invalid-pending-recovery-actor") {
				const action = actionRecord(actorId, "operation-invalid-pending-recovery", "1", "claim-pending", "pending");
				sendJson(socket, 200, "OK", JSON.stringify({ delivery: { kind: "reconcile", action } }));
				return;
			}
			if (actorId === "gate-invalid-recovery-actor") {
				sendJson(socket, 200, "OK", JSON.stringify({ delivery: { kind: "dispatch", action: invalidRecoveryAction } }));
				return;
			}
			sendJson(socket, 200, "OK", '{"delivery":null}');
			return;
		}
		if (path === "/runtime/v1/actions/receipt") {
			const { actorId } = JSON.parse(state.sourceRequests.at(-1).body);
			if (actorId === "gate-exhaust-actor") {
				socket.end();
				return;
			}
			sendJson(socket, 409, "Conflict", '{"error":{"code":"action_claim_conflict","message":"conflict"}}');
			return;
		}
		if (path === "/runtime/v1/actions/get") {
			sendJson(socket, 200, "OK", JSON.stringify({ action: currentAction }));
			return;
		}
		if (path === "/runtime/v1/actions/reconcile") {
			const reconciliationCount = state.sourceRequests.filter((request) => request.path === path).length;
			if (reconciliationCount <= 2) {
				sendJson(socket, 503, "Service Unavailable", '{"error":{"code":"runtime_unavailable","message":"retry"}}');
				return;
			}
			sendJson(socket, 200, "OK", "{}");
			return;
		}
		if (path === "/runtime/v1/deferred/read") {
			const readCount = state.sourceRequests.filter((request) => request.path === path).length;
			sendJson(socket, 200, "OK", readCount === 1 ? deferredPending : '{"deferred":null}');
			return;
		}
		if (path === "/runtime/v1/deferred/resume") {
			const operationId = JSON.parse(state.sourceRequests.at(-1).body).operationId;
			if (operationId === "conflict") {
				sendJson(socket, 409, "Conflict", '{"error":{"code":"deferred_busy","message":"busy"}}');
				return;
			}
			if (operationId === "missing") {
				sendJson(socket, 404, "Not Found", '{"error":{"code":"deferred_not_found","message":"missing"}}');
				return;
			}
			if (operationId === "malformed") {
				sendJson(socket, 500, "Internal Server Error", "not-json");
				return;
			}
			if (operationId === "partial") {
				sendJson(socket, 500, "Internal Server Error", '{"error":{"code":"deferred_busy"}}');
				return;
			}
			if (operationId === "redirect") {
				socket.end(
					`HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:${targetPort}/followed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n`,
				);
				return;
			}
			sendJson(socket, 202, "Accepted", deferredAccepted);
			return;
		}
		if (path === "/runtime/v1/deferred/abandon") {
			const abandonCount = state.sourceRequests.filter((request) => request.path === path).length;
			const providerCancellation = ["completed", "failed", "unavailable"][abandonCount - 1];
			sendJson(socket, 200, "OK", JSON.stringify({ operationId: "operation", providerCancellation }));
			return;
		}
		if (path === "/error-length") {
			socket.end(
				Buffer.concat([
					Buffer.from(
						`HTTP/1.1 400 Bad Request\r\nContent-Length: ${fixture.length}\r\nConnection: close\r\n\r\n`,
						"ascii",
					),
					fixture,
				]),
			);
			return;
		}
		if (path === "/error-chunked") {
			socket.end(
				Buffer.concat([
					Buffer.from(
						"HTTP/1.1 400 Bad Request\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n8\r\n",
						"ascii",
					),
					fixture,
					Buffer.from("\r\n0\r\n\r\n", "ascii"),
				]),
			);
			return;
		}
		socket.end("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
	});
});
const sourcePort = await listen(source);
source.on("error", recordServerError);
persistState();
process.stdout.write(`${JSON.stringify({ baseUrl: `http://127.0.0.1:${sourcePort}` })}\n`);

function close() {
	source.close();
	target.close();
}

process.on("SIGINT", close);
process.on("SIGTERM", close);
