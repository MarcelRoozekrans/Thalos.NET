using Microsoft.Extensions.Logging;

namespace Thalos.Git.Workspaces;

/// <summary>
/// Presents an existing <see cref="ILogger"/> as <see cref="ILogger{TCategoryName}"/> for another category, so a type
/// the provider builds for itself logs through the provider's own logger instead of needing a second one injected.
/// </summary>
internal sealed class CategoryLogger<T>(ILogger inner) : ILogger<T>
{
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => inner.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        inner.Log(logLevel, eventId, state, exception, formatter);
}
