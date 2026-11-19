using System.Text.Json;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class GoogleCalendarTests
{
    private const string Page = """
    {
      "items": [
        { "status": "confirmed", "summary": "Team sync", "location": "Room 1",
          "start": { "dateTime": "2030-03-04T12:00:00+05:00" }, "end": { "dateTime": "2030-03-04T13:00:00+05:00" } },
        { "status": "confirmed", "summary": "Holiday",
          "start": { "date": "2030-03-05" }, "end": { "date": "2030-03-06" } },
        { "status": "cancelled", "summary": "Gone",
          "start": { "dateTime": "2030-03-04T15:00:00Z" }, "end": { "dateTime": "2030-03-04T16:00:00Z" } },
        { "status": "confirmed", "eventType": "workingLocation", "summary": "Office",
          "start": { "date": "2030-03-04" }, "end": { "date": "2030-03-05" } },
        { "status": "confirmed", "summary": "Declined call",
          "attendees": [ { "self": true, "responseStatus": "declined" } ],
          "start": { "dateTime": "2030-03-04T18:00:00Z" }, "end": { "dateTime": "2030-03-04T19:00:00Z" } },
        { "status": "confirmed",
          "start": { "dateTime": "2030-03-04T20:00:00Z" }, "end": { "dateTime": "2030-03-04T20:30:00Z" } }
      ]
    }
    """;

    [Fact]
    public void Parses_timed_and_all_day_events_and_skips_cancelled_declined_and_working_location()
    {
        using var doc = JsonDocument.Parse(Page);
        var events = GoogleCalendar.ParseEvents(doc.RootElement, "Work", "#123456").ToList();

        Assert.Equal(["Team sync", "Holiday", "(no title)"], events.Select(e => e.Title));

        var sync = events[0];
        Assert.False(sync.AllDay);
        Assert.Equal(new DateTimeOffset(2030, 3, 4, 7, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), sync.Start);
        Assert.Equal(60 * 60 * 1000, sync.End - sync.Start);
        Assert.Equal("Room 1", sync.Location);
        Assert.Equal("Work", sync.Calendar);
        Assert.Equal("#123456", sync.Color);

        var holiday = events[1];
        Assert.True(holiday.AllDay);
        Assert.Equal(new DateTimeOffset(2030, 3, 5, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), holiday.Start);
        Assert.Equal(24 * 60 * 60 * 1000, holiday.End - holiday.Start);
    }

    [Fact]
    public void The_phone_config_never_carries_the_google_client()
    {
        var cfg = new AgentConfig();
        cfg.GoogleCalendar.ClientId = "test-client.apps.example";
        cfg.GoogleCalendar.ClientSecret = "test-secret-value";
        var json = JsonSerializer.Serialize(cfg.ToPublic());
        Assert.DoesNotContain("test-secret-value", json);
        Assert.DoesNotContain("test-client", json);
    }

    [Fact]
    public void A_page_without_items_gives_no_events()
    {
        using var doc = JsonDocument.Parse("""{ "kind": "calendar#events" }""");
        Assert.Empty(GoogleCalendar.ParseEvents(doc.RootElement, "Work", "#fff"));
    }
}
