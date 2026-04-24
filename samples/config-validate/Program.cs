// CLI entry point for config-validate.
//
// Splits cleanly by concern: Program.cs handles the boundary layer
// (argv, file IO, JSON deserialize) in C# where the exception vocabulary
// is native; Validator.ov handles domain validation (refinement types,
// cascading checks, typed error variants) in Overt where that story
// pays off. The IO/JSON boundary could also be written in Overt via
// `extern "csharp"` and `!{io}` effects; the split here is the one a
// real .NET project tends to have naturally.

using System.Text.Json;
using Overt.Generated.Validator;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: config-validate <path-to-config.json>");
    return 2;
}

string rawText;
try
{
    rawText = File.ReadAllText(args[0]);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"cannot read '{args[0]}': {ex.Message}");
    return 1;
}

ConfigJson? parsed;
try
{
    // Case-insensitive matching lets the JSON use either snake_case or
    // PascalCase; whichever shape the emitter generates for the Overt
    // record's properties, the JSON files use snake_case and line up.
    var options = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    };
    parsed = JsonSerializer.Deserialize<ConfigJson>(rawText, options);
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"invalid JSON in '{args[0]}': {ex.Message}");
    return 1;
}

if (parsed is null)
{
    Console.Error.WriteLine($"'{args[0]}' deserialized to null");
    return 1;
}

// Hand the raw shape to the Overt validator. From here on, everything
// the validator returns is either a fully-refined Config or a typed
// ValidationError we know how to describe.
var result = Module.validate(parsed);
switch (result)
{
    case Overt.Runtime.ResultOk<Config, ValidationError> ok:
        Console.WriteLine(Module.ok_summary(ok.Value));
        return 0;

    case Overt.Runtime.ResultErr<Config, ValidationError> err:
        Console.Error.WriteLine($"validation failed: {Module.describe(err.Error)}");
        return 1;

    default:
        // Cannot happen: Result<Ok, Err> is closed. The compiler should
        // eliminate this arm; keep it as a guard against bit-rot if the
        // runtime's Result representation ever changes.
        Console.Error.WriteLine("internal error: unexpected Result shape");
        return 1;
}
