// Cross-language call site: C# invokes the Overt-transpiled Module.greet.
// The build integration generated Greeter.g.cs into $(IntermediateOutputPath)overt/
// before Csc ran, so Overt.Generated.Greeter.Module is in scope here.
using Overt.Generated.Greeter;

var message = Module.greet("world");
Console.WriteLine(message);
if (message != "hello, world")
{
    Environment.ExitCode = 1;
}
