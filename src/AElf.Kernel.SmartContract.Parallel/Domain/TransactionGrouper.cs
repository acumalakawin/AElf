using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace AElf.Kernel.SmartContract.Parallel
{
    public class GrouperOptions
    {
        public int GroupingTimeOut { get; set; } = 500; // ms
        public int MaxTransactions { get; set; } = int.MaxValue; // Maximum transactions to group
        public int MinTransactionCountInGroup { get; set; } = 10;
        public int MaxGroupCount { get; set; } = 10;
    }

    public class TransactionGrouper : ITransactionGrouper, ISingletonDependency
    {
        private readonly IResourceExtractionService _resourceExtractionService;
        private readonly GrouperOptions _options;
        public ILogger<TransactionGrouper> Logger { get; set; }

        public TransactionGrouper(IResourceExtractionService resourceExtractionService,
            IOptionsSnapshot<GrouperOptions> options)
        {
            _resourceExtractionService = resourceExtractionService;
            _options = options.Value;
            Logger = NullLogger<TransactionGrouper>.Instance;
        }

        public async Task<GroupedTransactions> GroupAsync(IChainContext chainContext, List<Transaction> transactions)
        {
            Logger.LogTrace("Entered GroupAsync");

            var toBeGrouped = GetTransactionsToBeGrouped(transactions, out var groupedTransactions);

            using (var cts = new CancellationTokenSource(_options.GroupingTimeOut))
            {
                var parallelizables = new List<TransactionWithResourceInfo>();

                Logger.LogTrace("Extracting resources for transactions.");
                var txsWithResources =
                    await _resourceExtractionService.GetResourcesAsync(chainContext, toBeGrouped, cts.Token);
                Logger.LogTrace("Completed resource extraction.");

                foreach (var twr in txsWithResources)
                {
                    if (twr.TransactionResourceInfo.ParallelType == ParallelType.InvalidContractAddress)
                    {
                        groupedTransactions.TransactionsWithoutContract.Add(twr.Transaction);
                        continue;
                    }

                    // If timed out at this point, return all transactions as non-parallelizable
                    if (cts.IsCancellationRequested)
                    {
                        groupedTransactions.NonParallelizables.Add(twr.Transaction);
                        continue;
                    }

                    if (twr.TransactionResourceInfo.ParallelType == ParallelType.NonParallelizable)
                    {
                        groupedTransactions.NonParallelizables.Add(twr.Transaction);
                        continue;
                    }

                    if (twr.TransactionResourceInfo.Paths.Count == 0)
                    {
                        // groups.Add(new List<Transaction>() {twr.Item1}); // Run in their dedicated group
                        groupedTransactions.NonParallelizables.Add(twr.Transaction);
                        continue;
                    }

                    parallelizables.Add(twr);
                }

                var groupedParallelizables = GroupParallelizables(parallelizables);
                if (groupedParallelizables.Count > 0)
                {
                    groupedTransactions.Parallelizables.AddRange(GroupParallelizables(groupedParallelizables));
                    Logger.LogDebug(
                        $"Group {groupedParallelizables.Count} groups into {groupedTransactions.Parallelizables.Count} groups");
                }
                Logger.LogTrace("Completed transaction grouping.");
            }

            Logger.LogDebug($"From {transactions.Count} transactions, grouped {groupedTransactions.Parallelizables.Sum(p=>p.Count)} txs into " +
                            $"{groupedTransactions.Parallelizables.Count} groups, left " +
                            $"{groupedTransactions.NonParallelizables.Count} as non-parallelizable transactions.");

            return groupedTransactions;
        }

        private List<Transaction> GetTransactionsToBeGrouped(List<Transaction> transactions,
            out GroupedTransactions groupedTransactions)
        {
            List<Transaction> toBeGrouped;
            groupedTransactions = new GroupedTransactions();
            if (transactions.Count > _options.MaxTransactions)
            {
                groupedTransactions.NonParallelizables.AddRange(
                    transactions.GetRange(_options.MaxTransactions, transactions.Count - _options.MaxTransactions));

                toBeGrouped = transactions.GetRange(0, _options.MaxTransactions);
            }
            else
            {
                toBeGrouped = transactions;
            }

            return toBeGrouped;
        }

        private List<List<Transaction>> GroupParallelizables(List<TransactionWithResourceInfo> txsWithResources)
        {
            var resourceUnionSet = new Dictionary<int, UnionFindNode>();
            var transactionResourceHandle = new Dictionary<Transaction, int>();
            var groups = new List<List<Transaction>>();

            foreach (var txWithResource in txsWithResources)
            {
                UnionFindNode first = null;
                var transaction = txWithResource.Transaction;
                var transactionResourceInfo = txWithResource.TransactionResourceInfo;

                // Add resources to disjoint-set, later each resource will be connected to a node id, which will be our group id
                foreach (var resource in transactionResourceInfo.Paths.Select(p => p.GetHashCode()))
                {
                    if (!resourceUnionSet.TryGetValue(resource, out var node))
                    {
                        node = new UnionFindNode();
                        resourceUnionSet.Add(resource, node);
                    }

                    if (first == null)
                    {
                        first = node;
                        transactionResourceHandle.Add(transaction, resource);
                    }
                    else
                    {
                        node.Union(first);
                    }
                }
            }

            var grouped = new Dictionary<int, List<Transaction>>();

            foreach (var txWithResource in txsWithResources)
            {
                var transaction = txWithResource.Transaction;
                if (!transactionResourceHandle.TryGetValue(transaction, out var firstResource))
                    continue;

                // Node Id will be our group id
                var gId = resourceUnionSet[firstResource].Find().NodeId;

                if (!grouped.TryGetValue(gId, out var gTransactions))
                {
                    gTransactions = new List<Transaction>();
                    grouped.Add(gId, gTransactions);
                }

                // Add transaction to its group
                gTransactions.Add(transaction);
            }

            groups.AddRange(grouped.Values);

            return groups;
        }

        /// <summary>
        /// Transaction count in group should be greater than <see cref="GrouperOptions.MinTransactionCountInGroup"/>
        /// Group count should be less than or equal to <see cref="GrouperOptions.MaxGroupCount"/>
        /// </summary>
        /// <param name="groupedParallelizables"></param>
        /// <returns></returns>
        private List<List<Transaction>> GroupParallelizables(List<List<Transaction>> groupedParallelizables)
        {
            if (groupedParallelizables.Count <= 1) return groupedParallelizables;
            var total = groupedParallelizables.Sum(g => g.Count);
            var minTransactionCount = _options.MinTransactionCountInGroup;
            if (total <= minTransactionCount)
            {
                var transactions = groupedParallelizables.SelectMany(g=>g).ToList();
                return new List<List<Transaction>> {transactions};
            }
            var groupCount = (int) Math.Round((decimal) total / minTransactionCount);
            groupCount = groupCount > _options.MaxGroupCount ? _options.MaxGroupCount : groupCount;
            var mean = total / groupCount + (total % groupCount > 0 ? 1 : 0);
            groupedParallelizables = groupedParallelizables.OrderBy(g => g.Count).ToList();
            var newGroupdParallelizables = new List<List<Transaction>>();
            for (var i = 0; i < groupCount; i++)
            {
                var list = new List<Transaction>();
                var tempList = groupedParallelizables.ToList();
                foreach (var transactions in tempList)
                {
                    if (transactions.Count >= mean)
                    {
                        newGroupdParallelizables.Add(transactions);
                        groupedParallelizables.Remove(transactions);
                        continue;
                    }
                    list.AddRange(transactions);
                    groupedParallelizables.Remove(transactions);
                    if (list.Count >= mean) break;
                }
                if(list.Count == 0) continue;
                newGroupdParallelizables.Add(list);
            }
            return newGroupdParallelizables;
        }
    }
}