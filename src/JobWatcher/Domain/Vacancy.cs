namespace JobWatcher.Domain;

/// <param name="Salary">The salary text exactly as the source gives it; null when the source has none.</param>
/// <param name="RemoteRestrictedTo">The region a remote job is limited to; null means not restricted or unknown.</param>
public sealed record Vacancy(
    VacancySource Source,
    string SourceId,
    string Title,
    string Company,
    string Url,
    string? Salary,
    string? Location,
    WorkMode WorkMode,
    string? RemoteRestrictedTo,
    int? MinYearsExperience,
    bool IsJunior,
    string? Description);
