const pluginVariablePattern = /\$\{PLUGIN_(ROOT|DATA)\}/gu;

/** Agent Plugins substitution is one non-recursive pass over the original string. */
export function expandAgentPluginValue(value: string, pluginRoot: string, pluginDataPath: string): string {
	return value.replace(pluginVariablePattern, (_match, name: string) =>
		name === "ROOT" ? pluginRoot : pluginDataPath,
	);
}
