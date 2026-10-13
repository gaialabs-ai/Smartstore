using System.Diagnostics;
using Smartstore.Caching;
using Smartstore.Core.Data;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Core.Security;
using Smartstore.Data;
using Smartstore.Data.Hooks;
using Smartstore.Scheduling;

namespace Smartstore.Core.Identity.Rules;

public partial class TargetGroupEvaluatorTask(
    SmartDbContext db,
    ICacheManager cache,
    IRuleService ruleService,
    IRuleProviderFactory ruleProviderFactory) : ITask
{
    protected readonly SmartDbContext _db = db;
    protected readonly ICacheManager _cache = cache;
    protected readonly IRuleService _ruleService = ruleService;
    protected readonly ITargetGroupService _targetGroupService = ruleProviderFactory.GetProvider<ITargetGroupService>(RuleScope.Customer);

    /// <summary>
    /// Evaluates all active rule sets attached to customer roles, deletes existing
    /// system-generated customer-role mappings, and batch-inserts new mappings for
    /// matching customers.
    /// </summary>
    /// <param name="ctx">The task execution context providing progress reporting and parameters.</param>
    /// <param name="cancelToken">Token to observe for cancellation at rule evaluation and insertion stages.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// Runs daily at 02:15 by default. Can also be triggered on-demand from the admin UI
    /// for specific customer roles by passing a <c>CustomerRoleIds</c> parameter.
    /// </remarks>
    public async Task Run(TaskExecutionContext ctx, CancellationToken cancelToken = default)
    {
        var count = 0;
        var numDeleted = 0;
        var numAdded = 0;
        var rolesCount = 0;

        using (var scope = new DbContextScope(_db, autoDetectChanges: false, minHookImportance: HookImportance.Important, deferCommit: true))
        {
            // Delete existing system mappings.
            int[] filteredRoleIds = null;
            var deleteQuery = _db.CustomerRoleMappings.Where(x => x.IsSystemMapping);
            if (ctx.Parameters.ContainsKey("CustomerRoleIds"))
            {
                filteredRoleIds = ctx.Parameters["CustomerRoleIds"].ToIntArray();
                deleteQuery = deleteQuery.Where(x => filteredRoleIds.Contains(x.CustomerRoleId));
            }

            numDeleted = await DeleteSystemMappingsAsync(deleteQuery, scope, cancelToken);

            // Insert new customer role mappings.
            var rolesQuery = _db.CustomerRoles
                .Include(x => x.RuleSets)
                .ThenInclude(x => x.Rules)
                .AsNoTracking()
                .AsSplitQuery()
                .Where(x => x.Active && x.RuleSets.Any(y => y.IsActive));

            if (filteredRoleIds != null)
            {
                rolesQuery = rolesQuery.Where(x => filteredRoleIds.Contains(x.Id));
            }

            var roles = await rolesQuery.ToListAsync(cancelToken);
            rolesCount = roles.Count;

            foreach (var role in roles)
            {
                var ruleSetCustomerIds = new HashSet<int>();

                await ctx.SetProgressAsync(++count, roles.Count, $"Add customer assignments for role \"{role.SystemName.NaIfEmpty()}\".");

                // Execute active rule sets and collect customer ids.
                foreach (var ruleSet in role.RuleSets.Where(x => x.IsActive))
                {
                    if (cancelToken.IsCancellationRequested)
                        return;

                    var expressionGroup = await _ruleService.CreateExpressionGroupAsync(ruleSet, _targetGroupService);
                    if (expressionGroup is FilterExpression expression)
                    {
                        var filterResult = _targetGroupService.ProcessFilter(expression, 0, 500);
                        var resultPager = new FastPager<Customer>(filterResult.SourceQuery, 500);

                        while ((await resultPager.ReadNextPageAsync(x => x.Id, x => x, cancelToken)).Out(out var customerIds))
                        {
                            ruleSetCustomerIds.AddRange(customerIds);
                        }
                    }
                }

                // Add mappings.
                if (ruleSetCustomerIds.Any())
                {
                    foreach (var chunk in ruleSetCustomerIds.Chunk(500))
                    {
                        if (cancelToken.IsCancellationRequested)
                            return;

                        foreach (var customerId in chunk)
                        {
                            _db.CustomerRoleMappings.Add(new CustomerRoleMapping
                            {
                                CustomerId = customerId,
                                CustomerRoleId = role.Id,
                                IsSystemMapping = true
                            });

                            ++numAdded;
                        }

                        await scope.CommitAsync(cancelToken);
                        await OnAfterChunkCommittedAsync(cancelToken);
                    }

                    try
                    {
                        scope.DbContext.DetachEntities<CustomerRoleMapping>();
                    }
                    catch
                    {
                    }
                }
            }
        }

        if (numAdded > 0 || numDeleted > 0)
        {
            await _cache.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN);
        }

        Debug.WriteLineIf(numDeleted > 0 || numAdded > 0, $"Deleted {numDeleted} and added {numAdded} customer assignments for {rolesCount} roles.");
    }

    protected virtual async Task<int> DeleteSystemMappingsAsync(
        IQueryable<CustomerRoleMapping> deleteQuery,
        DbContextScope scope,
        CancellationToken cancelToken)
    {
        return await deleteQuery.ExecuteDeleteAsync(cancelToken);
    }

    protected virtual Task OnAfterChunkCommittedAsync(CancellationToken cancelToken)
    {
        return Task.CompletedTask;
    }

}