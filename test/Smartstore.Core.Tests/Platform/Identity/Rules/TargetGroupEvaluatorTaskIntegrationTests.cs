using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Core.Security;
using Smartstore.Test.Common;

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
public class TargetGroupEvaluatorTaskIntegrationTests : TargetGroupEvaluatorTaskTestBase
{
    [SetUp]
    public void IntegrationSetUp()
    {
        // Return a valid FilterExpressionGroup for any rule set.
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync(new FilterExpressionGroup(typeof(Customer)));
    }

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
        SeedRoleWithRuleSet(700, "IntegrationRoleA", 800);
        SeedRoleWithRuleSet(701, "IntegrationRoleB", 801);

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
    // 3. CustomerRoleIds parameter scopes both delete and insert phases
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
        SeedRoleWithRuleSet(704, "ParamRoleB", 804);

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
        // Both the delete and insert phases are scoped to role A only.
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

        // Role B should retain only its 15 pre-seeded mappings: the delete phase
        // did not target it (CustomerRoleIds = role A only), and the insert phase
        // is now also scoped to the specified roles, matching legacy behavior.
        var roleBCount = await DbContext.CustomerRoleMappings
            .CountAsync(x => x.IsSystemMapping && x.CustomerRoleId == 704);
        roleBCount.ShouldEqual(customerCount);
    }
}
