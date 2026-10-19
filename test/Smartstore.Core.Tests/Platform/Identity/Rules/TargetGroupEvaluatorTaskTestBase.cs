using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Smartstore.Caching;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Scheduling;
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

/// <summary>
/// Shared SQLite-backed <see cref="SmartDbContext"/>/<see cref="ICacheManager"/> test infrastructure
/// for the <see cref="TargetGroupEvaluatorTaskTests"/> (mocked rule engine) and
/// <see cref="TargetGroupEvaluatorTaskFunctionalTests"/> (real rule engine) sibling fixtures, which
/// otherwise duplicated this setup/teardown and helpers identically.
/// </summary>
public abstract class TargetGroupEvaluatorTaskTestBase : ServiceTestBase
{
    protected SqliteConnection _connection;
    protected SmartDbContext _db;
    protected Mock<ICacheManager> _cacheMock;

    [SetUp]
    public void SetUpSqliteContext()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        DataSettings.Instance = new DataSettings
        {
            AppVersion = SmartstoreVersion.Version,
            ConnectionString = "DataSource=:memory:",
            TenantName = "Default",
            TenantRoot = null,
            DbFactory = new TestSqliteDbFactory(_connection)
        };
        DataSettings.SetTestMode(true);

        var optionsBuilder = new DbContextOptionsBuilder<SmartDbContext>()
            .UseDbFactory(factoryBuilder =>
            {
                factoryBuilder.AddModelAssemblies(new[] { typeof(SmartDbContext).Assembly });
            });

        _db = new SmartDbContext((DbContextOptions<SmartDbContext>)optionsBuilder.Options);
        _db.Database.EnsureCreated();

        _cacheMock = new Mock<ICacheManager>();
        _cacheMock.Setup(x => x.RemoveByPatternAsync(It.IsAny<string>())).ReturnsAsync(0L);
    }

    [TearDown]
    public void TearDownSqliteContext()
    {
        _db?.Dispose();
        _connection?.Dispose();
    }

    protected static TaskExecutionContext CreateContext(IDictionary<string, string> parameters = null)
    {
        var taskStoreMock = new Mock<ITaskStore>();
        taskStoreMock.Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>())).Returns(Task.CompletedTask);

        var asyncStateMock = new Mock<IAsyncState>();
        asyncStateMock.Setup(x => x.GetAsync<TaskDescriptor>(It.IsAny<string>())).ReturnsAsync((TaskDescriptor)null);

        var componentContextMock = new Mock<IComponentContext>();

        var executionInfo = new TaskExecutionInfo
        {
            Id = 1,
            TaskDescriptorId = 1,
            Task = new TaskDescriptor { Id = 1, Name = nameof(TargetGroupEvaluatorTask) }
        };

        return new TaskExecutionContext(
            taskStoreMock.Object,
            asyncStateMock.Object,
            new DefaultHttpContext(),
            componentContextMock.Object,
            executionInfo,
            parameters);
    }

    protected CustomerRole AddRole(string name, bool active)
    {
        var role = new CustomerRole { Name = name, Active = active, SystemName = name };
        _db.CustomerRoles.Add(role);
        return role;
    }
}
