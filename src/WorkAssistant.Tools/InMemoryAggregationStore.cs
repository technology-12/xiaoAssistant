using System;
using System.Collections.Generic;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>
    /// 聚合结果内存暂存。生命周期与单个任务绑定（由宿主在任务开始时创建），
    /// aggregation_id 不能跨任务引用。
    /// </summary>
    public sealed class InMemoryAggregationStore : IAggregationStore
    {
        private readonly Dictionary<string, AggregationResult> _results =
            new(StringComparer.Ordinal);

        public string Add(AggregationResult result)
        {
            var id = "agg_" + Guid.NewGuid().ToString("N");
            _results[id] = result;
            return id;
        }

        public AggregationResult? Find(string aggregationId)
        {
            return _results.TryGetValue(aggregationId, out var result) ? result : null;
        }
    }
}
