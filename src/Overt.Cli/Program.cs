using Overt.Compiler.Syntax;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: overt <subcommand> [args]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("subcommands:");
    Console.Error.WriteLine("  lex <file.ov>     tokenize a source file and print the token stream");
    return 2;
}

var subcommand = args[0];
var rest = args.Skip(1).ToArray();

switch (subcommand)
{
    case "lex":
        return RunLex(rest);
    default:
        Console.Error.WriteLine($"overt: unknown subcommand '{subcommand}'");
        return 2;
}

static int RunLex(string[] args)
{
    if (args.Length != 1)
    {
        Console.Error.WriteLine("usage: overt lex <file.ov>");
        return 2;
    }

    var path = args[0];
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"overt: file not found: {path}");
        return 1;
    }

    var source = File.ReadAllText(path);
    var result = Lexer.Lex(source);

    foreach (var token in result.Tokens)
    {
        Console.WriteLine(token);
    }

    if (result.Diagnostics.Length > 0)
    {
        Console.Error.WriteLine();
        foreach (var diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(diagnostic);
        }
        return 1;
    }

    return 0;
}
