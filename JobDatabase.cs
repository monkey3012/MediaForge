using System.IO;
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
        x.CommandText = "CREATE TABLE IF NOT EXISTS Jobs(Id INTEGER PRIMARY KEY,SourceName TEXT NOT NULL,SourcePath TEXT NOT NULL,Status INTEGER NOT NULL,Stage TEXT NOT NULL,Progress INTEGER NOT NULL,MediaType TEXT,Title TEXT,Year INTEGER,Season INTEGER,Episode INTEGER,EpisodeTitle TEXT,OutputPath TEXT,ErrorMessage TEXT,TmdbId INTEGER,TmdbType TEXT,RippedSizeBytes INTEGER,EncodedSizeBytes INTEGER,ChapterStart INTEGER,ChapterEnd INTEGER);";
        x.ExecuteNonQuery();
        TryAddColumn(c, "TmdbId", "INTEGER");
        TryAddColumn(c, "TmdbType", "TEXT");
        TryAddColumn(c, "RippedSizeBytes", "INTEGER");
        TryAddColumn(c, "EncodedSizeBytes", "INTEGER");
        TryAddColumn(c, "ChapterStart", "INTEGER");
        TryAddColumn(c, "ChapterEnd", "INTEGER");
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
        x.CommandText = "SELECT Id,SourceName,SourcePath,Status,Stage,Progress,MediaType,Title,Year,Season,Episode,EpisodeTitle,OutputPath,ErrorMessage,TmdbId,TmdbType,RippedSizeBytes,EncodedSizeBytes,ChapterStart,ChapterEnd FROM Jobs ORDER BY Id DESC";
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
                MediaType = Val(r, 6),
                Title = Val(r, 7),
                Year = IntVal(r, 8),
                Season = IntVal(r, 9),
                Episode = IntVal(r, 10),
                EpisodeTitle = Val(r, 11),
                OutputPath = Val(r, 12),
                ErrorMessage = Val(r, 13),
                TmdbId = IntVal(r, 14),
                TmdbType = Val(r, 15),
                RippedSizeBytes = LongVal(r, 16),
                EncodedSizeBytes = LongVal(r, 17),
                ChapterStart = IntVal(r, 18),
                ChapterEnd = IntVal(r, 19)
            });
        }
        return a;
    }

    static string? Val(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    static int? IntVal(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    static long? LongVal(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    public void Upsert(MediaJob j)
    {
        using var c = Open();
        using var x = c.CreateCommand();
        x.CommandText = @"
INSERT INTO Jobs
(Id,SourceName,SourcePath,Status,Stage,Progress,MediaType,Title,Year,Season,Episode,EpisodeTitle,OutputPath,ErrorMessage,TmdbId,TmdbType,RippedSizeBytes,EncodedSizeBytes,ChapterStart,ChapterEnd)
VALUES($id,$sn,$sp,$st,$stage,$p,$mt,$t,$y,$s,$e,$et,$op,$err,$tid,$tt,$rs,$es,$cs,$ce)
ON CONFLICT(Id) DO UPDATE SET
SourceName=$sn,SourcePath=$sp,Status=$st,Stage=$stage,Progress=$p,MediaType=$mt,Title=$t,Year=$y,Season=$s,Episode=$e,EpisodeTitle=$et,OutputPath=$op,ErrorMessage=$err,TmdbId=$tid,TmdbType=$tt,RippedSizeBytes=$rs,EncodedSizeBytes=$es,ChapterStart=$cs,ChapterEnd=$ce";
        x.Parameters.AddWithValue("$id", j.Id);
        x.Parameters.AddWithValue("$sn", j.SourceName);
        x.Parameters.AddWithValue("$sp", j.SourcePath);
        x.Parameters.AddWithValue("$st", (int)j.Status);
        x.Parameters.AddWithValue("$stage", j.Stage);
        x.Parameters.AddWithValue("$p", j.Progress);
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
