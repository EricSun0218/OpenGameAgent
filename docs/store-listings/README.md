# Store listing source

This directory is the canonical source for the Godot Asset Store and Unity Asset Store listings. Keep the public forms synchronized with these files.

- `godot.md`: Godot listing copy and review disclosures.
- `unity.md`: Unity listing copy and review disclosures.
- `artwork/`: editable 16:9 SVG source and rendered upload assets.

Regenerate PNG upload assets with:

```powershell
./tools/Render-StoreArtwork.ps1 -Godot godot.exe
```

The packages are free, open-source engine adapters. They include the runtime/client surface and a deterministic no-key sample. Real model calls require a separately configured provider or an OpenGameAgent server; no provider account, API key, or paid service is bundled.
