@tool
extends EditorPlugin

const AUTOLOAD_NAME := "GameAgentRuntime"
const AUTOLOAD_SCENE := "res://addons/game_agent_runtime/runtime/GameAgentRuntimeNode.tscn"
const AUTOLOAD_SETTING := "autoload/" + AUTOLOAD_NAME
const AUTHORING_SCRIPT := (
	"res://addons/game_agent_runtime/authoring/GodotWorldAuthoringBridge.cs"
)

var _dock: VBoxContainer
var _source_path: LineEdit
var _package_id: LineEdit
var _content_version: LineEdit
var _output_path: LineEdit
var _character_card_path: LineEdit
var _character_content_id: LineEdit
var _lore_book_path: LineEdit
var _lore_content_id: LineEdit
var _agent_binding_id: LineEdit
var _accept_imports: CheckBox
var _status: RichTextLabel
var _authoring


func _enter_tree() -> void:
	_dock = VBoxContainer.new()
	_dock.name = "Agent World"
	_dock.custom_minimum_size = Vector2(360, 560)

	var title := Label.new()
	title.text = "Interactive World v1"
	title.add_theme_font_size_override("font_size", 18)
	_dock.add_child(title)

	var description := Label.new()
	description.text = (
		"Create, validate, and package inert world JSON. "
		+ "Compilation never grants tools or executes imported code."
	)
	description.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_dock.add_child(description)

	_source_path = _field(
		"Source directory",
		"res://world",
		"Directory containing world.json and optional catalogs."
	)
	_package_id = _field(
		"Package ID",
		"my-game.world",
		"Stable package identity used by saves."
	)
	_content_version = _field(
		"Content version",
		"1",
		"Authored content version."
	)
	_output_path = _field(
		"Package output",
		"res://build/world.gaworld",
		"Deterministic package archive destination."
	)
	_character_card_path = _field(
		"Character Card path (optional JSON or PNG)",
		"",
		"Empty means no character import is bound."
	)
	_character_content_id = _field(
		"Character content ID",
		"",
		"Portable ID required when a Character Card path is set."
	)
	_lore_book_path = _field(
		"Lorebook path (optional JSON)",
		"",
		"Empty means no lore import is bound."
	)
	_lore_content_id = _field(
		"Lore content ID",
		"",
		"Portable ID required when a Lorebook path is set."
	)
	_agent_binding_id = _field(
		"Agent binding ID",
		"",
		"Agent ID that references the imported character and lore data."
	)
	_accept_imports = CheckBox.new()
	_accept_imports.text = "Accept imports and warnings as untrusted data"
	_accept_imports.tooltip_text = (
		"Required to publish a package with imported content. "
		+ "Acceptance grants no tools, skills, credentials, or code execution."
	)
	_dock.add_child(_accept_imports)

	var actions := HBoxContainer.new()
	var create_button := Button.new()
	create_button.text = "Create starter"
	create_button.tooltip_text = "Requires an empty source directory."
	create_button.pressed.connect(_create_starter)
	actions.add_child(create_button)

	var validate_button := Button.new()
	validate_button.text = "Validate world"
	validate_button.pressed.connect(_validate_source)
	actions.add_child(validate_button)

	var build_button := Button.new()
	build_button.text = "Build package"
	build_button.pressed.connect(_build_package)
	actions.add_child(build_button)
	_dock.add_child(actions)

	var import_actions := HBoxContainer.new()
	var validate_imports_button := Button.new()
	validate_imports_button.text = "Validate imports"
	validate_imports_button.pressed.connect(_validate_imports)
	import_actions.add_child(validate_imports_button)

	var build_bound_button := Button.new()
	build_bound_button.text = "Build bound package"
	build_bound_button.pressed.connect(_build_bound_package)
	import_actions.add_child(build_bound_button)
	_dock.add_child(import_actions)

	_status = RichTextLabel.new()
	_status.fit_content = true
	_status.selection_enabled = true
	_status.custom_minimum_size = Vector2(0, 140)
	_status.text = "Ready."
	_dock.add_child(_status)

	add_control_to_dock(DOCK_SLOT_RIGHT_BL, _dock)


func _exit_tree() -> void:
	if is_instance_valid(_dock):
		remove_control_from_docks(_dock)
		_dock.queue_free()
	_authoring = null


func _enable_plugin() -> void:
	if not ProjectSettings.has_setting(AUTOLOAD_SETTING):
		add_autoload_singleton(AUTOLOAD_NAME, AUTOLOAD_SCENE)
		return

	if _configured_autoload_scene() != AUTOLOAD_SCENE:
		push_error(
			"Cannot register Game Agent Runtime: the GameAgentRuntime Autoload name "
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


func _field(label_text: String, value: String, tooltip: String) -> LineEdit:
	var label := Label.new()
	label.text = label_text
	_dock.add_child(label)
	var field := LineEdit.new()
	field.text = value
	field.tooltip_text = tooltip
	field.clear_button_enabled = true
	_dock.add_child(field)
	return field


func _create_starter() -> void:
	_show_status(
		_invoke_authoring(
			&"create_starter_world",
			[_filesystem_path(_source_path.text)]
		)
	)
	get_editor_interface().get_resource_filesystem().scan()


func _validate_source() -> void:
	_show_status(
		_invoke_authoring(
			&"validate_world_source",
			[
				_filesystem_path(_source_path.text),
				_package_id.text.strip_edges(),
				_content_version.text.strip_edges()
			]
		)
	)


func _build_package() -> void:
	_show_status(
		_invoke_authoring(
			&"build_world_package_file",
			[
				_filesystem_path(_source_path.text),
				_package_id.text.strip_edges(),
				_content_version.text.strip_edges(),
				_filesystem_path(_output_path.text)
			]
		)
	)
	get_editor_interface().get_resource_filesystem().scan()


func _validate_imports() -> void:
	_show_status(
		_invoke_authoring(
			&"validate_imports",
			[
				_filesystem_path(_character_card_path.text),
				_character_content_id.text.strip_edges(),
				_filesystem_path(_lore_book_path.text),
				_lore_content_id.text.strip_edges()
			]
		)
	)


func _build_bound_package() -> void:
	_show_status(
		_invoke_authoring(
			&"build_bound_world_package_file",
			[
				_filesystem_path(_source_path.text),
				_package_id.text.strip_edges(),
				_content_version.text.strip_edges(),
				_filesystem_path(_output_path.text),
				_filesystem_path(_character_card_path.text),
				_character_content_id.text.strip_edges(),
				_filesystem_path(_lore_book_path.text),
				_lore_content_id.text.strip_edges(),
				_agent_binding_id.text.strip_edges(),
				_accept_imports.button_pressed
			]
		)
	)
	get_editor_interface().get_resource_filesystem().scan()


func _invoke_authoring(method: StringName, arguments: Array) -> Dictionary:
	var bridge = _authoring_bridge()
	if bridge == null:
		return {
			"success": false,
			"message": (
				"The C# authoring bridge is not ready. Wait for the "
				+ ".NET build to finish, then try again."
			),
			"diagnostics": []
		}

	var result = bridge.callv(method, arguments)
	if result is Dictionary:
		return result
	return {
		"success": false,
		"message": "The C# authoring bridge returned an invalid result.",
		"diagnostics": []
	}


func _authoring_bridge():
	if is_instance_valid(_authoring):
		return _authoring
	var authoring_script = load(AUTHORING_SCRIPT)
	if (
		authoring_script == null
		or not authoring_script.can_instantiate()
	):
		return null
	_authoring = authoring_script.new()
	return _authoring


func _show_status(result: Dictionary) -> void:
	var lines := PackedStringArray()
	lines.append(str(result.get("message", "Unknown authoring result.")))
	if result.get("success", false):
		if result.has("package_digest"):
			lines.append("Package: " + str(result.get("package_id", "")))
			lines.append("Digest: " + str(result.get("package_digest", "")))
		if result.has("output_path"):
			lines.append("Output: " + str(result.get("output_path", "")))
	var displayed := 0
	for diagnostic in result.get("diagnostics", []):
		if displayed >= 128:
			lines.append("[WARNING] diagnostics_truncated: Additional diagnostics omitted.")
			break
		lines.append(
			"[%s] %s %s: %s"
			% [
				str(diagnostic.get("severity", "error")).to_upper(),
				str(diagnostic.get("code", "unknown")),
				str(diagnostic.get("path", "$authoring")),
				str(diagnostic.get("message", "")).left(2048)
			]
		)
		displayed += 1
	_status.text = "\n".join(lines)


func _filesystem_path(path: String) -> String:
	var normalized := path.strip_edges()
	if normalized.begins_with("res://") or normalized.begins_with("user://"):
		return ProjectSettings.globalize_path(normalized)
	return normalized
