# Patch tool distribution (EXE)

This repo includes a Python tool that patches the embedded `BaseConfiguration` + `LauncherConfig` TextAssets inside Unity `resources.assets`.

To distribute it without requiring users to install Python, build a standalone Windows executable with PyInstaller.

## Build

From the repo root:

- Build both variants:
  - `powershell -ExecutionPolicy Bypass -File tools/build_patch_tool_exe.ps1 -Mode both`

- Build only single-file EXE:
  - `powershell -ExecutionPolicy Bypass -File tools/build_patch_tool_exe.ps1 -Mode onefile`

- Build only folder-based EXE:
  - `powershell -ExecutionPolicy Bypass -File tools/build_patch_tool_exe.ps1 -Mode onedir`

Optional:
- Clean previous build outputs:
  - `powershell -ExecutionPolicy Bypass -File tools/build_patch_tool_exe.ps1 -Mode both -Clean`

## Output

- Onefile:
  - `clientsetup/patch_embedded_configs.exe`

- Onedir:
  - `clientsetup/patch_embedded_configs/patch_embedded_configs.exe`

## Notes

- Build on Windows for Windows.
- `--onefile` is simplest to ship, but `--onedir` can be more AV-friendly.
