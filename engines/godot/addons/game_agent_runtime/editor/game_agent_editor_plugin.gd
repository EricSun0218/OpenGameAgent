@tool
extends EditorPlugin

const AUTOLOAD_NAME := "GameAgentRuntime"
const AUTOLOAD_SCENE := "res://addons/game_agent_runtime/runtime/GameAgentRuntimeNode.tscn"
const AUTOLOAD_SETTING := "autoload/" + AUTOLOAD_NAME


func _enable_plugin() -> void:
	if not ProjectSettings.has_setting(AUTOLOAD_SETTING):
		add_autoload_singleton(AUTOLOAD_NAME, AUTOLOAD_SCENE)
		return

	if _configured_autoload_scene() != AUTOLOAD_SCENE:
		push_error(
			"Cannot register OpenGameAgent: the GameAgentRuntime Autoload name "
			+ "is already used by another scene."
		)


func _disable_plugin() -> void:
	if (
		ProjectSettings.has_setting(AUTOLOAD_SETTING)
		and _configured_autoload_scene() == AUTOLOAD_SCENE
	):
		remove_autoload_singleton(AUTOLOAD_NAME)


func _configured_autoload_scene() -> String:
	return str(ProjectSettings.get_setting(AUTOLOAD_SETTING, "")).trim_prefix("*")
