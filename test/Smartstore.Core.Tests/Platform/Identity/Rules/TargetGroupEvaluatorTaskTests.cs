using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Moq;
using NUnit.Framework;
using Smartstore.Caching;
using Smartstore.Collections;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Scheduling;
using Smartstore.Threading;
using Smartstore.Utilities;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

[TestFixture]
public class TargetGroupEvaluatorTaskTests
{
    private SqliteConnection _connection;
    private SmartDbContext _db;

    private Mock<ICacheManager> _cacheMock;
    private Mock<IRuleService> _ruleServiceMock;
    private Mock<IRuleProviderFactory> _ruleProviderFactoryMock;
    private Mock<ITargetGroupService> _targetGroupServiceMock;
    private Mock<ITaskStore> _taskStoreMock;
    private Mock<IAsyncState> _asyncStateMock;
    private TargetGroupEvaluatorTask _task;

    [OneTimeSetUp]
    public void SetUp()
    {
        CommonHelper.IsHosted = false;
        CommonHelper.IsDevEnvironment = false;

        // Open SQLite in-memory connection (must stay open for lifetime of tests).
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Create a SqliteTestDbFactory that uses the open connection.
        var factory = new SqliteTestDbFactory(_connection);

        // Set up DataSettings (required by SmartDbContext.OnModelCreating).
        DataSettings.Instance = new DataSettings
        {
            AppVersion = SmartstoreVersion.Version,
            ConnectionString = "DataSource=:memory:",
            TenantName = "Default",
            TenantRoot = null,
            DbFactory = factory
        };
        DataSettings.SetTestMode(true);

        // Build DbContext options using UseDbFactory (required for DbFactoryOptionsExtension).
        var builder = new DbContextOptionsBuilder<SmartDbContext>()
            .UseDbFactory(factory, "DataSource=:memory:", factoryBuilder =>
            {
                factoryBuilder.AddModelAssemblies(new[] { typeof(SmartDbContext).Assembly });
            });

        _db = new SmartDbContext((DbContextOptions<SmartDbContext>)builder.Options);
        _db.Database.EnsureCreated();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _db?.Dispose();
        _connection?.Dispose();
        DataSettings.Reload();
    }

    [SetUp]
    public async Task TestSetUp()
    {
        // Clean up database state from prior tests.
        var existingMappings = await _db.CustomerRoleMappings.ToListAsync();
        if (existingMappings.Count > 0)
        {
            _db.CustomerRoleMappings.RemoveRange(existingMappings);
        }

        var existingRoles = await _db.CustomerRoles
            .Include(x => x.RuleSets)
            .ToListAsync();
        foreach (var role in existingRoles)
        {
            role.RuleSets.Clear();
        }
        if (existingRoles.Count > 0)
        {
            _db.CustomerRoles.RemoveRange(existingRoles);
        }

        var existingRules = await _db.Rules.ToListAsync();
        if (existingRules.Count > 0)
        {
            _db.Rules.RemoveRange(existingRules);
        }

        var existingRuleSets = await _db.RuleSets.ToListAsync();
        if (existingRuleSets.Count > 0)
        {
            _db.RuleSets.RemoveRange(existingRuleSets);
        }

        var existingCustomers = await _db.Customers.ToListAsync();
        if (existingCustomers.Count > 0)
        {
            _db.Customers.RemoveRange(existingCustomers);
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Initialize mocks.
        _cacheMock = new Mock<ICacheManager>();
        _ruleServiceMock = new Mock<IRuleService>();
        _ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();
        _targetGroupServiceMock = new Mock<ITargetGroupService>();
        _taskStoreMock = new Mock<ITaskStore>();
        _asyncStateMock = new Mock<IAsyncState>();

        // Setup factory to return target group service via the interface method
        // that the extension method GetProvider<T> delegates to.
        _ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, null))
            .Returns(_targetGroupServiceMock.Object);

        // Setup task store for progress reporting.
        _taskStoreMock
            .Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Returns(Task.CompletedTask);

        // Setup async state for progress reporting.
        _asyncStateMock
            .Setup(x => x.GetAsync<TaskDescriptor>(It.IsAny<string>()))
            .ReturnsAsync((TaskDescriptor)null);

        // Setup cache mock to return a value for RemoveByPatternAsync.
        _cacheMock
            .Setup(x => x.RemoveByPatternAsync(It.IsAny<string>()))
            .ReturnsAsync(0);

        // Create the task under test.
        _task = new TargetGroupEvaluatorTask(
            _db,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);
    }

    [TearDown]
    public void TestTearDown()
    {
        _db.ChangeTracker.Clear();
    }

    private TaskExecutionContext CreateContext(IDictionary<string, string> parameters = null)
    {
        // TaskExecutionInfo.Clone() calls Task.Clone(), so Task must not be null.
        var executionInfo = new TaskExecutionInfo
        {
            TaskDescriptorId = 1,
            Task = new TaskDescriptor { Name = "TargetGroupEvaluator" }
        };
        var componentContextMock = new Mock<IComponentContext>();

        return new TaskExecutionContext(
            _taskStoreMock.Object,
            _asyncStateMock.Object,
            new DefaultHttpContext(),
            componentContextMock.Object,
            executionInfo,
            parameters);
    }

    private async Task<(CustomerRole role, RuleSetEntity ruleSet)> SeedRoleWithActiveRuleSetAsync(string systemName = "TestRole")
    {
        var ruleSet = new RuleSetEntity
        {
            IsActive = true,
            Scope = RuleScope.Customer,
            Name = "TestRuleSet",
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };

        _db.RuleSets.Add(ruleSet);
        await _db.SaveChangesAsync();

        var role = new CustomerRole
        {
            Active = true,
            Name = systemName,
            SystemName = systemName
        };
        role.RuleSets.Add(ruleSet);

        _db.CustomerRoles.Add(role);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        return (role, ruleSet);
    }

    private async Task<List<Customer>> SeedCustomersAsync(int count)
    {
        var customers = new List<Customer>();
        for (int i = 0; i < count; i++)
        {
            customers.Add(new Customer
            {
                Active = true,
                CustomerGuid = Guid.NewGuid(),
                CreatedOnUtc = DateTime.UtcNow,
                LastActivityDateUtc = DateTime.UtcNow
            });
        }

        _db.Customers.AddRange(customers);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        return customers;
    }

    private async Task<(RuleSetEntity ruleSet1, RuleSetEntity ruleSet2)> SeedTwoRuleSetsAsync(string name1 = "RuleSet1", string name2 = "RuleSet2")
    {
        var ruleSet1 = new RuleSetEntity
        {
            IsActive = true,
            Scope = RuleScope.Customer,
            Name = name1,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };
        var ruleSet2 = new RuleSetEntity
        {
            IsActive = true,
            Scope = RuleScope.Customer,
            Name = name2,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };
        _db.RuleSets.AddRange(ruleSet1, ruleSet2);
        await _db.SaveChangesAsync();

        return (ruleSet1, ruleSet2);
    }

    private void SetupNullExpressionGroup()
    {
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync((IRuleExpressionGroup)null);
    }

    private void SetupRuleServiceAndProcessFilter(List<int> customerIds)
    {
        // FilterExpressionGroup extends FilterExpression and implements IRuleExpressionGroup.
        // The task code checks "if (expressionGroup is FilterExpression expression)" which
        // matches FilterExpressionGroup since it inherits from FilterExpression.
        var expressionGroup = new FilterExpressionGroup(typeof(Customer));

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync(expressionGroup);

        // The extension method ProcessFilter(expression, 0, 500) calls the interface method
        // ProcessFilter(new[] { expression }, LogicalRuleOperator.And, 0, 500).
        // Build a mock IPagedList<Customer> with SourceQuery backed by the SQLite DbContext
        // so that FastPager can call ToListAsync successfully.
        var pagedListMock = new Mock<IPagedList<Customer>>();
        pagedListMock
            .Setup(x => x.SourceQuery)
            .Returns(_db.Customers.Where(c => customerIds.Contains(c.Id)));

        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                LogicalRuleOperator.And,
                0,
                500))
            .Returns(pagedListMock.Object);
    }

    #region Test 1: Full delete scenario

    [Test]
    public async Task Can_delete_all_system_mappings()
    {
        // Arrange: Seed customers.
        var customers = await SeedCustomersAsync(3);

        // Seed a role (no active rule sets needed for delete-only test).
        var role = new CustomerRole
        {
            Active = false,
            Name = "InactiveRole",
            SystemName = "InactiveRole"
        };
        _db.CustomerRoles.Add(role);
        await _db.SaveChangesAsync();

        // Seed CustomerRoleMappings: some system, some non-system.
        var systemMapping1 = new CustomerRoleMapping
        {
            CustomerId = customers[0].Id,
            CustomerRoleId = role.Id,
            IsSystemMapping = true
        };
        var systemMapping2 = new CustomerRoleMapping
        {
            CustomerId = customers[1].Id,
            CustomerRoleId = role.Id,
            IsSystemMapping = true
        };
        var nonSystemMapping = new CustomerRoleMapping
        {
            CustomerId = customers[2].Id,
            CustomerRoleId = role.Id,
            IsSystemMapping = false
        };

        _db.CustomerRoleMappings.AddRange(systemMapping1, systemMapping2, nonSystemMapping);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var ctx = CreateContext();

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert: System mappings should be deleted, non-system mapping should remain.
        _db.ChangeTracker.Clear();
        var remainingMappings = await _db.CustomerRoleMappings.ToListAsync();

        Assert.That(remainingMappings, Has.Count.EqualTo(1));
        Assert.That(remainingMappings[0].IsSystemMapping, Is.False);
        Assert.That(remainingMappings[0].CustomerId, Is.EqualTo(customers[2].Id));
    }

    #endregion

    #region Test 2: Scoped delete scenario

    [Test]
    public async Task Can_delete_scoped_system_mappings_by_role_ids()
    {
        // Arrange: Seed customers.
        var customers = await SeedCustomersAsync(3);

        // Seed two roles.
        var role1 = new CustomerRole
        {
            Active = false,
            Name = "Role1",
            SystemName = "Role1"
        };
        var role2 = new CustomerRole
        {
            Active = false,
            Name = "Role2",
            SystemName = "Role2"
        };
        _db.CustomerRoles.AddRange(role1, role2);
        await _db.SaveChangesAsync();

        // System mapping for role1 (should be deleted).
        var mappingRole1 = new CustomerRoleMapping
        {
            CustomerId = customers[0].Id,
            CustomerRoleId = role1.Id,
            IsSystemMapping = true
        };
        // System mapping for role2 (should NOT be deleted).
        var mappingRole2 = new CustomerRoleMapping
        {
            CustomerId = customers[1].Id,
            CustomerRoleId = role2.Id,
            IsSystemMapping = true
        };
        // Non-system mapping for role1 (should NOT be deleted).
        var nonSystemMapping = new CustomerRoleMapping
        {
            CustomerId = customers[2].Id,
            CustomerRoleId = role1.Id,
            IsSystemMapping = false
        };

        _db.CustomerRoleMappings.AddRange(mappingRole1, mappingRole2, nonSystemMapping);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Pass CustomerRoleIds parameter to scope the delete.
        var parameters = new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = role1.Id.ToString()
        };
        var ctx = CreateContext(parameters);

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert: Only role1's system mapping should be deleted.
        _db.ChangeTracker.Clear();
        var remainingMappings = await _db.CustomerRoleMappings.ToListAsync();

        Assert.That(remainingMappings, Has.Count.EqualTo(2));
        Assert.That(remainingMappings.Any(m => m.IsSystemMapping && m.CustomerRoleId == role1.Id), Is.False,
            "System mapping for role1 should have been deleted.");
        Assert.That(remainingMappings.Any(m => m.IsSystemMapping && m.CustomerRoleId == role2.Id), Is.True,
            "System mapping for role2 should remain.");
        Assert.That(remainingMappings.Any(m => !m.IsSystemMapping && m.CustomerRoleId == role1.Id), Is.True,
            "Non-system mapping for role1 should remain.");
    }

    #endregion

    #region Test 3: Rule evaluation

    [Test]
    public async Task Can_evaluate_rules_and_collect_customer_ids()
    {
        // Arrange: Seed customers and a role with an active rule set.
        var customers = await SeedCustomersAsync(3);
        var (role, ruleSet) = await SeedRoleWithActiveRuleSetAsync();

        var customerIds = customers.Select(c => c.Id).ToList();
        SetupRuleServiceAndProcessFilter(customerIds);

        var ctx = CreateContext();

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert: CreateExpressionGroupAsync should have been called.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()),
            Times.Once);

        // Assert: ProcessFilter should have been called via the extension method.
        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                LogicalRuleOperator.And,
                0,
                500),
            Times.Once);

        // Assert: System mappings should have been created for each customer.
        _db.ChangeTracker.Clear();
        var mappings = await _db.CustomerRoleMappings
            .Where(m => m.IsSystemMapping && m.CustomerRoleId == role.Id)
            .ToListAsync();

        Assert.That(mappings, Has.Count.EqualTo(customerIds.Count));
        foreach (var customerId in customerIds)
        {
            Assert.That(mappings.Any(m => m.CustomerId == customerId), Is.True,
                $"Expected a system mapping for customer {customerId}.");
        }
    }

    #endregion

    #region Test 4: Chunked inserts

    [Test]
    public async Task Can_insert_mappings_in_chunks_of_500()
    {
        // Arrange: Seed more than 500 customers so inserts happen in chunks.
        var customers = await SeedCustomersAsync(600);
        var (role, ruleSet) = await SeedRoleWithActiveRuleSetAsync("ChunkedRole");

        var customerIds = customers.Select(c => c.Id).ToList();
        SetupRuleServiceAndProcessFilter(customerIds);

        var ctx = CreateContext();

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert: All 600 system mappings should have been inserted.
        _db.ChangeTracker.Clear();
        var mappings = await _db.CustomerRoleMappings
            .Where(m => m.IsSystemMapping && m.CustomerRoleId == role.Id)
            .ToListAsync();

        Assert.That(mappings, Has.Count.EqualTo(600));
    }

    #endregion

    #region Test 5: Cache invalidation

    [Test]
    public async Task Can_invalidate_cache_when_mappings_change()
    {
        // Arrange: Seed customers and a role so that new mappings get added.
        var customers = await SeedCustomersAsync(2);
        var (role, ruleSet) = await SeedRoleWithActiveRuleSetAsync("CacheTestRole");

        var customerIds = customers.Select(c => c.Id).ToList();
        SetupRuleServiceAndProcessFilter(customerIds);

        var ctx = CreateContext();

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert: Cache pattern should be cleared because mappings were added.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync("acl:range-*"),
            Times.Once);
    }

    [Test]
    public async Task Can_invalidate_cache_when_mappings_are_deleted()
    {
        // Arrange: Seed system mappings but no active roles (so nothing is added).
        var customers = await SeedCustomersAsync(2);
        var role = new CustomerRole
        {
            Active = false,
            Name = "InactiveCacheRole",
            SystemName = "InactiveCacheRole"
        };
        _db.CustomerRoles.Add(role);
        await _db.SaveChangesAsync();

        _db.CustomerRoleMappings.AddRange(
            new CustomerRoleMapping { CustomerId = customers[0].Id, CustomerRoleId = role.Id, IsSystemMapping = true },
            new CustomerRoleMapping { CustomerId = customers[1].Id, CustomerRoleId = role.Id, IsSystemMapping = true });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var ctx = CreateContext();

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert: Cache should be cleared because mappings were deleted (numDeleted > 0).
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync("acl:range-*"),
            Times.Once);
    }

    [Test]
    public async Task Can_skip_cache_invalidation_when_no_changes()
    {
        // Arrange: No roles at all, so nothing gets deleted or added.
        var ctx = CreateContext();

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert: Cache should NOT be cleared because nothing changed.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never);
    }

    #endregion

    #region Test 6: Cancellation handling

    [Test]
    public async Task Can_handle_cancellation_and_return_early()
    {
        // Arrange: Seed a role with TWO active rule sets so the cancellation check
        // in the inner foreach can be exercised on the second iteration.
        var customers = await SeedCustomersAsync(3);

        var (ruleSet1, ruleSet2) = await SeedTwoRuleSetsAsync("CancelRuleSet1", "CancelRuleSet2");

        var role = new CustomerRole
        {
            Active = true,
            Name = "CancelRole",
            SystemName = "CancelRole"
        };
        role.RuleSets.Add(ruleSet1);
        role.RuleSets.Add(ruleSet2);
        _db.CustomerRoles.Add(role);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var cts = new CancellationTokenSource();

        // On the first call to CreateExpressionGroupAsync, cancel the token and
        // return null (no FilterExpression, so no ProcessFilter call).
        // The second ruleSet iteration checks cancelToken.IsCancellationRequested
        // and returns early.
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .Returns<RuleSetEntity, IRuleVisitor, bool>((rs, visitor, hidden) =>
            {
                cts.Cancel();
                return Task.FromResult<IRuleExpressionGroup>(null);
            });

        var ctx = CreateContext();

        // Act
        await _task.Run(ctx, cts.Token);

        // Assert: CreateExpressionGroupAsync should only be called once (for the first
        // ruleSet). The second ruleSet iteration should be skipped due to cancellation.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()),
            Times.Once);

        // ProcessFilter should never be called since CreateExpressionGroupAsync returned null.
        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Never);
    }

    #endregion

    #region Test 7: Empty rule set results

    [Test]
    public async Task Can_handle_zero_customer_results_and_continue_to_next_role()
    {
        // Arrange: Seed two roles with active rule sets.
        // ProcessFilter returns zero matching customers (empty list).
        var (role1, _) = await SeedRoleWithActiveRuleSetAsync("EmptyRole1");
        var (role2, _) = await SeedRoleWithActiveRuleSetAsync("EmptyRole2");

        SetupRuleServiceAndProcessFilter(new List<int>());

        var ctx = CreateContext();

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert: No mappings should have been inserted for either role.
        _db.ChangeTracker.Clear();
        var mappings = await _db.CustomerRoleMappings
            .Where(m => m.IsSystemMapping)
            .ToListAsync();

        Assert.That(mappings, Has.Count.EqualTo(0));

        // Assert: ProcessFilter was called for both roles (processing continued).
        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                LogicalRuleOperator.And,
                0,
                500),
            Times.Exactly(2));

        // Assert: CreateExpressionGroupAsync was called for both roles.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()),
            Times.Exactly(2));
    }

    #endregion

    #region Test 8: Progress reporting

    [Test]
    public async Task Can_report_progress_for_each_role()
    {
        // Arrange: Seed two roles with active rule sets.
        var customers = await SeedCustomersAsync(2);

        var (ruleSet1, ruleSet2) = await SeedTwoRuleSetsAsync();

        var role1 = new CustomerRole { Active = true, Name = "ProgressRole1", SystemName = "ProgressRole1" };
        role1.RuleSets.Add(ruleSet1);

        var role2 = new CustomerRole { Active = true, Name = "ProgressRole2", SystemName = "ProgressRole2" };
        role2.RuleSets.Add(ruleSet2);

        _db.CustomerRoles.AddRange(role1, role2);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        SetupNullExpressionGroup();

        var capturedPercents = new List<int?>();
        var capturedMessages = new List<string>();

        _taskStoreMock
            .Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Callback<TaskExecutionInfo>(info =>
            {
                capturedPercents.Add(info.ProgressPercent);
                capturedMessages.Add(info.ProgressMessage);
            })
            .Returns(Task.CompletedTask);

        var ctx = CreateContext();

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert: Progress was reported twice with correct percent and message.
        Assert.That(capturedPercents, Has.Count.EqualTo(2));
        Assert.That(capturedPercents[0], Is.EqualTo(50));
        Assert.That(capturedPercents[1], Is.EqualTo(100));
        Assert.That(capturedMessages[0], Does.Contain("ProgressRole1"));
        Assert.That(capturedMessages[1], Does.Contain("ProgressRole2"));
    }

    #endregion

    #region Test 9: Entity detachment

    [Test]
    public async Task Can_detach_entities_after_inserting_mappings()
    {
        // Arrange: Seed customers and a role with an active rule set.
        var customers = await SeedCustomersAsync(5);
        var (role, ruleSet) = await SeedRoleWithActiveRuleSetAsync("DetachRole");

        var customerIds = customers.Select(c => c.Id).ToList();
        SetupRuleServiceAndProcessFilter(customerIds);

        var ctx = CreateContext();

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert: After the task runs, DetachEntities<CustomerRoleMapping>() was called
        // inside the task. Verify the ChangeTracker has no tracked CustomerRoleMapping entities.
        var trackedMappings = _db.ChangeTracker.Entries<CustomerRoleMapping>().ToList();
        Assert.That(trackedMappings, Has.Count.EqualTo(0),
            "CustomerRoleMapping entities should have been detached from the ChangeTracker.");

        // Also verify the mappings were actually persisted.
        _db.ChangeTracker.Clear();
        var persistedMappings = await _db.CustomerRoleMappings
            .Where(m => m.IsSystemMapping && m.CustomerRoleId == role.Id)
            .ToListAsync();

        Assert.That(persistedMappings, Has.Count.EqualTo(customerIds.Count));
    }

    #endregion

    /// <summary>
    /// SQLite-backed DbFactory for tests. Uses a shared in-memory connection
    /// so that <c>ExecuteDeleteAsync</c> (and other relational operations) work,
    /// unlike the EF Core InMemory provider.
    /// </summary>
    private class SqliteTestDbFactory : DbFactory
    {
        private readonly SqliteConnection _connection;

        public SqliteTestDbFactory(SqliteConnection connection)
            => _connection = connection;

        public override DbSystemType DbSystem => DbSystemType.SQLite;

        public override DbConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
            => throw new NotImplementedException();

        public override DbConnectionStringBuilder CreateConnectionStringBuilder(
            string server, string database, string userName, string password)
            => throw new NotImplementedException();

        public override DataProvider CreateDataProvider(DatabaseFacade database)
            => throw new NotImplementedException();

        public override TContext CreateDbContext<TContext>(string connectionString, int? commandTimeout = null)
            => throw new NotImplementedException();

        public override DbContextOptionsBuilder ConfigureDbContext(DbContextOptionsBuilder builder, string connectionString)
        {
            return builder.UseSqlite(_connection);
        }

        public override void ConfigureModelConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Properties<decimal>().HaveConversion<double>();
            configurationBuilder.Properties<decimal?>().HaveConversion<double?>();
        }
    }
}
