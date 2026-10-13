using System.Linq;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Identity;
using Smartstore.Test.Common;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

/// <summary>
/// Verifies that the Order and Customer entities have EF Core global query filters
/// for soft-delete exclusion, which makes the modern filter descriptors (that omit
/// explicit <c>.Where(o => !o.Deleted)</c>) behaviorally equivalent to the legacy
/// SmartStoreNET descriptors that included the clause manually.
/// </summary>
[TestFixture]
public class OrderQueryFilterVerificationTests : ServiceTestBase
{
    [Test]
    public void Order_Entity_Has_Global_QueryFilter_For_Deleted()
    {
        var model = DbContext.Model;
        var orderEntityType = model.FindEntityType(typeof(Order));

        Assert.That(orderEntityType, Is.Not.Null, "Order entity type should be registered in the EF Core model.");

        var queryFilter = orderEntityType.GetQueryFilter();

        Assert.That(queryFilter, Is.Not.Null,
            "Order entity must have a global query filter configured (HasQueryFilter). " +
            "Without it, modern order-related filter descriptors would include soft-deleted " +
            "orders, diverging from legacy behavior.");

        // Verify the filter references the Deleted property
        var filterString = queryFilter.Body.ToString();
        Assert.That(filterString, Does.Contain("Deleted"),
            "Order query filter must reference the Deleted property to exclude soft-deleted records.");
    }

    [Test]
    public void Customer_Entity_Has_Global_QueryFilter_For_Deleted()
    {
        var model = DbContext.Model;
        var customerEntityType = model.FindEntityType(typeof(Customer));

        Assert.That(customerEntityType, Is.Not.Null, "Customer entity type should be registered in the EF Core model.");

        var queryFilter = customerEntityType.GetQueryFilter();

        Assert.That(queryFilter, Is.Not.Null,
            "Customer entity must have a global query filter configured (HasQueryFilter). " +
            "The modern base query omits explicit !x.Deleted because this filter handles it.");

        var filterString = queryFilter.Body.ToString();
        Assert.That(filterString, Does.Contain("Deleted"),
            "Customer query filter must reference the Deleted property to exclude soft-deleted records.");
    }
}
