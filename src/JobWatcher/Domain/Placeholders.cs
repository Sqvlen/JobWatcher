namespace JobWatcher.Domain;

/// <summary>Visible stand-in texts shared by the sources and the message formatter.</summary>
public static class Placeholders
{
    /// <summary>
    /// Shown for a value the source does not give (a company, a salary). A company with this text is never looked up
    /// for a rating.
    /// </summary>
    public const string NotSpecified = "не вказано";
}
