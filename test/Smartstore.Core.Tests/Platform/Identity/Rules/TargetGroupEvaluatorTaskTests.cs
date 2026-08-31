using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
public class TargetGroupEvaluatorTaskTests
{
    private SmartDbContext _db;
    private Mock<ICacheManager> _cacheMock;
    private Mock<IRuleService> _ruleServiceMock;
    private Mock<ITargetGroupService> _targetGroupServiceMock;
    private Mock<IRuleProviderFactory> _ruleProviderFactoryMock;

    private sealed class TargetGroupTestDbFactory : TestDbFactory
    {
        public override DbContextOptionsBuilder ConfigureDbContext(DbContextOptionsBuilder builder, string connectionString)
        {
            return builder
                .UseInMemoryDatabase("Test-TargetGroupEvaluatorTask")
                .ConfigureWarnings(b => b.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dataSettings = new DataSettings
        {
            AppVersion = SmartstoreVersion.Version,
            ConnectionString = "Test-TargetGroupEvaluatorTask",
            TenantName = "Default",
            TenantRoot = null,
            DbFactory = new TargetGroupTestDbFactory()
        };

        DataSettings.Instance = dataSettings;
        DataSettings.SetTestMode(true);

        var builder = new DbContextOptionsBuilder<SmartDbContext>()
            .UseDbFactory(factoryBuilder =>
            {
                factoryBuilder.AddModelAssemblies(new[] { typeof(SmartDbContext).Assembly });
            });

        _db = new SmartDbContext((DbContextOptions<SmartDbContext>)builder.Options);
        _db.Database.EnsureCreated();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _db.Dispose();
        DataSettings.Reload();
    }

    [SetUp]
    public async Task SetUp()
    {
        await _db.Database.EnsureDeletedAsync();
        await _db.Database.EnsureCreatedAsync();

        _cacheMock = new Mock<ICacheManager>();
        _cacheMock.Setup(x => x.RemoveByPatternAsync(It.IsAny<string>())).ReturnsAsync(0L);

        _ruleServiceMock = new Mock<IRuleService>();
        _targetGroupServiceMock = new Mock<ITargetGroupService>();
        _ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();
        _ruleProviderFactoryMock
            .Setup(x => x.GetProvider<ITargetGroupService>(RuleScope.Customer, null))
            .Returns(_targetGroupServiceMock.Object);
    }

    private TargetGroupEvaluatorTask CreateTask()
    {
        return new TargetGroupEvaluatorTask(
            _db,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);
    }

    private TaskExecutionContext CreateContext(IDictionary<string, string> parameters = null)
    {
        var taskStoreMock = new Mock<ITaskStore>();
        taskStoreMock
            .Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Returns(Task.CompletedTask);

        var asyncStateMock = new Mock<IAsyncState>();

        var httpContextMock = new Mock<HttpContext>();
        var componentContextMock = new Mock<IComponentContext>();

        var taskDescriptor = new TaskDescriptor
        {
            Id = 1,
            Name = "test"
        };

        var executionInfo = new TaskExecutionInfo
        {
            Id = 1,
            TaskDescriptorId = 1,
            IsRunning = true,
            StartedOnUtc = DateTime.UtcNow,
            MachineName = "Test",
            Task = taskDescriptor
        };

        return new TaskExecutionContext(
            taskStoreMock.Object,
            asyncStateMock.Object,
            httpContextMock.Object,
            componentContextMock.Object,
            executionInfo,
            parameters);
    }

    private async Task<CustomerRole> SeedRoleWithRuleSetAsync(int roleId, int ruleSetId, bool roleActive = true, bool ruleSetActive = true)
    {
        var role = new CustomerRole { Id = roleId, Name = $"Role{roleId}", Active = roleActive };
        var ruleSet = new RuleSetEntity
        {
            Id = ruleSetId,
            IsActive = ruleSetActive,
            Scope = RuleScope.Customer,
            LogicalOperator = LogicalRuleOperator.And,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };

        _db.CustomerRoles.Add(role);
        _db.RuleSets.Add(ruleSet);
        await _db.SaveChangesAsync();

        role.RuleSets.Add(ruleSet);
        await _db.SaveChangesAsync();

        return role;
    }

    private async Task SeedSystemMappingAsync(int customerId, int roleId)
    {
        // We need a customer role and a placeholder customer entity for FK.
        // Ensure role exists (caller must seed roles first).
        _db.CustomerRoleMappings.Add(new CustomerRoleMapping
        {
            CustomerId = customerId,
            CustomerRoleId = roleId,
            IsSystemMapping = true
        });
        await _db.SaveChangesAsync();
    }

    // ----------------------------------------------------------------
    // T1 - All system mappings deleted when no active roles with rulesets
    // ----------------------------------------------------------------
    [Test]
    public async Task T1_AllSystemMappingsDeleted_WhenNoActiveRolesWithRulesets()
    {
        // Seed 3 dummy customer roles (inactive) with system mappings.
        for (int i = 1; i <= 3; i++)
        {
            _db.CustomerRoles.Add(new CustomerRole { Id = i, Name = $"Role{i}", Active = false });
        }
        await _db.SaveChangesAsync();

        for (int i = 1; i <= 3; i++)
        {
            _db.CustomerRoleMappings.Add(new CustomerRoleMapping
            {
                CustomerId = i,
                CustomerRoleId = i,
                IsSystemMapping = true
            });
        }
        await _db.SaveChangesAsync();

        Assert.That(await _db.CustomerRoleMappings.CountAsync(x => x.IsSystemMapping), Is.EqualTo(3));

        var task = CreateTask();
        var ctx = CreateContext();
        await task.Run(ctx, CancellationToken.None);

        Assert.That(await _db.CustomerRoleMappings.CountAsync(x => x.IsSystemMapping), Is.EqualTo(0));
    }

    // ----------------------------------------------------------------
    // T2 - CustomerRoleIds parameter scopes both delete and load
    // ----------------------------------------------------------------
    [Test]
    public async Task T2_CustomerRoleIds_ScopesDeleteAndLoad()
    {
        var roleA = await SeedRoleWithRuleSetAsync(roleId: 1, ruleSetId: 1);
        var roleB = await SeedRoleWithRuleSetAsync(roleId: 2, ruleSetId: 2);

        // Seed one system mapping each.
        _db.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = 101, CustomerRoleId = 1, IsSystemMapping = true });
        _db.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = 102, CustomerRoleId = 2, IsSystemMapping = true });
        await _db.SaveChangesAsync();

        // CreateExpressionGroupAsync returns null -> no new mappings for either role.
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()))
            .ReturnsAsync((IRuleExpressionGroup)null);

        var task = CreateTask();
        var ctx = CreateContext(new Dictionary<string, string> { ["CustomerRoleIds"] = "1" });
        await task.Run(ctx, CancellationToken.None);

        // roleA's mapping should be deleted; roleB's should still exist.
        var mappings = await _db.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToListAsync();
        Assert.That(mappings.Any(m => m.CustomerRoleId == 1), Is.False, "roleA mapping should be deleted");
        Assert.That(mappings.Any(m => m.CustomerRoleId == 2), Is.True, "roleB mapping should still exist");

        // CreateExpressionGroupAsync should only have been called for roleA's ruleset (Id=1).
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == 1),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()),
            Times.Once);

        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == 2),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    // ----------------------------------------------------------------
    // T3 - Matching customers result in system mappings being created
    // ----------------------------------------------------------------
    [Test]
    public async Task T3_MatchingCustomers_MappingsCreated()
    {
        await SeedRoleWithRuleSetAsync(roleId: 1, ruleSetId: 1);

        // Seed 3 customers.
        for (int i = 1; i <= 3; i++)
        {
            _db.Customers.Add(new Customer { Id = i, IsSystemAccount = false });
        }
        await _db.SaveChangesAsync();

        var expressionGroup = new FilterExpressionGroup(typeof(Customer));

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()))
            .ReturnsAsync(expressionGroup);

        // ProcessFilter returns all 3 customers via EF-backed queryable.
        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(_db.Customers.AsNoTracking().ToPagedList(0, 500));

        var task = CreateTask();
        var ctx = CreateContext();
        await task.Run(ctx, CancellationToken.None);

        Assert.That(await _db.CustomerRoleMappings.CountAsync(x => x.IsSystemMapping), Is.EqualTo(3));
    }

    // ----------------------------------------------------------------
    // T4 - No matching customers → no mappings created
    // ----------------------------------------------------------------
    [Test]
    public async Task T4_NoMatchingCustomers_NoMappingsCreated()
    {
        await SeedRoleWithRuleSetAsync(roleId: 1, ruleSetId: 1);

        var expressionGroup = new FilterExpressionGroup(typeof(Customer));

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()))
            .ReturnsAsync(expressionGroup);

        // Empty EF-backed queryable (always returns 0 rows).
        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(_db.Customers.Where(x => x.Id < 0).AsNoTracking().ToPagedList(0, 500));

        var task = CreateTask();
        var ctx = CreateContext();
        await task.Run(ctx, CancellationToken.None);

        Assert.That(await _db.CustomerRoleMappings.CountAsync(x => x.IsSystemMapping), Is.EqualTo(0));
    }

    // ----------------------------------------------------------------
    // T5 - Inactive role is skipped
    // ----------------------------------------------------------------
    [Test]
    public async Task T5_InactiveRole_IsSkipped()
    {
        await SeedRoleWithRuleSetAsync(roleId: 1, ruleSetId: 1, roleActive: false);

        var task = CreateTask();
        var ctx = CreateContext();
        await task.Run(ctx, CancellationToken.None);

        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()),
            Times.Never);

        Assert.That(await _db.CustomerRoleMappings.CountAsync(x => x.IsSystemMapping), Is.EqualTo(0));
    }

    // ----------------------------------------------------------------
    // T6 - Null from CreateExpressionGroupAsync → no mappings, ProcessFilter never called
    // ----------------------------------------------------------------
    [Test]
    public async Task T6_NullExpressionGroup_NoMappings_ProcessFilterNotCalled()
    {
        await SeedRoleWithRuleSetAsync(roleId: 1, ruleSetId: 1);

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()))
            .ReturnsAsync((IRuleExpressionGroup)null);

        var task = CreateTask();
        var ctx = CreateContext();
        await task.Run(ctx, CancellationToken.None);

        Assert.That(await _db.CustomerRoleMappings.CountAsync(x => x.IsSystemMapping), Is.EqualTo(0));

        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Never);
    }

    // ----------------------------------------------------------------
    // T7 - Cache invalidated when mappings are added
    // ----------------------------------------------------------------
    [Test]
    public async Task T7_CacheInvalidated_WhenMappingsAdded()
    {
        await SeedRoleWithRuleSetAsync(roleId: 1, ruleSetId: 1);

        _db.Customers.Add(new Customer { Id = 1, IsSystemAccount = false });
        await _db.SaveChangesAsync();

        var expressionGroup = new FilterExpressionGroup(typeof(Customer));

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()))
            .ReturnsAsync(expressionGroup);

        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(_db.Customers.AsNoTracking().ToPagedList(0, 500));

        var task = CreateTask();
        var ctx = CreateContext();
        await task.Run(ctx, CancellationToken.None);

        _cacheMock.Verify(x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN), Times.Once);
    }

    // ----------------------------------------------------------------
    // T8 - Cache NOT invalidated when nothing changes
    // ----------------------------------------------------------------
    [Test]
    public async Task T8_CacheNotInvalidated_WhenNothingChanges()
    {
        // No active roles, no pre-existing system mappings.
        _db.CustomerRoles.Add(new CustomerRole { Id = 1, Name = "InactiveRole", Active = false });
        await _db.SaveChangesAsync();

        var task = CreateTask();
        var ctx = CreateContext();
        await task.Run(ctx, CancellationToken.None);

        _cacheMock.Verify(x => x.RemoveByPatternAsync(It.IsAny<string>()), Times.Never);
    }

    // ----------------------------------------------------------------
    // T9 - CancellationToken checked before CreateExpressionGroupAsync for each ruleset
    // ----------------------------------------------------------------
    [Test]
    public async Task T9_CancellationToken_CheckedBeforeCreateExpressionGroup()
    {
        await SeedRoleWithRuleSetAsync(roleId: 1, ruleSetId: 1);
        await SeedRoleWithRuleSetAsync(roleId: 2, ruleSetId: 2);

        var cts = new CancellationTokenSource();

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()))
            .Returns(() =>
            {
                // Cancel token on the first call; second role's ruleset loop will check cancellation first and return early.
                cts.Cancel();
                return Task.FromResult<IRuleExpressionGroup>(new FilterExpressionGroup(typeof(Customer)));
            });

        // ProcessFilter returns empty so no mappings are saved.
        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(_db.Customers.Where(x => x.Id < 0).AsNoTracking().ToPagedList(0, 500));

        var task = CreateTask();
        var ctx = CreateContext();
        await task.Run(ctx, cts.Token);

        // CreateExpressionGroupAsync should be called exactly once (roleA's ruleset).
        // roleB's ruleSet loop checks the cancelled token first and returns.
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()),
            Times.Once);
    }

    // ----------------------------------------------------------------
    // T10 - CancellationToken mid-chunk-loop: first chunk committed, second cancelled
    // ----------------------------------------------------------------
    [Test]
    public async Task T10_CancellationToken_MidChunk_FirstChunkCommitted()
    {
        await SeedRoleWithRuleSetAsync(roleId: 1, ruleSetId: 1);

        // Seed 600 customers (IDs 1-600).
        for (int i = 1; i <= 600; i++)
        {
            _db.Customers.Add(new Customer { Id = i, IsSystemAccount = false });
        }
        await _db.SaveChangesAsync();

        var cts = new CancellationTokenSource();

        // Cancel after first SaveChanges (first chunk of 500 committed).
        _db.SavedChanges += (_, _) => cts.Cancel();

        var expressionGroup = new FilterExpressionGroup(typeof(Customer));

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(It.IsAny<RuleSetEntity>(), It.IsAny<IRuleVisitor>(), It.IsAny<bool>()))
            .ReturnsAsync(expressionGroup);

        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(_db.Customers.AsNoTracking().ToPagedList(0, 500));

        var task = CreateTask();
        var ctx = CreateContext();
        await task.Run(ctx, cts.Token);

        // First chunk (500) committed; second chunk (100) was cancelled before commit.
        Assert.That(await _db.CustomerRoleMappings.CountAsync(x => x.IsSystemMapping), Is.EqualTo(500));
    }
}
