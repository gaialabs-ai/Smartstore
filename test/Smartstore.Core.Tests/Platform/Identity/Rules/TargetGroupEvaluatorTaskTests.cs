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
using Smartstore;
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
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

/// <summary>
/// Regression and behavior tests for <see cref="TargetGroupEvaluatorTask"/>.
///
/// Uses a SQLite in-memory database (via <see cref="TestSqliteDbFactory"/>) instead of the project-wide
/// InMemory provider, because the task's very first operation is <c>ExecuteDeleteAsync</c>, which the
/// EF Core InMemory provider does not support.
///
/// Inherits <see cref="ServiceTestBase"/> purely to get its one-time Autofac/engine bootstrap
/// (<see cref="Smartstore.Engine.EngineContext.Current"/> must be initialized before
/// <c>UseDbFactory()</c> can resolve <c>SmartConfiguration</c>); the InMemory <see cref="SmartDbContext"/>
/// that base class builds is intentionally unused here - a separate SQLite-backed context is built fresh
/// for every test via <see cref="SetUpSqliteContext"/>/<see cref="TearDownSqliteContext"/>.
///
/// <see cref="IRuleService.CreateExpressionGroupAsync"/> and <see cref="ITargetGroupService.ProcessFilter"/>
/// are mocked: each active <see cref="RuleSetEntity"/> is mapped (by its database-generated Id, stashed in
/// the returned <see cref="FilterExpressionGroup"/>'s <c>Value</c> property) to a fixed set of customer ids
/// via <see cref="_ruleSetCustomerMap"/>, configured per test. The mocked <c>ProcessFilter</c> result is
/// backed by a real EF Core queryable (<c>_db.Customers.Where(...)</c>) so that <see cref="FastPager{T}"/>'s
/// internal <c>ToListAsync()</c> call (which requires <see cref="IAsyncEnumerable{T}"/>) works correctly.
/// </summary>
[TestFixture]
public class TargetGroupEvaluatorTaskTests : ServiceTestBase
{
    private SqliteConnection _connection;
    private SmartDbContext _db;

    private Mock<ICacheManager> _cacheMock;
    private Mock<IRuleService> _ruleServiceMock;
    private Mock<ITargetGroupService> _targetGroupServiceMock;
    private Mock<IRuleProviderFactory> _ruleProviderFactoryMock;

    // RuleSetEntity.Id -> customer ids that the (mocked) rule evaluation should "match".
    private readonly Dictionary<int, int[]> _ruleSetCustomerMap = new();

    [SetUp]
    public void SetUpSqliteContext()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        DataSettings.Instance = new DataSettings
        {
            AppVersion = SmartstoreVersion.Version,
            ConnectionString = "DataSource=:memory:",
            TenantName = "Default",
            TenantRoot = null,
            DbFactory = new TestSqliteDbFactory(_connection)
        };
        DataSettings.SetTestMode(true);

        var optionsBuilder = new DbContextOptionsBuilder<SmartDbContext>()
            .UseDbFactory(factoryBuilder =>
            {
                factoryBuilder.AddModelAssemblies(new[] { typeof(SmartDbContext).Assembly });
            });

        _db = new SmartDbContext((DbContextOptions<SmartDbContext>)optionsBuilder.Options);
        _db.Database.EnsureCreated();

        _ruleSetCustomerMap.Clear();

        _cacheMock = new Mock<ICacheManager>();
        _cacheMock.Setup(x => x.RemoveByPatternAsync(It.IsAny<string>())).ReturnsAsync(0L);

        _targetGroupServiceMock = new Mock<ITargetGroupService>();
        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(It.IsAny<FilterExpression[]>(), It.IsAny<LogicalRuleOperator>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((FilterExpression[] filters, LogicalRuleOperator _, int pageIndex, int pageSize) =>
            {
                var ruleSetId = (int)filters[0].Value;
                var customerIds = _ruleSetCustomerMap.TryGetValue(ruleSetId, out var ids) ? ids : Array.Empty<int>();

                return _db.Customers
                    .Where(c => customerIds.Contains(c.Id))
                    .ToPagedList(pageIndex, pageSize);
            });

        _ruleServiceMock = new Mock<IRuleService>();
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()))
            .ReturnsAsync((RuleSetEntity ruleSet, IRuleVisitor _, bool _) =>
                (IRuleExpressionGroup)new FilterExpressionGroup(typeof(Customer)) { Value = ruleSet.Id });

        _ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();
        _ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, It.IsAny<object>()))
            .Returns(_targetGroupServiceMock.Object);
    }

    [TearDown]
    public void TearDownSqliteContext()
    {
        // NOTE: DataSettings.Reload() is intentionally NOT called here (per-test); the base class's
        // [OneTimeTearDown] already does that once after all tests in this fixture have completed.
        _db?.Dispose();
        _connection?.Dispose();
    }

    private TargetGroupEvaluatorTask CreateTask()
        => new(_db, _cacheMock.Object, _ruleServiceMock.Object, _ruleProviderFactoryMock.Object);

    private static TaskExecutionContext CreateContext(IDictionary<string, string> parameters = null)
    {
        var taskStoreMock = new Mock<ITaskStore>();
        taskStoreMock.Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>())).Returns(Task.CompletedTask);

        var asyncStateMock = new Mock<IAsyncState>();
        asyncStateMock.Setup(x => x.GetAsync<TaskDescriptor>(It.IsAny<string>())).ReturnsAsync((TaskDescriptor)null);

        var componentContextMock = new Mock<IComponentContext>();

        var executionInfo = new TaskExecutionInfo
        {
            Id = 1,
            TaskDescriptorId = 1,
            Task = new TaskDescriptor { Id = 1, Name = nameof(TargetGroupEvaluatorTask) }
        };

        return new TaskExecutionContext(
            taskStoreMock.Object,
            asyncStateMock.Object,
            new DefaultHttpContext(),
            componentContextMock.Object,
            executionInfo,
            parameters);
    }

    private Customer AddCustomer()
    {
        var customer = new Customer { CustomerGuid = Guid.NewGuid() };
        _db.Customers.Add(customer);
        return customer;
    }

    private CustomerRole AddRole(string name, bool active)
    {
        var role = new CustomerRole { Name = name, Active = active, SystemName = name };
        _db.CustomerRoles.Add(role);
        return role;
    }

    private RuleSetEntity AddRuleSet(bool isActive)
    {
        var ruleSet = new RuleSetEntity
        {
            IsActive = isActive,
            Scope = RuleScope.Customer,
            LogicalOperator = LogicalRuleOperator.And,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };
        _db.RuleSets.Add(ruleSet);
        return ruleSet;
    }

    // ------------------------------------------------------------------
    // Scenario 1: full run, two active roles each with an active rule set, disjoint customer sets.
    // ------------------------------------------------------------------
    [Test]
    public async Task Run_FullRun_AssignsMappingsForAllActiveRoles()
    {
        var role1 = AddRole("Role1", true);
        var role2 = AddRole("Role2", true);
        var ruleSet1 = AddRuleSet(true);
        var ruleSet2 = AddRuleSet(true);
        role1.RuleSets.Add(ruleSet1);
        role2.RuleSets.Add(ruleSet2);

        var c1 = AddCustomer();
        var c2 = AddCustomer();
        var c3 = AddCustomer();
        var c4 = AddCustomer();
        await _db.SaveChangesAsync();

        _ruleSetCustomerMap[ruleSet1.Id] = [c1.Id, c2.Id];
        _ruleSetCustomerMap[ruleSet2.Id] = [c3.Id, c4.Id];

        var task = CreateTask();
        await task.Run(CreateContext(), CancellationToken.None);

        var role1Ids = await _db.CustomerRoleMappings.Where(x => x.CustomerRoleId == role1.Id).Select(x => x.CustomerId).ToListAsync();
        var role2Ids = await _db.CustomerRoleMappings.Where(x => x.CustomerRoleId == role2.Id).Select(x => x.CustomerId).ToListAsync();

        Assert.That(role1Ids.OrderBy(x => x), Is.EqualTo(new[] { c1.Id, c2.Id }.OrderBy(x => x)));
        Assert.That(role2Ids.OrderBy(x => x), Is.EqualTo(new[] { c3.Id, c4.Id }.OrderBy(x => x)));
        Assert.That(role1Ids.All(x => x != 0), Is.True);

        var allMappings = await _db.CustomerRoleMappings.ToListAsync();
        Assert.That(allMappings, Has.All.Matches<CustomerRoleMapping>(x => x.IsSystemMapping));
    }

    // ------------------------------------------------------------------
    // Scenario 2 (regression test): scoped run via "CustomerRoleIds" parameter must only
    // touch (delete + recompute) the targeted role, leaving other active roles untouched.
    //
    // Pre-fix, TargetGroupEvaluatorTask filtered only the delete query by CustomerRoleIds, but
    // NOT the role-selection/recompute query, so every active role (with an active rule set) got
    // recomputed - silently re-adding mappings for roles that were never part of the request.
    // ------------------------------------------------------------------
    [Test]
    public async Task Run_ScopedByCustomerRoleIds_OnlyTouchesTargetedRole()
    {
        var targetRole = AddRole("TargetRole", true);
        var otherRole = AddRole("OtherRole", true);
        var targetRuleSet = AddRuleSet(true);
        var otherRuleSet = AddRuleSet(true);
        targetRole.RuleSets.Add(targetRuleSet);
        otherRole.RuleSets.Add(otherRuleSet);

        var targetCustomer1 = AddCustomer();
        var targetCustomer2 = AddCustomer();
        var otherNewCustomer = AddCustomer();
        var preExistingCustomer = AddCustomer();
        await _db.SaveChangesAsync();

        _ruleSetCustomerMap[targetRuleSet.Id] = [targetCustomer1.Id, targetCustomer2.Id];
        _ruleSetCustomerMap[otherRuleSet.Id] = [otherNewCustomer.Id];

        // Simulate a mapping left over from a previous run for the role that is NOT targeted.
        // If the recompute query is (incorrectly) not scoped by CustomerRoleIds, this role gets
        // recomputed too, and otherNewCustomer gets inserted alongside this leftover row (since
        // the delete query - correctly scoped - never removes it).
        _db.CustomerRoleMappings.Add(new CustomerRoleMapping
        {
            CustomerId = preExistingCustomer.Id,
            CustomerRoleId = otherRole.Id,
            IsSystemMapping = true
        });
        await _db.SaveChangesAsync();

        var task = CreateTask();
        var ctx = CreateContext(new Dictionary<string, string> { ["CustomerRoleIds"] = targetRole.Id.ToString() });

        await task.Run(ctx, CancellationToken.None);

        var targetRoleIds = await _db.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == targetRole.Id)
            .Select(x => x.CustomerId)
            .ToListAsync();

        var otherRoleIds = await _db.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == otherRole.Id)
            .Select(x => x.CustomerId)
            .ToListAsync();

        // The targeted role was recomputed as expected.
        Assert.That(targetRoleIds.OrderBy(x => x), Is.EqualTo(new[] { targetCustomer1.Id, targetCustomer2.Id }.OrderBy(x => x)));

        // The non-targeted role must be left completely untouched: still exactly the pre-existing
        // mapping, and NOT also containing otherNewCustomer (which would only appear if the
        // recompute query incorrectly re-evaluated this role too).
        Assert.That(otherRoleIds, Is.EqualTo(new[] { preExistingCustomer.Id }));
        Assert.That(otherRoleIds, Does.Not.Contain(otherNewCustomer.Id));

        // Direct proof the non-targeted role's rule set was never evaluated at all.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(It.Is<RuleSetEntity>(rs => rs.Id == otherRuleSet.Id), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // Scenario 3: inactive roles and inactive rule sets must be skipped entirely, not counted.
    // ------------------------------------------------------------------
    [Test]
    public async Task Run_SkipsInactiveRolesAndInactiveRuleSets()
    {
        // Role is active, but has no active rule set at all -> excluded entirely.
        var roleWithOnlyInactiveRuleSet = AddRole("InactiveRuleSetRole", true);
        var inactiveRuleSet = AddRuleSet(false);
        roleWithOnlyInactiveRuleSet.RuleSets.Add(inactiveRuleSet);

        // Role has an active rule set, but the role itself is inactive -> excluded entirely.
        var inactiveRole = AddRole("InactiveRole", false);
        var ruleSetOfInactiveRole = AddRuleSet(true);
        inactiveRole.RuleSets.Add(ruleSetOfInactiveRole);

        // Control role: active, with one active and one inactive rule set -> only the active
        // rule set's matches should be used.
        var controlRole = AddRole("ControlRole", true);
        var activeRuleSet = AddRuleSet(true);
        var inactiveRuleSetOfControl = AddRuleSet(false);
        controlRole.RuleSets.Add(activeRuleSet);
        controlRole.RuleSets.Add(inactiveRuleSetOfControl);

        var excludedCustomer1 = AddCustomer();
        var excludedCustomer2 = AddCustomer();
        var includedCustomer = AddCustomer();
        var excludedFromInactiveRuleSet = AddCustomer();
        await _db.SaveChangesAsync();

        _ruleSetCustomerMap[inactiveRuleSet.Id] = [excludedCustomer1.Id];
        _ruleSetCustomerMap[ruleSetOfInactiveRole.Id] = [excludedCustomer2.Id];
        _ruleSetCustomerMap[activeRuleSet.Id] = [includedCustomer.Id];
        _ruleSetCustomerMap[inactiveRuleSetOfControl.Id] = [excludedFromInactiveRuleSet.Id];

        var task = CreateTask();
        await task.Run(CreateContext(), CancellationToken.None);

        var allMappings = await _db.CustomerRoleMappings.ToListAsync();

        Assert.That(allMappings.Any(x => x.CustomerRoleId == roleWithOnlyInactiveRuleSet.Id), Is.False);
        Assert.That(allMappings.Any(x => x.CustomerRoleId == inactiveRole.Id), Is.False);

        var controlIds = allMappings.Where(x => x.CustomerRoleId == controlRole.Id).Select(x => x.CustomerId).ToList();
        Assert.That(controlIds, Is.EqualTo(new[] { includedCustomer.Id }));

        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(It.Is<RuleSetEntity>(rs => rs.Id == inactiveRuleSetOfControl.Id), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // Scenario 4a: cache invalidation must NOT fire when nothing changed (no roles matched, nothing
    // deleted, nothing added).
    // ------------------------------------------------------------------
    [Test]
    public async Task Run_DoesNotInvalidateCache_WhenNothingChanged()
    {
        // No roles, no rule sets, no pre-existing mappings at all.
        var task = CreateTask();
        await task.Run(CreateContext(), CancellationToken.None);

        _cacheMock.Verify(x => x.RemoveByPatternAsync(It.IsAny<string>()), Times.Never);
    }

    // ------------------------------------------------------------------
    // Scenario 4b: cache invalidation must fire when mappings were added or deleted.
    // ------------------------------------------------------------------
    [Test]
    public async Task Run_InvalidatesCache_WhenMappingsChanged()
    {
        var role = AddRole("Role", true);
        var ruleSet = AddRuleSet(true);
        role.RuleSets.Add(ruleSet);
        var customer = AddCustomer();
        await _db.SaveChangesAsync();

        _ruleSetCustomerMap[ruleSet.Id] = [customer.Id];

        var task = CreateTask();
        await task.Run(CreateContext(), CancellationToken.None);

        _cacheMock.Verify(x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN), Times.Once);
    }

    // ------------------------------------------------------------------
    // Scenario 5: an already-cancelled token must cause an early exit, before any rule evaluation
    // or insert happens.
    //
    // Run()'s very first operation is "await deleteQuery.ExecuteDeleteAsync(cancelToken)". EF Core's
    // relational command execution checks the token up front and - when it is already cancelled -
    // throws immediately rather than running the command, so Run() propagates a cancellation
    // exception instead of returning normally. The important, test-worthy guarantee is the second
    // half of that: no row is ever inserted and no rule set is ever evaluated.
    // ------------------------------------------------------------------
    [Test]
    public async Task Run_WithAlreadyCancelledToken_ExitsEarlyWithoutInserts()
    {
        var role = AddRole("Role", true);
        var ruleSet = AddRuleSet(true);
        role.RuleSets.Add(ruleSet);
        var customer = AddCustomer();
        await _db.SaveChangesAsync();

        _ruleSetCustomerMap[ruleSet.Id] = [customer.Id];

        var task = CreateTask();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await task.Run(CreateContext(), cts.Token));

        var mappings = await _db.CustomerRoleMappings.ToListAsync();
        Assert.That(mappings, Is.Empty);

        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()),
            Times.Never);
    }
}
