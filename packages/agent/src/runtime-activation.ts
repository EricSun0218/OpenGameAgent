import { GameAgent } from "./game-agent.js";
import {
	bindRuntimeGameAgentAuthority,
	type RuntimeGameAgentAuthority,
	releaseRuntimeGameAgentActivation,
} from "./runtime-activation-registry.js";
import type { GameAgentOptions } from "./types.js";

export interface RuntimeGameAgentActivation {
	readonly agent: GameAgent;
	release(): boolean;
}

export function createRuntimeGameAgentActivation(
	options: GameAgentOptions,
	authority?: RuntimeGameAgentAuthority,
): RuntimeGameAgentActivation {
	const agent = new GameAgent(options);
	if (authority) bindRuntimeGameAgentAuthority(agent, authority);
	return Object.freeze({
		agent,
		release: () => releaseRuntimeGameAgentActivation(agent),
	});
}
