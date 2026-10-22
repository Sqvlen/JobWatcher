using JobWatcher.Domain;

namespace JobWatcher.Tests.Domain;

public sealed class VacancySourceTests
{
    // The numbers are persisted in seen.db: a renumbered member would make every stored vacancy look new (or
    // attribute it to another site), so they are pinned here and must never change.
    [Fact]
    public void Numbers_are_fixed()
    {
        Assert.Equal(1, (int)VacancySource.Dou);
        Assert.Equal(2, (int)VacancySource.Djinni);
        Assert.Equal(3, (int)VacancySource.Jooble);
        Assert.Equal(4, (int)VacancySource.LinkedIn);
    }

    [Fact]
    public void Every_member_has_a_unique_non_empty_display_name()
    {
        var names = Enum.GetValues<VacancySource>().Select(source => source.DisplayName()).ToList();

        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(VacancySource.Dou, "DOU")]
    [InlineData(VacancySource.Djinni, "Djinni")]
    [InlineData(VacancySource.Jooble, "Jooble")]
    [InlineData(VacancySource.LinkedIn, "LinkedIn")]
    public void Display_name_is_the_text_the_message_shows(VacancySource source, string expected)
    {
        Assert.Equal(expected, source.DisplayName());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void Undefined_value_is_not_known_and_has_no_display_name(int value)
    {
        var source = (VacancySource)value;

        Assert.False(source.IsKnown());
        Assert.Throws<ArgumentOutOfRangeException>(() => source.DisplayName());
    }

    [Fact]
    public void Defined_values_are_known()
    {
        Assert.All(Enum.GetValues<VacancySource>(), source => Assert.True(source.IsKnown()));
    }
}
