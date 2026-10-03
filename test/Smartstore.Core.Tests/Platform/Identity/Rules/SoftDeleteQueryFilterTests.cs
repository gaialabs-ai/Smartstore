using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Identity;
using Smartstore.Test.Common;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

[TestFixture]
public class SoftDeleteQueryFilterTests : ServiceTestBase
{
    [OneTimeSetUp]
    public void Setup()
    {
    }

    [SetUp]
    public void TestSetUp()
    {
        DbContext.ChangeTracker.Clear();

        // Clean up test data from previous test runs.
        var existingMappings = DbContext.CustomerRoleMappings.ToList();
        if (existingMappings.Count > 0)
        {
            DbContext.CustomerRoleMappings.RemoveRange(existingMappings);
            DbContext.SaveChanges();
        }

        var existingOrders = DbContext.Orders.IgnoreQueryFilters().ToList();
        if (existingOrders.Count > 0)
        {
            DbContext.Orders.RemoveRange(existingOrders);
            DbContext.SaveChanges();
        }

        var existingCustomers = DbContext.Customers.IgnoreQueryFilters().ToList();
        if (existingCustomers.Count > 0)
        {
            DbContext.Customers.RemoveRange(existingCustomers);
            DbContext.SaveChanges();
        }

        DbContext.ChangeTracker.Clear();
    }

    [Test]
    public void Customer_GlobalQueryFilter_ExcludesSoftDeletedRecords()
    {
        // Seed an active customer.
        var activeCustomer = new Customer
        {
            CustomerGuid = Guid.NewGuid(),
            Deleted = false,
            Active = true,
            CreatedOnUtc = DateTime.UtcNow
        };

        // Seed a soft-deleted customer.
        var deletedCustomer = new Customer
        {
            CustomerGuid = Guid.NewGuid(),
            Deleted = true,
            Active = true,
            CreatedOnUtc = DateTime.UtcNow
        };

        DbContext.Customers.AddRange(activeCustomer, deletedCustomer);
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        // Standard query should exclude the soft-deleted customer via global query filter.
        var customers = DbContext.Customers.ToList();
        Assert.That(customers.Count, Is.EqualTo(1));
        customers[0].Id.ShouldEqual(activeCustomer.Id);

        // Query with IgnoreQueryFilters should include both customers.
        var allCustomers = DbContext.Customers.IgnoreQueryFilters().ToList();
        Assert.That(allCustomers.Count, Is.EqualTo(2));
    }

    [Test]
    public void Order_GlobalQueryFilter_ExcludesSoftDeletedOrders()
    {
        // Create a customer to serve as FK target for orders.
        var customer = new Customer
        {
            CustomerGuid = Guid.NewGuid(),
            Deleted = false,
            Active = true,
            CreatedOnUtc = DateTime.UtcNow
        };
        DbContext.Customers.Add(customer);
        DbContext.SaveChanges();

        // Seed an active order.
        var activeOrder = new Order
        {
            OrderGuid = Guid.NewGuid(),
            CustomerId = customer.Id,
            Deleted = false,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };

        // Seed a soft-deleted order.
        var deletedOrder = new Order
        {
            OrderGuid = Guid.NewGuid(),
            CustomerId = customer.Id,
            Deleted = true,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };

        DbContext.Orders.AddRange(activeOrder, deletedOrder);
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        // Standard query should exclude the soft-deleted order via global query filter.
        var orders = DbContext.Orders.ToList();
        Assert.That(orders.Count, Is.EqualTo(1));
        orders[0].Id.ShouldEqual(activeOrder.Id);

        // Query with IgnoreQueryFilters should include both orders.
        var allOrders = DbContext.Orders.IgnoreQueryFilters().ToList();
        Assert.That(allOrders.Count, Is.EqualTo(2));
    }

    [Test]
    public void ProcessFilter_BaseQuery_ExcludesSoftDeletedCustomersViaGlobalFilter()
    {
        // Seed a regular customer (not deleted, not system account).
        var regularCustomer = new Customer
        {
            CustomerGuid = Guid.NewGuid(),
            Deleted = false,
            IsSystemAccount = false,
            Active = true,
            CreatedOnUtc = DateTime.UtcNow
        };

        // Seed a soft-deleted customer (should be excluded by global query filter).
        var deletedCustomer = new Customer
        {
            CustomerGuid = Guid.NewGuid(),
            Deleted = true,
            IsSystemAccount = false,
            Active = true,
            CreatedOnUtc = DateTime.UtcNow
        };

        // Seed a system account customer (excluded by explicit !x.IsSystemAccount predicate).
        var systemCustomer = new Customer
        {
            CustomerGuid = Guid.NewGuid(),
            Deleted = false,
            IsSystemAccount = true,
            Active = true,
            CreatedOnUtc = DateTime.UtcNow,
            SystemName = "SearchEngine"
        };

        DbContext.Customers.AddRange(regularCustomer, deletedCustomer, systemCustomer);
        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();

        // Replicate the ProcessFilter base query from TargetGroupService:
        //   _db.Customers.AsNoTracking().Where(x => !x.IsSystemAccount)
        // This query does NOT have an explicit !x.Deleted predicate. The soft-deleted
        // customer should still be excluded because of the global query filter defined
        // in CustomerMap: builder.HasQueryFilter(c => !c.Deleted).
        var baseQuery = DbContext.Customers.AsNoTracking().Where(x => !x.IsSystemAccount);
        var results = baseQuery.ToList();

        Assert.That(results.Count, Is.EqualTo(1),
            "Only the non-deleted, non-system customer should be returned. " +
            "The soft-deleted customer should be excluded by the global query filter " +
            "without needing an explicit !x.Deleted predicate.");
        results[0].Id.ShouldEqual(regularCustomer.Id);
    }
}
