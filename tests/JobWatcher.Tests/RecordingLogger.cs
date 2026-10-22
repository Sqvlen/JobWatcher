using Microsoft.Extensions.Logging;

namespace JobWatcher.Tests;

/// <summary>Remembers every entry written through it: the level, the formatted message, the exception and the structured fields.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    /// <summary>The key under which the logging framework keeps the message template of an entry.</summary>
    public const string TemplateField = "{OriginalFormat}";

    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
            : [];
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, fields));
    }

    /// <param name="Fields">The values the message template names, plus the template itself under <see cref="TemplateField"/>.</param>
    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Fields);
}
