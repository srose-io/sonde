using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sonde.Cli;

/// <summary>The <c>sonde</c> command tree. Every action writes to the invocation's output so tests can capture it.</summary>
internal static class SondeCommand
{
    private static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Parses <paramref name="args"/> with response files off: <c>@file.cs</c> is a script to read, not arguments to splice in.</summary>
    public static ParseResult Parse(string[] args) =>
        Build().Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });

    public static RootCommand Build()
    {
        NameTheExecutable("sonde");
        var root = new RootCommand("Run C# inside a live .NET process. Start the process with `sonde env` or `sonde run`, then `sonde eval` into it.");
        root.Add(EnvCommand());
        root.Add(RunCommand());
        root.Add(HelloCommand());
        root.Add(StatusCommand());
        root.Add(EvalCommand());
        root.Add(ListCommand());
        return root;
    }

    /// <summary>
    /// Help prints the usage line from the entry assembly's file name, which for a dotnet tool is
    /// <c>Sonde.Cli.dll</c>, not the <c>sonde</c> command a person typed. System.CommandLine 2.0
    /// has no public way to set it, so this writes its private cache; if a release renames the
    /// field, help falls back to the assembly name and nothing else changes.
    /// </summary>
    private static void NameTheExecutable(string name)
    {
        var field = typeof(RootCommand).GetField("_executableName", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (field?.FieldType == typeof(string))
            field.SetValue(null, name);
    }

    // --- env ---------------------------------------------------------------------------------

    private static Command EnvCommand()
    {
        var name = new Option<string>("--name", "-n") { Description = "A name for the process; its endpoint is derived from it. Default: the current directory's name." };
        var project = new Option<string?>("--project", "-p") { Description = "The app's project directory, so its own runtimeconfig.template.json is merged into the one Sonde writes for `dotnet watch`." };
        var format = new Option<string>("--format", "-f") { Description = "How to print the variables.", DefaultValueFactory = _ => "shell" };
        format.AcceptOnlyFromAmong(EnvFormat.Names);
        var command = new Command("env", "Print the environment variables that load the probe into a process you launch yourself.");
        command.Add(name);
        command.Add(project);
        command.Add(format);
        command.SetAction(parse =>
        {
            var environment = BuildEnvironment(parse.GetValue(name), parse.GetValue(project), out var endpoint);
            parse.InvocationConfiguration.Output.Write(EnvFormat.Render(parse.GetRequiredValue(format), environment));
            parse.InvocationConfiguration.Error.WriteLine($"# endpoint: {endpoint}");
            return ExitCode.Ok;
        });
        return command;
    }

    private static Dictionary<string, string?> BuildEnvironment(string? name, string? projectDirectory, out string endpoint)
    {
        name = string.IsNullOrEmpty(name) ? Path.GetFileName(Directory.GetCurrentDirectory()) : name;
        if (string.IsNullOrEmpty(name))
            name = "app";
        endpoint = Launch.EndpointFor(name);
        var template = Launch.WriteRuntimeConfigTemplate(Path.Combine(Launch.HomeDirectory, "templates"), name, null, projectDirectory);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        Launch.ApplyEnvironment(environment, endpoint, null, template);
        return environment;
    }

    // --- run ---------------------------------------------------------------------------------

    private static Command RunCommand()
    {
        var name = new Option<string>("--name", "-n") { Description = "A name for the process; its endpoint is derived from it. Default: the current directory's name." };
        var project = new Option<string?>("--project", "-p") { Description = "The app's project directory (see `sonde env --project`)." };
        var commandLine = new Argument<string[]>("command") { Description = "The command to launch, after `--`: for example `-- dotnet run --project MyApp`.", Arity = ArgumentArity.OneOrMore };
        var command = new Command("run", "Launch a command with the probe loaded into it, and wait for it to exit.");
        command.Add(name);
        command.Add(project);
        command.Add(commandLine);
        command.SetAction(async (parse, cancellationToken) =>
        {
            var argv = parse.GetRequiredValue(commandLine);
            var environment = BuildEnvironment(parse.GetValue(name), parse.GetValue(project), out var endpoint);
            var start = new ProcessStartInfo(argv[0]) { UseShellExecute = false };
            foreach (var arg in argv.Skip(1))
                start.ArgumentList.Add(arg);
            foreach (var (key, value) in environment)
                start.Environment[key] = value;

            parse.InvocationConfiguration.Error.WriteLine($"sonde: endpoint {endpoint}");
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException($"Could not start {argv[0]}.");
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            });
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return process.ExitCode;
        });
        return command;
    }

    // --- hello / status ----------------------------------------------------------------------

    private static Argument<string> TargetArgument() =>
        new("target") { Description = "A name given to `sonde env`/`sonde run`, or an endpoint (socket path or pipe name)." };

    private static Command HelloCommand()
    {
        var target = TargetArgument();
        var command = new Command("hello", "Describe the process behind a probe: runtime, pid, loaded assemblies, probe version.");
        command.Add(target);
        command.SetAction(async (parse, cancellationToken) =>
        {
            var client = new SondeClient(Targets.Resolve(parse.GetRequiredValue(target)));
            try
            {
                var hello = await client.HelloAsync(cancellationToken).ConfigureAwait(false);
                parse.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(hello, Pretty));
                return ExitCode.Ok;
            }
            catch (SondeUnreachableException ex)
            {
                parse.InvocationConfiguration.Error.WriteLine(ex.Message);
                return ExitCode.NotRun;
            }
        });
        return command;
    }

    private static Command StatusCommand()
    {
        var target = TargetArgument();
        var command = new Command("status", "Report a probe's state: running eval, live contexts, hooks and taps.");
        command.Add(target);
        command.SetAction(async (parse, cancellationToken) =>
        {
            var client = new SondeClient(Targets.Resolve(parse.GetRequiredValue(target)));
            try
            {
                var status = await client.StatusAsync(cancellationToken).ConfigureAwait(false);
                parse.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(status, Pretty));
                return ExitCode.Ok;
            }
            catch (SondeUnreachableException ex)
            {
                parse.InvocationConfiguration.Error.WriteLine(ex.Message);
                return ExitCode.NotRun;
            }
        });
        return command;
    }

    // --- eval --------------------------------------------------------------------------------

    private static Command EvalCommand()
    {
        var target = TargetArgument();
        var script = new Argument<string>("script") { Description = "C# statements ending in `return x;`, or `@file.cs`, or `-` to read stdin." };
        var timeout = new Option<int>("--timeout", "-t") { Description = "Milliseconds to wait for the script.", DefaultValueFactory = _ => EvalOptions.DefaultTimeoutMs };
        var maxDepth = new Option<int>("--max-depth", "-d") { Description = "How deep the result is serialised.", DefaultValueFactory = _ => Probe.ResultJson.DefaultMaxDepth };
        var json = new Option<bool>("--json") { Description = "Print the whole result (outcome, result, logs, timing) as one JSON object." };
        var command = new Command("eval", "Compile a script, run it inside the process, print what came back.");
        command.Add(target);
        command.Add(script);
        command.Add(timeout);
        command.Add(maxDepth);
        command.Add(json);
        command.SetAction(async (parse, cancellationToken) =>
        {
            var source = await ReadScriptAsync(parse.GetRequiredValue(script), cancellationToken).ConfigureAwait(false);
            var client = new SondeClient(Targets.Resolve(parse.GetRequiredValue(target)));
            var result = await client.EvalAsync(source, new EvalOptions { TimeoutMs = parse.GetValue(timeout), MaxDepth = parse.GetValue(maxDepth) }, cancellationToken).ConfigureAwait(false);
            return Report(result, parse.GetValue(json), parse.InvocationConfiguration.Output, parse.InvocationConfiguration.Error);
        });
        return command;
    }

    private static async Task<string> ReadScriptAsync(string script, CancellationToken cancellationToken)
    {
        if (script == "-")
            return await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (script.StartsWith('@'))
            return await File.ReadAllTextAsync(script[1..], cancellationToken).ConfigureAwait(false);
        return script;
    }

    /// <summary>Prints an eval result and picks the exit code. Shared with tests so the mapping is in one place.</summary>
    internal static int Report(EvalResult result, bool asJson, TextWriter output, TextWriter error)
    {
        if (asJson)
        {
            output.WriteLine(JsonSerializer.Serialize(result, Pretty));
            return ExitCodeFor(result.Outcome);
        }

        foreach (var line in result.Logs)
            error.WriteLine($"log: {line}");

        switch (result.Outcome)
        {
            case EvalOutcome.Completed:
                output.WriteLine(result.Result is null ? "null" : result.Result.ToJsonString(Pretty));
                break;
            case EvalOutcome.ScriptThrew:
                error.WriteLine("The script threw:");
                error.WriteLine(result.Exception);
                break;
            case EvalOutcome.TimedOut:
                error.WriteLine($"The script did not finish within the timeout ({result.ElapsedMs} ms). It may still be running inside the process; the probe refuses further evals until it ends.");
                break;
            case EvalOutcome.CompileFailed:
                error.WriteLine("The script did not compile:");
                foreach (var diagnostic in result.Diagnostics)
                    error.WriteLine($"  script.cs({diagnostic.Line},{diagnostic.Column}): {diagnostic.Id}: {diagnostic.Message}");
                break;
            case EvalOutcome.Busy:
                error.WriteLine($"The probe is already running an eval (since {result.BusySinceUtc:O}). One eval at a time.");
                break;
            case EvalOutcome.Unreachable:
            case EvalOutcome.ProtocolError:
                error.WriteLine(result.Error);
                break;
        }
        return ExitCodeFor(result.Outcome);
    }

    internal static int ExitCodeFor(EvalOutcome outcome) => outcome switch
    {
        EvalOutcome.Completed => ExitCode.Ok,
        EvalOutcome.ScriptThrew or EvalOutcome.TimedOut => ExitCode.ScriptFailed,
        _ => ExitCode.NotRun,
    };

    // --- ls ----------------------------------------------------------------------------------

    private static Command ListCommand()
    {
        var json = new Option<bool>("--json") { Description = "Print as JSON." };
        var command = new Command("ls", "List the endpoints under the Sonde home directory and whether a probe answers at each.");
        command.Add(json);
        command.SetAction(async parse =>
        {
            var targets = await Targets.ListAsync().ConfigureAwait(false);
            var output = parse.InvocationConfiguration.Output;
            if (parse.GetValue(json))
            {
                var array = new JsonArray();
                foreach (var target in targets)
                    array.Add(new JsonObject { ["name"] = target.Name, ["endpoint"] = target.Endpoint, ["listening"] = target.Listening });
                output.WriteLine(array.ToJsonString(Pretty));
                return ExitCode.Ok;
            }
            if (targets.Count == 0)
            {
                output.WriteLine($"No endpoints under {Launch.HomeDirectory}. Launch something with `sonde run` or `sonde env` first.");
                return ExitCode.Ok;
            }
            foreach (var target in targets)
                output.WriteLine($"{(target.Listening ? "up  " : "down")}  {target.Name}  {target.Endpoint}");
            return ExitCode.Ok;
        });
        return command;
    }
}
