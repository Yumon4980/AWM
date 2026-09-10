using System;
using System.Collections.Concurrent;
using System.Windows.Threading;

namespace AltTabReplacer.Core.Infrastructure;

/// <summary>
/// 极简进程内事件总线：发布/订阅。
/// 跨线程订阅可使用 <see cref="SubscribeOnUI"/> 自动把回调派发到 UI 线程。
/// </summary>
public sealed class EventBus
{
    private readonly ConcurrentDictionary<Type, Delegate> _handlers = new();

    public void Publish<T>(T evt)
    {
        if (_handlers.TryGetValue(typeof(T), out var del))
        {
            foreach (var handler in del.GetInvocationList())
            {
                try
                {
                    ((Action<T>)handler).Invoke(evt);
                }
                catch (Exception ex)
                {
                    Logger.Error($"EventBus handler threw: {ex}");
                }
            }
        }
    }

    public void Subscribe<T>(Action<T> handler)
    {
        _handlers.AddOrUpdate(
            typeof(T),
            handler,
            (_, existing) => Delegate.Combine(existing, handler)!);
    }

    public void Unsubscribe<T>(Action<T> handler)
    {
        _handlers.AddOrUpdate(
            typeof(T),
            _ => (Action<T>)(_ => { }),
            (_, existing) => (Delegate.Remove(existing, handler) as Action<T>) ?? (_ => { }));
    }

    /// <summary>订阅并把回调强制派发到指定 Dispatcher（通常是 WPF UI 线程）。</summary>
    public IDisposable SubscribeOnUI<T>(Action<T> handler, Dispatcher dispatcher)
    {
        Action<T> wrapped = evt =>
        {
            if (dispatcher.CheckAccess())
                handler(evt);
            else
                dispatcher.BeginInvoke(handler, evt);
        };
        Subscribe(wrapped);
        return new Subscription(() => Unsubscribe(wrapped));
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;
        public Subscription(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _onDispose();
        }
    }
}
