import {
	createServer,
	type IncomingHttpHeaders,
	type IncomingMessage,
	type Server,
	type ServerResponse,
} from "node:http";
import type { AddressInfo } from "node:net";
import type { GameActionJournal } from "@opengameagent/actions";
import type { GameToolApprovalBroker, GameToolApprovalResponse } from "@opengameagent/approvals";
import {
	type GameActionReceipt,
	type GameConversationMessage,
	type GameConversationStore,
	type GameEventViewer,
	type GameImageAttachmentReference,
	type GameImageAttachmentStore,
	type GameInput,
	type GameRunCoordinate,
	type GameSessionKey,
	type JsonObject,
	projectGameAgentEvent,
} from "@opengameagent/protocol";
import type { GameAgentRuntime, GameRuntimeEventStore, GameUsageLedger } from "@opengameagent/runtime";

export type GameServerOperation =
	| "run"
	| "steer"
	| "follow-up"
	| "abort"
	| "usage"
	| "actions"
	| "transcript"
	| "attachments"
	| "approvals";

export interface GameServerPrincipal {
	id: string;
}

export interface GameServerAuthenticationRequest {
	method: string;
	path: string;
	headers: IncomingHttpHeaders;
	credential?: JsonObject;
}

export interface GameServerAuthenticator {
	authenticate(request: GameServerAuthenticationRequest, signal: AbortSignal): Promise<GameServerPrincipal | undefined>;
}

export interface GameServerAuthorizer {
	authorize(
		principal: GameServerPrincipal,
		operation: GameServerOperation,
		session: GameSessionKey,
		signal: AbortSignal,
	): Promise<GameEventViewer | undefined>;
}

export class OwnerGameServerAuthorizer implements GameServerAuthorizer {
	async authorize(
		principal: GameServerPrincipal,
		_operation: GameServerOperation,
		session: GameSessionKey,
	): Promise<GameEventViewer | undefined> {
		return principal.id === session.ownerId ? { principalId: principal.id } : undefined;
	}
}

export interface GameAgentServerOptions {
	runtime: GameAgentRuntime;
	authenticator: GameServerAuthenticator;
	authorizer: GameServerAuthorizer;
	actionJournal?: GameActionJournal;
	conversationStore?: GameConversationStore;
	imageAttachments?: GameImageAttachmentStore;
	eventStore?: GameRuntimeEventStore;
	usageLedger?: GameUsageLedger;
	approvalBroker?: GameToolApprovalBroker;
	maximumRequestBytes?: number;
}

interface RunBody {
	authentication?: JsonObject;
	input: GameInput;
	runId?: string;
}

interface ControlBody {
	authentication?: JsonObject;
	session: GameSessionKey;
	expected: GameRunCoordinate;
	input?: GameInput;
}

interface SessionBody {
	authentication?: JsonObject;
	session: GameSessionKey;
}

interface ActionClaimBody extends SessionBody {
	maximum?: number;
}

interface ActionReceiptBody extends SessionBody {
	receipt: GameActionReceipt;
}

interface ActionReconcileBody extends SessionBody {
	operationId: string;
}

interface TranscriptBody extends SessionBody {
	cursor?: string;
	limit?: number;
}

interface AttachmentReadBody extends SessionBody {
	attachmentId: string;
}

interface RunEventsBody extends SessionBody {
	runId: string;
	afterSequence?: number;
	maximum?: number;
}

interface ApprovalListBody extends SessionBody {
	maximum?: number;
}

interface ApprovalResponseBody extends SessionBody {
	approvalId: string;
	expectedRevision: number;
	decision: "approve" | "deny";
	reason?: string;
}

function isObject(value: unknown): value is Record<string, unknown> {
	return value !== null && typeof value === "object" && !Array.isArray(value);
}

export class GameAgentServer implements AsyncDisposable {
	private readonly server: Server;
	private readonly maximumRequestBytes: number;

	constructor(private readonly options: GameAgentServerOptions) {
		this.maximumRequestBytes = options.maximumRequestBytes ?? 1024 * 1024;
		if (!Number.isInteger(this.maximumRequestBytes) || this.maximumRequestBytes < 1024) {
			throw new RangeError("maximumRequestBytes must be an integer of at least 1024 bytes.");
		}
		this.server = createServer((request, response) => {
			void this.handle(request, response);
		});
	}

	async listen(port = 0, host = "127.0.0.1"): Promise<{ host: string; port: number }> {
		await new Promise<void>((resolve, reject) => {
			this.server.once("error", reject);
			this.server.listen(port, host, () => {
				this.server.off("error", reject);
				resolve();
			});
		});
		const address = this.server.address() as AddressInfo;
		return { host: address.address, port: address.port };
	}

	async close(): Promise<void> {
		if (!this.server.listening) return;
		await new Promise<void>((resolve, reject) => {
			this.server.close((error) => (error ? reject(error) : resolve()));
		});
	}

	async [Symbol.asyncDispose](): Promise<void> {
		await this.close();
	}

	private async handle(request: IncomingMessage, response: ServerResponse): Promise<void> {
		const controller = new AbortController();
		request.once("aborted", () => controller.abort());
		response.once("close", () => {
			if (!response.writableEnded) controller.abort();
		});
		try {
			const path = new URL(request.url ?? "/", "http://localhost").pathname;
			if (request.method === "GET" && path === "/health") {
				this.json(response, 200, { status: "ok" });
				return;
			}
			if (request.method !== "POST") {
				this.json(response, 404, { error: "not-found" });
				return;
			}
			const body = await this.readBody(request, controller.signal);
			if (path === "/v1/runs/stream") {
				await this.run(request, response, path, body as unknown as RunBody, controller);
				return;
			}
			if (path === "/v1/actions/claim") {
				await this.claimActions(request, response, path, body as unknown as ActionClaimBody, controller.signal);
				return;
			}
			if (path === "/v1/actions/receipt") {
				await this.submitActionReceipt(
					request,
					response,
					path,
					body as unknown as ActionReceiptBody,
					controller.signal,
				);
				return;
			}
			if (path === "/v1/actions/reconcile") {
				await this.reconcileAction(request, response, path, body as unknown as ActionReconcileBody, controller.signal);
				return;
			}
			if (path === "/v1/sessions/transcript/read") {
				await this.readTranscript(request, response, path, body as unknown as TranscriptBody, controller.signal);
				return;
			}
			if (path === "/v1/sessions/attachments/read") {
				await this.readAttachment(request, response, path, body as unknown as AttachmentReadBody, controller.signal);
				return;
			}
			if (path === "/v1/runs/events/read") {
				await this.readRunEvents(request, response, path, body as unknown as RunEventsBody, controller.signal);
				return;
			}
			if (path === "/v1/sessions/usage") {
				await this.readUsage(request, response, path, body as unknown as SessionBody, controller.signal);
				return;
			}
			if (path === "/v1/tool-approvals/list") {
				await this.listApprovals(request, response, path, body as unknown as ApprovalListBody, controller.signal);
				return;
			}
			if (path === "/v1/tool-approvals/respond") {
				await this.respondApproval(request, response, path, body as unknown as ApprovalResponseBody, controller.signal);
				return;
			}
			const operations = new Map<string, GameServerOperation>([
				["/v1/control/steer", "steer"],
				["/v1/control/follow-up", "follow-up"],
				["/v1/control/abort", "abort"],
			]);
			const operation = operations.get(path);
			if (operation) {
				await this.control(request, response, path, operation, body as unknown as ControlBody, controller.signal);
				return;
			}
			this.json(response, 404, { error: "not-found" });
		} catch (error) {
			if (!response.headersSent) {
				this.json(response, error instanceof RangeError ? 413 : 400, { error: "invalid-request" });
			} else if (!response.writableEnded) {
				response.write(`event: error\ndata: ${JSON.stringify({ error: "run-failed" })}\n\n`);
				response.end();
			}
		}
	}

	private async run(
		request: IncomingMessage,
		response: ServerResponse,
		path: string,
		body: RunBody,
		controller: AbortController,
	): Promise<void> {
		if (!isObject(body) || !isObject(body.input) || !isObject(body.input.session))
			throw new TypeError("Invalid run body.");
		const authorized = await this.authorize(
			request,
			path,
			"run",
			body.input.session,
			body.authentication,
			controller.signal,
		);
		if (!authorized) {
			this.json(response, 403, { error: "forbidden" });
			return;
		}
		response.writeHead(200, {
			"content-type": "text/event-stream; charset=utf-8",
			"cache-control": "no-store",
			connection: "keep-alive",
		});
		for await (const event of this.options.runtime.run(body.input, {
			...(body.runId === undefined ? {} : { runId: body.runId }),
			signal: controller.signal,
		})) {
			const projected = projectGameAgentEvent(event, body.input.session, authorized);
			if (projected)
				response.write(`id: ${projected.eventId}\nevent: ${projected.type}\ndata: ${JSON.stringify(projected)}\n\n`);
		}
		response.end();
	}

	private async control(
		request: IncomingMessage,
		response: ServerResponse,
		path: string,
		operation: GameServerOperation,
		body: ControlBody,
		signal: AbortSignal,
	): Promise<void> {
		if (!isObject(body) || !isObject(body.session) || !isObject(body.expected))
			throw new TypeError("Invalid control body.");
		const authorized = await this.authorize(request, path, operation, body.session, body.authentication, signal);
		if (!authorized) {
			this.json(response, 403, { error: "forbidden" });
			return;
		}
		const result =
			operation === "abort"
				? this.options.runtime.abort(body.session, body.expected)
				: operation === "steer"
					? this.options.runtime.steer(body.session, body.expected, this.requireControlInput(body))
					: this.options.runtime.followUp(body.session, body.expected, this.requireControlInput(body));
		this.json(response, result.accepted ? 200 : 409, result);
	}

	private requireControlInput(body: ControlBody): GameInput {
		if (!body.input) throw new TypeError("This control operation requires input.");
		return body.input;
	}

	private async claimActions(
		request: IncomingMessage,
		response: ServerResponse,
		path: string,
		body: ActionClaimBody,
		signal: AbortSignal,
	): Promise<void> {
		const journal = this.options.actionJournal;
		if (!journal) {
			this.json(response, 404, { error: "action-exchange-disabled" });
			return;
		}
		if (!isObject(body) || !isObject(body.session)) throw new TypeError("Invalid action claim body.");
		if (!(await this.authorize(request, path, "actions", body.session, body.authentication, signal))) {
			this.json(response, 403, { error: "forbidden" });
			return;
		}
		const maximum = body.maximum ?? 1;
		if (!Number.isInteger(maximum) || maximum < 1 || maximum > 32)
			throw new RangeError("maximum must be between 1 and 32.");
		const pending = await journal.listPending(body.session, maximum, signal);
		const claims = [];
		for (const entry of pending) claims.push(await journal.claimDispatch(entry.intent.operationId, signal));
		this.json(response, 200, { claims });
	}

	private async submitActionReceipt(
		request: IncomingMessage,
		response: ServerResponse,
		path: string,
		body: ActionReceiptBody,
		signal: AbortSignal,
	): Promise<void> {
		const journal = this.options.actionJournal;
		if (!journal) {
			this.json(response, 404, { error: "action-exchange-disabled" });
			return;
		}
		if (!isObject(body) || !isObject(body.session) || !isObject(body.receipt)) {
			throw new TypeError("Invalid action receipt body.");
		}
		if (!(await this.authorize(request, path, "actions", body.session, body.authentication, signal))) {
			this.json(response, 403, { error: "forbidden" });
			return;
		}
		const entry = await journal.submitReceipt(body.receipt, signal);
		this.json(response, 200, { entry });
	}

	private async reconcileAction(
		request: IncomingMessage,
		response: ServerResponse,
		path: string,
		body: ActionReconcileBody,
		signal: AbortSignal,
	): Promise<void> {
		const journal = this.options.actionJournal;
		if (!journal) {
			this.json(response, 404, { error: "action-exchange-disabled" });
			return;
		}
		if (!isObject(body) || !isObject(body.session) || typeof body.operationId !== "string") {
			throw new TypeError("Invalid action reconcile body.");
		}
		if (!(await this.authorize(request, path, "actions", body.session, body.authentication, signal))) {
			this.json(response, 403, { error: "forbidden" });
			return;
		}
		const entry = await journal.read(body.operationId, signal);
		if (entry && !this.sameSession(entry.intent.session, body.session)) {
			this.json(response, 404, { error: "action-not-found" });
			return;
		}
		this.json(response, entry ? 200 : 404, entry ? { entry } : { error: "action-not-found" });
	}

	private async readTranscript(
		request: IncomingMessage,
		response: ServerResponse,
		path: string,
		body: TranscriptBody,
		signal: AbortSignal,
	): Promise<void> {
		const store = this.options.conversationStore;
		if (!store) {
			this.json(response, 404, { error: "transcript-disabled" });
			return;
		}
		if (!isObject(body) || !isObject(body.session)) throw new TypeError("Invalid transcript body.");
		const viewer = await this.authorize(request, path, "transcript", body.session, body.authentication, signal);
		if (!viewer) {
			this.json(response, 403, { error: "forbidden" });
			return;
		}
		const limit = body.limit ?? 50;
		if (!Number.isInteger(limit) || limit < 1 || limit > 100) throw new RangeError("limit must be between 1 and 100.");
		const snapshot = await store.read(body.session, signal);
		const start = this.decodeCursor(body.cursor, snapshot.revision);
		const canonical = snapshot.messages.slice(start, start + limit);
		const messages = canonical.flatMap((message) => this.projectTranscriptMessage(message, viewer));
		const nextIndex = start + canonical.length;
		this.json(response, 200, {
			revision: snapshot.revision,
			messages,
			nextCursor: nextIndex < snapshot.messages.length ? this.encodeCursor(snapshot.revision, nextIndex) : null,
		});
	}

	private async readRunEvents(
		request: IncomingMessage,
		response: ServerResponse,
		path: string,
		body: RunEventsBody,
		signal: AbortSignal,
	): Promise<void> {
		const store = this.options.eventStore;
		if (!store?.read) {
			this.json(response, 404, { error: "event-replay-disabled" });
			return;
		}
		if (!isObject(body) || !isObject(body.session) || typeof body.runId !== "string") {
			throw new TypeError("Invalid run events body.");
		}
		const viewer = await this.authorize(request, path, "run", body.session, body.authentication, signal);
		if (!viewer) {
			this.json(response, 403, { error: "forbidden" });
			return;
		}
		const afterSequence = body.afterSequence ?? 0;
		const maximum = body.maximum ?? 100;
		const events = await store.read(body.session, body.runId, afterSequence, maximum, signal);
		const projected = events.flatMap((event) => {
			const item = projectGameAgentEvent(event, body.session, viewer);
			return item ? [item] : [];
		});
		this.json(response, 200, {
			events: projected,
			gap: events.length > 0 && events[0]?.sequence !== afterSequence + 1,
			nextSequence: events.at(-1)?.sequence ?? afterSequence,
		});
	}

	private async readAttachment(
		request: IncomingMessage,
		response: ServerResponse,
		path: string,
		body: AttachmentReadBody,
		signal: AbortSignal,
	): Promise<void> {
		const attachments = this.options.imageAttachments;
		const conversations = this.options.conversationStore;
		if (!attachments || !conversations) {
			this.json(response, 404, { error: "attachments-disabled" });
			return;
		}
		if (!isObject(body) || !isObject(body.session) || typeof body.attachmentId !== "string") {
			throw new TypeError("Invalid attachment read body.");
		}
		if (!(await this.authorize(request, path, "attachments", body.session, body.authentication, signal))) {
			this.json(response, 403, { error: "forbidden" });
			return;
		}
		const snapshot = await conversations.read(body.session, signal);
		const reference = this.findAttachmentReference(snapshot.messages, body.attachmentId);
		if (!reference) {
			this.json(response, 404, { error: "attachment-not-found" });
			return;
		}
		const attachment = await attachments.read(reference.id, signal);
		if (!attachment || JSON.stringify(attachment.reference) !== JSON.stringify(reference)) {
			throw new Error("Authorized image attachment is missing or does not match its transcript reference.");
		}
		response.writeHead(200, {
			"content-type": reference.mimeType,
			"content-length": attachment.data.byteLength,
			"cache-control": "private, no-store",
			etag: `"sha256-${reference.sha256}"`,
			"x-opengameagent-attachment-id": reference.id,
			"x-opengameagent-image-width": reference.width,
			"x-opengameagent-image-height": reference.height,
		});
		response.end(Buffer.from(attachment.data));
	}

	private async readUsage(
		request: IncomingMessage,
		response: ServerResponse,
		path: string,
		body: SessionBody,
		signal: AbortSignal,
	): Promise<void> {
		const ledger = this.options.usageLedger;
		if (!ledger) {
			this.json(response, 404, { error: "usage-disabled" });
			return;
		}
		if (!isObject(body) || !isObject(body.session)) throw new TypeError("Invalid usage body.");
		if (!(await this.authorize(request, path, "usage", body.session, body.authentication, signal))) {
			this.json(response, 403, { error: "forbidden" });
			return;
		}
		this.json(response, 200, { summary: await ledger.summarize(body.session, signal) });
	}

	private async listApprovals(
		request: IncomingMessage,
		response: ServerResponse,
		path: string,
		body: ApprovalListBody,
		signal: AbortSignal,
	): Promise<void> {
		const broker = this.options.approvalBroker;
		if (!broker) {
			this.json(response, 404, { error: "tool-approvals-disabled" });
			return;
		}
		if (!isObject(body) || !isObject(body.session)) throw new TypeError("Invalid approval list body.");
		if (!(await this.authorize(request, path, "approvals", body.session, body.authentication, signal))) {
			this.json(response, 403, { error: "forbidden" });
			return;
		}
		const maximum = body.maximum ?? 32;
		this.json(response, 200, { approvals: await broker.listPending(body.session, maximum, signal) });
	}

	private async respondApproval(
		request: IncomingMessage,
		response: ServerResponse,
		path: string,
		body: ApprovalResponseBody,
		signal: AbortSignal,
	): Promise<void> {
		const broker = this.options.approvalBroker;
		if (!broker) {
			this.json(response, 404, { error: "tool-approvals-disabled" });
			return;
		}
		if (
			!isObject(body) ||
			!isObject(body.session) ||
			typeof body.approvalId !== "string" ||
			!Number.isInteger(body.expectedRevision) ||
			(body.decision !== "approve" && body.decision !== "deny")
		)
			throw new TypeError("Invalid approval response body.");
		if (!(await this.authorize(request, path, "approvals", body.session, body.authentication, signal))) {
			this.json(response, 403, { error: "forbidden" });
			return;
		}
		const approvalResponse: GameToolApprovalResponse = {
			session: body.session,
			approvalId: body.approvalId,
			expectedRevision: body.expectedRevision,
			decision: body.decision,
			...(body.reason === undefined ? {} : { reason: body.reason }),
		};
		this.json(response, 200, { approval: await broker.respond(approvalResponse, signal) });
	}

	private projectTranscriptMessage(message: GameConversationMessage, viewer: GameEventViewer): unknown[] {
		if (viewer.internal === true) return [message];
		if (message.role === "toolResult") return [];
		if (message.role === "summary") return [message];
		if (typeof message.content === "string") return [message];
		const content = message.content.flatMap((part): unknown[] => {
			if (part.type === "reasoning" || part.type === "toolCall") return [];
			if (part.type === "image") return [{ type: "image", mimeType: part.mimeType, inline: false }];
			if (part.type === "imageRef") return [{ type: "imageRef", attachment: part.attachment }];
			return [part];
		});
		return [{ ...message, content }];
	}

	private findAttachmentReference(
		messages: readonly GameConversationMessage[],
		attachmentId: string,
	): GameImageAttachmentReference | undefined {
		for (const message of messages) {
			if (message.role === "summary" || typeof message.content === "string") continue;
			for (const part of message.content) {
				if (part.type === "imageRef" && part.attachment.id === attachmentId) return part.attachment;
			}
		}
		return undefined;
	}

	private encodeCursor(revision: number, index: number): string {
		return Buffer.from(`${revision}:${index}`, "utf8").toString("base64url");
	}

	private decodeCursor(cursor: string | undefined, revision: number): number {
		if (cursor === undefined) return 0;
		const decoded = Buffer.from(cursor, "base64url").toString("utf8");
		const match = /^(\d+):(\d+)$/.exec(decoded);
		if (!match) throw new TypeError("Invalid transcript cursor.");
		const cursorRevision = Number(match[1]);
		const index = Number(match[2]);
		if (cursorRevision !== revision) throw new Error("Transcript cursor revision is stale.");
		if (!Number.isSafeInteger(index) || index < 0) throw new TypeError("Invalid transcript cursor.");
		return index;
	}

	private sameSession(left: GameSessionKey, right: GameSessionKey): boolean {
		return (
			left.worldId === right.worldId &&
			left.saveId === right.saveId &&
			left.timelineId === right.timelineId &&
			left.generation === right.generation &&
			left.ownerId === right.ownerId &&
			left.sessionId === right.sessionId &&
			left.actorId === right.actorId
		);
	}

	private async authorize(
		request: IncomingMessage,
		path: string,
		operation: GameServerOperation,
		session: GameSessionKey,
		credential: JsonObject | undefined,
		signal: AbortSignal,
	): Promise<GameEventViewer | undefined> {
		const principal = await this.options.authenticator.authenticate(
			{
				method: request.method ?? "",
				path,
				headers: request.headers,
				...(credential === undefined ? {} : { credential }),
			},
			signal,
		);
		if (!principal) return undefined;
		return this.options.authorizer.authorize(principal, operation, session, signal);
	}

	private async readBody(request: IncomingMessage, signal: AbortSignal): Promise<Record<string, unknown>> {
		const chunks: Buffer[] = [];
		let bytes = 0;
		for await (const chunk of request) {
			signal.throwIfAborted();
			const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk as Uint8Array);
			bytes += buffer.byteLength;
			if (bytes > this.maximumRequestBytes) throw new RangeError("Request body is too large.");
			chunks.push(buffer);
		}
		const parsed: unknown = JSON.parse(Buffer.concat(chunks).toString("utf8"));
		if (!isObject(parsed)) throw new TypeError("A JSON object body is required.");
		return parsed;
	}

	private json(response: ServerResponse, status: number, body: unknown): void {
		response.writeHead(status, { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" });
		response.end(JSON.stringify(body));
	}
}
