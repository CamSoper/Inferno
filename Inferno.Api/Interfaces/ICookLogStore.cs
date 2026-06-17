using Inferno.Api.Models;

namespace Inferno.Api.Interfaces
{
    /// <summary>
    /// Persistence abstraction for cook history. Implementations own their storage
    /// connection for their lifetime; call <see cref="Initialize"/> once before use
    /// and dispose to flush and release resources. Methods are safe to call from the
    /// logger loop and request threads concurrently.
    /// </summary>
    public interface ICookLogStore : IDisposable
    {
        /// <summary>Open the store, applying any one-time setup (connection, schema).</summary>
        void Initialize();

        /// <summary>Begin a new cook session. Returns its id.</summary>
        long OpenSession(DateTime startUtc, string? label);

        /// <summary>Finalize a session with its end time and summary stats.</summary>
        void CloseSession(long id, DateTime endUtc, double peakGrillTemp, double peakProbeTemp, int sampleCount);

        /// <summary>Persist a batch of samples in a single transaction.</summary>
        void InsertSamples(long sessionId, IReadOnlyList<CookSample> samples);

        /// <summary>The id of the currently-open session (end_time IS NULL), or null.</summary>
        long? GetActiveSessionId();

        /// <summary>Set or rename a session's label.</summary>
        void SetLabel(long id, string label);

        /// <summary>All sessions, most recent first.</summary>
        IReadOnlyList<CookSessionDto> ListSessions();

        /// <summary>A single session, or null if it does not exist.</summary>
        CookSessionDto? GetSession(long id);

        /// <summary>A session's samples ordered by timestamp, optionally bounded to [fromUtc, toUtc].</summary>
        IReadOnlyList<CookSample> GetSamples(long id, DateTime? fromUtc, DateTime? toUtc);
    }
}
