using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Checkout.Shipping;
using Smartstore.Core.Checkout.Tax;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Localization;
using Smartstore.Core.Rules;
using Smartstore.Core.Stores;
using Smartstore.Test.Common;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

[TestFixture]
public class TargetGroupServiceDescriptorTests
{
    private TargetGroupService _targetGroupService;
    private RuleDescriptorCollection _descriptors;

    // All 39 legacy descriptor names in expected order.
    private static readonly string[] LegacyDescriptorNames =
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
        "BillingCountry",
        "ShippingCountry",
        "ReturnRequestCount",
        "LastActivityDays",
        "LastLoginDays",
        "LastForumVisitDays",
        "CreatedDays",
        "BirthDateDays",
        "RuleSet",
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

    [OneTimeSetUp]
    public async Task Setup()
    {
        var ruleServiceMock = new Mock<IRuleService>();

        var storeContextMock = new Mock<IStoreContext>();
        storeContextMock.Setup(x => x.GetAllStores()).Returns(new List<Store>());

        var localizationServiceMock = new Mock<ILocalizationService>();
        localizationServiceMock
            .Setup(x => x.GetLocalizedEnum(It.IsAny<VatNumberStatus>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns("mock");
        localizationServiceMock
            .Setup(x => x.GetLocalizedEnum(It.IsAny<TaxDisplayType>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns("mock");
        localizationServiceMock
            .Setup(x => x.GetLocalizedEnum(It.IsAny<ShippingStatus>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns("mock");
        localizationServiceMock
            .Setup(x => x.GetLocalizedEnum(It.IsAny<PaymentStatus>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns("mock");

        var currencyServiceMock = new Mock<ICurrencyService>();
        currencyServiceMock
            .Setup(x => x.PrimaryCurrency)
            .Returns(new Currency { CurrencyCode = "USD" });

        _targetGroupService = new TargetGroupService(
            null,
            ruleServiceMock.Object,
            storeContextMock.Object,
            localizationServiceMock.Object,
            currencyServiceMock.Object);

        _descriptors = await _targetGroupService.GetRuleDescriptorsAsync();
    }

    [Test]
    public void LoadDescriptors_ContainsAllLegacyDescriptors()
    {
        foreach (var name in LegacyDescriptorNames)
        {
            var descriptor = _descriptors.FindDescriptor(name);
            Assert.That(descriptor, Is.Not.Null, $"Legacy descriptor '{name}' is missing from modern implementation.");
        }
    }

    [Test]
    public void LoadDescriptors_DescriptorNamesAndRuleTypesMatch()
    {
        // Boolean descriptors.
        AssertDescriptorRuleType("Active", RuleType.Boolean);
        AssertDescriptorRuleType("TaxExempt", RuleType.Boolean);
        AssertDescriptorRuleType("AcceptThirdPartyEmailHandOver", RuleType.Boolean);

        // String descriptors.
        AssertDescriptorRuleType("Salutation", RuleType.String);
        AssertDescriptorRuleType("Title", RuleType.String);
        AssertDescriptorRuleType("Company", RuleType.String);
        AssertDescriptorRuleType("Gender", RuleType.String);
        AssertDescriptorRuleType("CustomerNumber", RuleType.String);
        AssertDescriptorRuleType("TimeZone", RuleType.String);
        AssertDescriptorRuleType("LastUserAgent", RuleType.String);
        AssertDescriptorRuleType("CurrencyCode", RuleType.String);
        AssertDescriptorRuleType("PaymentMethod", RuleType.String);
        AssertDescriptorRuleType("ShippingMethod", RuleType.String);
        AssertDescriptorRuleType("ShippingRateComputationMethod", RuleType.String);

        // Int descriptors.
        AssertDescriptorRuleType("VatNumberStatus", RuleType.Int);
        AssertDescriptorRuleType("TaxDisplayType", RuleType.Int);
        AssertDescriptorRuleType("ReturnRequestCount", RuleType.Int);
        AssertDescriptorRuleType("NewOrderCount", RuleType.Int);
        AssertDescriptorRuleType("CompletedOrderCount", RuleType.Int);
        AssertDescriptorRuleType("CancelledOrderCount", RuleType.Int);
        AssertDescriptorRuleType("RuleSet", RuleType.Int);
        AssertDescriptorRuleType("ShippingStatus", RuleType.Int);
        AssertDescriptorRuleType("PaymentStatus", RuleType.Int);
        AssertDescriptorRuleType("OrderLanguage", RuleType.Int);

        // IntArray descriptors.
        AssertDescriptorRuleType("IsInCustomerRole", RuleType.IntArray);
        AssertDescriptorRuleType("BillingCountry", RuleType.IntArray);
        AssertDescriptorRuleType("ShippingCountry", RuleType.IntArray);
        AssertDescriptorRuleType("OrderInStore", RuleType.IntArray);
        AssertDescriptorRuleType("HasPurchasedProduct", RuleType.IntArray);
        AssertDescriptorRuleType("HasPurchasedAllProducts", RuleType.IntArray);

        // NullableInt descriptors.
        AssertDescriptorRuleType("LastActivityDays", RuleType.NullableInt);
        AssertDescriptorRuleType("LastLoginDays", RuleType.NullableInt);
        AssertDescriptorRuleType("LastForumVisitDays", RuleType.NullableInt);
        AssertDescriptorRuleType("CreatedDays", RuleType.NullableInt);
        AssertDescriptorRuleType("BirthDateDays", RuleType.NullableInt);
        AssertDescriptorRuleType("LastOrderDateDays", RuleType.NullableInt);

        // Money descriptors.
        AssertDescriptorRuleType("OrderTotal", RuleType.Money);
        AssertDescriptorRuleType("OrderSubtotalInclTax", RuleType.Money);
        AssertDescriptorRuleType("OrderSubtotalExclTax", RuleType.Money);
    }

    [Test]
    public void LoadDescriptors_ContainsAdditiveLastDeviceFamilyDescriptor()
    {
        var descriptor = _descriptors.FindDescriptor("LastDeviceFamily");
        descriptor.ShouldNotBeNull();
        descriptor.RuleType.ShouldEqual(RuleType.StringArray);
    }

    [Test]
    public void LoadDescriptors_TotalCountReflectsLegacyPlusModernAdditions()
    {
        // 39 legacy descriptors + 1 modern-only (LastDeviceFamily) = 40 total.
        _descriptors.Count.ShouldEqual(40);
    }

    [Test]
    public void LoadDescriptors_OrderDescriptorsDoNotHaveExplicitDeletedFilter()
    {
        // Documents the architectural decision: order-related descriptors rely on global query
        // filters (builder.HasQueryFilter(c => !c.Deleted)) in OrderMap instead of explicit
        // !o.Deleted predicates. This is correct because EF Core applies the global query filter
        // automatically to all queries involving the Order entity.
        var orderDescriptorNames = new[]
        {
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
        };

        foreach (var name in orderDescriptorNames)
        {
            var descriptor = _descriptors.FindDescriptor(name);
            Assert.That(descriptor, Is.Not.Null, $"Order descriptor '{name}' should exist.");

            // The descriptor should not contain an explicit Deleted filter in its member
            // expression. With global query filters, the filter is applied at the database
            // level automatically, so the lambda expressions in descriptors reference only
            // Order navigation properties (e.g., o.StoreId, o.OrderStatusId) without
            // !o.Deleted predicates.
            if (descriptor is Smartstore.Core.Rules.Filters.FilterDescriptor filterDescriptor
                && filterDescriptor.MemberExpression != null)
            {
                var expressionString = filterDescriptor.MemberExpression.ToString();
                Assert.That(
                    expressionString.Contains(".Deleted"),
                    Is.False,
                    $"Order descriptor '{name}' should not contain an explicit .Deleted filter; " +
                    "the global query filter in OrderMap handles soft-delete exclusion.");
            }
        }
    }

    private void AssertDescriptorRuleType(string descriptorName, RuleType expectedRuleType)
    {
        var descriptor = _descriptors.FindDescriptor(descriptorName);
        Assert.That(descriptor, Is.Not.Null, $"Descriptor '{descriptorName}' not found.");
        Assert.That(descriptor.RuleType, Is.EqualTo(expectedRuleType),
            $"Descriptor '{descriptorName}' has RuleType '{descriptor.RuleType?.Name}' " +
            $"but expected '{expectedRuleType.Name}'.");
    }
}
