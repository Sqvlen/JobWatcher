using JobWatcher.Domain;

namespace JobWatcher.Sources;

/// <summary>A source is deliberately not connected (for example no legal access route is known). This is not a failure.</summary>
public sealed class SourceNotConnectedException : Exception
{
    // The inherited Exception.Source is a string about the code that threw; the typed source has its own property.
    public SourceNotConnectedException(VacancySource source, string reason)
        : base($"Source {source.DisplayName()} is not connected: {reason}")
    {
        SourceKind = source;
        Reason = reason;
    }

    /// <summary>The job board that is not connected.</summary>
    public VacancySource SourceKind { get; }

    public string Reason { get; }
}
