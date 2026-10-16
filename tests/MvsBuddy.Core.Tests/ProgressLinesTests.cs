using MvsBuddy.Changes;

namespace MvsBuddy.Tests;

// The lines a working screen keeps: a phase stays one line however often it reports, and the phases before it stay.
public sealed class ProgressLinesTests
{
    [Fact]
    public void A_line_that_updates_the_last_takes_its_place_and_any_other_is_added_after_it()
    {
        var lines = new ProgressLines();
        foreach (string line in new[]
        {
            "Reading the game's folder...",
            "Downloading OpenVersus.zip",
            "Downloading OpenVersus.zip (3%)",
            "Downloading OpenVersus.zip (100%)",
            "Unpacking OpenVersus.zip...",
            "Moving the paks...",
            "Moving the paks... (1 of 2: a.pak)",
            "Moving the paks... (2 of 2: b.pak)",
            "Tidying up MVS Buddy's folder...",
        })
        {
            lines.Add(line);
        }

        Assert.Equal(["Reading the game's folder...", "Downloading OpenVersus.zip (100%)", "Unpacking OpenVersus.zip...", "Moving the paks... (2 of 2: b.pak)",
            "Tidying up MVS Buddy's folder..."], lines.Lines);
        Assert.Equal("Tidying up MVS Buddy's folder...", lines.Current);
    }

    [Fact]
    public void The_same_phase_reported_twice_stays_one_line_and_a_phase_that_comes_back_later_is_a_new_one()
    {
        var lines = new ProgressLines();
        lines.Add("Checking the paks already here... (a.pak)");
        lines.Add("Checking the paks already here... (b.pak)");
        lines.Add("Downloading c.pak (50%)");
        lines.Add("Checking the paks already here... (c.pak)");

        Assert.Equal(["Checking the paks already here... (b.pak)", "Downloading c.pak (50%)", "Checking the paks already here... (c.pak)"], lines.Lines);
        Assert.Null(new ProgressLines().Current);
    }
}
