using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Smartstore.Caching;
using Smartstore.Collections;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Core.Security;
using Smartstore.Data;
using Smartstore.Data.Hooks;
using Smartstore.Scheduling;
using Smartstore.Test.Common;
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

[TestFixture]
public class TargetGroupEvaluatorTaskTests : ServiceTestBase
{
    private Mock<ICacheManager> _cacheMock;
    private Mock<IRuleService> _ruleServiceMock;
    private Mock<IRuleProviderFactory> _ruleProviderFactoryMock;
    private Mock<ITargetGroupService> _targetGroupServiceMock;
    private Mock<ITaskStore> _taskStoreMock;
    private Mock<IAsyncState> _asyncStateMock;
    private Mock<IComponentContext> _componentContextMock;

    private TestableTargetGroupEvaluatorTask _sut;

    [SetUp]
    public new void SetUp()
    {
        _cacheMock = new Mock<ICacheManager>();
        _ruleServiceMock = new Mock<IRuleService>();
        _ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();
        _targetGroupServiceMock = new Mock<ITargetGroupService>();
        _taskStoreMock = new Mock<ITaskStore>();
        _asyncStateMock = new Mock<IAsyncState>();
        _componentContextMock = new Mock<IComponentContext>();

        _ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, null))
            .Returns(_targetGroupServiceMock.Object);

        _sut = new TestableTargetGroupEvaluatorTask(
            DbContext,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);

        // Clean up any leftover data from previous tests.
        CleanUpDatabase();
    }

    [TearDown]
    public void TestTearDown()
    {
        CleanUpDatabase();
    }

    #region Helper Methods

    private void CleanUpDatabase()
    {
        var mappings = DbContext.CustomerRoleMappings.ToList();
        if (mappings.Count > 0)
        {
            DbContext.CustomerRoleMappings.RemoveRange(mappings);
        }

        var roles = DbContext.CustomerRoles.ToList();
        if (roles.Count > 0)
        {
            DbContext.CustomerRoles.RemoveRange(roles);
        }

        var customers = DbContext.Customers.IgnoreQueryFilters().ToList();
        if (customers.Count > 0)
        {
            DbContext.Customers.RemoveRange(customers);
        }

        var ruleSets = DbContext.RuleSets.ToList();
        if (ruleSets.Count > 0)
        {
            DbContext.RuleSets.RemoveRange(ruleSets);
        }

        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();
    }

    private TaskExecutionContext CreateTaskExecutionContext(IDictionary<string, string> parameters = null)
        => CreateMockableTaskExecutionContext(parameters).Object;

    private Customer SeedCustomer(int id)
    {
        var customer = new Customer
        {
            Id = id,
            CustomerGuid = Guid.NewGuid(),
            CreatedOnUtc = DateTime.UtcNow,
            LastActivityDateUtc = DateTime.UtcNow
        };

        DbContext.Customers.Add(customer);
        return customer;
    }

    private CustomerRole SeedCustomerRoleWithRuleSet(int roleId, string systemName, int ruleSetId)
    {
        var ruleSet = new RuleSetEntity
        {
            Id = ruleSetId,
            IsActive = true,
            Scope = RuleScope.Customer,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };

        var role = new CustomerRole
        {
            Id = roleId,
            Name = systemName,
            SystemName = systemName,
            Active = true
        };

        // Use EF Core navigation properties to set up the many-to-many relationship.
        // InMemory provider does not support ExecuteSqlRaw.
        role.RuleSets.Add(ruleSet);

        DbContext.CustomerRoles.Add(role);
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        return role;
    }

    private void SeedSystemMapping(int customerId, int customerRoleId, bool isSystemMapping = true)
    {
        DbContext.CustomerRoleMappings.Add(new CustomerRoleMapping
        {
            CustomerId = customerId,
            CustomerRoleId = customerRoleId,
            IsSystemMapping = isSystemMapping
        });

        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();
    }

    private void SetupRuleServiceToReturnFilterExpression(RuleSetEntity ruleSet)
    {
        var filterExpression = new FilterExpressionGroup(typeof(Customer));

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(ruleSet, _targetGroupServiceMock.Object, false))
            .ReturnsAsync(filterExpression);
    }

    private void SetupTargetGroupProcessFilter(IQueryable<Customer> customerQuery)
    {
        // Create a mock IPagedList<Customer> that exposes the original EF Core-backed
        // IQueryable as SourceQuery. This is necessary because FastPager.ReadNextPageAsync
        // requires the queryable to implement IAsyncEnumerable<T>.
        var pagedListMock = new Mock<IPagedList<Customer>>();
        pagedListMock.Setup(x => x.SourceQuery).Returns(customerQuery);

        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(pagedListMock.Object);
    }

    #endregion

    #region 1. Run_WithNoRoleIds_DeletesAllSystemMappings

    [Test]
    public async Task Run_WithNoRoleIds_DeletesAllSystemMappings()
    {
        // Arrange: Seed customers, roles, and system mappings for multiple roles.
        SeedCustomer(101);
        SeedCustomer(102);
        DbContext.SaveChanges();

        SeedCustomerRoleWithRuleSet(201, "Role1", 301);
        SeedCustomerRoleWithRuleSet(202, "Role2", 302);

        SeedSystemMapping(101, 201, isSystemMapping: true);
        SeedSystemMapping(102, 202, isSystemMapping: true);

        // Also seed a non-system mapping that should NOT be deleted.
        SeedSystemMapping(101, 202, isSystemMapping: false);

        var ctx = CreateTaskExecutionContext();

        // Set up rules to return no matching customers (so no new mappings are created).
        var ruleSet1 = await DbContext.RuleSets.FindAsync(301);
        var ruleSet2 = await DbContext.RuleSets.FindAsync(302);

        SetupRuleServiceToReturnFilterExpression(ruleSet1);
        SetupRuleServiceToReturnFilterExpression(ruleSet2);

        SetupTargetGroupProcessFilter(DbContext.Customers.Where(c => c.Id < 0));

        // Act
        await _sut.Run(ctx);

        // Assert: All system mappings should have been deleted.
        var remainingSystemMappings = await DbContext.CustomerRoleMappings
            .Where(x => x.IsSystemMapping)
            .ToListAsync();

        remainingSystemMappings.Count.ShouldEqual(0);

        // Non-system mapping should still exist.
        var remainingNonSystemMappings = await DbContext.CustomerRoleMappings
            .Where(x => !x.IsSystemMapping)
            .ToListAsync();

        remainingNonSystemMappings.Count.ShouldEqual(1);
    }

    #endregion

    #region 2. Run_WithSpecificRoleIds_DeletesOnlyScopedMappings

    [Test]
    public async Task Run_WithSpecificRoleIds_DeletesOnlyScopedMappings()
    {
        // Arrange: Seed customers, roles, and system mappings.
        SeedCustomer(111);
        SeedCustomer(112);
        DbContext.SaveChanges();

        SeedCustomerRoleWithRuleSet(211, "ScopedRole", 311);
        SeedCustomerRoleWithRuleSet(212, "UntouchedRole", 312);

        SeedSystemMapping(111, 211, isSystemMapping: true);
        SeedSystemMapping(112, 212, isSystemMapping: true);

        // Pass CustomerRoleIds parameter to scope the delete.
        var parameters = new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = "211"
        };
        var ctx = CreateTaskExecutionContext(parameters);

        // Set up rules to return no matching customers.
        var ruleSet1 = await DbContext.RuleSets.FindAsync(311);
        var ruleSet2 = await DbContext.RuleSets.FindAsync(312);

        SetupRuleServiceToReturnFilterExpression(ruleSet1);
        SetupRuleServiceToReturnFilterExpression(ruleSet2);

        SetupTargetGroupProcessFilter(DbContext.Customers.Where(c => c.Id < 0));

        // Act
        await _sut.Run(ctx);

        // Assert: Only mappings for role 211 should have been deleted.
        var remainingMappingsForRole211 = await DbContext.CustomerRoleMappings
            .Where(x => x.IsSystemMapping && x.CustomerRoleId == 211)
            .ToListAsync();

        remainingMappingsForRole211.Count.ShouldEqual(0);

        // The system mapping for role 212 should still exist.
        var remainingMappingsForRole212 = await DbContext.CustomerRoleMappings
            .Where(x => x.IsSystemMapping && x.CustomerRoleId == 212)
            .ToListAsync();

        remainingMappingsForRole212.Count.ShouldEqual(1);
    }

    #endregion

    #region 3. Run_CreatesSystemMappings

    [Test]
    public async Task Run_CreatesSystemMappings()
    {
        // Arrange: Seed customers that will be returned by rule evaluation.
        SeedCustomer(121);
        SeedCustomer(122);
        DbContext.SaveChanges();

        SeedCustomerRoleWithRuleSet(221, "TestRole", 321);
        var ctx = CreateTaskExecutionContext();

        // Set up rules so that ProcessFilter returns a queryable backed by our seeded customers.
        var ruleSet = await DbContext.RuleSets.FindAsync(321);
        SetupRuleServiceToReturnFilterExpression(ruleSet);

        // Return customers from ProcessFilter so they get added as system mappings.
        var customerQuery = DbContext.Customers.Where(c => c.Id == 121 || c.Id == 122);
        SetupTargetGroupProcessFilter(customerQuery);

        // Act
        await _sut.Run(ctx);

        // Assert: System mappings should have been created for both customers.
        var mappings = await DbContext.CustomerRoleMappings
            .Where(x => x.IsSystemMapping && x.CustomerRoleId == 221)
            .ToListAsync();

        mappings.Count.ShouldEqual(2);
        mappings.All(m => m.IsSystemMapping).ShouldBeTrue();
        mappings.Any(m => m.CustomerId == 121).ShouldBeTrue();
        mappings.Any(m => m.CustomerId == 122).ShouldBeTrue();
    }

    #endregion

    #region 4. Run_ReportsProgress

    [Test]
    public async Task Run_ReportsProgress()
    {
        // Arrange: Seed two roles so progress should be reported twice.
        SeedCustomer(131);
        DbContext.SaveChanges();

        SeedCustomerRoleWithRuleSet(231, "ProgressRole1", 331);
        SeedCustomerRoleWithRuleSet(232, "ProgressRole2", 332);

        var ctxMock = CreateMockableTaskExecutionContext();

        var ruleSet1 = await DbContext.RuleSets.FindAsync(331);
        var ruleSet2 = await DbContext.RuleSets.FindAsync(332);

        SetupRuleServiceToReturnFilterExpression(ruleSet1);
        SetupRuleServiceToReturnFilterExpression(ruleSet2);

        SetupTargetGroupProcessFilter(DbContext.Customers.Where(c => c.Id < 0));

        // Act
        await _sut.Run(ctxMock.Object);

        // Assert: SetProgressAsync should have been called twice (once per role).
        // The non-virtual SetProgressAsync(int, int, string) delegates to virtual SetProgressAsync(int?, string).
        ctxMock.Verify(
            x => x.SetProgressAsync(It.IsAny<int?>(), It.IsAny<string>()),
            Times.Exactly(2));
    }

    private Mock<TaskExecutionContext> CreateMockableTaskExecutionContext(IDictionary<string, string> parameters = null)
    {
        var executionInfo = new TaskExecutionInfo
        {
            Id = 1,
            TaskDescriptorId = 1,
            IsRunning = true,
            StartedOnUtc = DateTime.UtcNow,
            Task = new TaskDescriptor
            {
                Id = 1,
                Name = "TargetGroupEvaluatorTask",
                Type = typeof(TargetGroupEvaluatorTask).AssemblyQualifiedName,
                Enabled = true,
                CronExpression = "0 15 2 * * *"
            }
        };

        var ctxMock = new Mock<TaskExecutionContext>(
            _taskStoreMock.Object,
            _asyncStateMock.Object,
            new DefaultHttpContext(),
            _componentContextMock.Object,
            executionInfo,
            parameters ?? new Dictionary<string, string>())
        { CallBase = true };

        ctxMock
            .Setup(x => x.SetProgressAsync(It.IsAny<int?>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        return ctxMock;
    }

    #endregion

    #region 5. Run_RespectsRuleEvalCancellation

    [Test]
    public async Task Run_RespectsRuleEvalCancellation()
    {
        // Arrange: Seed a role with two active rule sets.
        SeedCustomer(141);
        DbContext.SaveChanges();

        var ruleSet1 = new RuleSetEntity
        {
            Id = 341,
            IsActive = true,
            Scope = RuleScope.Customer,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };
        var ruleSet2 = new RuleSetEntity
        {
            Id = 342,
            IsActive = true,
            Scope = RuleScope.Customer,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };

        var role = new CustomerRole
        {
            Id = 241,
            Name = "CancelRole",
            SystemName = "CancelRole",
            Active = true
        };

        // Associate both rule sets with the role via navigation properties.
        role.RuleSets.Add(ruleSet1);
        role.RuleSets.Add(ruleSet2);

        DbContext.CustomerRoles.Add(role);
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        var cts = new CancellationTokenSource();

        // Cancel after the first rule set is processed.
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == 341),
                _targetGroupServiceMock.Object,
                false))
            .ReturnsAsync((IRuleExpressionGroup)null)
            .Callback(() => cts.Cancel());

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == 342),
                _targetGroupServiceMock.Object,
                false))
            .ReturnsAsync(new FilterExpressionGroup(typeof(Customer)));

        SetupTargetGroupProcessFilter(DbContext.Customers.Where(c => c.Id < 0));

        var ctx = CreateTaskExecutionContext();

        // Act: Pass the CancellationToken that gets cancelled during rule evaluation.
        await _sut.Run(ctx, cts.Token);

        // Assert: The second rule set should NOT have been fully processed
        // because the token was cancelled after the first rule set.
        // No mappings should be created since only the first ruleSet was processed
        // (and it returned null, not a FilterExpression).
        var mappings = await DbContext.CustomerRoleMappings
            .Where(x => x.IsSystemMapping)
            .ToListAsync();

        mappings.Count.ShouldEqual(0);
    }

    #endregion

    #region 6. Run_RespectsInsertCancellation

    [Test]
    public async Task Run_RespectsInsertCancellation()
    {
        // Arrange: Seed 501 customers to trigger two insert chunks (chunk size is 500).
        // The first chunk (500) will commit successfully.
        // The cancellation token is cancelled after the first commit.
        // When the second chunk starts, the cancellation check returns early.
        const int customerIdStart = 10000;
        const int customerCount = 501;

        for (int i = 0; i < customerCount; i++)
        {
            SeedCustomer(customerIdStart + i);
        }
        DbContext.SaveChanges();

        SeedCustomerRoleWithRuleSet(251, "InsertCancelRole", 351);
        var ruleSet = await DbContext.RuleSets.FindAsync(351);

        SetupRuleServiceToReturnFilterExpression(ruleSet);

        var customerQuery = DbContext.Customers
            .Where(c => c.Id >= customerIdStart && c.Id < customerIdStart + customerCount);
        SetupTargetGroupProcessFilter(customerQuery);

        var cts = new CancellationTokenSource();

        // Cancel after the first chunk commits successfully.
        // The second chunk will detect cancellation at its top-of-loop check and return early.
        _sut.OnAfterCommit = () => cts.Cancel();

        var ctx = CreateTaskExecutionContext();

        // Act: The second chunk will detect cancellation and return early.
        await _sut.Run(ctx, cts.Token);

        // Assert: Only the first chunk (500) should have been committed.
        var mappings = await DbContext.CustomerRoleMappings
            .Where(x => x.IsSystemMapping && x.CustomerRoleId == 251)
            .ToListAsync();

        // Exactly 500 mappings from the first chunk; the second chunk was skipped.
        mappings.Count.ShouldEqual(500);

        // The task returned early (before the "if numAdded > 0 || numDeleted > 0" check),
        // so the cache should NOT have been invalidated.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN),
            Times.Never);
    }

    #endregion

    #region 7. Run_InvalidatesCacheWhenChanges

    [Test]
    public async Task Run_InvalidatesCacheWhenChanges()
    {
        // Arrange: Seed data so that the task produces changes.
        SeedCustomer(171);
        DbContext.SaveChanges();

        SeedCustomerRoleWithRuleSet(271, "CacheRole", 371);

        // Seed an existing system mapping to be deleted (numDeleted > 0).
        SeedSystemMapping(171, 271, isSystemMapping: true);

        var ruleSet = await DbContext.RuleSets.FindAsync(371);
        SetupRuleServiceToReturnFilterExpression(ruleSet);

        // Return the customer from ProcessFilter so a new mapping is created (numAdded > 0).
        var customerQuery = DbContext.Customers.Where(c => c.Id == 171);
        SetupTargetGroupProcessFilter(customerQuery);

        _cacheMock
            .Setup(x => x.RemoveByPatternAsync(It.IsAny<string>()))
            .ReturnsAsync(1);

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx);

        // Assert: Cache should have been invalidated because changes occurred.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN),
            Times.Once);
    }

    #endregion

    #region 8. Run_DoesNotInvalidateCacheWhenNoChanges

    [Test]
    public async Task Run_DoesNotInvalidateCacheWhenNoChanges()
    {
        // Arrange: No system mappings exist, and no matching customers are returned.
        SeedCustomer(181);
        DbContext.SaveChanges();

        SeedCustomerRoleWithRuleSet(281, "NoCacheRole", 381);

        var ruleSet = await DbContext.RuleSets.FindAsync(381);
        SetupRuleServiceToReturnFilterExpression(ruleSet);

        // Return no customers from ProcessFilter, so no new mappings are created.
        SetupTargetGroupProcessFilter(DbContext.Customers.Where(c => c.Id < 0));

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx);

        // Assert: Cache should NOT have been invalidated because nothing changed.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never);
    }

    #endregion
}

/// <summary>
/// Testable subclass of <see cref="TargetGroupEvaluatorTask"/> that replaces
/// <c>ExecuteDeleteAsync</c> with InMemory-compatible deletion logic.
/// </summary>
internal class TestableTargetGroupEvaluatorTask : TargetGroupEvaluatorTask
{
    public Action OnAfterCommit { get; set; }

    public TestableTargetGroupEvaluatorTask(
        SmartDbContext db,
        ICacheManager cache,
        IRuleService ruleService,
        IRuleProviderFactory ruleProviderFactory)
        : base(db, cache, ruleService, ruleProviderFactory)
    {
    }

    protected override async Task<int> DeleteSystemMappingsAsync(
        IQueryable<CustomerRoleMapping> deleteQuery,
        DbContextScope scope,
        CancellationToken cancelToken)
    {
        var entitiesToDelete = await deleteQuery.ToListAsync(cancelToken);
        _db.CustomerRoleMappings.RemoveRange(entitiesToDelete);
        var count = entitiesToDelete.Count;
        if (count > 0)
        {
            await scope.CommitAsync(cancelToken);
        }

        return count;
    }

    protected override async Task OnAfterChunkCommittedAsync(CancellationToken cancelToken)
    {
        OnAfterCommit?.Invoke();
        await Task.CompletedTask;
    }
}
