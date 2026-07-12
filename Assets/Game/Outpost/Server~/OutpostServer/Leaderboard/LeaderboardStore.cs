using Microsoft.Data.Sqlite;
using Outpost.Server.Protocol;

namespace Outpost.Server.Leaderboard;

/// <summary>
/// SQLite 持久化的排行榜（进程内 dev server 的内存榜的生产化替身）：每玩家一条、只留最好成绩，按分数降序。
/// 单表 <c>leaderboard(player PK, score, wave, kills)</c>；连接串来自配置（默认 <c>outpost.db</c> 文件，Docker 里挂卷持久）。
/// </summary>
/// <remarks>
/// 并发：SQLite 默认串行写；本类每次操作开新连接（连接池由 Microsoft.Data.Sqlite 复用），
/// 提交成绩用单条 upsert + 事务保证「取旧值 → 决定是否刷新 → 算名次」的原子性，无需应用层锁。
/// 首次启动 seed 几条"驻军"成绩让空榜有人气（与客户端 dev server 的预置一致）。
/// </remarks>
public sealed class LeaderboardStore
{
    private readonly string _connectionString;

    public LeaderboardStore(string connectionString)
    {
        _connectionString = connectionString;
        Initialize();
    }

    private void Initialize()
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS leaderboard (
                    player TEXT PRIMARY KEY,
                    score  INTEGER NOT NULL,
                    wave   INTEGER NOT NULL,
                    kills  INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_leaderboard_score ON leaderboard(score DESC);
                """;
            cmd.ExecuteNonQuery();
        }

        // 空榜 seed：与客户端进程内 dev server 的驻军成绩一致，给玩家一个追赶目标。
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM leaderboard";
            if (Convert.ToInt64(check.ExecuteScalar()) == 0)
            {
                var seed = new[]
                {
                    new LeaderboardEntry("Vanguard", 32000, 96, 21400),
                    new LeaderboardEntry("Bastion", 18500, 74, 12800),
                    new LeaderboardEntry("Aegis", 9400, 52, 6900),
                    new LeaderboardEntry("Sentry", 4200, 33, 3100),
                    new LeaderboardEntry("Nova", 1500, 18, 1200),
                };
                foreach (var e in seed)
                {
                    using var ins = conn.CreateCommand();
                    ins.CommandText = "INSERT INTO leaderboard(player, score, wave, kills) VALUES ($p, $s, $w, $k)";
                    Bind(ins, e);
                    ins.ExecuteNonQuery();
                }
            }
        }
    }

    /// <summary>
    /// 提交一局成绩：只并入更好成绩（每玩家最好一条），返回 (名次, 是否刷新全服纪录)。
    /// 全程一个事务：读旧的全服最高 → upsert → 算名次，避免并发提交时名次错乱 / 纪录判定竞态。
    /// </summary>
    public (int Rank, bool NewTop) Submit(SubmitScoreRequest req)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        int previousTop = ScalarInt(conn, tx, "SELECT COALESCE(MAX(score), 0) FROM leaderboard");

        // upsert：新玩家插入；老玩家仅当新分更高才覆盖（保留最好成绩的那一局的 wave/kills）。
        using (var up = conn.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = """
                INSERT INTO leaderboard(player, score, wave, kills)
                VALUES ($p, $s, $w, $k)
                ON CONFLICT(player) DO UPDATE SET
                    score = excluded.score, wave = excluded.wave, kills = excluded.kills
                WHERE excluded.score > leaderboard.score;
                """;
            Bind(up, new LeaderboardEntry(req.Player, req.Score, req.Wave, req.Kills));
            up.ExecuteNonQuery();
        }

        // 名次 = 严格高于「本玩家当前最好分」的人数 + 1（与降序榜的显示自洽）。
        int myBest = ScalarInt(conn, tx, "SELECT score FROM leaderboard WHERE player = $p", ("$p", req.Player));
        int betterCount = ScalarInt(conn, tx, "SELECT COUNT(*) FROM leaderboard WHERE score > $s", ("$s", myBest));
        int rank = betterCount + 1;

        bool newTop = myBest > previousTop; // 本次提交把全服最高刷新了

        tx.Commit();
        return (rank, newTop);
    }

    /// <summary>取分数降序的前 <paramref name="count"/> 条（1..100）。</summary>
    public List<LeaderboardEntry> Top(int count)
    {
        count = Math.Clamp(count, 1, 100);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT player, score, wave, kills FROM leaderboard ORDER BY score DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", count);

        var list = new List<LeaderboardEntry>(count);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new LeaderboardEntry(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        return list;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void Bind(SqliteCommand cmd, LeaderboardEntry e)
    {
        cmd.Parameters.AddWithValue("$p", e.Player);
        cmd.Parameters.AddWithValue("$s", e.Score);
        cmd.Parameters.AddWithValue("$w", e.Wave);
        cmd.Parameters.AddWithValue("$k", e.Kills);
    }

    private static int ScalarInt(SqliteConnection conn, SqliteTransaction tx, string sql, params (string name, object value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        object? result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }
}
