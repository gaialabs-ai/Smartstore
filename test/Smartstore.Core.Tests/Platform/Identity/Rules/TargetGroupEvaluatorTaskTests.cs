using System;
using System.Collections.Generic;
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
public class TargetGroupEvaluatorTaskTests : ServiceTestBase
{
    private SqliteConnection _sqliteConnection;
    private SmartDbContext _sqliteDbContext;

    // Shadow the base class DbContext to use SQLite instead of the InMemory provider.
    // SQLite supports ExecuteDeleteAsync (a relational-only operation) that the task calls.
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
        // Create a SQLite in-memory connection and keep it open for the fixture lifetime.
        // SQLite supports ExecuteDeleteAsync (unlike the EF Core InMemory provider).
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

        // GetProvider<T> is an extension method that calls the interface method
        // GetProvider(RuleScope, object?) and casts the result.
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

        // Remove customer role mappings.
        var mappings = DbContext.CustomerRoleMappings.ToList();
        if (mappings.Count > 0)
        {
            DbContext.CustomerRoleMappings.RemoveRange(mappings);
        }

        // Clear many-to-many associations before removing roles.
        var roles = DbContext.CustomerRoles.Include(x => x.RuleSets).ToList();
        foreach (var r in roles)
        {
            r.RuleSets.Clear();
        }
        if (roles.Count > 0)
        {
            DbContext.CustomerRoles.RemoveRange(roles);
        }

        // Remove rule sets.
        var ruleSets = DbContext.RuleSets.ToList();
        if (ruleSets.Count > 0)
        {
            DbContext.RuleSets.RemoveRange(ruleSets);
        }

        // Remove customers including soft-deleted ones.
        var customers = DbContext.Customers.IgnoreQueryFilters().ToList();
        if (customers.Count > 0)
        {
            DbContext.Customers.RemoveRange(customers);
        }

        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        // Reset all mock state for clean test isolation.
        _cacheMock.Reset();
        _ruleServiceMock.Reset();
        _targetGroupServiceMock.Reset();
    }

    private TaskExecutionContext CreateContext(IDictionary<string, string> parameters = null)
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
            parameters);
    }

    private static RuleSetEntity CreateRuleSet(string name, bool isActive = true)
    {
        return new RuleSetEntity
        {
            Name = name,
            IsActive = isActive,
            Scope = RuleScope.Customer,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };
    }

    private FilterExpressionGroup SetupRuleEvaluationMocks(IQueryable<Customer> customerQuery = null)
    {
        var expression = new FilterExpressionGroup(typeof(Customer))
        {
            LogicalOperator = LogicalRuleOperator.And
        };

        _ruleServiceMock.Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync(expression);

        var query = customerQuery ?? DbContext.Customers.Where(x => false);
        _targetGroupServiceMock.Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(query.ToPagedList(0, 500));

        return expression;
    }

    #region Task Execution Tests

    [Test]
    public async Task Run_DeletesExistingSystemMappingsBeforeReEvaluation()
    {
        // Seed a customer and roles.
        var customer = new Customer
        {
            CustomerGuid = Guid.NewGuid(),
            Active = true,
            CreatedOnUtc = DateTime.UtcNow
        };
        DbContext.Customers.Add(customer);

        var role = new CustomerRole { Active = false, Name = "TestRole", SystemName = "TestRole" };
        DbContext.CustomerRoles.Add(role);
        DbContext.SaveChanges();

        // Seed 2 system mappings and 1 non-system mapping.
        DbContext.CustomerRoleMappings.AddRange(
            new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = role.Id, IsSystemMapping = true },
            new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = role.Id, IsSystemMapping = true },
            new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = role.Id, IsSystemMapping = false }
        );
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        var ctx = CreateContext();
        await _task.Run(ctx, CancellationToken.None);
        DbContext.ChangeTracker.Clear();

        // System mappings should be deleted; non-system mapping should remain.
        var remaining = DbContext.CustomerRoleMappings.ToList();
        Assert.That(remaining.Count, Is.EqualTo(1));
        remaining[0].IsSystemMapping.ShouldBeFalse();
    }

    [Test]
    public async Task Run_WhenCustomerRoleIdsProvided_OnlyDeletesThoseRoleMappings()
    {
        // Seed 3 roles and a customer.
        var role1 = new CustomerRole { Active = false, Name = "Role1", SystemName = "R1" };
        var role2 = new CustomerRole { Active = false, Name = "Role2", SystemName = "R2" };
        var role3 = new CustomerRole { Active = false, Name = "Role3", SystemName = "R3" };
        DbContext.CustomerRoles.AddRange(role1, role2, role3);

        var customer = new Customer
        {
            CustomerGuid = Guid.NewGuid(),
            Active = true,
            CreatedOnUtc = DateTime.UtcNow
        };
        DbContext.Customers.Add(customer);
        DbContext.SaveChanges();

        // Seed system mappings for each role.
        DbContext.CustomerRoleMappings.AddRange(
            new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = role1.Id, IsSystemMapping = true },
            new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = role2.Id, IsSystemMapping = true },
            new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = role3.Id, IsSystemMapping = true }
        );
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        // Only delete mappings for role1 and role2.
        var parameters = new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = $"{role1.Id},{role2.Id}"
        };
        var ctx = CreateContext(parameters);
        await _task.Run(ctx, CancellationToken.None);
        DbContext.ChangeTracker.Clear();

        var remaining = DbContext.CustomerRoleMappings.ToList();
        Assert.That(remaining.Count, Is.EqualTo(1));
        remaining[0].CustomerRoleId.ShouldEqual(role3.Id);
    }

    [Test]
    public async Task Run_WhenNoParameter_ProcessesAllActiveRolesWithActiveRuleSets()
    {
        // Seed 2 active roles with active rule sets and 1 inactive role.
        var ruleSet1 = CreateRuleSet("RS1");
        var ruleSet2 = CreateRuleSet("RS2");

        var activeRole1 = new CustomerRole { Active = true, Name = "Active1", SystemName = "AR1" };
        activeRole1.RuleSets.Add(ruleSet1);

        var activeRole2 = new CustomerRole { Active = true, Name = "Active2", SystemName = "AR2" };
        activeRole2.RuleSets.Add(ruleSet2);

        var inactiveRole = new CustomerRole { Active = false, Name = "Inactive", SystemName = "IR" };
        DbContext.CustomerRoles.AddRange(activeRole1, activeRole2, inactiveRole);

        // Seed 2 customers.
        var customer1 = new Customer
        {
            CustomerGuid = Guid.NewGuid(),
            Active = true,
            CreatedOnUtc = DateTime.UtcNow
        };
        var customer2 = new Customer
        {
            CustomerGuid = Guid.NewGuid(),
            Active = true,
            CreatedOnUtc = DateTime.UtcNow
        };
        DbContext.Customers.AddRange(customer1, customer2);
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        // Mock rule evaluation to return both customers.
        var targetIds = new[] { customer1.Id, customer2.Id };
        var customerQuery = DbContext.Customers.Where(x => targetIds.Contains(x.Id));
        SetupRuleEvaluationMocks(customerQuery);

        var ctx = CreateContext();
        await _task.Run(ctx, CancellationToken.None);
        DbContext.ChangeTracker.Clear();

        // Should create system mappings for each customer-role combination.
        var systemMappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(systemMappings.Count, Is.EqualTo(4),
            "Expected 4 system mappings: 2 customers * 2 active roles.");

        // Verify both roles received mappings.
        var role1Mappings = systemMappings.Where(x => x.CustomerRoleId == activeRole1.Id).ToList();
        var role2Mappings = systemMappings.Where(x => x.CustomerRoleId == activeRole2.Id).ToList();
        Assert.That(role1Mappings.Count, Is.EqualTo(2));
        Assert.That(role2Mappings.Count, Is.EqualTo(2));

        // Verify inactive role has no mappings.
        var inactiveMappings = systemMappings.Where(x => x.CustomerRoleId == inactiveRole.Id).ToList();
        Assert.That(inactiveMappings.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task Run_RespectsCancellationToken()
    {
        // Seed an active role with 2 active rule sets so the inner loop iterates twice.
        var ruleSet1 = CreateRuleSet("RS1");
        var ruleSet2 = CreateRuleSet("RS2");
        var role = new CustomerRole { Active = true, Name = "TestRole", SystemName = "TR" };
        role.RuleSets.Add(ruleSet1);
        role.RuleSets.Add(ruleSet2);
        DbContext.CustomerRoles.Add(role);
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        using var cts = new CancellationTokenSource();
        var callCount = 0;

        // Set up default mocks (empty ProcessFilter results).
        var expression = SetupRuleEvaluationMocks();

        // Override CreateExpressionGroupAsync to cancel the token on the first call.
        _ruleServiceMock.Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    cts.Cancel();
                }
                return Task.FromResult<IRuleExpressionGroup>(expression);
            });

        var ctx = CreateContext();
        await _task.Run(ctx, cts.Token);

        // CreateExpressionGroupAsync was called once before cancellation was detected.
        Assert.That(callCount, Is.EqualTo(1),
            "Only the first rule set should have been processed before cancellation.");
    }

    #endregion

    #region Cache Invalidation Tests

    [Test]
    public async Task Run_WhenMappingsChanged_ClearsAclCachePattern()
    {
        // Seed a system mapping so that the delete step produces numDeleted > 0.
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

        // Cache should be cleared because numDeleted > 0.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN),
            Times.Once(),
            "ACL cache should be cleared when system mappings are deleted.");
    }

    [Test]
    public async Task Run_WhenNoChanges_DoesNotClearCache()
    {
        // No system mappings exist and no active roles with rule sets -> nothing changes.
        var ctx = CreateContext();
        await _task.Run(ctx, CancellationToken.None);

        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never(),
            "ACL cache should NOT be cleared when no mappings changed.");
    }

    #endregion

    #region Edge Case Tests

    [Test]
    public async Task Run_WhenRolesHaveNoActiveRuleSets_SkipsThem()
    {
        // Seed an active role with only an inactive rule set.
        var inactiveRuleSet = CreateRuleSet("InactiveRS", isActive: false);
        var role = new CustomerRole { Active = true, Name = "TestRole", SystemName = "TR" };
        role.RuleSets.Add(inactiveRuleSet);
        DbContext.CustomerRoles.Add(role);
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        var ctx = CreateContext();
        await _task.Run(ctx, CancellationToken.None);
        DbContext.ChangeTracker.Clear();

        // The role should be excluded from processing because it has no active rule sets.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()),
            Times.Never(),
            "CreateExpressionGroupAsync should not be called when no roles have active rule sets.");

        // No new system mappings should exist.
        var systemMappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(systemMappings.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task Run_WhenRuleSetProducesNoMatchingCustomers_NoInserts()
    {
        // Seed an active role with an active rule set.
        var ruleSet = CreateRuleSet("RS");
        var role = new CustomerRole { Active = true, Name = "TestRole", SystemName = "TR" };
        role.RuleSets.Add(ruleSet);
        DbContext.CustomerRoles.Add(role);
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        // Mock rule evaluation with empty results (no matching customers).
        SetupRuleEvaluationMocks();

        var ctx = CreateContext();
        await _task.Run(ctx, CancellationToken.None);
        DbContext.ChangeTracker.Clear();

        // No system mappings should be created when ProcessFilter finds no customers.
        var systemMappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(systemMappings.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task Run_WithEmptyCustomerRoleIds_HandledCorrectly()
    {
        // Seed a system mapping that should NOT be deleted.
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

        // Pass empty CustomerRoleIds parameter. ToIntArray() on an empty string produces
        // an empty array, which means Contains() matches nothing.
        var parameters = new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = ""
        };
        var ctx = CreateContext(parameters);
        await _task.Run(ctx, CancellationToken.None);
        DbContext.ChangeTracker.Clear();

        // The system mapping should still exist because empty role ID array matches nothing.
        var remaining = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(remaining.Count, Is.EqualTo(1),
            "Empty CustomerRoleIds should result in no deletions.");
    }

    #endregion
}
