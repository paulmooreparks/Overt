using Overt.Generated.Hello;

// Invokes the transpiled hello.ov `Main` function. The explicit class form avoids
// CS7022 — with top-level statements, C# would try to use Module.Main from Generated.cs
// as a second entry point.

namespace Overt.EndToEnd;

public static class Harness
{
    public static int Main()
    {
        var result = Module.Main();
        if (result.IsErr)
        {
            Console.Error.WriteLine("harness: transpiled Main returned Err");
            return 1;
        }
        return 0;
    }
}
