import type { RunCoordinate } from "@opengameagent/protocol";
import type { GameAgent } from "./game-agent.js";

export interface RuntimeGameAgentAuthority {
	assertRunAuthorityOpen(coordinate: RunCoordinate): Promise<void>;
}
const activationAuthorities = new WeakMap<GameAgent, RuntimeGameAgentAuthority>();
export function bindRuntimeGameAgentAuthority(agent: GameAgent, authority: RuntimeGameAgentAuthority): void {
	activationAuthorities.set(
		agent,
		Object.freeze({ assertRunAuthorityOpen: authority.assertRunAuthorityOpen.bind(authority) }),
	);
}
export function runtimeGameAgentAuthority(agent: GameAgent): RuntimeGameAgentAuthority | undefined {
	return activationAuthorities.get(agent);
}

const activationReleases = new WeakMap<GameAgent, () => void>();

export function registerRuntimeGameAgentActivationRelease(agent: GameAgent, release: () => void): void {
	activationReleases.set(agent, release);
}

export function releaseRuntimeGameAgentActivation(agent: GameAgent): boolean {
	const release = activationReleases.get(agent);
	if (!release) return false;
	activationReleases.delete(agent);
	release();
	return true;
}
