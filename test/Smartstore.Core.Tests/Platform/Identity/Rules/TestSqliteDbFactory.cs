using System;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Test.Common;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

/// <summary>
/// A SQLite (in-memory) backed <see cref="DbFactory"/> used only by <see cref="TargetGroupEvaluatorTaskTests"/>.
/// The project-wide <see cref="TestDbFactory"/> is hardwired to Microsoft.EntityFrameworkCore.InMemory, which does
/// NOT support EF Core's ExecuteDelete(Async)/ExecuteUpdate(Async) bulk operations (throws
/// <see cref="InvalidOperationException"/> regardless of row count - a provider capability limitation, not a data
/// issue). <see cref="Identity.Rules.TargetGroupEvaluatorTask"/> relies on ExecuteDeleteAsync as its very first
/// database operation, so it cannot run end-to-end against the shared InMemory-backed context.
/// SQLite is a real relational EF Core provider - and one of Smartstore's own officially supported production
/// database providers (see Smartstore.Data.Sqlite) - so it supports ExecuteDeleteAsync and lets the task run
/// end-to-end here, including its bulk-delete statement.
/// </summary>
internal sealed class TestSqliteDbFactory : DbFactory
{
    private readonly DbConnection _connection;

    public TestSqliteDbFactory(DbConnection connection)
    {
        _connection = connection;
    }

    public override DbSystemType DbSystem { get; } = DbSystemType.Unknown;

    public override DbConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
        => throw new NotImplementedException();

    public override DbConnectionStringBuilder CreateConnectionStringBuilder(
        string server,
        string database,
        string userName,
        string password)
        => throw new NotImplementedException();

    public override DataProvider CreateDataProvider(DatabaseFacade database)
        => new TestDataProvider(database);

    public override TContext CreateDbContext<TContext>(string connectionString, int? commandTimeout = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TContext>().UseSqlite(_connection);
        return (TContext)Activator.CreateInstance(typeof(TContext), new object[] { optionsBuilder.Options });
    }

    public override DbContextOptionsBuilder ConfigureDbContext(DbContextOptionsBuilder builder, string connectionString)
        => builder.UseSqlite(_connection);
}
