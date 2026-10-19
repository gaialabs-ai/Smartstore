using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Smartstore;
using Smartstore.Caching;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Checkout.Shipping;
using Smartstore.Core.Checkout.Tax;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Localization;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Rendering;
using Smartstore.Core.Stores;
using Smartstore.Core.Web;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Engine;
using Smartstore.Scheduling;
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

/// <summary>
/// End-to-end FUNCTIONAL test for <see cref="TargetGroupEvaluatorTask"/>: drives the task's real
/// <c>Run</c> method against a real SQLite-backed <see cref="SmartDbContext"/>, using the REAL
/// <see cref="RuleService"/> and REAL <see cref="TargetGroupService"/> rule-evaluation pipeline -
/// i.e. an actual <see cref="RuleSetEntity"/>/<see cref="RuleEntity"/> row stored in the database is
/// converted into a real <c>FilterExpression</c> and executed as a real LINQ predicate against SQLite.
///
/// This is intentionally different from the sibling <see cref="TargetGroupEvaluatorTaskTests"/> class,
/// which mocks <c>IRuleService</c>/<c>ITargetGroupService</c>/<c>ProcessFilter</c> and hardcodes
/// rule-set-id -> customer-id matches instead of evaluating real rules. Only infrastructure that is
/// genuinely orthogonal to the audited regression (store context, localization of enum option lists,
/// primary currency, cache invalidation) is stubbed.
///
/// Each test writes a small JSON snapshot of the resulting CustomerRoleMapping table (keyed by
/// business keys - role SystemName and customer CustomerNumber - not database-generated ids) to a
/// file, so an external pytest harness can run this exact test class unmodified against both the
/// pre-fix baseline ref and the current (fixed) ref and diff the real, observed behavior.
///
/// SQLite/cache test-infra setup/teardown and <c>CreateContext</c>/<c>AddRole</c> helpers are shared
/// with the sibling <see cref="TargetGroupEvaluatorTaskTests"/> class via <see cref="TargetGroupEvaluatorTaskTestBase"/>.
/// </summary>
[TestFixture]
public class TargetGroupEvaluatorTaskFunctionalTests : TargetGroupEvaluatorTaskTestBase
{
    private IServiceProviderContainer _providerContainer;
    private IServiceProvider _originalApplicationServices;
    private AutofacServiceProvider _augmentedServiceProvider;

    // ------------------------------------------------------------------
    // TargetGroupService.LoadDescriptorsAsync() unconditionally builds a "LastDeviceFamily" filter
    // descriptor that eagerly calls DeviceRule.GetDefaultOptions(), a static method resolving
    // IUserAgentParser from the GLOBAL EngineContext.Current.Application.Services container. This is
    // orthogonal to the audited CustomerRoleIds regression (device/user-agent detection has nothing
    // to do with role-scoped rule recomputation) but is unavoidable when exercising the REAL
    // TargetGroupService rule-conversion pipeline instead of a mock. Rather than editing the shared
    // ServiceTestBase (used by many unrelated fixtures), patch the container with a minimal,
    // fixture-scoped child lifetime scope that adds only the missing registration, and restore the
    // original afterwards so no state leaks to other test fixtures.
    // ------------------------------------------------------------------
    [OneTimeSetUp]
    public void PatchUserAgentParserRegistration()
    {
        _providerContainer = (EngineContext.Current.Application as IServiceProviderContainer)
            ?? throw new InvalidOperationException($"'{nameof(EngineContext.Current.Application)}' must implement '{nameof(IServiceProviderContainer)}'.");

        _originalApplicationServices = _providerContainer.ApplicationServices;

        var rootScope = _originalApplicationServices.AsLifetimeScope();

        var userAgentParserMock = new Mock<IUserAgentParser>();
        userAgentParserMock
            .Setup(x => x.GetDetectableAgents(It.IsAny<UserAgentSegment>()))
            .Returns(Array.Empty<string>());

        var augmentedScope = rootScope.BeginLifetimeScope(cb =>
        {
            cb.RegisterInstance(userAgentParserMock.Object).As<IUserAgentParser>().SingleInstance();
        });

        _augmentedServiceProvider = new AutofacServiceProvider(augmentedScope);
        _providerContainer.ApplicationServices = _augmentedServiceProvider;
    }

    [OneTimeTearDown]
    public void RestoreApplicationServices()
    {
        if (_providerContainer != null)
        {
            _providerContainer.ApplicationServices = _originalApplicationServices;
        }

        _augmentedServiceProvider?.Dispose();
    }

    // ------------------------------------------------------------------
    // Real-service wiring. Only infrastructure fully orthogonal to the rule-evaluation pipeline
    // (store list, enum localization text, primary currency code) is stubbed; RuleService and
    // TargetGroupService themselves run unmocked, real business logic.
    // ------------------------------------------------------------------

    private static IStoreContext CreateStoreContextStub()
    {
        var mock = new Mock<IStoreContext>();
        mock.Setup(x => x.GetCachedStores()).Returns(new StoreEntityCache(new List<Store>()));
        return mock.Object;
    }

    private static ILocalizationService CreateLocalizationServiceStub()
    {
        var mock = new Mock<ILocalizationService>();
        mock.Setup(x => x.GetLocalizedEnum(It.IsAny<VatNumberStatus>(), It.IsAny<int>(), It.IsAny<bool>())).Returns("n/a");
        mock.Setup(x => x.GetLocalizedEnum(It.IsAny<TaxDisplayType>(), It.IsAny<int>(), It.IsAny<bool>())).Returns("n/a");
        mock.Setup(x => x.GetLocalizedEnum(It.IsAny<ShippingStatus>(), It.IsAny<int>(), It.IsAny<bool>())).Returns("n/a");
        mock.Setup(x => x.GetLocalizedEnum(It.IsAny<PaymentStatus>(), It.IsAny<int>(), It.IsAny<bool>())).Returns("n/a");
        return mock.Object;
    }

    private static ICurrencyService CreateCurrencyServiceStub()
    {
        var mock = new Mock<ICurrencyService>();
        mock.SetupGet(x => x.PrimaryCurrency).Returns(new Currency { CurrencyCode = "USD" });
        return mock.Object;
    }

    private sealed class StubRuleProviderFactory : IRuleProviderFactory
    {
        private readonly IRuleProvider _provider;
        public StubRuleProviderFactory(IRuleProvider provider) => _provider = provider;
        public IRuleProvider GetProvider(RuleScope scope, object context = null) => _provider;
    }

    private TargetGroupEvaluatorTask CreateRealTask()
    {
        // IWorkContext and the rule-options-provider list are only touched by RuleService's
        // ApplyMetadataAsync/ApplyRuleDataAsync - neither is on the call path exercised by
        // TargetGroupEvaluatorTask.Run (which only calls CreateExpressionGroupAsync). An
        // unconfigured mock / empty lazy is therefore sufficient and never actually invoked.
        var workContextMock = new Mock<IWorkContext>();
        var ruleService = new RuleService(_db, workContextMock.Object, new Lazy<IEnumerable<IRuleOptionsProvider>>(Enumerable.Empty<IRuleOptionsProvider>));

        var targetGroupService = new TargetGroupService(
            _db,
            ruleService,
            CreateStoreContextStub(),
            CreateLocalizationServiceStub(),
            CreateCurrencyServiceStub());

        var ruleProviderFactory = new StubRuleProviderFactory(targetGroupService);

        return new TargetGroupEvaluatorTask(_db, _cacheMock.Object, ruleService, ruleProviderFactory);
    }

    private Customer AddCustomer(string customerNumber)
    {
        var customer = new Customer { CustomerGuid = Guid.NewGuid(), CustomerNumber = customerNumber };
        _db.Customers.Add(customer);
        return customer;
    }

    /// <summary>
    /// Builds a real, DB-stored rule set with a single "CustomerNumber IsEqualTo &lt;value&gt;" rule -
    /// the simplest descriptor exposed by <see cref="TargetGroupService.LoadDescriptorsAsync"/> that
    /// (a) is a plain column comparison translatable by SQLite (unlike the several DateDiffDay-based
    /// descriptors) and (b) exercises the full real pipeline: RuleEntity -> FilterExpression ->
    /// FilterExpressionGroup.ToPredicate() -> LINQ query against _db.Customers.
    /// </summary>
    private RuleSetEntity AddRuleSetMatchingCustomerNumber(string customerNumber)
    {
        var ruleSet = new RuleSetEntity
        {
            IsActive = true,
            Scope = RuleScope.Customer,
            LogicalOperator = LogicalRuleOperator.And,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };
        _db.RuleSets.Add(ruleSet);

        ruleSet.Rules.Add(new RuleEntity
        {
            RuleType = "CustomerNumber",
            Operator = RuleOperator.IsEqualTo,
            Value = customerNumber
        });

        return ruleSet;
    }

    /// <summary>
    /// Snapshot of the CustomerRoleMapping table keyed by business keys (role SystemName -> sorted
    /// CustomerNumbers), so the JSON is directly comparable between a baseline-ref run and a
    /// current-ref run without depending on database-generated identity values.
    /// </summary>
    private async Task<Dictionary<string, object>> SnapshotAsync(int numDeleted, int numAdded)
    {
        var mappings = await (
            from m in _db.CustomerRoleMappings.AsNoTracking()
            join r in _db.CustomerRoles.AsNoTracking() on m.CustomerRoleId equals r.Id
            join c in _db.Customers.AsNoTracking() on m.CustomerId equals c.Id
            select new { Role = r.SystemName, Customer = c.CustomerNumber, m.IsSystemMapping }
        ).ToListAsync();

        var byRole = mappings
            .GroupBy(x => x.Role)
            .OrderBy(x => x.Key)
            .ToDictionary(
                g => g.Key,
                g => (object)g.Select(x => x.Customer).OrderBy(x => x).ToArray());

        return new Dictionary<string, object>
        {
            ["numDeleted"] = numDeleted,
            ["numAdded"] = numAdded,
            ["roleMappings"] = byRole
        };
    }

    private static void WriteResult(string testName, Dictionary<string, object> snapshot)
    {
        var outputDir = Environment.GetEnvironmentVariable("FT_HARNESS_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputDir))
        {
            outputDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "ft-harness-output");
        }

        Directory.CreateDirectory(outputDir);

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDir, testName + ".json"), json);
        TestContext.Out.WriteLine(json);
    }

    // ------------------------------------------------------------------
    // Scenario A: unscoped (nightly scheduler) full run with two active roles, each backed by a
    // real rule set matched against real customer rows. Behavior must be identical whether run
    // against the pre-fix baseline ref or the current fixed ref - the fix only changes the SCOPED
    // (CustomerRoleIds-filtered) code path.
    // ------------------------------------------------------------------
    [Test]
    public async Task Run_Unscoped_RealRuleEngine_RecomputesAllActiveRoles()
    {
        var role1 = AddRole("Role1", true);
        var role2 = AddRole("Role2", true);

        var ruleSet1 = AddRuleSetMatchingCustomerNumber("CUST-A");
        var ruleSet2 = AddRuleSetMatchingCustomerNumber("CUST-B");
        role1.RuleSets.Add(ruleSet1);
        role2.RuleSets.Add(ruleSet2);

        AddCustomer("CUST-A");
        AddCustomer("CUST-B");
        AddCustomer("CUST-C"); // matches neither rule set - must never be assigned
        var staleCustomer = AddCustomer("CUST-STALE"); // leftover match from a previous run
        await _db.SaveChangesAsync();

        // Simulate stale IsSystemMapping=true rows left over from a previous run, for both roles,
        // whose matched-customer set has since changed (CUST-STALE no longer matches either rule set).
        _db.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = staleCustomer.Id, CustomerRoleId = role1.Id, IsSystemMapping = true });
        _db.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = staleCustomer.Id, CustomerRoleId = role2.Id, IsSystemMapping = true });
        await _db.SaveChangesAsync();

        // Real numDeleted: Run()'s delete query removes every pre-existing IsSystemMapping=true row
        // (unscoped) before recomputing, so this is exactly that count, read from the database rather
        // than hardcoded.
        var numDeletedExpected = await _db.CustomerRoleMappings.CountAsync(x => x.IsSystemMapping);

        var task = CreateRealTask();
        await task.Run(CreateContext(), CancellationToken.None);

        var role1Numbers = await _db.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role1.Id)
            .Join(_db.Customers, m => m.CustomerId, c => c.Id, (m, c) => c.CustomerNumber)
            .ToListAsync();
        var role2Numbers = await _db.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role2.Id)
            .Join(_db.Customers, m => m.CustomerId, c => c.Id, (m, c) => c.CustomerNumber)
            .ToListAsync();

        var numAdded = await _db.CustomerRoleMappings.CountAsync();
        var snapshot = await SnapshotAsync(numDeleted: numDeletedExpected, numAdded: numAdded);
        WriteResult(nameof(Run_Unscoped_RealRuleEngine_RecomputesAllActiveRoles), snapshot);

        Assert.That(role1Numbers, Is.EqualTo(new[] { "CUST-A" }));
        Assert.That(role2Numbers, Is.EqualTo(new[] { "CUST-B" }));
    }

    // ------------------------------------------------------------------
    // Scenario B (THE REGRESSION, real rule engine): scoped run via "CustomerRoleIds" must only
    // touch (delete + recompute) the targeted role. A second active role, with its own real rule
    // set that WOULD match a real customer if it were (incorrectly) re-evaluated, must be left
    // completely untouched - still exactly its pre-existing leftover mapping.
    //
    // Pre-fix (baseline ref 485b8dac6), the role-selection/recompute query ignored CustomerRoleIds,
    // so this scenario incorrectly recomputes OtherRole too, and "CUST-OTHER-NEW" gets added
    // alongside the pre-existing mapping. Post-fix (commit 3220151fe), OtherRole's rule set is never
    // evaluated and its mapping stays exactly as it was before the scoped run.
    // ------------------------------------------------------------------
    [Test]
    public async Task Run_ScopedByCustomerRoleIds_RealRuleEngine_OnlyRecomputesTargetedRole()
    {
        var targetRole = AddRole("TargetRole", true);
        var otherRole = AddRole("OtherRole", true);

        var targetRuleSet = AddRuleSetMatchingCustomerNumber("CUST-TARGET");
        var otherRuleSet = AddRuleSetMatchingCustomerNumber("CUST-OTHER-NEW");
        targetRole.RuleSets.Add(targetRuleSet);
        otherRole.RuleSets.Add(otherRuleSet);

        AddCustomer("CUST-TARGET");
        AddCustomer("CUST-OTHER-NEW");
        var preExistingCustomer = AddCustomer("CUST-PRE-EXISTING");
        var staleTargetCustomer = AddCustomer("CUST-STALE-TARGET"); // leftover mapping on the role that WILL be recomputed
        await _db.SaveChangesAsync();

        // Simulate a mapping left over from a previous run for the role that is NOT targeted by
        // this scoped invocation.
        _db.CustomerRoleMappings.Add(new CustomerRoleMapping
        {
            CustomerId = preExistingCustomer.Id,
            CustomerRoleId = otherRole.Id,
            IsSystemMapping = true
        });

        // A stale mapping on targetRole itself, whose rule-matched customer set has since changed
        // (CUST-STALE-TARGET no longer matches targetRuleSet's rule). This is the case the previous
        // version of this test never exercised: deletion of a recomputed role's own stale system
        // mappings.
        _db.CustomerRoleMappings.Add(new CustomerRoleMapping
        {
            CustomerId = staleTargetCustomer.Id,
            CustomerRoleId = targetRole.Id,
            IsSystemMapping = true
        });
        await _db.SaveChangesAsync();

        // Real numDeleted: the task's delete query is scoped to CustomerRoleIds = [targetRole.Id], so
        // this is exactly the count of pre-existing IsSystemMapping=true rows for targetRole, read
        // from the database rather than hardcoded.
        var numDeletedExpected = await _db.CustomerRoleMappings
            .CountAsync(x => x.IsSystemMapping && x.CustomerRoleId == targetRole.Id);

        var task = CreateRealTask();
        var ctx = CreateContext(new Dictionary<string, string> { ["CustomerRoleIds"] = targetRole.Id.ToString() });

        await task.Run(ctx, CancellationToken.None);

        var numAdded = await _db.CustomerRoleMappings.CountAsync(x => x.CustomerRoleId == targetRole.Id);
        var snapshot = await SnapshotAsync(numDeleted: numDeletedExpected, numAdded: numAdded);
        WriteResult(nameof(Run_ScopedByCustomerRoleIds_RealRuleEngine_OnlyRecomputesTargetedRole), snapshot);

        var targetRoleNumbers = await _db.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == targetRole.Id)
            .Join(_db.Customers, m => m.CustomerId, c => c.Id, (m, c) => c.CustomerNumber)
            .ToListAsync();
        var otherRoleNumbers = await _db.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == otherRole.Id)
            .Join(_db.Customers, m => m.CustomerId, c => c.Id, (m, c) => c.CustomerNumber)
            .ToListAsync();

        // The targeted role was recomputed as expected, using the real rule engine.
        Assert.That(targetRoleNumbers, Is.EqualTo(new[] { "CUST-TARGET" }));
        Assert.That(targetRoleNumbers, Does.Not.Contain("CUST-STALE-TARGET"));

        // THE REGRESSION ASSERTION: the non-targeted role must be left completely untouched -
        // still exactly the pre-existing mapping, and its own (real, matching) rule set must NEVER
        // have been evaluated. This fails against the pre-fix baseline ref and passes against the
        // fixed current ref.
        Assert.That(otherRoleNumbers, Is.EqualTo(new[] { "CUST-PRE-EXISTING" }),
            "Scoped 'Apply rules' run must not touch roles outside CustomerRoleIds - " +
            "OtherRole's real rule set must never be evaluated.");
    }
}
