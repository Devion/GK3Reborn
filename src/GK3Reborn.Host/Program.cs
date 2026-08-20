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
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Launch(string[] args)
    {
        if (args == null || args.Length == 0)
        {
            args = ["--scene", "R25", "--timeblock", "N", "--rt", "high", "--enhanced"];
        }

        return GK3Reborn.Application.Run(args, NativeLibraryLocator.LibsRoot);
    }
}
