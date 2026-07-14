using DownKyi.Core.Logging;
using Microsoft.Data.Sqlite;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Core.Storage.Database;

/// <summary>
/// SQLite 数据库操作助手，支持加密数据库操作
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly string _connectionString;

    /// <summary>
    /// 创建或连接一个SQLite数据库
    /// </summary>
    /// <param name="dbPath">数据库文件路径</param>
    public SqliteDatabase(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            Mode = SqliteOpenMode.ReadWriteCreate,
            DataSource = dbPath,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }

    /// <summary>
    /// 创建或连接一个加密的SQLite数据库
    /// </summary>
    /// <param name="dbPath">数据库文件路径</param>
    /// <param name="secretKey">加密密钥</param>
    public SqliteDatabase(string dbPath, string secretKey)
    {
        var legacySqlCipherUri = new Uri(Path.GetFullPath(dbPath)).AbsoluteUri
            + "?cipher=sqlcipher&legacy=4";
        _connectionString = new SqliteConnectionStringBuilder
        {
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = secretKey,
            DataSource = legacySqlCipherUri,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }


    /// <summary>
    /// 执行查询SQL语句
    /// </summary>
    /// <param name="configureCommand">建立固定查询并绑定参数的委托</param>
    /// <param name="readAction">读取数据的委托</param>
    public void ExecuteQuery(Action<SqliteCommand> configureCommand, Action<SqliteDataReader> readAction)
    {
        ArgumentNullException.ThrowIfNull(configureCommand);
        ArgumentNullException.ThrowIfNull(readAction);

        try
        {
            using var connection = CreateConnection();
            using var command = connection.CreateCommand();

            configureCommand(command);
            if (string.IsNullOrWhiteSpace(command.CommandText))
            {
                throw new InvalidOperationException("SQLite query command text must be configured.");
            }

            using var reader = command.ExecuteReader();

            readAction(reader);
        }
        catch (SqliteException ex)
        {
            Console.PrintLine("ExecuteQuery() 发生异常: {0}", ex);
            LogManager.Error("SqliteDatabase.ExecuteQuery()", ex);
            throw;
        }
    }

    /// <summary>
    /// 创建并打开一个新的数据库连接
    /// </summary>
    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ConfigureConnection(connection);
        return connection;
    }

    private static void ConfigureConnection(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
    }
}
