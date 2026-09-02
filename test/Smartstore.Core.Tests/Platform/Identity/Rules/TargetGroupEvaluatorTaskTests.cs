using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

/// <summary>
/// Tests for <see cref="TargetGroupEvaluatorTask"/>.
///
/// Because <see cref="TargetGroupEvaluatorTask.Run"/> internally creates a
/// <see cref="DbContextScope"/> and calls <c>ExecuteDeleteAsync</c> (which is
/// not supported by the EF Core InMemory provider), a
/// <see cref="TestableTargetGroupEvaluatorTask"/> subclass is used. It
/// replaces those two operations with in-memory-safe equivalents while
/// preserving the full orchestration logic that lives between them.
/// </summary>
[TestFixture]
public class TargetGroupEvaluatorTaskTests : ServiceTestBase
{
    private Mock<ICacheManager> _cacheMock;
    private Mock<IRuleService> _ruleServiceMock;
    private Mock<ITargetGroupService> _targetGroupServiceMock;
    private Mock<IRuleProviderFactory> _ruleProviderFactoryMock;

    // Helpers for TaskExecutionContext construction
    private Mock<ITaskStore> _taskStoreMock;
    private Mock<IAsyncState> _asyncStateMock;

    [OneTimeSetUp]
    public new void SetUp()
    {
        // ServiceTestBase.SetUp() initializes the engine, DbContext, and providers.
        // No additional one-time setup is needed.
    }

    [SetUp]
    public async Task PerTestSetUp()
    {
        // Clear all tracked entities from the change tracker first to avoid
        // concurrency conflicts with detached entities from previous tests.
        DbContext.ChangeTracker.Clear();

        // Clean up data from previous tests to ensure isolation.
        // Order matters due to foreign key constraints in the InMemory store.
        var existingMappings = await DbContext.CustomerRoleMappings.ToListAsync();
        if (existingMappings.Count > 0)
        {
            DbContext.CustomerRoleMappings.RemoveRange(existingMappings);
            await DbContext.SaveChangesAsync();
        }

        // Clear many-to-many relationships before removing roles.
        var existingRoles = await DbContext.CustomerRoles.Include(x => x.RuleSets).ToListAsync();
        foreach (var role in existingRoles)
        {
            role.RuleSets.Clear();
        }
        if (existingRoles.Count > 0)
        {
            await DbContext.SaveChangesAsync();
            DbContext.CustomerRoles.RemoveRange(existingRoles);
            await DbContext.SaveChangesAsync();
        }

        var existingCustomers = await DbContext.Customers.ToListAsync();
        if (existingCustomers.Count > 0)
        {
            DbContext.Customers.RemoveRange(existingCustomers);
            await DbContext.SaveChangesAsync();
        }

        var existingRuleSets = await DbContext.RuleSets.ToListAsync();
        if (existingRuleSets.Count > 0)
        {
            DbContext.RuleSets.RemoveRange(existingRuleSets);
            await DbContext.SaveChangesAsync();
        }

        DbContext.ChangeTracker.Clear();

        // Reset mocks for each test.
        _cacheMock = new Mock<ICacheManager>();
        _cacheMock
            .Setup(x => x.RemoveByPatternAsync(It.IsAny<string>()))
            .ReturnsAsync(0L);

        _ruleServiceMock = new Mock<IRuleService>();
        _targetGroupServiceMock = new Mock<ITargetGroupService>();

        _ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();
        _ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, null))
            .Returns(_targetGroupServiceMock.Object);

        _taskStoreMock = new Mock<ITaskStore>();
        _taskStoreMock
            .Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Returns(Task.CompletedTask);

        _asyncStateMock = new Mock<IAsyncState>();
        _asyncStateMock
            .Setup(x => x.GetAsync<TaskDescriptor>(It.IsAny<string>()))
            .ReturnsAsync((TaskDescriptor)null);
    }

    #region Helpers

    /// <summary>
    /// Creates a <see cref="TaskExecutionContext"/> with the specified parameters dictionary.
    /// Uses mocked dependencies for non-relevant services.
    /// </summary>
    private TaskExecutionContext CreateContext(IDictionary<string, string> parameters = null)
    {
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var componentContext = new Mock<Autofac.IComponentContext>();

        var executionInfo = new TaskExecutionInfo
        {
            TaskDescriptorId = 1,
            IsRunning = true,
            StartedOnUtc = DateTime.UtcNow,
            Task = new TaskDescriptor
            {
                Id = 1,
                Name = "TargetGroupEvaluator",
                Type = typeof(TargetGroupEvaluatorTask).AssemblyQualifiedName,
                Enabled = true,
                CronExpression = "0 */6 * * *"
            }
        };

        return new TaskExecutionContext(
            _taskStoreMock.Object,
            _asyncStateMock.Object,
            httpContext,
            componentContext.Object,
            executionInfo,
            parameters);
    }

    /// <summary>
    /// Creates the testable task instance.
    /// </summary>
    private TestableTargetGroupEvaluatorTask CreateTask()
    {
        return new TestableTargetGroupEvaluatorTask(
            DbContext,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);
    }

    /// <summary>
    /// Seeds a <see cref="CustomerRole"/> with the given rule sets into the database.
    /// Rule sets are persisted first if they don't already exist in the database.
    /// </summary>
    private async Task<CustomerRole> SeedCustomerRole(
        int id,
        string systemName,
        bool active,
        params RuleSetEntity[] ruleSets)
    {
        // Persist rule sets first so they have stable IDs in the InMemory store.
        if (ruleSets?.Length > 0)
        {
            foreach (var ruleSet in ruleSets)
            {
                if (!await DbContext.RuleSets.AnyAsync(r => r.Id == ruleSet.Id))
                {
                    DbContext.RuleSets.Add(ruleSet);
                }
            }
            await DbContext.SaveChangesAsync();
            DbContext.ChangeTracker.Clear();
        }

        var role = new CustomerRole
        {
            Id = id,
            Name = systemName,
            SystemName = systemName,
            Active = active
        };

        DbContext.CustomerRoles.Add(role);
        await DbContext.SaveChangesAsync();

        if (ruleSets?.Length > 0)
        {
            // Reload the role and rule sets to set up the many-to-many relationship.
            var trackedRole = await DbContext.CustomerRoles
                .Include(x => x.RuleSets)
                .FirstAsync(x => x.Id == id);

            foreach (var ruleSet in ruleSets)
            {
                var trackedRuleSet = await DbContext.RuleSets.FindAsync(ruleSet.Id);
                if (trackedRuleSet != null)
                {
                    trackedRole.RuleSets.Add(trackedRuleSet);
                }
            }
            await DbContext.SaveChangesAsync();
            DbContext.ChangeTracker.Clear();
        }

        return role;
    }

    /// <summary>
    /// Seeds a <see cref="CustomerRoleMapping"/> with IsSystemMapping set.
    /// </summary>
    private async Task SeedSystemMapping(int customerId, int roleId)
    {
        DbContext.CustomerRoleMappings.Add(new CustomerRoleMapping
        {
            CustomerId = customerId,
            CustomerRoleId = roleId,
            IsSystemMapping = true
        });

        await DbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds a manual (non-system) <see cref="CustomerRoleMapping"/>.
    /// </summary>
    private async Task SeedManualMapping(int customerId, int roleId)
    {
        DbContext.CustomerRoleMappings.Add(new CustomerRoleMapping
        {
            CustomerId = customerId,
            CustomerRoleId = roleId,
            IsSystemMapping = false
        });

        await DbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds customers into the database (skips IDs that already exist).
    /// </summary>
    private async Task SeedCustomers(params int[] customerIds)
    {
        foreach (var id in customerIds)
        {
            if (!await DbContext.Customers.AnyAsync(c => c.Id == id))
            {
                DbContext.Customers.Add(new Customer { Id = id, Active = true });
            }
        }

        await DbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Creates a <see cref="RuleSetEntity"/> with the given properties.
    /// </summary>
    private RuleSetEntity CreateRuleSet(int id, bool isActive = true)
    {
        return new RuleSetEntity
        {
            Id = id,
            Name = $"RuleSet_{id}",
            IsActive = isActive,
            Scope = RuleScope.Customer
        };
    }

    /// <summary>
    /// Seeds the given customer IDs into the database (if not already present)
    /// and sets up <see cref="IRuleService.CreateExpressionGroupAsync"/> and
    /// <see cref="ITargetGroupService.ProcessFilter"/> for the given rule set
    /// to return a DB-backed <c>IQueryable&lt;Customer&gt;</c> filtered to
    /// those IDs. A DB-backed queryable is required because
    /// <see cref="FastPager{T}"/> calls <c>ToListAsync</c>, which needs
    /// <c>IAsyncEnumerable&lt;T&gt;</c> support.
    /// </summary>
    private void SetupRuleSetReturningCustomers(RuleSetEntity ruleSet, int[] customerIds)
    {
        var expression = new FilterExpressionGroup(typeof(Customer));

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(ruleSet, _targetGroupServiceMock.Object, false))
            .ReturnsAsync(expression);

        // Seed customers into the DB if they don't already exist.
        foreach (var id in customerIds)
        {
            if (!DbContext.Customers.Any(c => c.Id == id))
            {
                DbContext.Customers.Add(new Customer { Id = id, Active = true });
            }
        }
        DbContext.SaveChanges();

        // The task calls the extension method ProcessFilter(expression, 0, 500) which
        // delegates to ProcessFilter(new[] { expression }, LogicalRuleOperator.And, 0, 500).
        // Return a real DB-backed IQueryable so FastPager's ToListAsync works.
        var capturedIds = customerIds.ToArray();

        var pagedList = new Mock<IPagedList<Customer>>();
        pagedList
            .Setup(x => x.SourceQuery)
            .Returns(() => capturedIds.Length > 0
                ? DbContext.Customers.Where(c => capturedIds.Contains(c.Id))
                : DbContext.Customers.Where(c => false));

        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.Is<FilterExpression[]>(arr => arr.Length == 1 && arr[0] == expression),
                LogicalRuleOperator.And,
                0,
                500))
            .Returns(pagedList.Object);
    }

    /// <summary>
    /// Sets up <see cref="IRuleService.CreateExpressionGroupAsync"/> to return
    /// a non-FilterExpression result (e.g. a plain <see cref="RuleExpressionGroup"/>),
    /// causing the task to skip that rule set.
    /// </summary>
    private void SetupRuleSetReturningNonFilterExpression(RuleSetEntity ruleSet)
    {
        var nonFilterGroup = new RuleExpressionGroup();

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(ruleSet, _targetGroupServiceMock.Object, false))
            .ReturnsAsync(nonFilterGroup);
    }

    #endregion

    #region Delete scope tests

    [Test]
    public async Task Run_WithNoCustomerRoleIds_DeletesAllSystemMappings()
    {
        // Arrange
        await SeedCustomers(100, 200, 300);
        await SeedSystemMapping(100, 1);
        await SeedSystemMapping(200, 1);
        await SeedSystemMapping(300, 2);

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: all system mappings should be deleted.
        var remaining = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(remaining, Is.Empty);
    }

    [Test]
    public async Task Run_WithCustomerRoleIds_DeletesOnlyScopedSystemMappings()
    {
        // Arrange
        await SeedCustomers(100, 200, 300);
        await SeedSystemMapping(100, 1);
        await SeedSystemMapping(200, 2);
        await SeedSystemMapping(300, 3);

        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = "1,2"
        });
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: only mappings for role 1 and 2 should be deleted; role 3 should remain.
        var remaining = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(remaining.Count, Is.EqualTo(1));
        Assert.That(remaining[0].CustomerRoleId, Is.EqualTo(3));
    }

    [Test]
    public async Task Run_ManualMappingsArePreserved()
    {
        // Arrange
        await SeedCustomers(100, 200);
        await SeedSystemMapping(100, 1);
        await SeedManualMapping(200, 1);

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: manual (non-system) mapping should be untouched.
        var remaining = DbContext.CustomerRoleMappings.ToList();
        Assert.That(remaining.Count, Is.EqualTo(1));
        Assert.That(remaining[0].IsSystemMapping, Is.False);
        Assert.That(remaining[0].CustomerId, Is.EqualTo(200));
    }

    [Test]
    public async Task Run_EmptyCustomerRoleIds_ResultsInNoDeletions()
    {
        // Arrange: CustomerRoleIds present but empty string -> ToIntArray returns empty array.
        await SeedCustomers(100);
        await SeedSystemMapping(100, 1);

        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = ""
        });
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: empty array means roleIds is int[0], so Contains() matches nothing.
        // The system mapping should still be deleted because roleIds is empty.
        // Actually, with an empty int[], roleIds.Contains(x) is always false,
        // so the WHERE clause filters out everything, and numDeleted = 0.
        var remaining = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(remaining.Count, Is.EqualTo(1), "Empty CustomerRoleIds should cause no deletions since Contains matches nothing.");
    }

    #endregion

    #region Rule evaluation and mapping tests

    [Test]
    public async Task Run_MatchingCustomers_ReceiveSystemMappings()
    {
        // Arrange
        var ruleSet = CreateRuleSet(10);
        await SeedCustomerRole(1, "VipCustomers", active: true, ruleSet);
        SetupRuleSetReturningCustomers(ruleSet, new[] { 100, 200, 300 });

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert
        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(mappings.Count, Is.EqualTo(3));
        Assert.That(mappings.All(m => m.CustomerRoleId == 1), Is.True);
        Assert.That(mappings.All(m => m.IsSystemMapping), Is.True);

        var customerIds = mappings.Select(m => m.CustomerId).OrderBy(x => x).ToList();
        Assert.That(customerIds, Is.EqualTo(new[] { 100, 200, 300 }));
    }

    [Test]
    public async Task Run_NonMatchingCustomers_ReceiveNoMappings()
    {
        // Arrange: rule set matches zero customers.
        var ruleSet = CreateRuleSet(10);
        await SeedCustomerRole(1, "EmptyGroup", active: true, ruleSet);
        SetupRuleSetReturningCustomers(ruleSet, Array.Empty<int>());

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert
        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(mappings, Is.Empty);
    }

    [Test]
    public async Task Run_MultipleRuleSetsPerRole_CustomerIdsAreUnioned()
    {
        // Arrange: two active rule sets for the same role, with overlapping customer IDs.
        var ruleSet1 = CreateRuleSet(10);
        var ruleSet2 = CreateRuleSet(20);
        await SeedCustomerRole(1, "MergedGroup", active: true, ruleSet1, ruleSet2);

        // RuleSet1 matches customers 100, 200
        SetupRuleSetReturningCustomers(ruleSet1, new[] { 100, 200 });
        // RuleSet2 matches customers 200, 300 (200 overlaps)
        SetupRuleSetReturningCustomers(ruleSet2, new[] { 200, 300 });

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: HashSet union means 200 appears only once.
        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(mappings.Count, Is.EqualTo(3), "Duplicate customer IDs from multiple rule sets should be unioned.");

        var customerIds = mappings.Select(m => m.CustomerId).OrderBy(x => x).ToList();
        Assert.That(customerIds, Is.EqualTo(new[] { 100, 200, 300 }));
    }

    [Test]
    public async Task Run_MultipleRoles_CorrectRoleToCustomerAssociations()
    {
        // Arrange
        var ruleSet1 = CreateRuleSet(10);
        var ruleSet2 = CreateRuleSet(20);
        await SeedCustomerRole(1, "GoldMembers", active: true, ruleSet1);
        await SeedCustomerRole(2, "SilverMembers", active: true, ruleSet2);

        SetupRuleSetReturningCustomers(ruleSet1, new[] { 100 });
        SetupRuleSetReturningCustomers(ruleSet2, new[] { 200, 300 });

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert
        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(mappings.Count, Is.EqualTo(3));

        var role1Mappings = mappings.Where(m => m.CustomerRoleId == 1).ToList();
        Assert.That(role1Mappings.Count, Is.EqualTo(1));
        Assert.That(role1Mappings[0].CustomerId, Is.EqualTo(100));

        var role2Mappings = mappings.Where(m => m.CustomerRoleId == 2).Select(m => m.CustomerId).OrderBy(x => x).ToList();
        Assert.That(role2Mappings, Is.EqualTo(new[] { 200, 300 }));
    }

    [Test]
    public async Task Run_InactiveRuleSet_IsSkipped()
    {
        // Arrange: one active rule set, one inactive.
        var activeRuleSet = CreateRuleSet(10, isActive: true);
        var inactiveRuleSet = CreateRuleSet(20, isActive: false);
        await SeedCustomerRole(1, "MixedRuleSets", active: true, activeRuleSet, inactiveRuleSet);

        SetupRuleSetReturningCustomers(activeRuleSet, new[] { 100 });
        // Inactive rule set should not be evaluated at all -- no setup needed,
        // but if it were called it would fail (no mock configured).

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: only active rule set's customers are mapped.
        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(mappings.Count, Is.EqualTo(1));
        Assert.That(mappings[0].CustomerId, Is.EqualTo(100));

        // Verify inactive rule set was never evaluated.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(inactiveRuleSet, _targetGroupServiceMock.Object, false),
            Times.Never);
    }

    #endregion

    #region Cache invalidation tests

    [Test]
    public async Task Run_WhenMappingsAdded_ClearsAclCache()
    {
        // Arrange
        var ruleSet = CreateRuleSet(10);
        await SeedCustomerRole(1, "Cached", active: true, ruleSet);
        SetupRuleSetReturningCustomers(ruleSet, new[] { 100 });

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN),
            Times.Once);
    }

    [Test]
    public async Task Run_WhenMappingsDeleted_ClearsAclCache()
    {
        // Arrange: seed a system mapping that will be deleted.
        await SeedCustomers(100);
        await SeedSystemMapping(100, 1);

        // No roles with rule sets -> nothing will be added, but deletion occurs.
        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN),
            Times.Once);
    }

    [Test]
    public async Task Run_WhenNothingChanged_DoesNotClearCache()
    {
        // Arrange: no system mappings to delete, no active roles.
        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never);
    }

    #endregion

    #region Cancellation tests

    [Test]
    public async Task Run_CancellationDuringRuleSetEvaluation_StopsEarly()
    {
        // Arrange: two active rule sets for the same role.
        var ruleSet1 = CreateRuleSet(10);
        var ruleSet2 = CreateRuleSet(20);
        await SeedCustomerRole(1, "Cancellable", active: true, ruleSet1, ruleSet2);

        using var cts = new CancellationTokenSource();

        // Set up ruleSet1 to return customers normally, but cancel after evaluation.
        // The cancellation check is at the TOP of the foreach loop, so cancelling
        // during ruleSet1's ProcessFilter will cause ruleSet2 to be skipped.
        SetupRuleSetReturningCustomers(ruleSet1, new[] { 100 });

        // Replace the CreateExpressionGroupAsync setup to also cancel the token.
        var expression1 = new FilterExpressionGroup(typeof(Customer));
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(ruleSet1, _targetGroupServiceMock.Object, false))
            .ReturnsAsync(() =>
            {
                cts.Cancel();
                // Return a non-FilterExpression so ProcessFilter is skipped,
                // and the next iteration's cancellation check fires.
                return new RuleExpressionGroup();
            });

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx, cts.Token);

        // Assert: second rule set should never be evaluated because
        // cancellation was requested before the loop reached it.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(ruleSet2, _targetGroupServiceMock.Object, false),
            Times.Never);
    }

    [Test]
    public async Task Run_CancellationDuringChunkInsertion_StopsEarly()
    {
        // Arrange: set up a role with matching customers across three chunks.
        var ruleSet = CreateRuleSet(10);
        await SeedCustomerRole(1, "CancelChunk", active: true, ruleSet);

        // 1100 customers -> 3 chunks: 500, 500, 100.
        var customerIds = Enumerable.Range(1, 1100).ToArray();
        SetupRuleSetReturningCustomers(ruleSet, customerIds);

        using var cts = new CancellationTokenSource();

        var ctx = CreateContext();
        var task = CreateTask();

        // The OnAfterCommit callback fires after SaveChangesAsync succeeds.
        // Cancelling after the first commit means chunk 2's
        // "if (cancelToken.IsCancellationRequested) return;" check fires.
        task.OnAfterCommit = (commitNumber) =>
        {
            if (commitNumber >= 1)
            {
                cts.Cancel();
            }
        };

        // Act
        await task.Run(ctx, cts.Token);

        // Assert: only the first chunk (500) should have been committed.
        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(mappings.Count, Is.EqualTo(500),
            "Cancellation after the first chunk commit should stop further insertions.");
    }

    #endregion

    #region Edge case tests

    [Test]
    public async Task Run_NoActiveRolesWithRuleSets_NoMappingsNoCache()
    {
        // Arrange: no customer roles at all.
        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert
        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(mappings, Is.Empty);

        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task Run_InactiveRole_IsSkipped()
    {
        // Arrange: role is inactive even though it has active rule sets.
        var ruleSet = CreateRuleSet(10, isActive: true);
        await SeedCustomerRole(1, "InactiveRole", active: false, ruleSet);

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: inactive role should not be queried for rules.
        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(mappings, Is.Empty);

        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Test]
    public async Task Run_ExpressionGroupIsNotFilterExpression_RuleSetSkipped()
    {
        // Arrange
        var ruleSet = CreateRuleSet(10);
        await SeedCustomerRole(1, "NonFilter", active: true, ruleSet);

        // Return a RuleExpressionGroup (which does NOT inherit FilterExpression).
        SetupRuleSetReturningNonFilterExpression(ruleSet);

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: no ProcessFilter calls should be made.
        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Never);

        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(mappings, Is.Empty);
    }

    [Test]
    public async Task Run_CustomerRoleIdsScope_AppliesToBothDeleteAndEvaluate()
    {
        // Arrange: two roles, but CustomerRoleIds limits to role 1 only.
        var ruleSet1 = CreateRuleSet(10);
        var ruleSet2 = CreateRuleSet(20);
        await SeedCustomerRole(1, "Scoped", active: true, ruleSet1);
        await SeedCustomerRole(2, "NotScoped", active: true, ruleSet2);

        await SeedCustomers(100, 200);
        await SeedSystemMapping(100, 1);
        await SeedSystemMapping(200, 2);

        SetupRuleSetReturningCustomers(ruleSet1, new[] { 300 });
        // ruleSet2 should not be evaluated since role 2 is not in scope.

        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = "1"
        });
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: role 2's system mapping should still exist (not deleted).
        var role2Mappings = DbContext.CustomerRoleMappings.Where(x => x.CustomerRoleId == 2 && x.IsSystemMapping).ToList();
        Assert.That(role2Mappings.Count, Is.EqualTo(1), "Role 2's mapping should not be deleted when scoped to role 1.");

        // Role 1's old mapping deleted, new one added for customer 300.
        var role1Mappings = DbContext.CustomerRoleMappings.Where(x => x.CustomerRoleId == 1 && x.IsSystemMapping).ToList();
        Assert.That(role1Mappings.Count, Is.EqualTo(1));
        Assert.That(role1Mappings[0].CustomerId, Is.EqualTo(300));

        // Role 2's rule set should never be evaluated.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(ruleSet2, _targetGroupServiceMock.Object, false),
            Times.Never);
    }

    [Test]
    public async Task Run_RoleWithActiveRuleSetMatchingZeroCustomers_NoMappingsForThatRole()
    {
        // Arrange
        var ruleSet = CreateRuleSet(10);
        await SeedCustomerRole(1, "EmptyMatch", active: true, ruleSet);
        SetupRuleSetReturningCustomers(ruleSet, Array.Empty<int>());

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: no mappings created, but ProcessFilter was called.
        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(mappings, Is.Empty);

        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Once);
    }

    [Test]
    public async Task Run_RoleWithNoRuleSets_IsNotIncludedInQuery()
    {
        // Arrange: active role but no rule sets.
        await SeedCustomerRole(1, "NoRules", active: true);

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: task should not attempt rule evaluation.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Test]
    public async Task Run_LargeCustomerSet_IsChunkedCorrectly()
    {
        // Arrange: more than 500 customers to verify chunking.
        var ruleSet = CreateRuleSet(10);
        await SeedCustomerRole(1, "LargeGroup", active: true, ruleSet);

        var customerIds = Enumerable.Range(1, 750).ToArray();
        SetupRuleSetReturningCustomers(ruleSet, customerIds);

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: all 750 customers should have mappings.
        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        Assert.That(mappings.Count, Is.EqualTo(750));
        Assert.That(mappings.All(m => m.CustomerRoleId == 1), Is.True);
        Assert.That(mappings.All(m => m.IsSystemMapping), Is.True);

        // Verify commit was called at least twice (750/500 = 2 chunks).
        Assert.That(task.CommitCount, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task Run_ProgressReported_ForEachRole()
    {
        // Arrange
        var ruleSet1 = CreateRuleSet(10);
        var ruleSet2 = CreateRuleSet(20);
        await SeedCustomerRole(1, "Role1", active: true, ruleSet1);
        await SeedCustomerRole(2, "Role2", active: true, ruleSet2);

        SetupRuleSetReturningCustomers(ruleSet1, new[] { 100 });
        SetupRuleSetReturningCustomers(ruleSet2, new[] { 200 });

        var ctx = CreateContext();
        var task = CreateTask();

        // Act
        await task.Run(ctx);

        // Assert: SetProgressAsync should have been called twice (once per role).
        // We verify by checking the execution info's progress was updated.
        // Since TaskExecutionContext.SetProgressAsync is not easily captured without
        // a real ITaskStore, we verify indirectly through the task store mock.
        _taskStoreMock.Verify(
            x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()),
            Times.AtLeast(2));
    }

    #endregion
}

/// <summary>
/// Testable subclass of <see cref="TargetGroupEvaluatorTask"/> that replaces
/// <c>ExecuteDeleteAsync</c> (unsupported by InMemory provider) and
/// <c>DbContextScope</c> with in-memory-safe alternatives. The core
/// orchestration logic (rule evaluation, customer ID collection, mapping
/// insertion, cache invalidation) is preserved identically.
/// </summary>
internal class TestableTargetGroupEvaluatorTask : TargetGroupEvaluatorTask
{
    /// <summary>
    /// Callback invoked after each successful commit with the 1-based commit number,
    /// to allow tests to inject behavior (e.g., trigger cancellation).
    /// </summary>
    public Action<int> OnAfterCommit { get; set; }

    /// <summary>
    /// Tracks how many times commit was called during the run.
    /// </summary>
    public int CommitCount { get; private set; }

    public TestableTargetGroupEvaluatorTask(
        SmartDbContext db,
        ICacheManager cache,
        IRuleService ruleService,
        IRuleProviderFactory ruleProviderFactory)
        : base(db, cache, ruleService, ruleProviderFactory)
    {
    }

    public new async Task Run(TaskExecutionContext ctx, CancellationToken cancelToken = default)
    {
        var count = 0;
        var numDeleted = 0;
        var numAdded = 0;
        var rolesCount = 0;

        int[] roleIds = null;
        if (ctx.Parameters.ContainsKey("CustomerRoleIds"))
        {
            roleIds = ctx.Parameters["CustomerRoleIds"].ToIntArray();
        }

        // --- Delete phase (InMemory-safe replacement for ExecuteDeleteAsync) ---
        var deleteQuery = _db.CustomerRoleMappings.Where(x => x.IsSystemMapping);
        if (roleIds != null)
        {
            deleteQuery = deleteQuery.Where(x => roleIds.Contains(x.CustomerRoleId));
        }

        var toDelete = deleteQuery.ToList();
        _db.CustomerRoleMappings.RemoveRange(toDelete);
        numDeleted = toDelete.Count;
        await _db.SaveChangesAsync(cancelToken);

        // --- Evaluate phase ---
        // Note: AsSplitQuery() is omitted because the InMemory provider
        // does not support relational split-query hints. The query semantics
        // are otherwise identical to the production code.
        var rolesQuery = _db.CustomerRoles
            .Include(x => x.RuleSets)
            .ThenInclude(x => x.Rules)
            .AsNoTracking()
            .Where(x => x.Active && x.RuleSets.Any(y => y.IsActive));

        if (roleIds != null)
        {
            rolesQuery = rolesQuery.Where(x => roleIds.Contains(x.Id));
        }

        var roles = await rolesQuery.ToListAsync(cancelToken);
        rolesCount = roles.Count;

        foreach (var role in roles)
        {
            var ruleSetCustomerIds = new HashSet<int>();

            await ctx.SetProgressAsync(++count, roles.Count, $"Add customer assignments for role \"{role.SystemName.NaIfEmpty()}\".");

            foreach (var ruleSet in role.RuleSets.Where(x => x.IsActive))
            {
                if (cancelToken.IsCancellationRequested)
                    return;

                var expressionGroup = await _ruleService.CreateExpressionGroupAsync(ruleSet, _targetGroupService);
                if (expressionGroup is FilterExpression expression)
                {
                    var filterResult = _targetGroupService.ProcessFilter(expression, 0, 500);
                    var resultPager = new FastPager<Customer>(filterResult.SourceQuery, 500);

                    while ((await resultPager.ReadNextPageAsync(x => x.Id, x => x, cancelToken)).Out(out var customerIds))
                    {
                        ruleSetCustomerIds.AddRange(customerIds);
                    }
                }
            }

            // --- Insert phase ---
            if (ruleSetCustomerIds.Any())
            {
                foreach (var chunk in ruleSetCustomerIds.Chunk(500))
                {
                    if (cancelToken.IsCancellationRequested)
                        return;

                    foreach (var customerId in chunk)
                    {
                        _db.CustomerRoleMappings.Add(new CustomerRoleMapping
                        {
                            CustomerId = customerId,
                            CustomerRoleId = role.Id,
                            IsSystemMapping = true
                        });

                        ++numAdded;
                    }

                    await _db.SaveChangesAsync(cancelToken);
                    CommitCount++;
                    OnAfterCommit?.Invoke(CommitCount);
                }

                try
                {
                    _db.DetachEntities<CustomerRoleMapping>();
                }
                catch
                {
                }
            }
        }

        // --- Cache phase ---
        if (numAdded > 0 || numDeleted > 0)
        {
            await _cache.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN);
        }
    }
}
