using System.Diagnostics;
using System.Globalization;
using Inferno.Api.Interfaces;
using Inferno.Api.Models;
using Microsoft.Data.Sqlite;

namespace Inferno.Api.Services
{
    /// <summary>
    /// SQLite-backed cook history store. Holds a single long-lived connection (so an
    /// in-memory database survives for the process lifetime in tests, and the Pi keeps
    /// one handle instead of churning file opens). WAL + synchronous=NORMAL minimize
    /// SD-card fsyncs; writes are batched into transactions by the caller. A lock
    /// serializes all access to the connection across the logger loop and request threads.
    /// </summary>
    public class SqliteCookLogStore : ICookLogStore
    {
        // Round-trippable, sortable, timezone-unambiguous timestamps.
        const string TimeFormat = "o";

        readonly string _connectionString;
        readonly object _sync = new();
        SqliteConnection? _connection;

        public SqliteCookLogStore(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
        }

        public void Initialize()
        {
            lock (_sync)
            {
                if (_connection != null)
                {
                    return;
                }

                _connection = new SqliteConnection(_connectionString);
                _connection.Open();

                Execute(
                    "PRAGMA journal_mode=WAL;" +
                    "PRAGMA synchronous=NORMAL;" +
                    "CREATE TABLE IF NOT EXISTS cook_session (" +
                    "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
                    "  start_time TEXT NOT NULL," +
                    "  end_time TEXT," +
                    "  label TEXT," +
                    "  peak_grill_temp REAL," +
                    "  peak_probe_temp REAL," +
                    "  sample_count INTEGER NOT NULL DEFAULT 0" +
                    ");" +
                    "CREATE TABLE IF NOT EXISTS sample (" +
                    "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
                    "  session_id INTEGER NOT NULL REFERENCES cook_session(id)," +
                    "  timestamp TEXT NOT NULL," +
                    "  grill_temp REAL," +
                    "  probe_temp REAL," +
                    "  mode TEXT NOT NULL," +
                    "  setpoint INTEGER," +
                    "  pvalue INTEGER," +
                    "  auger_on INTEGER NOT NULL," +
                    "  blower_on INTEGER NOT NULL," +
                    "  igniter_on INTEGER NOT NULL," +
                    "  fire_healthy INTEGER NOT NULL," +
                    "  preheated INTEGER NOT NULL" +
                    ");" +
                    "CREATE INDEX IF NOT EXISTS ix_sample_session_time ON sample(session_id, timestamp);");
            }
        }

        public long OpenSession(DateTime startUtc, string? label)
        {
            lock (_sync)
            {
                using var cmd = Connection().CreateCommand();
                cmd.CommandText =
                    "INSERT INTO cook_session (start_time, label) VALUES ($start, $label);" +
                    "SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$start", startUtc.ToUniversalTime().ToString(TimeFormat));
                cmd.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
                return (long)cmd.ExecuteScalar()!;
            }
        }

        public void CloseSession(long id, DateTime endUtc, double peakGrillTemp, double peakProbeTemp, int sampleCount)
        {
            lock (_sync)
            {
                using var cmd = Connection().CreateCommand();
                cmd.CommandText =
                    "UPDATE cook_session SET end_time = $end, peak_grill_temp = $pg, " +
                    "peak_probe_temp = $pp, sample_count = $count WHERE id = $id;";
                cmd.Parameters.AddWithValue("$end", endUtc.ToUniversalTime().ToString(TimeFormat));
                cmd.Parameters.AddWithValue("$pg", double.IsNaN(peakGrillTemp) ? (object)DBNull.Value : peakGrillTemp);
                cmd.Parameters.AddWithValue("$pp", double.IsNaN(peakProbeTemp) ? (object)DBNull.Value : peakProbeTemp);
                cmd.Parameters.AddWithValue("$count", sampleCount);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        public void InsertSamples(long sessionId, IReadOnlyList<CookSample> samples)
        {
            if (samples.Count == 0)
            {
                return;
            }

            lock (_sync)
            {
                var connection = Connection();
                using var tx = connection.BeginTransaction();
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO sample (session_id, timestamp, grill_temp, probe_temp, mode, setpoint, " +
                    "pvalue, auger_on, blower_on, igniter_on, fire_healthy, preheated) VALUES " +
                    "($sid, $ts, $grill, $probe, $mode, $sp, $pv, $auger, $blower, $igniter, $fire, $preheat);";

                var sid = cmd.Parameters.Add("$sid", SqliteType.Integer);
                var ts = cmd.Parameters.Add("$ts", SqliteType.Text);
                var grill = cmd.Parameters.Add("$grill", SqliteType.Real);
                var probe = cmd.Parameters.Add("$probe", SqliteType.Real);
                var mode = cmd.Parameters.Add("$mode", SqliteType.Text);
                var sp = cmd.Parameters.Add("$sp", SqliteType.Integer);
                var pv = cmd.Parameters.Add("$pv", SqliteType.Integer);
                var auger = cmd.Parameters.Add("$auger", SqliteType.Integer);
                var blower = cmd.Parameters.Add("$blower", SqliteType.Integer);
                var igniter = cmd.Parameters.Add("$igniter", SqliteType.Integer);
                var fire = cmd.Parameters.Add("$fire", SqliteType.Integer);
                var preheat = cmd.Parameters.Add("$preheat", SqliteType.Integer);

                foreach (var s in samples)
                {
                    sid.Value = sessionId;
                    ts.Value = s.Timestamp.ToUniversalTime().ToString(TimeFormat);
                    grill.Value = s.GrillTemp;
                    probe.Value = s.ProbeTemp;
                    mode.Value = s.Mode;
                    sp.Value = s.SetPoint;
                    pv.Value = s.PValue;
                    auger.Value = s.AugerOn ? 1 : 0;
                    blower.Value = s.BlowerOn ? 1 : 0;
                    igniter.Value = s.IgniterOn ? 1 : 0;
                    fire.Value = s.FireHealthy ? 1 : 0;
                    preheat.Value = s.Preheated ? 1 : 0;
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
        }

        public long? GetActiveSessionId()
        {
            lock (_sync)
            {
                using var cmd = Connection().CreateCommand();
                cmd.CommandText = "SELECT id FROM cook_session WHERE end_time IS NULL ORDER BY id DESC LIMIT 1;";
                var result = cmd.ExecuteScalar();
                return result is long id ? id : null;
            }
        }

        public void SetLabel(long id, string label)
        {
            lock (_sync)
            {
                using var cmd = Connection().CreateCommand();
                cmd.CommandText = "UPDATE cook_session SET label = $label WHERE id = $id;";
                cmd.Parameters.AddWithValue("$label", label);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        public IReadOnlyList<CookSessionDto> ListSessions()
        {
            lock (_sync)
            {
                using var cmd = Connection().CreateCommand();
                cmd.CommandText =
                    "SELECT id, start_time, end_time, label, peak_grill_temp, peak_probe_temp, sample_count " +
                    "FROM cook_session ORDER BY id DESC;";
                using var reader = cmd.ExecuteReader();
                var sessions = new List<CookSessionDto>();
                while (reader.Read())
                {
                    sessions.Add(ReadSession(reader));
                }
                return sessions;
            }
        }

        public CookSessionDto? GetSession(long id)
        {
            lock (_sync)
            {
                using var cmd = Connection().CreateCommand();
                cmd.CommandText =
                    "SELECT id, start_time, end_time, label, peak_grill_temp, peak_probe_temp, sample_count " +
                    "FROM cook_session WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadSession(reader) : null;
            }
        }

        public IReadOnlyList<CookSample> GetSamples(long id, DateTime? fromUtc, DateTime? toUtc)
        {
            lock (_sync)
            {
                using var cmd = Connection().CreateCommand();
                cmd.CommandText =
                    "SELECT timestamp, grill_temp, probe_temp, mode, setpoint, pvalue, " +
                    "auger_on, blower_on, igniter_on, fire_healthy, preheated FROM sample " +
                    "WHERE session_id = $id" +
                    (fromUtc != null ? " AND timestamp >= $from" : "") +
                    (toUtc != null ? " AND timestamp <= $to" : "") +
                    " ORDER BY timestamp, id;";
                cmd.Parameters.AddWithValue("$id", id);
                if (fromUtc != null)
                {
                    cmd.Parameters.AddWithValue("$from", fromUtc.Value.ToUniversalTime().ToString(TimeFormat));
                }
                if (toUtc != null)
                {
                    cmd.Parameters.AddWithValue("$to", toUtc.Value.ToUniversalTime().ToString(TimeFormat));
                }

                using var reader = cmd.ExecuteReader();
                var samples = new List<CookSample>();
                while (reader.Read())
                {
                    samples.Add(new CookSample
                    {
                        Timestamp = ParseTime(reader.GetString(0)),
                        GrillTemp = reader.GetDouble(1),
                        ProbeTemp = reader.GetDouble(2),
                        Mode = reader.GetString(3),
                        SetPoint = reader.GetInt32(4),
                        PValue = reader.GetInt32(5),
                        AugerOn = reader.GetInt32(6) != 0,
                        BlowerOn = reader.GetInt32(7) != 0,
                        IgniterOn = reader.GetInt32(8) != 0,
                        FireHealthy = reader.GetInt32(9) != 0,
                        Preheated = reader.GetInt32(10) != 0,
                    });
                }
                return samples;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_connection == null)
                {
                    return;
                }

                try
                {
                    Execute("PRAGMA wal_checkpoint(TRUNCATE);");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{DateTime.Now} Cook log checkpoint failed: {ex.Message}");
                }

                _connection.Dispose();
                _connection = null;
                // Release the pooled native handle so a temp-file DB can be deleted in tests.
                SqliteConnection.ClearAllPools();
            }
        }

        SqliteConnection Connection() =>
            _connection ?? throw new InvalidOperationException("Store not initialized. Call Initialize() first.");

        void Execute(string sql)
        {
            using var cmd = Connection().CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        static CookSessionDto ReadSession(SqliteDataReader reader) => new()
        {
            Id = reader.GetInt64(0),
            StartTime = ParseTime(reader.GetString(1)),
            EndTime = reader.IsDBNull(2) ? null : ParseTime(reader.GetString(2)),
            Label = reader.IsDBNull(3) ? null : reader.GetString(3),
            PeakGrillTemp = reader.IsDBNull(4) ? null : reader.GetDouble(4),
            PeakProbeTemp = reader.IsDBNull(5) ? null : reader.GetDouble(5),
            SampleCount = reader.GetInt32(6),
        };

        static DateTime ParseTime(string value) =>
            DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
