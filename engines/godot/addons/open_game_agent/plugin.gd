@tool
extends EditorPlugin

# The runtime is exposed by OpenGameAgentNode in the .NET assembly. Keeping this
# EditorPlugin intentionally side-effect free makes the add-on discoverable and
# allows projects to enable or disable it without changing scenes or settings.
