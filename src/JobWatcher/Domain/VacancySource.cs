namespace JobWatcher.Domain;

/// <summary>The job board a vacancy comes from.</summary>
/// <remarks>Values are PERSISTED in seen.db: never renumber, reuse or reorder them; only append new ones.</remarks>
public enum VacancySource
{
    Dou = 1,
    Djinni = 2,
    Jooble = 3,
    LinkedIn = 4,
    JobsPipe = 5,
}

public static class VacancySourceExtensions
{
    /// <summary>The site name as the user sees it in Telegram messages.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined member (including <c>default</c>).</exception>
    public static string DisplayName(this VacancySource source) => source switch
    {
        VacancySource.Dou => "DOU",
        VacancySource.Djinni => "Djinni",
        VacancySource.Jooble => "Jooble",
        VacancySource.LinkedIn => "LinkedIn",
        VacancySource.JobsPipe => "JobsPipe",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown vacancy source."),
    };

    /// <summary>True when the value is a defined member; <c>default</c> (0) and casts of other numbers are not.</summary>
    public static bool IsKnown(this VacancySource source) => Enum.IsDefined(source);
}
