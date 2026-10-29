using Microsoft.Data.Sqlite;

namespace AfifPbx.Data;

public sealed record CallRecord(long Id, DateTime Start, DateTime? Answer, DateTime? End, string FromNumber, string FromName,
    string ToNumber, string Direction, string Disposition, int DurationSeconds, string Recording);

public sealed record SmsMessage(long Id, DateTime Time, string Direction, string Number, string Text, string Status);

public sealed record VoicemailMessage(long Id, string Mailbox, string Caller, DateTime Time, int DurationSeconds, string File, bool Heard);

/// <summary>SQLite store for call log, SMS and voicemail.</summary>
public sealed class Database
{
    private readonly string _cs;
    private readonly object _lock = new();

    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared }.ToString();
        Exec(@"
CREATE TABLE IF NOT EXISTS cdr(id INTEGER PRIMARY KEY AUTOINCREMENT, start TEXT, answer TEXT, end TEXT,
  from_num TEXT, from_name TEXT, to_num TEXT, direction TEXT, disposition TEXT, duration INTEGER, recording TEXT);
CREATE TABLE IF NOT EXISTS sms(id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT, direction TEXT, number TEXT, text TEXT, status TEXT);
CREATE TABLE IF NOT EXISTS voicemail(id INTEGER PRIMARY KEY AUTOINCREMENT, mailbox TEXT, caller TEXT, time TEXT,
  duration INTEGER, file TEXT, heard INTEGER DEFAULT 0);
CREATE INDEX IF NOT EXISTS ix_cdr_start ON cdr(start);
CREATE INDEX IF NOT EXISTS ix_vm_box ON voicemail(mailbox);
CREATE TABLE IF NOT EXISTS sms_pending(id INTEGER PRIMARY KEY AUTOINCREMENT, sms_id INTEGER, ext TEXT, tries INTEGER DEFAULT 0);");
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private void Exec(string sql, params (string, object?)[] p)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private long Insert(string sql, params (string, object?)[] p)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql + "; SELECT last_insert_rowid();";
            foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] p)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("o");
    private static DateTime? ReadDate(SqliteDataReader r, int i) =>
        r.IsDBNull(i) || string.IsNullOrEmpty(r.GetString(i)) ? null : DateTime.Parse(r.GetString(i)).ToUniversalTime();
    private static string S(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    // ---- call log ----
    public long AddCall(CallRecord c) => Insert(
        "INSERT INTO cdr(start,answer,end,from_num,from_name,to_num,direction,disposition,duration,recording) VALUES($s,$a,$e,$f,$fn,$t,$d,$disp,$dur,$rec)",
        ("$s", Iso(c.Start)), ("$a", c.Answer is null ? null : Iso(c.Answer.Value)), ("$e", c.End is null ? null : Iso(c.End.Value)),
        ("$f", c.FromNumber), ("$fn", c.FromName), ("$t", c.ToNumber), ("$d", c.Direction), ("$disp", c.Disposition),
        ("$dur", c.DurationSeconds), ("$rec", c.Recording));

    public List<CallRecord> Calls(int limit = 200, string? search = null) => Query(
        "SELECT id,start,answer,end,from_num,from_name,to_num,direction,disposition,duration,recording FROM cdr " +
        "WHERE ($q IS NULL OR from_num LIKE $q OR to_num LIKE $q OR from_name LIKE $q) ORDER BY id DESC LIMIT $l",
        r => new CallRecord(r.GetInt64(0), ReadDate(r, 1) ?? DateTime.MinValue, ReadDate(r, 2), ReadDate(r, 3), S(r, 4), S(r, 5), S(r, 6),
            S(r, 7), S(r, 8), r.IsDBNull(9) ? 0 : r.GetInt32(9), S(r, 10)),
        ("$q", string.IsNullOrWhiteSpace(search) ? null : $"%{search}%"), ("$l", limit));

    public void ClearRecording(string file) => Exec("UPDATE cdr SET recording='' WHERE recording=$f", ("$f", file));

    // ---- SMS ----
    public long AddSms(string direction, string number, string text, string status) => Insert(
        "INSERT INTO sms(time,direction,number,text,status) VALUES($t,$d,$n,$x,$s)",
        ("$t", Iso(DateTime.UtcNow)), ("$d", direction), ("$n", number), ("$x", text), ("$s", status));

    public void SetSmsStatus(long id, string status) => Exec("UPDATE sms SET status=$s WHERE id=$i", ("$s", status), ("$i", id));

    public List<SmsMessage> Sms(int limit = 300) => Query(
        "SELECT id,time,direction,number,text,status FROM sms ORDER BY id DESC LIMIT $l",
        r => new SmsMessage(r.GetInt64(0), ReadDate(r, 1) ?? DateTime.MinValue, S(r, 2), S(r, 3), S(r, 4), S(r, 5)), ("$l", limit));

    public void DeleteSms(long id)
    {
        Exec("DELETE FROM sms WHERE id=$i", ("$i", id));
        Exec("DELETE FROM sms_pending WHERE sms_id=$i", ("$i", id));
    }

    // SMS waiting to be delivered to an extension's phone (AFIF Phone was offline)
    public void AddSmsPending(long smsId, string ext) =>
        Insert("INSERT INTO sms_pending(sms_id,ext,tries) VALUES($s,$e,0)", ("$s", smsId), ("$e", ext));

    public List<(long PendingId, string Ext, SmsMessage Sms)> SmsPending(string? ext = null) => Query(
        "SELECT p.id,p.ext,s.id,s.time,s.direction,s.number,s.text,s.status FROM sms_pending p JOIN sms s ON s.id=p.sms_id " +
        "WHERE ($e IS NULL OR p.ext=$e) ORDER BY p.id",
        r => (r.GetInt64(0), S(r, 1), new SmsMessage(r.GetInt64(2), ReadDate(r, 3) ?? DateTime.MinValue, S(r, 4), S(r, 5), S(r, 6), S(r, 7))),
        ("$e", ext));

    public void RemoveSmsPending(long pendingId) => Exec("DELETE FROM sms_pending WHERE id=$i", ("$i", pendingId));

    // ---- voicemail ----
    public long AddVoicemail(string mailbox, string caller, int seconds, string file) => Insert(
        "INSERT INTO voicemail(mailbox,caller,time,duration,file,heard) VALUES($m,$c,$t,$d,$f,0)",
        ("$m", mailbox), ("$c", caller), ("$t", Iso(DateTime.UtcNow)), ("$d", seconds), ("$f", file));

    public List<VoicemailMessage> Voicemails(string? mailbox = null) => Query(
        "SELECT id,mailbox,caller,time,duration,file,heard FROM voicemail WHERE ($m IS NULL OR mailbox=$m) ORDER BY id DESC",
        r => new VoicemailMessage(r.GetInt64(0), S(r, 1), S(r, 2), ReadDate(r, 3) ?? DateTime.MinValue, r.GetInt32(4), S(r, 5), r.GetInt32(6) != 0),
        ("$m", mailbox));

    public VoicemailMessage? Voicemail(long id) => Query(
        "SELECT id,mailbox,caller,time,duration,file,heard FROM voicemail WHERE id=$i",
        r => new VoicemailMessage(r.GetInt64(0), S(r, 1), S(r, 2), ReadDate(r, 3) ?? DateTime.MinValue, r.GetInt32(4), S(r, 5), r.GetInt32(6) != 0),
        ("$i", id)).FirstOrDefault();

    public void MarkVoicemailHeard(long id) => Exec("UPDATE voicemail SET heard=1 WHERE id=$i", ("$i", id));
    public void DeleteVoicemail(long id) => Exec("DELETE FROM voicemail WHERE id=$i", ("$i", id));
}
