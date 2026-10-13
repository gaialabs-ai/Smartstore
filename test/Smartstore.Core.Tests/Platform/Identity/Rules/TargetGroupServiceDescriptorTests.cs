using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Moq;
using NUnit.Framework;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Checkout.Shipping;
using Smartstore.Core.Checkout.Tax;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Localization;
using Smartstore.Core.Rules;
using Smartstore.Core.Stores;
using Smartstore.Core.Web;
using Smartstore.Engine;
using Smartstore.Test.Common;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

/// <summary>
/// Verifies that <see cref="TargetGroupService.LoadDescriptorsAsync"/> returns the correct
/// filter descriptors, ensuring parity with the legacy SmartStoreNET implementation (39 descriptors)
/// plus the modern-only <c>LastDeviceFamily</c> addition (40 total).
/// </summary>
[TestFixture]
public class TargetGroupServiceDescriptorTests
{
    private RuleDescriptorCollection _descriptors;
    private ILifetimeScope _autofacScope;
    private IEngine _previousEngine;

    [OneTimeSetUp]
    public async Task Setup()
    {
        // --- Set up EngineContext with IUserAgentParser ---
        // DeviceRule.GetDefaultOptions() resolves IUserAgentParser from EngineContext.Current.Application.Services.
        // We build a minimal Autofac container to satisfy this dependency.
        var containerBuilder = new ContainerBuilder();
        var userAgentParserMock = new Mock<IUserAgentParser>();
        userAgentParserMock
            .Setup(x => x.GetDetectableAgents(UserAgentSegment.Device))
            .Returns(new[] { "Mobile", "Tablet", "Desktop" });
        containerBuilder.RegisterInstance(userAgentParserMock.Object).As<IUserAgentParser>();
        var container = containerBuilder.Build();
        _autofacScope = container;

        var appContextMock = new Mock<IApplicationContext>();
        appContextMock.Setup(x => x.Services).Returns(container);

        var engineMock = new Mock<IEngine>();
        engineMock.Setup(x => x.Application).Returns(appContextMock.Object);

        _previousEngine = EngineContext.Current;
        EngineContext.Replace(engineMock.Object);

        // --- Mock IStoreContext ---
        var store = new Store { Id = 1, Name = "TestStore" };
        var storeCache = new StoreEntityCache(new List<Store> { store });
        var storeContextMock = new Mock<IStoreContext>();
        storeContextMock
            .Setup(x => x.GetCachedStores())
            .Returns(storeCache);

        // --- Mock IRuleService ---
        var ruleServiceMock = new Mock<IRuleService>();

        // --- Mock ILocalizationService ---
        var localizationServiceMock = new Mock<ILocalizationService>();
        localizationServiceMock
            .Setup(x => x.GetLocalizedEnum(It.IsAny<VatNumberStatus>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns<VatNumberStatus, int, bool>((val, _, _) => val.ToString());
        localizationServiceMock
            .Setup(x => x.GetLocalizedEnum(It.IsAny<TaxDisplayType>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns<TaxDisplayType, int, bool>((val, _, _) => val.ToString());
        localizationServiceMock
            .Setup(x => x.GetLocalizedEnum(It.IsAny<ShippingStatus>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns<ShippingStatus, int, bool>((val, _, _) => val.ToString());
        localizationServiceMock
            .Setup(x => x.GetLocalizedEnum(It.IsAny<PaymentStatus>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns<PaymentStatus, int, bool>((val, _, _) => val.ToString());

        // --- Mock ICurrencyService ---
        var currencyServiceMock = new Mock<ICurrencyService>();
        currencyServiceMock
            .Setup(x => x.PrimaryCurrency)
            .Returns(new Currency { CurrencyCode = "USD" });

        // --- Create SUT ---
        // SmartDbContext is stored by the constructor but not used during LoadDescriptorsAsync(),
        // so we pass null to avoid the heavyweight ServiceTestBase infrastructure.
        var service = new TargetGroupService(
            db: null,
            ruleServiceMock.Object,
            storeContextMock.Object,
            localizationServiceMock.Object,
            currencyServiceMock.Object);

        // GetRuleDescriptorsAsync() is public on RuleProviderBase and internally calls LoadDescriptorsAsync().
        _descriptors = await service.GetRuleDescriptorsAsync();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        // Restore previous engine context and dispose our container.
        EngineContext.Replace(_previousEngine);
        _autofacScope?.Dispose();
    }

    // ------------------------------------------------------------------
    // 1. Total count
    // ------------------------------------------------------------------

    [Test]
    public void LoadDescriptors_ReturnsExpectedCount()
    {
        _descriptors.Count.ShouldEqual(40);
    }

    // ------------------------------------------------------------------
    // 2. Customer property descriptors (13)
    // ------------------------------------------------------------------

    private static readonly string[] CustomerPropertyNames =
    [
        "Active",
        "Salutation",
        "Title",
        "Company",
        "Gender",
        "CustomerNumber",
        "IsInCustomerRole",
        "TaxExempt",
        "VatNumberStatus",
        "TaxDisplayType",
        "TimeZone",
        "LastUserAgent",
        "LastDeviceFamily"
    ];

    [Test]
    public void LoadDescriptors_ContainsAllCustomerPropertyDescriptors()
    {
        foreach (var name in CustomerPropertyNames)
        {
            var descriptor = _descriptors.FindDescriptor(name);
            descriptor.ShouldNotBeNull();
            descriptor.Name.ShouldEqual(name);
        }

        // Verify count: all 13 customer property descriptors exist.
        var foundCount = CustomerPropertyNames.Count(n => _descriptors.FindDescriptor(n) != null);
        foundCount.ShouldEqual(13);
    }

    // ------------------------------------------------------------------
    // 3. Address descriptors (2)
    // ------------------------------------------------------------------

    [Test]
    public void LoadDescriptors_ContainsAddressDescriptors()
    {
        var billing = _descriptors.FindDescriptor("BillingCountry");
        billing.ShouldNotBeNull();
        billing.RuleType.ShouldEqual(RuleType.IntArray);

        var shipping = _descriptors.FindDescriptor("ShippingCountry");
        shipping.ShouldNotBeNull();
        shipping.RuleType.ShouldEqual(RuleType.IntArray);
    }

    // ------------------------------------------------------------------
    // 4. Date-diff descriptors (5)
    // ------------------------------------------------------------------

    private static readonly string[] DateDiffNames =
    [
        "LastActivityDays",
        "LastLoginDays",
        "LastForumVisitDays",
        "CreatedDays",
        "BirthDateDays"
    ];

    [Test]
    public void LoadDescriptors_ContainsDateDiffDescriptors()
    {
        foreach (var name in DateDiffNames)
        {
            var descriptor = _descriptors.FindDescriptor(name);
            descriptor.ShouldNotBeNull();
            descriptor.RuleType.ShouldEqual(RuleType.NullableInt);
        }

        var foundCount = DateDiffNames.Count(n => _descriptors.FindDescriptor(n) != null);
        foundCount.ShouldEqual(5);
    }

    // ------------------------------------------------------------------
    // 5. Order-related descriptors (18)
    // ------------------------------------------------------------------

    private static readonly string[] OrderDescriptorNames =
    [
        "OrderInStore",
        "NewOrderCount",
        "CompletedOrderCount",
        "CancelledOrderCount",
        "LastOrderDateDays",
        "OrderTotal",
        "OrderSubtotalInclTax",
        "OrderSubtotalExclTax",
        "ShippingStatus",
        "PaymentStatus",
        "HasPurchasedProduct",
        "HasPurchasedAllProducts",
        "AcceptThirdPartyEmailHandOver",
        "CurrencyCode",
        "OrderLanguage",
        "PaymentMethod",
        "ShippingMethod",
        "ShippingRateComputationMethod"
    ];

    [Test]
    public void LoadDescriptors_ContainsOrderDescriptors()
    {
        foreach (var name in OrderDescriptorNames)
        {
            var descriptor = _descriptors.FindDescriptor(name);
            descriptor.ShouldNotBeNull();
            descriptor.GroupKey.ShouldEqual("Admin.Orders");
        }

        var foundCount = OrderDescriptorNames.Count(n => _descriptors.FindDescriptor(n) != null);
        foundCount.ShouldEqual(18);
    }

    // ------------------------------------------------------------------
    // 6. RuleSet descriptor
    // ------------------------------------------------------------------

    [Test]
    public void LoadDescriptors_ContainsRuleSetDescriptor()
    {
        var ruleSet = _descriptors.FindDescriptor("RuleSet");
        ruleSet.ShouldNotBeNull();
        ruleSet.RuleType.ShouldEqual(RuleType.Int);

        // RuleSet explicitly sets Operators to IsEqualTo and IsNotEqualTo only.
        ruleSet.Operators.Length.ShouldEqual(2);
        Assert.That(ruleSet.Operators, Does.Contain(RuleOperator.IsEqualTo));
        Assert.That(ruleSet.Operators, Does.Contain(RuleOperator.IsNotEqualTo));
    }

    // ------------------------------------------------------------------
    // 7. Modern-only LastDeviceFamily
    // ------------------------------------------------------------------

    [Test]
    public void LoadDescriptors_ContainsModernOnlyLastDeviceFamily()
    {
        var descriptor = _descriptors.FindDescriptor("LastDeviceFamily");
        descriptor.ShouldNotBeNull();
        descriptor.RuleType.ShouldEqual(RuleType.StringArray);
    }

    // ------------------------------------------------------------------
    // 8. Money descriptors have currency code postfix
    // ------------------------------------------------------------------

    [Test]
    public void LoadDescriptors_MoneyDescriptorsHavePostfix()
    {
        var moneyDescriptors = _descriptors.Where(d => d.RuleType == RuleType.Money).ToList();

        // There should be exactly 3 money descriptors: OrderTotal, OrderSubtotalInclTax, OrderSubtotalExclTax.
        moneyDescriptors.Count.ShouldEqual(3);

        foreach (var descriptor in moneyDescriptors)
        {
            descriptor.Metadata.ContainsKey("postfix").ShouldBeTrue();
            descriptor.Metadata["postfix"].ShouldEqual("USD");
        }
    }

    // ------------------------------------------------------------------
    // 9. Spot-check RuleType values
    // ------------------------------------------------------------------

    [Test]
    public void LoadDescriptors_DescriptorRuleTypesAreCorrect()
    {
        // Active -> Boolean
        _descriptors.FindDescriptor("Active").RuleType.ShouldEqual(RuleType.Boolean);

        // CustomerNumber -> String
        _descriptors.FindDescriptor("CustomerNumber").RuleType.ShouldEqual(RuleType.String);

        // VatNumberStatus -> Int
        _descriptors.FindDescriptor("VatNumberStatus").RuleType.ShouldEqual(RuleType.Int);

        // LastActivityDays -> NullableInt
        _descriptors.FindDescriptor("LastActivityDays").RuleType.ShouldEqual(RuleType.NullableInt);

        // OrderTotal -> Money
        _descriptors.FindDescriptor("OrderTotal").RuleType.ShouldEqual(RuleType.Money);

        // IsInCustomerRole -> IntArray
        _descriptors.FindDescriptor("IsInCustomerRole").RuleType.ShouldEqual(RuleType.IntArray);

        // TaxExempt -> Boolean
        _descriptors.FindDescriptor("TaxExempt").RuleType.ShouldEqual(RuleType.Boolean);

        // TaxDisplayType -> Int
        _descriptors.FindDescriptor("TaxDisplayType").RuleType.ShouldEqual(RuleType.Int);

        // ReturnRequestCount -> Int
        _descriptors.FindDescriptor("ReturnRequestCount").RuleType.ShouldEqual(RuleType.Int);

        // Salutation -> String
        _descriptors.FindDescriptor("Salutation").RuleType.ShouldEqual(RuleType.String);

        // LastDeviceFamily -> StringArray
        _descriptors.FindDescriptor("LastDeviceFamily").RuleType.ShouldEqual(RuleType.StringArray);

        // CompletedOrderCount -> Int
        _descriptors.FindDescriptor("CompletedOrderCount").RuleType.ShouldEqual(RuleType.Int);

        // LastOrderDateDays -> NullableInt
        _descriptors.FindDescriptor("LastOrderDateDays").RuleType.ShouldEqual(RuleType.NullableInt);

        // AcceptThirdPartyEmailHandOver -> Boolean
        _descriptors.FindDescriptor("AcceptThirdPartyEmailHandOver").RuleType.ShouldEqual(RuleType.Boolean);

        // PaymentMethod -> String
        _descriptors.FindDescriptor("PaymentMethod").RuleType.ShouldEqual(RuleType.String);

        // HasPurchasedProduct -> IntArray
        _descriptors.FindDescriptor("HasPurchasedProduct").RuleType.ShouldEqual(RuleType.IntArray);

        // HasPurchasedAllProducts -> IntArray
        _descriptors.FindDescriptor("HasPurchasedAllProducts").RuleType.ShouldEqual(RuleType.IntArray);

        // BillingCountry -> IntArray
        _descriptors.FindDescriptor("BillingCountry").RuleType.ShouldEqual(RuleType.IntArray);

        // ShippingCountry -> IntArray
        _descriptors.FindDescriptor("ShippingCountry").RuleType.ShouldEqual(RuleType.IntArray);

        // OrderInStore -> IntArray
        _descriptors.FindDescriptor("OrderInStore").RuleType.ShouldEqual(RuleType.IntArray);

        // CurrencyCode -> String
        _descriptors.FindDescriptor("CurrencyCode").RuleType.ShouldEqual(RuleType.String);

        // OrderLanguage -> Int
        _descriptors.FindDescriptor("OrderLanguage").RuleType.ShouldEqual(RuleType.Int);
    }
}
