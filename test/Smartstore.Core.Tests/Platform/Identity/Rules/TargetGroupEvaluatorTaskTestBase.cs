using System;
using System.Collections.Generic;
using System.Linq;
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
using Smartstore.Scheduling;
using Smartstore.Test.Common;
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

public abstract class TargetGroupEvaluatorTaskTestBase : ServiceTestBase
{
    protected Mock<ICacheManager> _cacheMock;
    protected Mock<IRuleService> _ruleServiceMock;
    protected Mock<IRuleProviderFactory> _ruleProviderFactoryMock;
    protected Mock<ITargetGroupService> _targetGroupServiceMock;
    protected Mock<ITaskStore> _taskStoreMock;
    protected Mock<IAsyncState> _asyncStateMock;
    protected Mock<IComponentContext> _componentContextMock;

    protected TestableTargetGroupEvaluatorTask _sut;

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

    protected void CleanUpDatabase()
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

    protected TaskExecutionContext CreateTaskContext(IDictionary<string, string> parameters = null)
        => CreateMockableTaskContext(parameters).Object;

    protected Mock<TaskExecutionContext> CreateMockableTaskContext(IDictionary<string, string> parameters = null)
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

        return ctxMock;
    }

    protected CustomerRole SeedRoleWithRuleSet(int roleId, string systemName, int ruleSetId)
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

    protected void SetupProcessFilter(IQueryable<Customer> customerQuery)
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

    protected void SeedCustomers(int startId, int count)
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

    protected void SeedSystemMapping(int customerId, int customerRoleId, bool isSystemMapping = true)
    {
        DbContext.CustomerRoleMappings.Add(new CustomerRoleMapping
        {
            CustomerId = customerId,
            CustomerRoleId = customerRoleId,
            IsSystemMapping = isSystemMapping
        });

        DbContext.SaveChanges();
        DbContext.ChangeTracker.Clear();
    }
}
