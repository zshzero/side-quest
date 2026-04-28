using System.Text;
using NCache.Protocol;
using NCache.Server.Commands.Infrastructure;
using NCache.Server.Storage;

namespace NCache.Server.Tests.Commands;

public class CommandDispatcherTests
{
    /// <summary>
    /// Test double: a configurable handler. Lets a single test set the arity,
    /// the response, or arrange for an exception — without coupling to any
    /// real command implementation.
    /// </summary>
    private sealed class FakeHandler : ICommandHandler
    {
        public CommandArity Arity { get; init; } = new CommandArity.Exact(1);
        public Func<CommandContext, RespValue> ExecuteFn { get; init; }
            = _ => new RespValue.SimpleString("OK");

        public RespValue Execute(CommandContext ctx) => ExecuteFn(ctx);
    }

    /// <summary>Builds a RESP Array of BulkStrings from string args — what real commands look like on the wire.</summary>
    private static RespValue.Array Cmd(params string[] parts)
    {
        var items = parts.Select(p => (RespValue)new RespValue.BulkString(Encoding.UTF8.GetBytes(p))).ToArray();
        return new RespValue.Array(items);
    }

    private static (CommandDispatcher dispatcher, CacheStore store, CommandRegistry registry) Setup()
    {
        var store = new CacheStore();
        var registry = new CommandRegistry();
        var dispatcher = new CommandDispatcher(registry);
        return (dispatcher, store, registry);
    }

    // ════════════════════════════════════════════════════════════════════
    // Happy path
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_RegisteredCommandWithCorrectArity_CallsHandlerAndReturnsResponse()
    {
        var (dispatcher, store, registry) = Setup();
        registry.Register("PING", new FakeHandler
        {
            Arity = new CommandArity.Exact(1),
            ExecuteFn = _ => new RespValue.SimpleString("PONG"),
        });

        var response = dispatcher.Execute(Cmd("PING"), store);

        var simple = Assert.IsType<RespValue.SimpleString>(response);
        Assert.Equal("PONG", simple.Value);
    }

    [Fact]
    public void Execute_CommandNameLookupIsCaseInsensitive()
    {
        var (dispatcher, store, registry) = Setup();
        registry.Register("PING", new FakeHandler
        {
            ExecuteFn = _ => new RespValue.SimpleString("PONG"),
        });

        // Registered as "PING" — try lowercase, mixed case
        Assert.IsType<RespValue.SimpleString>(dispatcher.Execute(Cmd("ping"), store));
        Assert.IsType<RespValue.SimpleString>(dispatcher.Execute(Cmd("Ping"), store));
        Assert.IsType<RespValue.SimpleString>(dispatcher.Execute(Cmd("PiNg"), store));
    }

    [Fact]
    public void Execute_PassesAllArgumentsToHandler()
    {
        var (dispatcher, store, registry) = Setup();
        ReadOnlyMemory<byte>[]? capturedArgs = null;
        registry.Register("ECHO", new FakeHandler
        {
            Arity = new CommandArity.AtLeast(1),
            ExecuteFn = ctx =>
            {
                capturedArgs = ctx.Args;
                return new RespValue.SimpleString("OK");
            },
        });

        dispatcher.Execute(Cmd("ECHO", "one", "two", "three"), store);

        Assert.NotNull(capturedArgs);
        Assert.Equal(4, capturedArgs!.Length);
        Assert.Equal("ECHO",  Encoding.UTF8.GetString(capturedArgs[0].Span));
        Assert.Equal("one",   Encoding.UTF8.GetString(capturedArgs[1].Span));
        Assert.Equal("two",   Encoding.UTF8.GetString(capturedArgs[2].Span));
        Assert.Equal("three", Encoding.UTF8.GetString(capturedArgs[3].Span));
    }

    // ════════════════════════════════════════════════════════════════════
    // Error: top-level not an Array
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_NonArrayInput_ReturnsProtocolError()
    {
        var (dispatcher, store, _) = Setup();

        var response = dispatcher.Execute(new RespValue.SimpleString("PING"), store);

        var error = Assert.IsType<RespValue.Error>(response);
        Assert.Contains("Protocol error", error.Message);
        Assert.Contains("expected array", error.Message);
    }

    // ════════════════════════════════════════════════════════════════════
    // Error: empty or null array
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_EmptyArray_ReturnsEmptyCommandError()
    {
        var (dispatcher, store, _) = Setup();

        var response = dispatcher.Execute(new RespValue.Array(Array.Empty<RespValue>()), store);

        var error = Assert.IsType<RespValue.Error>(response);
        Assert.Contains("empty command", error.Message);
    }

    [Fact]
    public void Execute_NullArrayItems_ReturnsEmptyCommandError()
    {
        var (dispatcher, store, _) = Setup();

        var response = dispatcher.Execute(new RespValue.Array(null), store);

        var error = Assert.IsType<RespValue.Error>(response);
        Assert.Contains("empty command", error.Message);
    }

    // ════════════════════════════════════════════════════════════════════
    // Error: bad command-name shape
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_FirstElementNotBulkString_ReturnsInvalidFormatError()
    {
        var (dispatcher, store, _) = Setup();
        // Array whose first element is an Integer, not a BulkString
        var bad = new RespValue.Array(new RespValue[] { new RespValue.Integer(42) });

        var response = dispatcher.Execute(bad, store);

        var error = Assert.IsType<RespValue.Error>(response);
        Assert.Contains("invalid command format", error.Message);
    }

    [Fact]
    public void Execute_FirstElementBulkStringWithNullData_ReturnsInvalidFormatError()
    {
        var (dispatcher, store, _) = Setup();
        // BulkString with Data == null is the RESP "nil" — invalid as a command name
        var bad = new RespValue.Array(new RespValue[] { new RespValue.BulkString(null) });

        var response = dispatcher.Execute(bad, store);

        var error = Assert.IsType<RespValue.Error>(response);
        Assert.Contains("invalid command format", error.Message);
    }

    [Fact]
    public void Execute_SubsequentArgIsNotBulkString_ReturnsInvalidFormatError()
    {
        var (dispatcher, store, registry) = Setup();
        registry.Register("SET", new FakeHandler { Arity = new CommandArity.Exact(3) });
        // First arg ok, second arg is an Integer (not allowed)
        var bad = new RespValue.Array(new RespValue[]
        {
            new RespValue.BulkString(Encoding.UTF8.GetBytes("SET")),
            new RespValue.BulkString(Encoding.UTF8.GetBytes("key")),
            new RespValue.Integer(42),
        });

        var response = dispatcher.Execute(bad, store);

        var error = Assert.IsType<RespValue.Error>(response);
        Assert.Contains("invalid command format", error.Message);
    }

    // ════════════════════════════════════════════════════════════════════
    // Error: unknown command
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_UnknownCommand_ReturnsUnknownCommandError()
    {
        var (dispatcher, store, _) = Setup();

        var response = dispatcher.Execute(Cmd("BOGUS"), store);

        var error = Assert.IsType<RespValue.Error>(response);
        Assert.Contains("unknown command", error.Message);
        Assert.Contains("'BOGUS'", error.Message);
    }

    // ════════════════════════════════════════════════════════════════════
    // Error: arity mismatch
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_ArityExactMismatch_ReturnsWrongArgCountError()
    {
        var (dispatcher, store, registry) = Setup();
        registry.Register("SET", new FakeHandler { Arity = new CommandArity.Exact(3) });

        // SET requires exactly 3 args; sending 2 should fail
        var response = dispatcher.Execute(Cmd("SET", "key"), store);

        var error = Assert.IsType<RespValue.Error>(response);
        Assert.Contains("wrong number of arguments", error.Message);
        Assert.Contains("'SET'", error.Message);
    }

    [Fact]
    public void Execute_ArityExactTooMany_ReturnsWrongArgCountError()
    {
        var (dispatcher, store, registry) = Setup();
        registry.Register("GET", new FakeHandler { Arity = new CommandArity.Exact(2) });

        // GET requires exactly 2 args; sending 3 should fail
        var response = dispatcher.Execute(Cmd("GET", "k1", "k2"), store);

        var error = Assert.IsType<RespValue.Error>(response);
        Assert.Contains("wrong number of arguments", error.Message);
    }

    [Fact]
    public void Execute_ArityAtLeastBelowMinimum_ReturnsWrongArgCountError()
    {
        var (dispatcher, store, registry) = Setup();
        registry.Register("DEL", new FakeHandler { Arity = new CommandArity.AtLeast(2) });

        // DEL requires at least 2 (command + ≥1 key); just "DEL" should fail
        var response = dispatcher.Execute(Cmd("DEL"), store);

        var error = Assert.IsType<RespValue.Error>(response);
        Assert.Contains("wrong number of arguments", error.Message);
    }

    [Fact]
    public void Execute_ArityAtLeastAtMinimum_Succeeds()
    {
        var (dispatcher, store, registry) = Setup();
        registry.Register("DEL", new FakeHandler
        {
            Arity = new CommandArity.AtLeast(2),
            ExecuteFn = _ => new RespValue.Integer(1),
        });

        var response = dispatcher.Execute(Cmd("DEL", "k1"), store);

        Assert.IsType<RespValue.Integer>(response);
    }

    [Fact]
    public void Execute_ArityAtLeastAboveMinimum_Succeeds()
    {
        var (dispatcher, store, registry) = Setup();
        registry.Register("DEL", new FakeHandler
        {
            Arity = new CommandArity.AtLeast(2),
            ExecuteFn = ctx => new RespValue.Integer(ctx.ArgCount - 1),
        });

        var response = dispatcher.Execute(Cmd("DEL", "k1", "k2", "k3"), store);

        var integer = Assert.IsType<RespValue.Integer>(response);
        Assert.Equal(3, integer.Value);
    }

    // ════════════════════════════════════════════════════════════════════
    // Error: handler throws
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Execute_HandlerThrows_ReturnsInternalErrorAndDoesNotPropagate()
    {
        var (dispatcher, store, registry) = Setup();
        registry.Register("BOOM", new FakeHandler
        {
            ExecuteFn = _ => throw new InvalidOperationException("intentional"),
        });

        // The whole point: this should NOT throw. The dispatcher catches and
        // converts to a clean Error response.
        var response = dispatcher.Execute(Cmd("BOOM"), store);

        var error = Assert.IsType<RespValue.Error>(response);
        Assert.Contains("internal error", error.Message);
    }

    [Fact]
    public void Execute_HandlerThrows_LoggerIsInvokedWithException()
    {
        var registry = new CommandRegistry();
        var store = new CacheStore();
        Exception? loggedException = null;
        var dispatcher = new CommandDispatcher(registry, ex => loggedException = ex);

        registry.Register("BOOM", new FakeHandler
        {
            ExecuteFn = _ => throw new InvalidOperationException("the original problem"),
        });

        dispatcher.Execute(Cmd("BOOM"), store);

        Assert.NotNull(loggedException);
        Assert.IsType<InvalidOperationException>(loggedException);
        Assert.Equal("the original problem", loggedException!.Message);
    }
}
