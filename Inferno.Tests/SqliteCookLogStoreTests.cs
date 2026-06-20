using Inferno.Api.Models;
using Inferno.Api.Services;

namespace Inferno.Tests;

public class SqliteCookLogStoreTests
{
    // A fresh in-memory store. Each gets a uniquely named shared-cache memory DB so
    // tests are isolated yet the data survives for the store's single connection.
    static SqliteCookLogStore NewStore()
    {
        var store = new SqliteCookLogStore($"file:cooklog-{Guid.NewGuid():N}?mode=memory&cache=shared");
        store.Initialize();
        return store;
    }

    static CookSample Sample(DateTime ts, double grill = 225, double probe = 140) => new()
    {
        Timestamp = ts,
        GrillTemp = grill,
        ProbeTemp = probe,
        Mode = "Hold",
        SetPoint = 225,
        PValue = 2,
        AugerOn = true,
        BlowerOn = true,
        IgniterOn = false,
        FireHealthy = true,
        Preheated = true,
    };

    [Fact]
    public void Initialize_IsIdempotent()
    {
        using var store = NewStore();
        // Second Initialize on an already-open store is a no-op, not an error.
        store.Initialize();
        Assert.Empty(store.ListSessions());
    }

    [Fact]
    public void OpenClose_RoundTrips_AndActiveIdTracksState()
    {
        using var store = NewStore();
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        long id = store.OpenSession(start, "brisket");
        Assert.Equal(id, store.GetActiveSessionId());

        store.CloseSession(id, start.AddHours(8), 250, 203, 42);
        Assert.Null(store.GetActiveSessionId());

        var session = store.GetSession(id);
        Assert.NotNull(session);
        Assert.Equal("brisket", session!.Label);
        Assert.Equal(start, session.StartTime);
        Assert.Equal(start.AddHours(8), session.EndTime);
        Assert.Equal(250, session.PeakGrillTemp);
        Assert.Equal(203, session.PeakProbeTemp);
        Assert.Equal(42, session.SampleCount);
    }

    [Fact]
    public void InsertSamples_PersistsBatch_OrderedByTimestamp()
    {
        using var store = NewStore();
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        long id = store.OpenSession(t, null);

        // Insert out of order; GetSamples must return them sorted by timestamp.
        store.InsertSamples(id, new[]
        {
            Sample(t.AddSeconds(20), grill: 230),
            Sample(t.AddSeconds(0), grill: 210),
            Sample(t.AddSeconds(10), grill: 220),
        });

        var samples = store.GetSamples(id, null, null);
        Assert.Equal(3, samples.Count);
        Assert.Equal(210, samples[0].GrillTemp);
        Assert.Equal(220, samples[1].GrillTemp);
        Assert.Equal(230, samples[2].GrillTemp);
        Assert.False(samples[0].IgniterOn);
        Assert.True(samples[0].AugerOn);
        Assert.Equal("Hold", samples[0].Mode);
    }

    [Fact]
    public void GetSamples_FiltersByTimeBounds()
    {
        using var store = NewStore();
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        long id = store.OpenSession(t, null);
        store.InsertSamples(id, new[]
        {
            Sample(t.AddSeconds(0)),
            Sample(t.AddSeconds(10)),
            Sample(t.AddSeconds(20)),
            Sample(t.AddSeconds(30)),
        });

        var bounded = store.GetSamples(id, t.AddSeconds(10), t.AddSeconds(20));
        Assert.Equal(2, bounded.Count);
        Assert.Equal(t.AddSeconds(10), bounded[0].Timestamp);
        Assert.Equal(t.AddSeconds(20), bounded[1].Timestamp);
    }

    [Fact]
    public void ListSessions_ReturnsNewestFirst()
    {
        using var store = NewStore();
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        long first = store.OpenSession(t, "first");
        long second = store.OpenSession(t.AddHours(1), "second");

        var sessions = store.ListSessions();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(second, sessions[0].Id);
        Assert.Equal(first, sessions[1].Id);
    }

    [Fact]
    public void SetLabel_UpdatesSession()
    {
        using var store = NewStore();
        long id = store.OpenSession(DateTime.UtcNow, null);
        store.SetLabel(id, "pork shoulder");
        Assert.Equal("pork shoulder", store.GetSession(id)!.Label);
    }

    [Fact]
    public void GetSession_ReturnsNull_ForUnknownId()
    {
        using var store = NewStore();
        Assert.Null(store.GetSession(999));
    }
}
