extends SceneTree


func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() != 2:
		push_error("Expected source SVG and destination PNG paths.")
		quit(2)
		return

	var source := args[0]
	var destination := args[1]
	var content := FileAccess.get_file_as_bytes(source)
	if content.is_empty():
		push_error("Could not read SVG: " + source)
		quit(3)
		return

	var image := Image.new()
	var load_error := image.load_svg_from_buffer(content, 1.0)
	if load_error != OK:
		push_error("Could not render SVG: " + error_string(load_error))
		quit(4)
		return

	var save_error := image.save_png(destination)
	if save_error != OK:
		push_error("Could not save PNG: " + error_string(save_error))
		quit(5)
		return

	quit(0)
