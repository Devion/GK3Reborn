using System.Reflection;
using System.Runtime.CompilerServices;

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

        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            Console.Error.WriteLine($"Unhandled exception: {e.ExceptionObject}");
        };

        return Launch(args);
    }

    // Kept out of Main so that resolving GK3Reborn.App does not happen until after
    // the resolver above is installed.
    //
    // No arguments are substituted here. Somebody double-clicking the game passes none,
    // so no arguments has to *be* the way the game starts - the defaults belong where
    // they can be read and changed, not in a launcher nobody looks inside.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Launch(string[] args)
    {
        return GK3Reborn.Application.Run(args ?? [], NativeLibraryLocator.LibsRoot);
    }
}
