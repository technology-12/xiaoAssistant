// net40 的 AsyncLocal 等价实现（基线 XP-02：随逻辑执行上下文传递的隔离容器）。
// - 每个 AsyncLocal 实例有唯一键；值存放在不可变链上（写时复制）。
// - XpContinuation/XpTask 在每个 await 边界与任务起点捕获并恢复链头，
//   子 Agent 各自的令牌、工具名与检查点回调不会串扰；禁止 ThreadStatic 替代。

using System;
using System.Threading;
using WorkAssistant.Compatibility.Xp;

namespace System.Threading
{
    public class AsyncLocalValueChangedArgs<T>
    {
        public T PreviousValue { get; } = default!;
        public T CurrentValue { get; } = default!;
        public bool ThreadContextChanged { get; }
    }

    public sealed class AsyncLocal<T>
    {
        private readonly string _key;

        public AsyncLocal() { _key = "XpAsyncLocal:" + Guid.NewGuid().ToString("N"); }
        public AsyncLocal(Action<AsyncLocalValueChangedArgs<T>>? valueChangedHandler) : this() { }

        public T Value
        {
            get
            {
                var current = XpLogicalContext.Get(_key);
                return current is T value ? value : default!;
            }
            set => XpLogicalContext.Set(_key, value);
        }
    }
}

namespace WorkAssistant.Compatibility.Xp
{
    /// <summary>逻辑上下文：不可变键值链 + 写时复制；由兼容层在延续边界流转。</summary>
    internal static class XpLogicalContext
    {
        private sealed class Node
        {
            public readonly string Key;
            public readonly object? Value;
            public readonly Node? Next;
            public Node(string key, object? value, Node? next) { Key = key; Value = value; Next = next; }
        }

        [ThreadStatic]
        private static Node? _current;

        public static object? Capture() => _current;

        public static object? Swap(object? state)
        {
            var previous = _current;
            _current = state as Node;
            return previous;
        }

        public static void Restore(object? state) => _current = state as Node;

        public static object? Get(string key)
        {
            for (var node = _current; node != null; node = node.Next)
            {
                if (node.Key == key) return node.Value;
            }
            return null;
        }

        public static void Set(string key, object? value)
        {
            _current = new Node(key, value, _current);
        }
    }
}
