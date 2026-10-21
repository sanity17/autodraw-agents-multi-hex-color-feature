# AutoDraw Agent Guide

## Project at a glance

- AutoDraw is a C# Avalonia desktop application for tracing imported images with cursor and keyboard automation.
- The main project is `Autodraw.csproj`, targeting `net8.0-windows`; `global.json` pins the .NET 8 SDK.
- The repository contains a small companion project in `AutoDraw_ColorIntegration/` for color-picker calibration and Windows input integration.
- User-facing background and release information is in [README.md](README.md). Do not duplicate it here.

## Build and validation

- Restore when needed: `dotnet restore Autodraw.sln`.
- Build the solution: `dotnet build Autodraw.sln --no-restore`.
- Run the app during manual validation with `dotnet run --project Autodraw.csproj`.
- There is no test project in the solution. For changes involving Avalonia UI, global input hooks, cursor automation, rendering, or native integrations, build first and perform focused manual validation on Windows.
- The current build has pre-existing warnings, including `SimWinMouse` being restored from .NET Framework assets and nullable-property/event warnings. Avoid broad warning cleanup unless the task requires it.
- Release packaging is interactive and documented in [build.sh](build.sh); verify its paths and runtime identifier before using it on Windows.

## Architecture and ownership

- `MainWindow.axaml` and `MainWindow.axaml.cs` own the primary workflow, controls, event wiring, image import, layer display, configuration dialogs, and drawing orchestration.
- `ImageProcessing.cs` owns bitmap processing, filtering, palette/layer generation, and image conversion logic. Keep pixel algorithms out of window event handlers.
- `Draw.cs` owns drawing execution and action-stack playback; `Input.cs` owns global keyboard/mouse hooks and input lifecycle.
- `ColorPicker.cs` owns color-wheel calibration and color selection. Calibration depends on screen coordinates and should be validated on the target Windows display setup.
- `App.axaml.cs` owns Avalonia startup/theme loading. `Program.cs` owns process startup and the compatibility rendering mode.
- `Utils.cs` owns persisted configuration, logging, theme/cache paths, and marketplace helpers. Configuration is stored under `%LOCALAPPDATA%\\AutoDraw`.
- Each `.axaml` view generally has a matching `.axaml.cs` code-behind file. Preserve existing event-handler wiring and `DataContext` patterns unless introducing a deliberate architectural change.

## Project conventions and pitfalls

- Keep changes scoped to the owning layer above and reuse existing helpers and UI patterns before adding abstractions.
- Dispose `SKBitmap` and other native/image resources when replacing or clearing them; image processing and layer previews can hold unmanaged memory.
- Marshal work that updates Avalonia controls back to the UI thread. Global hook callbacks and drawing work may run outside it.
- Treat theme files and assets as runtime resources. Update project resource metadata when adding or moving `.axaml`, images, sounds, or theme files.
- Do not assume non-Windows input behavior works: the project targets Windows and uses SharpHook, SimWinMouse, and Win32 compatibility options.
- Do not commit generated `bin/` or `obj/` output. Keep API keys and local configuration out of source control.
- Preserve public names and serialized configuration keys unless migration is explicitly handled.

## Change workflow

1. Identify the controlling code path and a nearby existing implementation or UI pattern.
2. Make the smallest focused change, keeping UI markup and code-behind synchronized.
3. Run `dotnet build Autodraw.sln --no-restore` and report any warnings that are new or unrelated.
4. For runtime behavior, manually exercise the affected workflow on Windows, especially input hooks, drawing, image disposal, themes, and persisted settings.
