using Microsoft.Extensions.Logging;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// A test logger that keeps every line it is given. Each line is the formatted message and, when an exception was
/// passed, that exception's <see cref="Exception.ToString"/>, so a test sees anything a log call would leak.
/// </summary>
internal sealed class ListLogger
{
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public ILogger<T> For<T>() => new Logger<T>(this);

    private void Add(string line)
    {
        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    private sealed class Logger<T>(ListLogger owner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Add(exception is null ? formatter(state, exception) : $"{formatter(state, exception)} {exception}");
    }
}
