using System;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Test.Common;

namespace Smartstore.Core.Tests;

/// <summary>
/// A test-only <see cref="DbFactory"/> that configures EF Core to use a pre-opened
/// SQLite in-memory connection. Unlike the InMemory provider, SQLite supports
/// <c>ExecuteDeleteAsync</c> and other relational operations.
/// </summary>
public class TestSqliteDbFactory : DbFactory
{
    private readonly SqliteConnection _connection;

    public TestSqliteDbFactory(SqliteConnection connection)
    {
        _connection = connection;
    }

    public override DbSystemType DbSystem => DbSystemType.SQLite;

    public override DbConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
        => new SqliteConnectionStringBuilder(connectionString);

    public override DbConnectionStringBuilder CreateConnectionStringBuilder(
        string server, string database, string userName, string password)
        => throw new NotImplementedException();

    public override DataProvider CreateDataProvider(DatabaseFacade database)
        => new TestDataProvider(database);

    public override TContext CreateDbContext<TContext>(string connectionString, int? commandTimeout = null)
        => throw new NotImplementedException();

    public override DbContextOptionsBuilder ConfigureDbContext(DbContextOptionsBuilder builder, string connectionString)
    {
        return builder.UseSqlite(_connection);
    }
}
