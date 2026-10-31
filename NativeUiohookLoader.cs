using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Autodraw;

/// <summary>
/// SharpHook's Windows backend is the native libuiohook, which passes its own module handle to
/// <c>SetWindowsHookEx</c>.  Windows resolves that handle through the module's file path, so the
/// call fails with <c>ERROR_MOD_NOT_FOUND</c> (0x7E) when the path is longer than MAX_PATH.  That
/// happens with the SDK layout used here: the DLL lives in
/// <c>runtimes\win-x64\native\uiohook.dll</c>, so a deeply nested checkout pushes the path past
/// 260 characters and every global hook (F1-F7 hotkeys, mouse tracking, picker capture) dies.
///
/// The fix is to load the same DLL once from a short path before SharpHook touches it.  The
/// Windows loader then reuses that module by name, the module path stays under MAX_PATH, and the
/// low-level hook installs normally.  Nothing is copied when the packaged path is already short
/// enough, so normal installs are unaffected.
/// </summary>
internal static class NativeUiohookLoader
{
    private const int MaxPath = 260;
    private const string LibraryName = "uiohook.dll";

    [ModuleInitializer]
    internal static void Initialize() => TryPreload();

    private static void TryPreload()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;

            var runtimeId = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _ => null
            };
            if (runtimeId is null) return;

            var packaged = Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeId, "native", LibraryName);
            if (!File.Exists(packaged)) return;
            if (packaged.Length < MaxPath) return; // The normal load already works.

            // Prefer a copy next to the executable; fall back to the temp folder because the
            // install directory may be read-only (Program Files) or already have a stale copy.
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, LibraryName),
                Path.Combine(Path.GetTempPath(), "AutoDraw-native", runtimeId, LibraryName)
            };

            foreach (var target in candidates)
            {
                try
                {
                    if (target.Length >= MaxPath) continue;

                    var directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    try
                    {
                        File.Copy(packaged, target, overwrite: true);
                    }
                    catch (IOException) when (File.Exists(target))
                    {
                        // Another running instance has the copy open; it ships the same library.
                    }

                    var handle = NativeLibrary.Load(target);

                    // SharpHook declares [DllImport("uiohook")]. Without a resolver the CLR would
                    // probe the packaged (too long) path again and install the hook from that
                    // module, so bind every "uiohook" import to the short-path copy instead.
                    var sharpHookAssembly = typeof(SharpHook.TaskPoolGlobalHook).Assembly;
                    NativeLibrary.SetDllImportResolver(sharpHookAssembly, (name, _, _) =>
                        string.Equals(name, "uiohook", StringComparison.OrdinalIgnoreCase)
                            ? handle
                            : IntPtr.Zero);

                    Console.WriteLine($"Loaded {LibraryName} from a short path: {target}");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not preload {LibraryName} from {target}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // Loading is best effort: SharpHook still gets its own chance to load the library and
            // reports a normal hook failure if that does not work either.
            Console.WriteLine($"Unexpected error while preloading {LibraryName}: {ex.Message}");
        }
    }
}
