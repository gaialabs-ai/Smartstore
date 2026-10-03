using System;
using System.Linq;
using System.Threading;
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
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Core.Security;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Scheduling;
using Smartstore.Test.Common;
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

[TestFixture]
public class TargetGroupEvaluatorCacheTests : ServiceTestBase
{
    private SqliteConnection _sqliteConnection;
    private SmartDbContext _sqliteDbContext;

    protected new SmartDbContext DbContext => _sqliteDbContext;

    private Mock<ICacheManager> _cacheMock;
    private Mock<IRuleService> _ruleServiceMock;
    private Mock<IRuleProviderFactory> _ruleProviderFactoryMock;
    private Mock<ITargetGroupService> _targetGroupServiceMock;
    private Mock<ITaskStore> _taskStoreMock;
    private TargetGroupEvaluatorTask _task;

    [OneTimeSetUp]
    public void Setup()
    {
        _sqliteConnection = new SqliteConnection("DataSource=:memory:");
        _sqliteConnection.Open();

        var sqliteFactory = new TestSqliteDbFactory(_sqliteConnection);

        var builder = new DbContextOptionsBuilder<SmartDbContext>()
            .UseDbFactory(sqliteFactory, "DataSource=:memory:", factoryBuilder =>
            {
                factoryBuilder.AddModelAssemblies(new[] { typeof(SmartDbContext).Assembly });
            });

        _sqliteDbContext = new SmartDbContext((DbContextOptions<SmartDbContext>)builder.Options);
        _sqliteDbContext.Database.EnsureCreated();

        _cacheMock = new Mock<ICacheManager>();
        _ruleServiceMock = new Mock<IRuleService>();
        _targetGroupServiceMock = new Mock<ITargetGroupService>();
        _ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();
        _taskStoreMock = new Mock<ITaskStore>();

        _ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, null))
            .Returns(_targetGroupServiceMock.Object);

        _task = new TargetGroupEvaluatorTask(
            DbContext,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);
    }

    [OneTimeTearDown]
    public void SqliteTearDown()
    {
        _sqliteDbContext?.Dispose();
        _sqliteConnection?.Close();
        _sqliteConnection?.Dispose();
    }

    [SetUp]
    public void TestSetUp()
    {
        DbContext.ChangeTracker.Clear();

        var mappings = DbContext.CustomerRoleMappings.ToList();
        if (mappings.Count > 0)
        {
            DbContext.CustomerRoleMappings.RemoveRange(mappings);
        }

        var roles = DbContext.CustomerRoles.Include(x => x.RuleSets).ToList();
        foreach (var r in roles)
        {
            r.RuleSets.Clear();
        }
        if (roles.Count > 0)
        {
            DbContext.CustomerRoles.RemoveRange(roles);
        }

        var ruleSets = DbContext.RuleSets.ToList();
        if (ruleSets.Count > 0)
        {
            DbContext.RuleSets.RemoveRange(ruleSets);
        }

        var customers = DbContext.Customers.IgnoreQueryFilters().ToList();
        if (customers.Count > 0)
        {
            DbContext.Customers.RemoveRange(customers);
        }

        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        _cacheMock.Reset();
        _ruleServiceMock.Reset();
        _targetGroupServiceMock.Reset();
    }

    private TaskExecutionContext CreateContext()
    {
        return new TaskExecutionContext(
            _taskStoreMock.Object,
            Mock.Of<IAsyncState>(),
            Mock.Of<HttpContext>(),
            Mock.Of<IComponentContext>(),
            new TaskExecutionInfo
            {
                Task = new TaskDescriptor
                {
                    Name = "TargetGroupEvaluator",
                    Type = "TargetGroupEvaluatorTask"
                }
            },
            null);
    }

    [Test]
    public async Task Run_WhenMappingsChanged_ClearsAclCachePattern()
    {
        var role = new CustomerRole { Active = false, Name = "TestRole", SystemName = "TR" };
        DbContext.CustomerRoles.Add(role);

        var customer = new Customer
        {
            CustomerGuid = Guid.NewGuid(),
            Active = true,
            CreatedOnUtc = DateTime.UtcNow
        };
        DbContext.Customers.Add(customer);
        DbContext.SaveChanges();

        DbContext.CustomerRoleMappings.Add(
            new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = role.Id, IsSystemMapping = true });
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        var ctx = CreateContext();
        await _task.Run(ctx, CancellationToken.None);

        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN),
            Times.Once(),
            "ACL cache should be cleared when system mappings are deleted.");
    }

    [Test]
    public async Task Run_WhenMappingsAdded_ClearsAclCachePattern()
    {
        var ruleSet = new RuleSetEntity
        {
            Name = "RS",
            IsActive = true,
            Scope = RuleScope.Customer,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };
        var role = new CustomerRole { Active = true, Name = "TestRole", SystemName = "TR" };
        role.RuleSets.Add(ruleSet);
        DbContext.CustomerRoles.Add(role);

        var customer = new Customer
        {
            CustomerGuid = Guid.NewGuid(),
            Active = true,
            CreatedOnUtc = DateTime.UtcNow
        };
        DbContext.Customers.Add(customer);
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        var expression = new FilterExpressionGroup(typeof(Customer))
        {
            LogicalOperator = LogicalRuleOperator.And
        };

        _ruleServiceMock.Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync(expression);

        var targetIds = new[] { customer.Id };
        var customerQuery = DbContext.Customers.Where(x => targetIds.Contains(x.Id));
        _targetGroupServiceMock.Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(customerQuery.ToPagedList(0, 500));

        var ctx = CreateContext();
        await _task.Run(ctx, CancellationToken.None);

        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN),
            Times.Once(),
            "ACL cache should be cleared when new mappings are added (numAdded > 0).");
    }

    [Test]
    public async Task Run_WhenNoChanges_DoesNotClearCache()
    {
        var ctx = CreateContext();
        await _task.Run(ctx, CancellationToken.None);

        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never(),
            "ACL cache should NOT be cleared when no mappings changed.");
    }
}
