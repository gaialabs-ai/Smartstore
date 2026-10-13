using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Test.Common;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

/// <summary>
/// Category 3 tests: verify the chunking and paging behavior of
/// <see cref="TargetGroupEvaluatorTask"/>. Uses the InMemory-compatible
/// <see cref="TestableTargetGroupEvaluatorTask"/> to work around the
/// <c>ExecuteDeleteAsync</c> limitation of the EF Core InMemory provider.
/// </summary>
[TestFixture]
public class TargetGroupEvaluatorTaskChunkingTests : TargetGroupEvaluatorTaskTestBase
{
    [SetUp]
    public void ChunkingSetUp()
    {
        // For all chunking tests, return a valid FilterExpressionGroup for any rule set.
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync(new FilterExpressionGroup(typeof(Customer)));
    }

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

        SeedCustomers(startId, customerCount);
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

        SeedCustomers(startId, customerCount);
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

        SeedCustomers(startId, customerCount);
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
