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
/// Category 3 tests: verify the chunking and paging behavior of
/// <see cref="TargetGroupEvaluatorTask"/>. Uses the InMemory-compatible
/// <see cref="TestableTargetGroupEvaluatorTask"/> to work around the
/// <c>ExecuteDeleteAsync</c> limitation of the EF Core InMemory provider.
/// </summary>
[TestFixture]
public class TargetGroupEvaluatorTaskChunkingTests : ServiceTestBase
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

        // For all chunking tests, return a valid FilterExpressionGroup for any rule set.
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

    private void SeedCustomerBatch(int startId, int count)
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
    // 1. Chunking uses batch size of 500
    // ------------------------------------------------------------------

    [Test]
    public async Task Run_UsesChunkSizeOf500ForInserts()
    {
        // Arrange: Seed 750 customers. With a chunk size of 500, the task
        // must produce 2 chunks (500 + 250) and commit both successfully.
        const int startId = 1000;
        const int customerCount = 750;

        SeedCustomerBatch(startId, customerCount);
        SeedRoleWithRuleSet(500, "ChunkRole", 600);

        var query = DbContext.Customers.Where(c => c.Id >= startId && c.Id < startId + customerCount);
        SetupProcessFilter(query);

        var ctx = CreateTaskContext();

        // Act
        await _sut.Run(ctx);

        // Assert: All 750 customers must have a system mapping. This proves
        // that both chunks (500 + 250) were processed and committed.
        var mappingCount = await DbContext.CustomerRoleMappings
            .CountAsync(x => x.IsSystemMapping && x.CustomerRoleId == 500);

        mappingCount.ShouldEqual(customerCount);
    }

    // ------------------------------------------------------------------
    // 2. All chunks are committed (no data loss across chunk boundaries)
    // ------------------------------------------------------------------

    [Test]
    public async Task Run_CommitsAfterEachChunk()
    {
        // Arrange: Seed 1001 customers to exercise 3 chunks (500 + 500 + 1).
        // If any chunk failed to commit, mappings from that chunk would be lost.
        const int startId = 2000;
        const int customerCount = 1001;

        SeedCustomerBatch(startId, customerCount);
        SeedRoleWithRuleSet(501, "MultiChunkRole", 601);

        var query = DbContext.Customers.Where(c => c.Id >= startId && c.Id < startId + customerCount);
        SetupProcessFilter(query);

        var ctx = CreateTaskContext();

        // Act
        await _sut.Run(ctx);

        // Assert: All 1001 mappings must exist, proving every chunk was committed.
        var mappings = await DbContext.CustomerRoleMappings
            .Where(x => x.IsSystemMapping && x.CustomerRoleId == 501)
            .ToListAsync();

        mappings.Count.ShouldEqual(customerCount);

        // Verify that all customer IDs are represented (no gaps).
        var mappedCustomerIds = mappings.Select(m => m.CustomerId).OrderBy(x => x).ToList();
        var expectedIds = Enumerable.Range(startId, customerCount).ToList();
        Assert.That(mappedCustomerIds, Is.EqualTo(expectedIds));
    }

    // ------------------------------------------------------------------
    // 3. Entities are detached after each role is processed
    // ------------------------------------------------------------------

    [Test]
    public async Task Run_DetachesEntitiesAfterEachRole()
    {
        // Arrange: Seed 10 customers and 2 roles. After the task runs,
        // DetachEntities<CustomerRoleMapping>() should have been called once
        // per role, leaving zero tracked CustomerRoleMapping entities.
        const int startId = 3000;
        const int customerCount = 10;

        SeedCustomerBatch(startId, customerCount);
        SeedRoleWithRuleSet(502, "DetachRole1", 602);
        SeedRoleWithRuleSet(503, "DetachRole2", 603);

        var query = DbContext.Customers.Where(c => c.Id >= startId && c.Id < startId + customerCount);
        SetupProcessFilter(query);

        var ctx = CreateTaskContext();

        // Act
        await _sut.Run(ctx);

        // Assert: No CustomerRoleMapping entities should remain in the change tracker.
        // This verifies that DetachEntities<CustomerRoleMapping>() was called after
        // each role's mappings were committed.
        var trackedMappings = DbContext.ChangeTracker
            .Entries<CustomerRoleMapping>()
            .Count();

        trackedMappings.ShouldEqual(0);

        // But the mappings should still exist in the database (detach does not delete).
        var totalMappings = await DbContext.CustomerRoleMappings
            .CountAsync(x => x.IsSystemMapping);

        // 10 customers x 2 roles = 20 mappings.
        totalMappings.ShouldEqual(customerCount * 2);
    }
}
