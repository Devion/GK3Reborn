using System.Reflection;
using System.Runtime.CompilerServices;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Bootstrap;

/// <summary>
/// The executable entry point.
/// </summary>
/// <remarks>
/// This assembly stays deliberately thin: it installs native library resolution and
/// crash handling before anything else can trigger a load, then hands off. See
/// Plan/01-architecture.md section 3, step 1.
/// </remarks>
public static class Program
{
    /// <summary>Process entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Zero on clean exit.</returns>
    public static int Main(string[] args)
    {
        NativeLibraryLocator.Install(Assembly.GetExecutingAssembly());

        return Launch(args);
    }

    // Kept out of Main so that resolving GK3Reborn.App does not happen until after
    // the resolver above is installed. That applies to the log as much as to the game:
    // both live in the engine assembly, and a reference to either from Main would have
    // the JIT load it before Install had run.
    //
    // No arguments are substituted here. Somebody double-clicking the game passes none,
    // so no arguments has to *be* the way the game starts - the defaults belong where
    // they can be read and changed, not in a launcher nobody looks inside.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Launch(string[] args)
    {
        // Before the crash handler, so that a crash has somewhere to be written down, and
        // before the game, so that a failure inside its first few lines is still recorded.
        Log.Open();

        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            if (e.ExceptionObject is Exception error)
            {
                Log.Exception("Unhandled exception", error);
            }
            else
            {
                Log.Error($"Unhandled exception: {e.ExceptionObject}");
            }

            // The handler runs on the way down and the process may be killed before the
            // ProcessExit handler ever runs, so the file is closed here rather than trusted
            // to it. Every write is flushed anyway; this only makes it certain.
            Log.Close();
        };

        return GK3Reborn.Application.Run(args ?? [], NativeLibraryLocator.LibsRoot);
    }
}
