using PhoneBox.Agent;
using Xunit;

public class IcsTests
{
    private static readonly DateTime From = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);   // Monday
    private static readonly DateTime To = From.AddDays(14);

    private static string Cal(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + string.Concat(events.Select(e => "BEGIN:VEVENT\r\n" + e.Trim().Replace("\n", "\r\n") + "\r\nEND:VEVENT\r\n")) + "END:VCALENDAR\r\n";

    private static DateTime Utc(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    [Fact]
    public void SingleEventInUtc()
    {
        var e = Ics.Parse(Cal("UID:1\nDTSTART:20261110T090000Z\nDTEND:20261110T093000Z\nSUMMARY:Stand-up\\, team\nLOCATION:Room 1"), From, To).Single();
        Assert.Equal("Stand-up, team", e.Title);
        Assert.Equal("Room 1", e.Location);
        Assert.Equal(new DateTime(2026, 11, 10, 9, 0, 0), Utc(e.Start));
        Assert.Equal(TimeSpan.FromMinutes(30), Utc(e.End) - Utc(e.Start));
        Assert.False(e.AllDay);
    }

    [Fact]
    public void EscapedBackslashIsNotReadAsALineBreak()
    {
        // RFC 5545 TEXT: "\\" is one backslash, so "\\new" is a backslash and "new", not a line break and "ew"
        var e = Ics.Parse(Cal("UID:1\nDTSTART:20261110T090000Z\n" + @"SUMMARY:Fix C:\\new\\build\, then\ntest" + "\n" + @"LOCATION:\\\\Nas\\Meeting"), From, To).Single();
        Assert.Equal(@"Fix C:\new\build, then test", e.Title);
        Assert.Equal(@"\\Nas\Meeting", e.Location);
    }

    [Fact]
    public void FoldedLinesAreJoined()
    {
        var e = Ics.Parse(Cal("UID:1\nDTSTART:20261110T090000Z\nSUMMARY:A very long\n  title"), From, To).Single();
        Assert.Equal("A very long title", e.Title);
    }

    [Fact]
    public void AllDayEvent()
    {
        var e = Ics.Parse(Cal("UID:1\nDTSTART;VALUE=DATE:20261112\nDTEND;VALUE=DATE:20261113\nSUMMARY:Holiday"), From, To).Single();
        Assert.True(e.AllDay);
        Assert.Equal(TimeSpan.FromDays(1), Utc(e.End) - Utc(e.Start));
    }

    [Fact]
    public void TimeZoneIsApplied()
    {
        // 10:00 in Tallinn in November (UTC+2) is 08:00 UTC
        var e = Ics.Parse(Cal("UID:1\nDTSTART;TZID=Europe/Tallinn:20261110T100000\nDTEND;TZID=Europe/Tallinn:20261110T110000\nSUMMARY:Call"), From, To).Single();
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0), Utc(e.Start));
    }

    [Fact]
    public void WeeklyByDayWithExceptionAndMovedOccurrence()
    {
        var events = Ics.Parse(Cal(
            "UID:w\nDTSTART:20261001T070000Z\nDTEND:20261001T073000Z\nRRULE:FREQ=WEEKLY;BYDAY=MO,TH\nEXDATE:20261112T070000Z\nSUMMARY:Sync",
            "UID:w\nRECURRENCE-ID:20261116T070000Z\nDTSTART:20261116T120000Z\nDTEND:20261116T123000Z\nSUMMARY:Sync (moved)"), From, To).ToList();
        var starts = events.Select(e => (Utc(e.Start), e.Title)).OrderBy(x => x.Item1).ToList();
        Assert.Equal(
        [
            (new DateTime(2026, 11, 9, 7, 0, 0), "Sync"),
            // Thursday the 12th is excluded
            (new DateTime(2026, 11, 16, 12, 0, 0), "Sync (moved)"),
            (new DateTime(2026, 11, 19, 7, 0, 0), "Sync"),
        ], starts);
    }

    [Fact]
    public void DailyStandUpFromYearsAgoStillShows()
    {
        var events = Ics.Parse(Cal("UID:d\nDTSTART:20150105T080000Z\nDTEND:20150105T081500Z\nRRULE:FREQ=DAILY\nSUMMARY:Daily"), From, To).ToList();
        Assert.Equal(14, events.Count);
        Assert.Equal(new DateTime(2026, 11, 9, 8, 0, 0), Utc(events[0].Start));
    }

    [Fact]
    public void CountAndUntilEndTheSeries()
    {
        var counted = Ics.Parse(Cal("UID:c\nDTSTART:20261109T100000Z\nRRULE:FREQ=DAILY;COUNT=3\nSUMMARY:Three"), From, To);
        Assert.Equal(3, counted.Count());
        var until = Ics.Parse(Cal("UID:u\nDTSTART:20261109T100000Z\nRRULE:FREQ=DAILY;UNTIL=20261111T235959Z\nSUMMARY:Until"), From, To);
        Assert.Equal(3, until.Count());
    }

    [Fact]
    public void DailyWithByDayKeepsOnlyThoseWeekdays()
    {
        // Thunderbird writes "every weekday" as FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR (RFC 5545: BYDAY limits a DAILY rule)
        var weekdays = Ics.Parse(Cal("UID:d\nDTSTART:20261102T080000Z\nRRULE:FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR\nSUMMARY:Stand-up"), From, To)
            .Select(e => Utc(e.Start)).ToList();
        Assert.Equal(10, weekdays.Count);
        Assert.DoesNotContain(weekdays, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.Equal(new DateTime(2026, 11, 9, 8, 0, 0), weekdays[0]);

        // COUNT counts only the matching days
        var counted = Ics.Parse(Cal("UID:c\nDTSTART:20261109T080000Z\nRRULE:FREQ=DAILY;BYDAY=MO,WE,FR;COUNT=4\nSUMMARY:Gym"), From, To)
            .Select(e => Utc(e.Start)).ToList();
        Assert.Equal(
            [new DateTime(2026, 11, 9, 8, 0, 0), new DateTime(2026, 11, 11, 8, 0, 0), new DateTime(2026, 11, 13, 8, 0, 0), new DateTime(2026, 11, 16, 8, 0, 0)],
            counted);

        // INTERVAL still steps in days before the weekday filter: every 2nd day from Mon 9 Nov, weekdays only
        var everyOther = Ics.Parse(Cal("UID:i\nDTSTART:20261109T080000Z\nRRULE:FREQ=DAILY;INTERVAL=2;BYDAY=MO,TU,WE,TH,FR\nSUMMARY:Alt"), From, To)
            .Select(e => Utc(e.Start).Day).ToList();
        Assert.Equal([9, 11, 13, 17, 19], everyOther);
    }

    [Fact]
    public void MonthlySecondTuesday()
    {
        var e = Ics.Parse(Cal("UID:m\nDTSTART:20260113T150000Z\nRRULE:FREQ=MONTHLY;BYDAY=2TU\nSUMMARY:Review"), From, To).Single();
        Assert.Equal(new DateTime(2026, 11, 10, 15, 0, 0), Utc(e.Start));
    }

    [Fact]
    public void YearlyByMonthAndByDayPicksTheRightDay()
    {
        // Outlook / Google write "yearly on the last Thursday of November" as FREQ=YEARLY;BYMONTH=11;BYDAY=-1TH
        var nov = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var thanksgiving = Ics.Parse(Cal("UID:t\nDTSTART:20251127T120000Z\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=-1TH\nSUMMARY:Dinner"), nov, nov.AddDays(30)).Single();
        Assert.Equal(new DateTime(2026, 11, 26, 12, 0, 0), Utc(thanksgiving.Start));

        // second Sunday of May, across the year boundary of the series
        var may = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var mothersDay = Ics.Parse(Cal("UID:m\nDTSTART:20250511T090000Z\nRRULE:FREQ=YEARLY;BYDAY=2SU;BYMONTH=5\nSUMMARY:Mother's Day"), may, may.AddDays(30)).Single();
        Assert.Equal(new DateTime(2026, 5, 10, 9, 0, 0), Utc(mothersDay.Start));

        // several months without BYDAY keep the start's day of the month: 15 March and 15 September
        var jan = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var twice = Ics.Parse(Cal("UID:s\nDTSTART:20250315T080000Z\nRRULE:FREQ=YEARLY;BYMONTH=3,9\nSUMMARY:Service"), jan, jan.AddYears(1))
            .Select(e => Utc(e.Start)).ToList();
        Assert.Equal([new DateTime(2026, 3, 15, 8, 0, 0), new DateTime(2026, 9, 15, 8, 0, 0)], twice);
    }

    [Theory]
    // RFC 5545 3.8.5.3: "An example where the days generated makes a difference because of WKST"
    [InlineData("WKST=MO", new[] { 5, 10, 19, 24 })]
    [InlineData("WKST=SU", new[] { 5, 17, 19, 31 })] // Google Calendar writes WKST=SU
    public void BiweeklyRuleUsesItsWeekStart(string wkst, int[] augustDays)
    {
        var events = Ics.Parse(Cal($"UID:x\nDTSTART:19970805T130000Z\nRRULE:FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;{wkst}\nSUMMARY:Biweekly"),
            new DateTime(1997, 8, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(1997, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(augustDays, events.Select(e => Utc(e.Start).Day));
    }

    [Theory]
    // RFC 5545 3.8.5.3 examples, and Outlook's "last weekday of every month"
    [InlineData("19970904T130000Z", "FREQ=MONTHLY;COUNT=3;BYDAY=TU,WE,TH;BYSETPOS=3", "09-04,10-07,11-06")]
    [InlineData("19970929T130000Z", "FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-2", "09-29,10-30,11-27,12-30")]
    [InlineData("19970930T130000Z", "FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1", "09-30,10-31,11-28,12-31")]
    public void BySetPosPicksFromEachMonthsSet(string start, string rrule, string dates)
    {
        var events = Ics.Parse(Cal($"UID:x\nDTSTART:{start}\nRRULE:{rrule}\nSUMMARY:S"),
            new DateTime(1997, 9, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(1998, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(dates, string.Join(",", events.Select(e => Utc(e.Start).ToString("MM-dd"))));
    }

    [Fact]
    public void LeapDayBirthdayOnlyHappensInLeapYears()
    {
        // RFC 5545: invalid dates (29 February in 2026) are ignored, not moved to the 28th
        var y2026 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Empty(Ics.Parse(Cal("UID:b\nDTSTART;VALUE=DATE:20240229\nRRULE:FREQ=YEARLY\nSUMMARY:Birthday"), y2026, y2026.AddYears(1)));
        var y2028 = new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2028, 2, 29), Utc(Ics.Parse(Cal("UID:b\nDTSTART;VALUE=DATE:20240229\nRRULE:FREQ=YEARLY\nSUMMARY:Birthday"), y2028, y2028.AddYears(1)).Single().Start));
        // COUNT counts the real occurrences
        var counted = Ics.Parse(Cal("UID:c\nDTSTART:20240229T090000Z\nRRULE:FREQ=YEARLY;COUNT=3\nSUMMARY:Leap"),
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Select(e => Utc(e.Start).Year).ToList();
        Assert.Equal([2024, 2028, 2032], counted);
    }

    [Fact]
    public void CancelledEventsAreSkipped()
    {
        Assert.Empty(Ics.Parse(Cal("UID:x\nDTSTART:20261110T090000Z\nSTATUS:CANCELLED\nSUMMARY:Gone"), From, To));
    }

    [Fact]
    public void WallClockTimeSurvivesDaylightSavingChange()
    {
        // weekly at 09:00 Berlin time across the end of DST (25 Oct 2026): 07:00 UTC before, 08:00 UTC after
        var from = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        var events = Ics.Parse(Cal("UID:b\nDTSTART;TZID=Europe/Berlin:20261019T090000\nRRULE:FREQ=WEEKLY\nSUMMARY:Weekly"), from, from.AddDays(10)).ToList();
        Assert.Equal(new DateTime(2026, 10, 19, 7, 0, 0), Utc(events[0].Start));
        Assert.Equal(new DateTime(2026, 10, 26, 8, 0, 0), Utc(events[1].Start));
    }

    [Fact]
    public void LocalTimeSeriesKeepsItsWallClockAcrossDaylightSaving()
    {
        // floating times and TZIDs the computer does not know are the computer's own time: 09:00 in winter and in summer
        // A series repeating one fixed UTC time can match at most one of the two, whatever this machine's zone is.
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var winter = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);
        var summer = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);
        foreach (var start in new[] { "DTSTART:20260302T090000", "DTSTART;TZID=Customized Time Zone:20260302T090000" })
        {
            var ics = Cal($"UID:f\n{start}\nRRULE:FREQ=WEEKLY\nSUMMARY:Standup");
            Assert.Equal(new DateTime(2026, 3, 9, 8, 0, 0), Utc(Ics.Parse(ics, winter, winter.AddDays(7), berlin).Single().Start)); // 09:00 CET
            Assert.Equal(new DateTime(2026, 7, 6, 7, 0, 0), Utc(Ics.Parse(ics, summer, summer.AddDays(7), berlin).Single().Start)); // 09:00 CEST
        }
        // a UTC series still repeats in UTC
        var utc = Ics.Parse(Cal("UID:u\nDTSTART:20260302T080000Z\nRRULE:FREQ=WEEKLY\nSUMMARY:Sync"), summer, summer.AddDays(7), berlin).Single();
        Assert.Equal(new DateTime(2026, 7, 6, 8, 0, 0), Utc(utc.Start));
    }
}

public class RuleWindowTests
{
    [Theory]
    [InlineData("22:00-07:00", "23:30", true)]
    [InlineData("22:00-07:00", "06:59", true)]
    [InlineData("22:00-07:00", "07:00", false)]
    [InlineData("09:00-17:00", "12:00", true)]
    [InlineData("09:00-17:00", "17:00", false)]
    [InlineData("08:30", "08:30", true)]
    [InlineData("08:30", "08:31", false)]
    [InlineData("nonsense", "08:30", false)]
    // "until midnight" written as 24:00 holds all evening, not only its first minute
    [InlineData("18:00-24:00", "23:30", true)]
    [InlineData("18:00-24:00", "20:00", true)]
    [InlineData("18:00-24:00", "17:59", false)]
    [InlineData("18:00-24:00", "00:00", false)]
    public void TimeWindows(string window, string now, bool expected) =>
        Assert.Equal(expected, RuleEngine.InWindow(window, TimeSpan.Parse(now)));
}
