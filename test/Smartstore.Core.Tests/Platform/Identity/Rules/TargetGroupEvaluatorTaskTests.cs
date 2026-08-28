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
using Smartstore.Test.Common;
using Smartstore.Threading;
using Smartstore.Utilities;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

#region Test Infrastructure

/// <summary>
/// A DbFactory that uses SQLite in-memory for tests.
/// SQLite supports ExecuteDeleteAsync, which the InMemory provider does not.
/// </summary>
internal class SqliteTestDbFactory : DbFactory
{
    private readonly SqliteConnection _connection;

    public SqliteTestDbFactory(SqliteConnection connection)
    {
        _connection = connection;
    }

    public override DbSystemType DbSystem => DbSystemType.Unknown;

    public override DbConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
        => throw new NotImplementedException();

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

#endregion

[TestFixture]
public class TargetGroupEvaluatorTaskTests
{
    private SqliteConnection _connection;
    private SmartDbContext _db;
    private Mock<ICacheManager> _cacheMock;
    private Mock<IRuleService> _ruleServiceMock;
    private Mock<ITargetGroupService> _targetGroupServiceMock;
    private Mock<IRuleProviderFactory> _ruleProviderFactoryMock;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        CommonHelper.IsHosted = false;
        CommonHelper.IsDevEnvironment = false;
    }

    [SetUp]
    public void SetUp()
    {
        // Open a SQLite in-memory connection that persists for the test lifetime.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Configure DataSettings with a SQLite-based test factory.
        var dataSettings = new DataSettings
        {
            AppVersion = SmartstoreVersion.Version,
            ConnectionString = "DataSource=:memory:",
            TenantName = "Default",
            TenantRoot = null,
            DbFactory = new SqliteTestDbFactory(_connection)
        };

        DataSettings.Instance = dataSettings;
        DataSettings.SetTestMode(true);

        // Build SmartDbContext using the UseDbFactory pattern with SQLite.
        var builder = new DbContextOptionsBuilder<SmartDbContext>()
            .UseDbFactory(factoryBuilder =>
            {
                factoryBuilder.AddModelAssemblies(new[] { typeof(SmartDbContext).Assembly });
            });

        _db = new SmartDbContext((DbContextOptions<SmartDbContext>)builder.Options);
        _db.Database.EnsureCreated();

        // Set up mocks.
        _cacheMock = new Mock<ICacheManager>();
        _cacheMock
            .Setup(x => x.RemoveByPatternAsync(It.IsAny<string>()))
            .ReturnsAsync(0);

        _ruleServiceMock = new Mock<IRuleService>();
        _targetGroupServiceMock = new Mock<ITargetGroupService>();
        _ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();
        _ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, null))
            .Returns(_targetGroupServiceMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _db?.Dispose();
        _connection?.Close();
        _connection?.Dispose();
        DataSettings.Reload();
    }

    #region Helpers

    private TargetGroupEvaluatorTask CreateTask()
    {
        return new TargetGroupEvaluatorTask(
            _db,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);
    }

    private TaskExecutionContext CreateContext(IDictionary<string, string> parameters = null)
        => CreateContextWithTaskStore(parameters).ctx;

    /// <summary>
    /// Creates a TaskExecutionContext whose ITaskStore mock is returned,
    /// so tests can verify calls to UpdateExecutionInfoAsync.
    /// </summary>
    private (TaskExecutionContext ctx, Mock<ITaskStore> taskStoreMock) CreateContextWithTaskStore(
        IDictionary<string, string> parameters = null)
    {
        var taskStoreMock = new Mock<ITaskStore>();
        taskStoreMock
            .Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Returns(Task.CompletedTask);

        var asyncStateMock = new Mock<IAsyncState>();
        asyncStateMock
            .Setup(x => x.GetAsync<TaskDescriptor>(It.IsAny<string>()))
            .ReturnsAsync((TaskDescriptor)null);

        var taskDescriptor = new TaskDescriptor
        {
            Id = 1,
            Name = "TargetGroupEvaluator",
            Type = typeof(TargetGroupEvaluatorTask).AssemblyQualifiedName
        };

        var executionInfo = new TaskExecutionInfo
        {
            Id = 1,
            TaskDescriptorId = 1,
            Task = taskDescriptor
        };

        var ctx = new TaskExecutionContext(
            taskStoreMock.Object,
            asyncStateMock.Object,
            new DefaultHttpContext(),
            new Mock<IComponentContext>().Object,
            executionInfo,
            parameters);

        return (ctx, taskStoreMock);
    }

    private async Task<int[]> SeedCustomersAsync(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _db.Customers.Add(new Customer
            {
                CustomerGuid = Guid.NewGuid(),
                Active = true,
                CreatedOnUtc = DateTime.UtcNow,
                LastActivityDateUtc = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();

        return await _db.Customers
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToArrayAsync();
    }

    private async Task<CustomerRole> SeedRoleWithRuleSetsAsync(
        string systemName,
        bool active = true,
        int ruleSetCount = 1,
        bool ruleSetsActive = true)
    {
        var role = new CustomerRole
        {
            Name = systemName,
            SystemName = systemName,
            Active = active
        };

        _db.CustomerRoles.Add(role);
        await _db.SaveChangesAsync();

        for (int i = 0; i < ruleSetCount; i++)
        {
            var ruleSet = new RuleSetEntity
            {
                Name = $"RuleSet_{systemName}_{i}",
                IsActive = ruleSetsActive,
                Scope = RuleScope.Customer,
                LogicalOperator = LogicalRuleOperator.And
            };

            _db.RuleSets.Add(ruleSet);
            await _db.SaveChangesAsync();

            var rule = new RuleEntity
            {
                RuleSetId = ruleSet.Id,
                RuleType = "TestRule",
                Operator = "Is",
                Value = "TestValue"
            };

            _db.Rules.Add(rule);
            await _db.SaveChangesAsync();

            // Establish the many-to-many relationship.
            role.RuleSets.Add(ruleSet);
        }

        await _db.SaveChangesAsync();
        return role;
    }

    private async Task SeedMappingAsync(int customerId, int roleId, bool isSystemMapping)
    {
        _db.CustomerRoleMappings.Add(new CustomerRoleMapping
        {
            CustomerId = customerId,
            CustomerRoleId = roleId,
            IsSystemMapping = isSystemMapping
        });

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Configures the rule service and target group service mocks so that
    /// evaluating the given rule set produces a PagedList whose SourceQuery
    /// is a real EF Core queryable (from the SQLite-backed _db) that returns
    /// customers with the given IDs. This ensures FastPager's ToListAsync works.
    /// </summary>
    private void SetupRuleEvaluation(RuleSetEntity ruleSet, int[] customerIds)
    {
        var filterExpression = new FilterExpressionGroup();

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == ruleSet.Id),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync(filterExpression);

        // Use the real DbContext query so FastPager can call ToListAsync
        // (which requires IAsyncEnumerable<T>, only provided by EF Core queryables).
        var customerQuery = customerIds.Length > 0
            ? _db.Customers.Where(x => customerIds.Contains(x.Id))
            : _db.Customers.Where(x => false);

        var pagedListMock = new Mock<IPagedList<Customer>>();
        pagedListMock.Setup(x => x.SourceQuery).Returns(customerQuery);

        // The extension method ProcessFilter(FilterExpression, int, int)
        // wraps the single expression in an array and delegates to the interface method.
        // Match on the specific FilterExpression instance so multiple rule sets
        // on the same role each return their own customer set.
        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.Is<FilterExpression[]>(arr => arr.Length == 1 && ReferenceEquals(arr[0], filterExpression)),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(pagedListMock.Object);
    }

    private void SetupRuleEvaluationNonFilter(RuleSetEntity ruleSet)
    {
        var expressionGroupMock = new Mock<IRuleExpressionGroup>();

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == ruleSet.Id),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync(expressionGroupMock.Object);
    }

    #endregion

    #region 1. System Mapping Deletion

    [Test]
    public async Task Run_DeletesAllSystemMappings_WhenNoRoleIdFilter()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(3);
        var role1 = await SeedRoleWithRuleSetsAsync("Role1");
        var role2 = await SeedRoleWithRuleSetsAsync("Role2");

        await SeedMappingAsync(customerIds[0], role1.Id, true);
        await SeedMappingAsync(customerIds[1], role1.Id, true);
        await SeedMappingAsync(customerIds[2], role2.Id, true);

        // Also seed a manual mapping that should NOT be deleted.
        await SeedMappingAsync(customerIds[0], role1.Id, false);

        foreach (var role in new[] { role1, role2 })
        {
            foreach (var ruleSet in role.RuleSets)
            {
                SetupRuleEvaluation(ruleSet, Array.Empty<int>());
            }
        }

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: all system mappings deleted, manual mapping preserved.
        var remainingMappings = await _db.CustomerRoleMappings.ToListAsync();
        Assert.That(remainingMappings.Count, Is.EqualTo(1));
        Assert.That(remainingMappings[0].IsSystemMapping, Is.False);
    }

    [Test]
    public async Task Run_DeletesOnlyFilteredSystemMappings_WhenCustomerRoleIdsProvided()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(3);
        var role1 = await SeedRoleWithRuleSetsAsync("Role1");
        var role2 = await SeedRoleWithRuleSetsAsync("Role2");

        await SeedMappingAsync(customerIds[0], role1.Id, true);
        await SeedMappingAsync(customerIds[1], role2.Id, true);

        foreach (var ruleSet in role1.RuleSets)
        {
            SetupRuleEvaluation(ruleSet, Array.Empty<int>());
        }

        var parameters = new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = role1.Id.ToString()
        };

        var ctx = CreateContext(parameters);
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: only role1's mapping deleted; role2's system mapping preserved.
        var remainingMappings = await _db.CustomerRoleMappings.ToListAsync();
        Assert.That(remainingMappings.Count, Is.EqualTo(1));
        Assert.That(remainingMappings[0].CustomerRoleId, Is.EqualTo(role2.Id));
        Assert.That(remainingMappings[0].IsSystemMapping, Is.True);
    }

    [Test]
    public async Task Run_DoesNotDeleteManualMappings()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(2);
        var role = await SeedRoleWithRuleSetsAsync("Role1");

        await SeedMappingAsync(customerIds[0], role.Id, false);
        await SeedMappingAsync(customerIds[1], role.Id, false);

        foreach (var ruleSet in role.RuleSets)
        {
            SetupRuleEvaluation(ruleSet, Array.Empty<int>());
        }

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: manual mappings remain untouched.
        var mappings = await _db.CustomerRoleMappings.ToListAsync();
        Assert.That(mappings.Count, Is.EqualTo(2));
        Assert.That(mappings.All(m => !m.IsSystemMapping), Is.True);
    }

    #endregion

    #region 2. Role Filtering

    [Test]
    public async Task Run_OnlyProcessesActiveRolesWithActiveRuleSets()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(2);

        var activeRole = await SeedRoleWithRuleSetsAsync("ActiveRole", active: true, ruleSetCount: 1, ruleSetsActive: true);
        var inactiveRole = await SeedRoleWithRuleSetsAsync("InactiveRole", active: false, ruleSetCount: 1, ruleSetsActive: true);
        var noActiveRulesRole = await SeedRoleWithRuleSetsAsync("NoActiveRulesRole", active: true, ruleSetCount: 1, ruleSetsActive: false);

        foreach (var ruleSet in activeRole.RuleSets)
        {
            SetupRuleEvaluation(ruleSet, new[] { customerIds[0] });
        }

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: only the active role's customer was mapped.
        var mappings = await _db.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToListAsync();
        Assert.That(mappings.Count, Is.EqualTo(1));
        Assert.That(mappings[0].CustomerRoleId, Is.EqualTo(activeRole.Id));
        Assert.That(mappings[0].CustomerId, Is.EqualTo(customerIds[0]));
    }

    [Test]
    public async Task Run_SkipsRolesWithNoRuleSets()
    {
        // Arrange
        var role = new CustomerRole
        {
            Name = "EmptyRole",
            SystemName = "EmptyRole",
            Active = true
        };

        _db.CustomerRoles.Add(role);
        await _db.SaveChangesAsync();

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: no mappings created and CreateExpressionGroupAsync never called.
        var mappings = await _db.CustomerRoleMappings.ToListAsync();
        Assert.That(mappings, Is.Empty);

        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    #endregion

    #region 3. Rule Evaluation

    [Test]
    public async Task Run_EvaluatesRuleSetsViaRuleService()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(3);
        var role = await SeedRoleWithRuleSetsAsync("TestRole", ruleSetCount: 1);

        var ruleSet = role.RuleSets.First();
        SetupRuleEvaluation(ruleSet, new[] { customerIds[0], customerIds[1] });

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: CreateExpressionGroupAsync called with the correct rule set.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == ruleSet.Id),
                It.IsAny<IRuleVisitor>(),
                false),
            Times.Once);

        // Assert: ProcessFilter called with page size 500.
        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                0,
                500),
            Times.Once);
    }

    [Test]
    public async Task Run_MergesCustomerIdsFromMultipleRuleSetsOnSameRole()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(4);
        var role = await SeedRoleWithRuleSetsAsync("TestRole", ruleSetCount: 2);

        var ruleSets = role.RuleSets.ToList();

        // RuleSet 1 returns customers 0 and 1.
        SetupRuleEvaluation(ruleSets[0], new[] { customerIds[0], customerIds[1] });
        // RuleSet 2 returns customers 1 and 2 (overlap on customer 1).
        SetupRuleEvaluation(ruleSets[1], new[] { customerIds[1], customerIds[2] });

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: 3 unique customer IDs mapped (deduplication via HashSet).
        var mappings = await _db.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToListAsync();
        Assert.That(mappings.Count, Is.EqualTo(3));

        var mappedCustomerIds = mappings.Select(m => m.CustomerId).OrderBy(x => x).ToArray();
        var expected = new[] { customerIds[0], customerIds[1], customerIds[2] }.OrderBy(x => x).ToArray();
        Assert.That(mappedCustomerIds, Is.EqualTo(expected));
    }

    [Test]
    public async Task Run_SkipsRuleSetThatDoesNotProduceFilterExpression()
    {
        // Arrange
        await SeedCustomersAsync(2);
        var role = await SeedRoleWithRuleSetsAsync("TestRole", ruleSetCount: 1);

        var ruleSet = role.RuleSets.First();
        SetupRuleEvaluationNonFilter(ruleSet);

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: no mappings because expression was not a FilterExpression.
        var mappings = await _db.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToListAsync();
        Assert.That(mappings, Is.Empty);

        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Never);
    }

    #endregion

    #region 4. Batch Insertion

    [Test]
    public async Task Run_InsertsAllCustomerRoleMappingsWithIsSystemMappingTrue()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(3);
        var role = await SeedRoleWithRuleSetsAsync("TestRole");

        SetupRuleEvaluation(role.RuleSets.First(), customerIds);

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert
        var mappings = await _db.CustomerRoleMappings.ToListAsync();
        Assert.That(mappings.Count, Is.EqualTo(3));
        Assert.That(mappings.All(m => m.IsSystemMapping), Is.True);
        Assert.That(mappings.All(m => m.CustomerRoleId == role.Id), Is.True);

        var mappedIds = mappings.Select(m => m.CustomerId).OrderBy(x => x).ToArray();
        Assert.That(mappedIds, Is.EqualTo(customerIds.OrderBy(x => x).ToArray()));
    }

    [Test]
    public async Task Run_InsertsMultipleRoleMappingsForMultipleRoles()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(2);
        var role1 = await SeedRoleWithRuleSetsAsync("Role1");
        var role2 = await SeedRoleWithRuleSetsAsync("Role2");

        SetupRuleEvaluation(role1.RuleSets.First(), new[] { customerIds[0] });
        SetupRuleEvaluation(role2.RuleSets.First(), new[] { customerIds[1] });

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: one mapping per role.
        var mappings = await _db.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToListAsync();
        Assert.That(mappings.Count, Is.EqualTo(2));
        Assert.That(mappings.Any(m => m.CustomerRoleId == role1.Id && m.CustomerId == customerIds[0]), Is.True);
        Assert.That(mappings.Any(m => m.CustomerRoleId == role2.Id && m.CustomerId == customerIds[1]), Is.True);
    }

    [Test]
    public async Task Run_HandlesLargeBatchesByChunking()
    {
        // Arrange: > 500 customers to exercise the chunking logic.
        var customerCount = 520;
        var customerIds = await SeedCustomersAsync(customerCount);
        var role = await SeedRoleWithRuleSetsAsync("BigRole");

        SetupRuleEvaluation(role.RuleSets.First(), customerIds);

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: all 520 customers mapped despite requiring 2 chunks (500 + 20).
        var mappings = await _db.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToListAsync();
        Assert.That(mappings.Count, Is.EqualTo(customerCount));
    }

    #endregion

    #region 5. Cache Invalidation

    [Test]
    public async Task Run_InvalidatesAclCache_WhenMappingsAdded()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(1);
        var role = await SeedRoleWithRuleSetsAsync("TestRole");

        SetupRuleEvaluation(role.RuleSets.First(), customerIds);

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync("acl:range-*"),
            Times.Once);
    }

    [Test]
    public async Task Run_InvalidatesAclCache_WhenMappingsDeleted()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(1);
        var role = await SeedRoleWithRuleSetsAsync("TestRole");

        await SeedMappingAsync(customerIds[0], role.Id, true);

        // Return no customers so nothing is added, but a deletion has occurred.
        SetupRuleEvaluation(role.RuleSets.First(), Array.Empty<int>());

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync("acl:range-*"),
            Times.Once);
    }

    [Test]
    public async Task Run_DoesNotInvalidateCache_WhenNoChanges()
    {
        // Arrange: no system mappings and no active roles -> nothing to do.
        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never);
    }

    #endregion

    #region 6. Cancellation

    [Test]
    public async Task Run_StopsProcessingRuleSets_WhenCancelled()
    {
        // Arrange: a role with 2 active rule sets.
        // The first rule set triggers cancellation during evaluation
        // and returns a non-FilterExpression so ProcessFilter is NOT called
        // (avoiding ToListAsync on a cancelled token).
        // The cancellation check at the top of the inner loop then fires
        // for the second rule set.
        var customerIds = await SeedCustomersAsync(2);
        var role = await SeedRoleWithRuleSetsAsync("TestRole", ruleSetCount: 2);

        var ruleSets = role.RuleSets.ToList();
        using var cts = new CancellationTokenSource();

        // First rule set: returns a non-FilterExpression and triggers cancellation.
        var expressionGroupMock = new Mock<IRuleExpressionGroup>();
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == ruleSets[0].Id),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync((RuleSetEntity _, IRuleVisitor _, bool _) =>
            {
                cts.Cancel();
                return expressionGroupMock.Object;
            });

        // Second rule set: set up normally but should never be reached.
        SetupRuleEvaluation(ruleSets[1], customerIds.ToArray());

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, cts.Token);

        // Assert: second rule set's CreateExpressionGroupAsync was never called.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == ruleSets[1].Id),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    [Test]
    public async Task Run_ExitsCleanly_WhenCancelledBetweenRoles()
    {
        // Arrange: two roles. The first processes normally.
        // After the first role completes, cancel the token.
        // Since the outer role loop does not check cancellation between roles,
        // the second role will enter the inner rule set loop where the
        // cancellation check fires at the top.
        var customerIds = await SeedCustomersAsync(3);
        var role1 = await SeedRoleWithRuleSetsAsync("Role1");
        var role2 = await SeedRoleWithRuleSetsAsync("Role2");

        using var cts = new CancellationTokenSource();

        // Role1: returns 1 customer normally.
        SetupRuleEvaluation(role1.RuleSets.First(), new[] { customerIds[0] });

        // Role2: triggers cancellation in CreateExpressionGroupAsync
        // but returns a non-FilterExpression so no FastPager/ToListAsync.
        var nonFilterGroup = new Mock<IRuleExpressionGroup>();
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == role2.RuleSets.First().Id),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync((RuleSetEntity _, IRuleVisitor _, bool _) =>
            {
                cts.Cancel();
                return nonFilterGroup.Object;
            });

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, cts.Token);

        // Assert: role1's mapping was created; role2 was evaluated but
        // produced no results (non-FilterExpression) and subsequent
        // cancellation-guarded operations would exit early.
        var mappings = await _db.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToListAsync();
        Assert.That(mappings.Count, Is.EqualTo(1));
        Assert.That(mappings[0].CustomerRoleId, Is.EqualTo(role1.Id));
    }

    #endregion

    #region 7. Progress Reporting

    [Test]
    public async Task Run_ReportsProgressForEachRole()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(2);
        var role1 = await SeedRoleWithRuleSetsAsync("AdminRole");
        var role2 = await SeedRoleWithRuleSetsAsync("GuestRole");

        SetupRuleEvaluation(role1.RuleSets.First(), new[] { customerIds[0] });
        SetupRuleEvaluation(role2.RuleSets.First(), new[] { customerIds[1] });

        var (ctx, taskStoreMock) = CreateContextWithTaskStore();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: UpdateExecutionInfoAsync was called exactly twice (once per role).
        taskStoreMock.Verify(
            x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()),
            Times.Exactly(2));
    }

    [Test]
    public async Task Run_ProgressMessageContainsRoleSystemName()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(1);
        var role = await SeedRoleWithRuleSetsAsync("VipCustomers");

        SetupRuleEvaluation(role.RuleSets.First(), new[] { customerIds[0] });

        string capturedMessage = null;
        var (ctx, taskStoreMock) = CreateContextWithTaskStore();

        taskStoreMock
            .Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Callback<TaskExecutionInfo>(info =>
            {
                capturedMessage = info.ProgressMessage;
            })
            .Returns(Task.CompletedTask);

        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: progress message includes the role's SystemName.
        Assert.That(capturedMessage, Does.Contain("VipCustomers"));
    }

    #endregion

    #region End-to-End

    [Test]
    public async Task Run_FullCycle_DeletesOldAndInsertsNewMappings()
    {
        // Arrange
        var customerIds = await SeedCustomersAsync(5);
        var role = await SeedRoleWithRuleSetsAsync("TestRole");

        // Pre-seed old system mappings.
        await SeedMappingAsync(customerIds[0], role.Id, true);
        await SeedMappingAsync(customerIds[1], role.Id, true);

        // Rule evaluation now yields a different set of customers.
        SetupRuleEvaluation(role.RuleSets.First(), new[] { customerIds[2], customerIds[3], customerIds[4] });

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, CancellationToken.None);

        // Assert: old mappings deleted, new mappings present.
        var mappings = await _db.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToListAsync();
        Assert.That(mappings.Count, Is.EqualTo(3));

        var mappedIds = mappings.Select(m => m.CustomerId).OrderBy(x => x).ToArray();
        var expected = new[] { customerIds[2], customerIds[3], customerIds[4] }.OrderBy(x => x).ToArray();
        Assert.That(mappedIds, Is.EqualTo(expected));

        // Cache invalidated because changes occurred.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync("acl:range-*"),
            Times.Once);
    }

    #endregion
}
