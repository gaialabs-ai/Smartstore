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

/// <summary>
/// Category 4 integration tests for <see cref="TargetGroupEvaluatorTask"/>. Exercises
/// the full pipeline (delete, rule evaluation, chunked insert, detach, cache
/// invalidation) against the EF Core InMemory database, verifying end-to-end
/// behavior rather than individual code paths.
///
/// Uses <see cref="TestableTargetGroupEvaluatorTask"/> (defined in
/// <c>TargetGroupEvaluatorTaskTests.cs</c>) to replace <c>ExecuteDeleteAsync</c>
/// with InMemory-compatible deletion logic.
/// </summary>
[TestFixture]
public class TargetGroupEvaluatorTaskIntegrationTests : ServiceTestBase
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

        // Return a valid FilterExpressionGroup for any rule set.
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync(new FilterExpressionGroup(typeof(Customer)));

        _sut = new TestableTargetGroupEvaluatorTask(
            DbContext,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);

        CleanUpDatabase();
    }

    [TearDown]
    public void TestTearDown()
    {
        CleanUpDatabase();
    }

    #region Helpers

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

    private TaskExecutionContext CreateTaskContext(IDictionary<string, string> parameters = null)
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

        return ctxMock.Object;
    }

    private void SeedCustomers(int startId, int count)
    {
        for (int i = 0; i < count; i++)
        {
            DbContext.Customers.Add(new Customer
            {
                Id = startId + i,
                CustomerGuid = Guid.NewGuid(),
                CreatedOnUtc = DateTime.UtcNow,
                LastActivityDateUtc = DateTime.UtcNow
            });
        }

        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();
    }

    private CustomerRole SeedRoleWithRuleSet(int roleId, string systemName, int ruleSetId)
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

        role.RuleSets.Add(ruleSet);
        DbContext.CustomerRoles.Add(role);
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        return role;
    }

    private void SeedSystemMapping(int customerId, int customerRoleId)
    {
        DbContext.CustomerRoleMappings.Add(new CustomerRoleMapping
        {
            CustomerId = customerId,
            CustomerRoleId = customerRoleId,
            IsSystemMapping = true
        });

        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();
    }

    private void SetupProcessFilter(IQueryable<Customer> customerQuery)
    {
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

    // ------------------------------------------------------------------
    // 1. Full pipeline creates correct system mappings
    // ------------------------------------------------------------------

    [Test]
    public async Task Run_Integration_CreatesCorrectMappings()
    {
        // Arrange: Two active roles with rule sets, 50 customers.
        const int startId = 5000;
        const int customerCount = 50;

        SeedCustomers(startId, customerCount);
        var roleA = SeedRoleWithRuleSet(700, "IntegrationRoleA", 800);
        var roleB = SeedRoleWithRuleSet(701, "IntegrationRoleB", 801);

        var query = DbContext.Customers
            .Where(c => c.Id >= startId && c.Id < startId + customerCount);
        SetupProcessFilter(query);

        var ctx = CreateTaskContext();

        // Act
        await _sut.Run(ctx);

        // Assert: Correct mappings exist.
        var allMappings = await DbContext.CustomerRoleMappings
            .Where(x => x.IsSystemMapping)
            .ToListAsync();

        // 50 customers x 2 roles = 100 mappings.
        allMappings.Count.ShouldEqual(100);

        // All mappings must have IsSystemMapping = true.
        allMappings.All(m => m.IsSystemMapping).ShouldBeTrue();

        // Verify role A has exactly 50 mappings with correct customer IDs.
        var roleAMappings = allMappings.Where(m => m.CustomerRoleId == 700).ToList();
        roleAMappings.Count.ShouldEqual(customerCount);

        var roleACustomerIds = roleAMappings
            .Select(m => m.CustomerId).OrderBy(x => x).ToList();
        Assert.That(roleACustomerIds,
            Is.EqualTo(Enumerable.Range(startId, customerCount).ToList()));

        // Verify role B has exactly 50 mappings with correct customer IDs.
        var roleBMappings = allMappings.Where(m => m.CustomerRoleId == 701).ToList();
        roleBMappings.Count.ShouldEqual(customerCount);

        var roleBCustomerIds = roleBMappings
            .Select(m => m.CustomerId).OrderBy(x => x).ToList();
        Assert.That(roleBCustomerIds,
            Is.EqualTo(Enumerable.Range(startId, customerCount).ToList()));

        // Cache should have been invalidated.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN),
            Times.Once);
    }

    // ------------------------------------------------------------------
    // 2. Running the task twice yields the same final state (idempotent)
    // ------------------------------------------------------------------

    [Test]
    public async Task Run_Integration_IsIdempotent()
    {
        // Arrange: Seed 20 customers and 1 role.
        const int startId = 6000;
        const int customerCount = 20;

        SeedCustomers(startId, customerCount);
        SeedRoleWithRuleSet(702, "IdempotentRole", 802);

        var query = DbContext.Customers
            .Where(c => c.Id >= startId && c.Id < startId + customerCount);
        SetupProcessFilter(query);

        var ctx1 = CreateTaskContext();
        var ctx2 = CreateTaskContext();

        // Act: Run the task twice. The second run should delete old mappings
        // (via InMemory-compatible RemoveRange) and re-insert fresh ones.
        await _sut.Run(ctx1);

        // Snapshot after first run.
        var afterFirstRun = await DbContext.CustomerRoleMappings
            .CountAsync(x => x.IsSystemMapping && x.CustomerRoleId == 702);
        afterFirstRun.ShouldEqual(customerCount);

        // Run again.
        await _sut.Run(ctx2);

        // Assert: Final state should match a single run (no duplicate mappings).
        var afterSecondRun = await DbContext.CustomerRoleMappings
            .CountAsync(x => x.IsSystemMapping && x.CustomerRoleId == 702);
        afterSecondRun.ShouldEqual(customerCount);

        // Verify the customer IDs are correct.
        var customerIds = await DbContext.CustomerRoleMappings
            .Where(x => x.IsSystemMapping && x.CustomerRoleId == 702)
            .Select(x => x.CustomerId)
            .OrderBy(x => x)
            .ToListAsync();

        Assert.That(customerIds,
            Is.EqualTo(Enumerable.Range(startId, customerCount).ToList()));
    }

    // ------------------------------------------------------------------
    // 3. CustomerRoleIds parameter scopes the delete phase only
    // ------------------------------------------------------------------

    [Test]
    public async Task Run_Integration_ParameterizedExecution()
    {
        // Arrange: Two active roles (A and B) with 15 customers.
        // Pre-seed system mappings for role B to verify the delete scope.
        const int startId = 7000;
        const int customerCount = 15;

        SeedCustomers(startId, customerCount);
        var roleA = SeedRoleWithRuleSet(703, "ParamRoleA", 803);
        var roleB = SeedRoleWithRuleSet(704, "ParamRoleB", 804);

        // Pre-seed system mappings for role B (simulating a previous task run).
        for (int i = 0; i < customerCount; i++)
        {
            SeedSystemMapping(startId + i, 704);
        }

        // Verify pre-seeded mappings exist.
        var preSeedCount = await DbContext.CustomerRoleMappings
            .CountAsync(x => x.IsSystemMapping && x.CustomerRoleId == 704);
        preSeedCount.ShouldEqual(customerCount);

        var query = DbContext.Customers
            .Where(c => c.Id >= startId && c.Id < startId + customerCount);
        SetupProcessFilter(query);

        // Run with CustomerRoleIds = role A's ID only.
        // The delete phase should only remove role A's system mappings (none exist).
        // The insert phase processes ALL active roles regardless of the parameter.
        var parameters = new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = roleA.Id.ToString()
        };
        var ctx = CreateTaskContext(parameters);

        // Act
        await _sut.Run(ctx);

        // Assert: Role A should have 15 new mappings.
        var roleACount = await DbContext.CustomerRoleMappings
            .CountAsync(x => x.IsSystemMapping && x.CustomerRoleId == 703);
        roleACount.ShouldEqual(customerCount);

        // Role B should have 30 mappings: 15 pre-seeded (not deleted because
        // CustomerRoleIds only targeted role A) + 15 newly inserted.
        // This documents the production behavior: the CustomerRoleIds parameter
        // scopes only the delete phase, not the insert phase.
        var roleBCount = await DbContext.CustomerRoleMappings
            .CountAsync(x => x.IsSystemMapping && x.CustomerRoleId == 704);
        roleBCount.ShouldEqual(customerCount * 2);
    }
}
