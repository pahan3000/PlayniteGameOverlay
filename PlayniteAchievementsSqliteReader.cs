using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Playnite.SDK;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Reads achievement data from PlayniteAchievements' SQLite cache (achievement_cache.db)
    /// using Windows' built-in winsqlite3.dll (Win10+) with optional sqlite3.dll fallback.
    /// Schema matches justin-delano / Santodan PlayniteAchievements SqlNadoCacheStore.
    /// </summary>
    internal static class PlayniteAchievementsSqliteReader
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;
        private const int SQLITE_OPEN_READONLY = 1;
        private const int SQLITE_OPEN_URI = 0x00000040;
        private const int SQLITE_OPEN_NOMUTEX = 0x00008000;
        private const int SQLITE_NULL = 5;

        // SQLITE_TRANSIENT: sqlite makes its own copy of bound text
        private static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);

        private const string WinSqlite = "winsqlite3";
        private const string Sqlite3 = "sqlite3";

        private static bool _useWinSqlite = true;

        #region P/Invoke (winsqlite3)

        [DllImport(WinSqlite, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int win_open_v2(byte[] filename, out IntPtr ppDb, int flags, IntPtr zVfs);

        [DllImport(WinSqlite, EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern int win_close(IntPtr db);

        [DllImport(WinSqlite, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int win_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

        [DllImport(WinSqlite, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
        private static extern int win_step(IntPtr stmt);

        [DllImport(WinSqlite, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
        private static extern int win_finalize(IntPtr stmt);

        [DllImport(WinSqlite, EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl)]
        private static extern int win_bind_text(IntPtr stmt, int index, byte[] value, int nData, IntPtr destructor);

        [DllImport(WinSqlite, EntryPoint = "sqlite3_bind_int64", CallingConvention = CallingConvention.Cdecl)]
        private static extern int win_bind_int64(IntPtr stmt, int index, long value);

        [DllImport(WinSqlite, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr win_column_text(IntPtr stmt, int iCol);

        [DllImport(WinSqlite, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
        private static extern long win_column_int64(IntPtr stmt, int iCol);

        [DllImport(WinSqlite, EntryPoint = "sqlite3_column_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern int win_column_int(IntPtr stmt, int iCol);

        [DllImport(WinSqlite, EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)]
        private static extern int win_column_type(IntPtr stmt, int iCol);

        [DllImport(WinSqlite, EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr win_errmsg(IntPtr db);

        #endregion

        #region P/Invoke (sqlite3 fallback)

        [DllImport(Sqlite3, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sq3_open_v2(byte[] filename, out IntPtr ppDb, int flags, IntPtr zVfs);

        [DllImport(Sqlite3, EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sq3_close(IntPtr db);

        [DllImport(Sqlite3, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sq3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

        [DllImport(Sqlite3, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sq3_step(IntPtr stmt);

        [DllImport(Sqlite3, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sq3_finalize(IntPtr stmt);

        [DllImport(Sqlite3, EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sq3_bind_text(IntPtr stmt, int index, byte[] value, int nData, IntPtr destructor);

        [DllImport(Sqlite3, EntryPoint = "sqlite3_bind_int64", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sq3_bind_int64(IntPtr stmt, int index, long value);

        [DllImport(Sqlite3, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sq3_column_text(IntPtr stmt, int iCol);

        [DllImport(Sqlite3, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sq3_column_int64(IntPtr stmt, int iCol);

        [DllImport(Sqlite3, EntryPoint = "sqlite3_column_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sq3_column_int(IntPtr stmt, int iCol);

        [DllImport(Sqlite3, EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sq3_column_type(IntPtr stmt, int iCol);

        [DllImport(Sqlite3, EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sq3_errmsg(IntPtr db);

        #endregion

        private static byte[] Utf8Z(string s)
        {
            if (s == null) return new byte[] { 0 };
            var bytes = Encoding.UTF8.GetBytes(s);
            var withNull = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, withNull, 0, bytes.Length);
            return withNull;
        }

        private static int Open(string path, out IntPtr db)
        {
            int flags = SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX | SQLITE_OPEN_URI;
            var pathBytes = Utf8Z(path);
            if (_useWinSqlite)
            {
                try { return win_open_v2(pathBytes, out db, flags, IntPtr.Zero); }
                catch (DllNotFoundException) { _useWinSqlite = false; }
                catch (EntryPointNotFoundException) { _useWinSqlite = false; }
            }
            return sq3_open_v2(pathBytes, out db, flags, IntPtr.Zero);
        }

        private static int OpenPlain(string path, out IntPtr db)
        {
            int flags = SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX;
            var pathBytes = Utf8Z(path);
            return _useWinSqlite
                ? win_open_v2(pathBytes, out db, flags, IntPtr.Zero)
                : sq3_open_v2(pathBytes, out db, flags, IntPtr.Zero);
        }

        private static int Close(IntPtr db) =>
            _useWinSqlite ? win_close(db) : sq3_close(db);

        private static int Prepare(IntPtr db, string sql, out IntPtr stmt)
        {
            var sqlBytes = Utf8Z(sql);
            return _useWinSqlite
                ? win_prepare_v2(db, sqlBytes, -1, out stmt, IntPtr.Zero)
                : sq3_prepare_v2(db, sqlBytes, -1, out stmt, IntPtr.Zero);
        }

        private static int Step(IntPtr stmt) =>
            _useWinSqlite ? win_step(stmt) : sq3_step(stmt);

        private static int Finalize(IntPtr stmt) =>
            _useWinSqlite ? win_finalize(stmt) : sq3_finalize(stmt);

        private static int BindText(IntPtr stmt, int index, string value)
        {
            if (value == null)
            {
                // bind null text
                return _useWinSqlite
                    ? win_bind_text(stmt, index, null, 0, IntPtr.Zero)
                    : sq3_bind_text(stmt, index, null, 0, IntPtr.Zero);
            }
            var bytes = Encoding.UTF8.GetBytes(value);
            return _useWinSqlite
                ? win_bind_text(stmt, index, bytes, bytes.Length, SQLITE_TRANSIENT)
                : sq3_bind_text(stmt, index, bytes, bytes.Length, SQLITE_TRANSIENT);
        }

        private static int BindInt64(IntPtr stmt, int index, long value) =>
            _useWinSqlite ? win_bind_int64(stmt, index, value) : sq3_bind_int64(stmt, index, value);

        private static IntPtr ColumnTextPtr(IntPtr stmt, int col) =>
            _useWinSqlite ? win_column_text(stmt, col) : sq3_column_text(stmt, col);

        private static long ColumnInt64(IntPtr stmt, int col) =>
            _useWinSqlite ? win_column_int64(stmt, col) : sq3_column_int64(stmt, col);

        private static int ColumnInt(IntPtr stmt, int col) =>
            _useWinSqlite ? win_column_int(stmt, col) : sq3_column_int(stmt, col);

        private static int ColumnType(IntPtr stmt, int col) =>
            _useWinSqlite ? win_column_type(stmt, col) : sq3_column_type(stmt, col);

        private static string Errmsg(IntPtr db)
        {
            if (db == IntPtr.Zero) return "null db";
            IntPtr p = _useWinSqlite ? win_errmsg(db) : sq3_errmsg(db);
            return PtrToUtf8(p) ?? "unknown";
        }

        private static string PtrToUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            var bytes = new List<byte>(64);
            int i = 0;
            while (true)
            {
                byte b = Marshal.ReadByte(ptr, i);
                if (b == 0) break;
                bytes.Add(b);
                i++;
                if (i > 1024 * 1024) break; // safety
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static string ColumnText(IntPtr stmt, int col)
        {
            if (ColumnType(stmt, col) == SQLITE_NULL) return null;
            return PtrToUtf8(ColumnTextPtr(stmt, col));
        }

        /// <summary>
        /// Best-effort read of a rarity/percent column (numeric or text).
        /// </summary>
        private static double? TryReadPercentColumn(IntPtr stmt, int col)
        {
            try
            {
                if (ColumnType(stmt, col) == SQLITE_NULL)
                    return null;
                // Prefer text parse so we do not need sqlite3_column_double P/Invoke
                string text = ColumnText(stmt, col);
                if (!string.IsNullOrWhiteSpace(text) &&
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double fromText))
                {
                    if (fromText >= 0 && fromText <= 100)
                        return fromText;
                }
                // Fallback: integer column
                long asInt = ColumnInt64(stmt, col);
                if (asInt >= 0 && asInt <= 100)
                    return asInt;
            }
            catch
            {
                // column missing / type mismatch — ignore
            }
            return null;
        }

        /// <summary>
        /// Load achievements for a Playnite game from achievement_cache.db.
        /// </summary>
        private static readonly object NativeGate = new object();

        public static List<AchievementData> LoadAchievements(
            string databasePath,
            string playniteGameId,
            string pluginDataRoot)
        {
            // One native SQLite read at a time (game-start preload and overlay-open refresh can overlap).
            lock (NativeGate)
                return LoadAchievementsCore(databasePath, playniteGameId, pluginDataRoot);
        }

        private static List<AchievementData> LoadAchievementsCore(
            string databasePath,
            string playniteGameId,
            string pluginDataRoot)
        {
            var result = new List<AchievementData>();
            if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
                return result;
            if (string.IsNullOrWhiteSpace(playniteGameId))
                return result;

            IntPtr db = IntPtr.Zero;
            try
            {
                // URI + immutable helps when PA has the DB open with a WAL writer
                string uriPath = "file:" + databasePath.Replace('\\', '/') + "?mode=ro";
                int rc = Open(uriPath, out db);
                if (rc != SQLITE_OK)
                {
                    if (db != IntPtr.Zero) { Close(db); db = IntPtr.Zero; }
                    rc = OpenPlain(databasePath, out db);
                }

                if (rc != SQLITE_OK || db == IntPtr.Zero)
                {
                    Logger.Error($"Failed to open PlayniteAchievements DB '{databasePath}': {Errmsg(db)} (code {rc})");
                    return result;
                }

                long userGameProgressId = 0;
                long gameId = 0;

                const string progressSql =
                    @"SELECT ugp.Id, ugp.GameId
                      FROM UserGameProgress ugp
                      INNER JOIN Users u ON u.Id = ugp.UserId
                      INNER JOIN Games g ON g.Id = ugp.GameId
                      WHERE u.IsCurrentUser = 1
                        AND (
                              lower(ifnull(g.PlayniteGameId,'')) = lower(?)
                           OR lower(ifnull(ugp.CacheKey,'')) = lower(?)
                        )
                      ORDER BY ugp.LastUpdatedUtc DESC
                      LIMIT 1;";

                IntPtr stmt = IntPtr.Zero;
                rc = Prepare(db, progressSql, out stmt);
                if (rc != SQLITE_OK)
                {
                    Logger.Error($"prepare progress failed: {Errmsg(db)}");
                    return result;
                }

                try
                {
                    BindText(stmt, 1, playniteGameId);
                    BindText(stmt, 2, playniteGameId);
                    rc = Step(stmt);
                    if (rc == SQLITE_ROW)
                    {
                        userGameProgressId = ColumnInt64(stmt, 0);
                        gameId = ColumnInt64(stmt, 1);
                    }
                    else if (rc != SQLITE_DONE)
                    {
                        Logger.Error($"step progress failed: {Errmsg(db)} (code {rc})");
                        return result;
                    }
                }
                finally
                {
                    Finalize(stmt);
                    stmt = IntPtr.Zero;
                }

                if (gameId == 0)
                {
                    Logger.Debug($"No PlayniteAchievements progress row for game {playniteGameId}");
                    return result;
                }

                // PlayniteAchievements persists rarity as GlobalPercentUnlocked
                // (AchievementDetail.GlobalPercentUnlocked). Older forks may use other names.
                // Try rarity column names one-by-one — a multi-column SELECT fails if ANY is missing.
                string[] rarityColumnCandidates = new[]
                {
                    "ad.GlobalPercentUnlocked",
                    "ad.GlobalUnlockPercent",
                    "ad.Percent",
                    "ad.RarityPercent"
                };

                const string detailSqlBase =
                    @"SELECT
                        ad.ApiName,
                        ad.DisplayName,
                        ad.Description,
                        ad.UnlockedIconPath,
                        ad.LockedIconPath,
                        ua.Unlocked,
                        ua.UnlockTimeUtc
                      {0}
                      FROM AchievementDefinitions ad
                      LEFT JOIN UserAchievements ua
                        ON ua.AchievementDefinitionId = ad.Id
                       AND ua.UserGameProgressId = ?
                      WHERE ad.GameId = ?
                      ORDER BY ad.Id;";

                bool hasRarityColumns = false;
                stmt = IntPtr.Zero;
                foreach (var rarityCol in rarityColumnCandidates)
                {
                    string sql = string.Format(detailSqlBase, ", " + rarityCol);
                    rc = Prepare(db, sql, out stmt);
                    if (rc == SQLITE_OK)
                    {
                        hasRarityColumns = true;
                        Logger.Debug($"PlayniteAchievements SQLite rarity column: {rarityCol}");
                        break;
                    }
                    if (stmt != IntPtr.Zero) { Finalize(stmt); stmt = IntPtr.Zero; }
                }

                if (!hasRarityColumns)
                {
                    string sql = string.Format(detailSqlBase, "");
                    rc = Prepare(db, sql, out stmt);
                    if (rc != SQLITE_OK)
                    {
                        Logger.Error($"prepare details failed: {Errmsg(db)}");
                        return result;
                    }
                }

                try
                {
                    BindInt64(stmt, 1, userGameProgressId);
                    BindInt64(stmt, 2, gameId);

                    while ((rc = Step(stmt)) == SQLITE_ROW)
                    {
                        string apiName = ColumnText(stmt, 0);
                        if (string.IsNullOrWhiteSpace(apiName))
                            continue;

                        string displayName = ColumnText(stmt, 1);
                        string description = ColumnText(stmt, 2);
                        string unlockedIcon = ColumnText(stmt, 3);
                        string lockedIcon = ColumnText(stmt, 4);

                        int unlockedInt = 0;
                        if (ColumnType(stmt, 5) != SQLITE_NULL)
                            unlockedInt = ColumnInt(stmt, 5);

                        string unlockTimeStr = ColumnText(stmt, 6);
                        DateTime? unlockDate = ParseUtcToLocal(unlockTimeStr);
                        bool isUnlocked = unlockedInt != 0 || unlockDate.HasValue;

                        string icon = isUnlocked
                            ? ResolveIconPath(unlockedIcon, pluginDataRoot)
                            : ResolveIconPath(lockedIcon ?? unlockedIcon, pluginDataRoot);

                        double? globalPercent = null;
                        if (hasRarityColumns)
                        {
                            // Single optional rarity column at index 7
                            globalPercent = TryReadPercentColumn(stmt, 7);
                        }

                        result.Add(new AchievementData
                        {
                            Name = !string.IsNullOrWhiteSpace(displayName) ? displayName : apiName,
                            Description = description,
                            IsUnlocked = isUnlocked,
                            UnlockDate = unlockDate,
                            IconUrl = icon,
                            GlobalPercent = globalPercent
                        });
                    }

                    if (rc != SQLITE_DONE && rc != SQLITE_ROW)
                    {
                        Logger.Error($"step details failed: {Errmsg(db)} (code {rc})");
                    }
                }
                finally
                {
                    Finalize(stmt);
                }

                Logger.Info($"Loaded {result.Count} achievements from PlayniteAchievements SQLite for {playniteGameId}");
            }
            catch (DllNotFoundException ex)
            {
                Logger.Error($"SQLite native library not found (winsqlite3/sqlite3): {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed reading PlayniteAchievements SQLite cache");
            }
            finally
            {
                if (db != IntPtr.Zero)
                {
                    try { Close(db); } catch { /* ignore */ }
                }
            }

            return result;
        }

        private static DateTime? ParseUtcToLocal(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (!DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var utc))
            {
                if (!DateTime.TryParse(value, out utc))
                    return null;
            }

            if (utc == DateTime.MinValue)
                return null;

            if (utc.Kind == DateTimeKind.Unspecified)
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

            return utc.ToLocalTime();
        }

        private static string ResolveIconPath(string path, string pluginDataRoot)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                Path.IsPathRooted(path))
            {
                return path;
            }

            if (string.IsNullOrWhiteSpace(pluginDataRoot))
                return path;

            // Was Path.GetFullPath + File.Exists for every achievement row (hundreds of file-system
            // calls and path-normalisation buffers during game start). Just combine: a missing icon
            // simply fails to load in the UI, exactly as the unresolved relative path used to.
            try
            {
                return Path.Combine(pluginDataRoot, path);
            }
            catch { /* ignore */ }

            return path;
        }
    }
}
