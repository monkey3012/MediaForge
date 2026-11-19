using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MediaForge.Models;

namespace MediaForge.Services;

public sealed class JobDatabase
{
    readonly string dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaForge", "mediaforge.db");

    SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var c = new SqliteConnection($"Data Source={dbPath}");
        c.Open();
        return c;
    }

    public JobDatabase()
    {
        using var c = Open();
        using var x = c.CreateCommand();
        x.CommandText = "CREATE TABLE IF NOT EXISTS Jobs(Id INTEGER PRIMARY KEY,SourceName TEXT NOT NULL,SourcePath TEXT NOT NULL,Status INTEGER NOT NULL,Stage TEXT NOT NULL,Progress INTEGER NOT NULL,SourceDurationSeconds INTEGER,SourceChapterCount INTEGER,SourceKind INTEGER NOT NULL DEFAULT 0,MediaType TEXT,Title TEXT,Year INTEGER,Season INTEGER,Episode INTEGER,EpisodeTitle TEXT,OutputPath TEXT,ErrorMessage TEXT,TmdbId INTEGER,TmdbType TEXT,RippedSizeBytes INTEGER,EncodedSizeBytes INTEGER,ChapterStart INTEGER,ChapterEnd INTEGER);";
        x.ExecuteNonQuery();
        TryAddColumn(c, "TmdbId", "INTEGER");
        TryAddColumn(c, "TmdbType", "TEXT");
        TryAddColumn(c, "RippedSizeBytes", "INTEGER");
        TryAddColumn(c, "EncodedSizeBytes", "INTEGER");
        TryAddColumn(c, "ChapterStart", "INTEGER");
        TryAddColumn(c, "ChapterEnd", "INTEGER");
        TryAddColumn(c, "SourceDurationSeconds", "INTEGER");
        TryAddColumn(c, "SourceChapterCount", "INTEGER");
        TryAddColumn(c, "SourceKind", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(c, "ChapterDurationsSeconds", "TEXT");
    }

    static void TryAddColumn(SqliteConnection c, string name, string type)
    {
        using var x = c.CreateCommand();
        x.CommandText = $"ALTER TABLE Jobs ADD COLUMN {name} {type}";
        try { x.ExecuteNonQuery(); } catch (SqliteException) { }
    }

    public List<MediaJob> LoadAll()
    {
        using var c = Open();
        using var x = c.CreateCommand();
        x.CommandText = "SELECT Id,SourceName,SourcePath,Status,Stage,Progress,SourceDurationSeconds,SourceChapterCount,SourceKind,MediaType,Title,Year,Season,Episode,EpisodeTitle,OutputPath,ErrorMessage,TmdbId,TmdbType,RippedSizeBytes,EncodedSizeBytes,ChapterStart,ChapterEnd,ChapterDurationsSeconds FROM Jobs ORDER BY Id DESC";
        using var r = x.ExecuteReader();
        var a = new List<MediaJob>();
        while (r.Read())
        {
            a.Add(new MediaJob
            {
                Id = r.GetInt64(0),
                SourceName = r.GetString(1),
                SourcePath = r.GetString(2),
                Status = (JobStatus)r.GetInt32(3),
                Stage = r.GetString(4),
                Progress = r.GetInt32(5),
                SourceDurationSeconds = LongVal(r, 6),
                SourceChapterCount = IntVal(r, 7),
                SourceKind = EnumVal<MediaSourceKind>(r, 8),
                MediaType = Val(r, 9),
                Title = Val(r, 10),
                Year = IntVal(r, 11),
                Season = IntVal(r, 12),
                Episode = IntVal(r, 13),
                EpisodeTitle = Val(r, 14),
                OutputPath = Val(r, 15),
                ErrorMessage = Val(r, 16),
                TmdbId = IntVal(r, 17),
                TmdbType = Val(r, 18),
                RippedSizeBytes = LongVal(r, 19),
                EncodedSizeBytes = LongVal(r, 20),
                ChapterStart = IntVal(r, 21),
                ChapterEnd = IntVal(r, 22),
                ChapterDurationsSeconds = DeserializeChapterDurations(Val(r, 23))
            });
        }
        return a;
    }

    static List<double> DeserializeChapterDurations(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? new List<double>() : JsonSerializer.Deserialize<List<double>>(json) ?? new List<double>(); }
        catch { return new List<double>(); }
    }

    static string? Val(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    static int? IntVal(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    static long? LongVal(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    static T EnumVal<T>(SqliteDataReader r, int i) where T : struct, Enum => r.IsDBNull(i) ? default : (T)Enum.ToObject(typeof(T), r.GetInt32(i));

    public void Upsert(MediaJob j)
    {
        using var c = Open();
        using var x = c.CreateCommand();
        x.CommandText = @"
INSERT INTO Jobs
(Id,SourceName,SourcePath,Status,Stage,Progress,SourceDurationSeconds,SourceChapterCount,SourceKind,MediaType,Title,Year,Season,Episode,EpisodeTitle,OutputPath,ErrorMessage,TmdbId,TmdbType,RippedSizeBytes,EncodedSizeBytes,ChapterStart,ChapterEnd,ChapterDurationsSeconds)
VALUES($id,$sn,$sp,$st,$stage,$p,$dur,$chap,$kind,$mt,$t,$y,$s,$e,$et,$op,$err,$tid,$tt,$rs,$es,$cs,$ce,$chapterDurations)
ON CONFLICT(Id) DO UPDATE SET
SourceName=$sn,SourcePath=$sp,Status=$st,Stage=$stage,Progress=$p,SourceDurationSeconds=$dur,SourceChapterCount=$chap,SourceKind=$kind,MediaType=$mt,Title=$t,Year=$y,Season=$s,Episode=$e,EpisodeTitle=$et,OutputPath=$op,ErrorMessage=$err,TmdbId=$tid,TmdbType=$tt,RippedSizeBytes=$rs,EncodedSizeBytes=$es,ChapterStart=$cs,ChapterEnd=$ce,ChapterDurationsSeconds=$chapterDurations";
        x.Parameters.AddWithValue("$id", j.Id);
        x.Parameters.AddWithValue("$sn", j.SourceName);
        x.Parameters.AddWithValue("$sp", j.SourcePath);
        x.Parameters.AddWithValue("$st", (int)j.Status);
        x.Parameters.AddWithValue("$stage", j.Stage);
        x.Parameters.AddWithValue("$p", j.Progress);
        x.Parameters.AddWithValue("$dur", (object?)j.SourceDurationSeconds ?? DBNull.Value);
        x.Parameters.AddWithValue("$chap", (object?)j.SourceChapterCount ?? DBNull.Value);
        x.Parameters.AddWithValue("$kind", (int)j.SourceKind);
        x.Parameters.AddWithValue("$mt", (object?)j.MediaType ?? DBNull.Value);
        x.Parameters.AddWithValue("$t", (object?)j.Title ?? DBNull.Value);
        x.Parameters.AddWithValue("$y", (object?)j.Year ?? DBNull.Value);
        x.Parameters.AddWithValue("$s", (object?)j.Season ?? DBNull.Value);
        x.Parameters.AddWithValue("$e", (object?)j.Episode ?? DBNull.Value);
        x.Parameters.AddWithValue("$et", (object?)j.EpisodeTitle ?? DBNull.Value);
        x.Parameters.AddWithValue("$op", (object?)j.OutputPath ?? DBNull.Value);
        x.Parameters.AddWithValue("$err", (object?)j.ErrorMessage ?? DBNull.Value);
        x.Parameters.AddWithValue("$tid", (object?)j.TmdbId ?? DBNull.Value);
        x.Parameters.AddWithValue("$tt", (object?)j.TmdbType ?? DBNull.Value);
        x.Parameters.AddWithValue("$rs", (object?)j.RippedSizeBytes ?? DBNull.Value);
        x.Parameters.AddWithValue("$es", (object?)j.EncodedSizeBytes ?? DBNull.Value);
        x.Parameters.AddWithValue("$cs", (object?)j.ChapterStart ?? DBNull.Value);
        x.Parameters.AddWithValue("$ce", (object?)j.ChapterEnd ?? DBNull.Value);
        x.Parameters.AddWithValue("$chapterDurations", j.ChapterDurationsSeconds.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(j.ChapterDurationsSeconds));
        x.ExecuteNonQuery();
    }
    public void Delete(long id)
    {
        using var c = Open();
        using var x = c.CreateCommand();
        x.CommandText = "DELETE FROM Jobs WHERE Id=$id";
        x.Parameters.AddWithValue("$id", id);
        x.ExecuteNonQuery();
    }
}
