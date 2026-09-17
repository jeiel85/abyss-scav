using System.Collections.Concurrent;

namespace AbyssScav.Foundation;

/// <summary>Minimal thread-safe dependency registry (EPIC A-01 bootstrap).</summary>
public sealed class DependencyRegistry
{
    private readonly ConcurrentDictionary<Type, Func<object>> _entries = new();

    public void RegisterInstance<T>(T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        _entries[typeof(T)] = () => instance;
    }

    public void RegisterFactory<T>(Func<T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        _entries[typeof(T)] = () => factory() ?? throw new InvalidOperationException($"Factory for {typeof(T).Name} returned null.");
    }

    public bool IsRegistered<T>() => _entries.ContainsKey(typeof(T));

    public bool TryResolve<T>(out T? service) where T : class
    {
        if (_entries.TryGetValue(typeof(T), out var factory))
        {
            service = (T)factory();
            return true;
        }

        service = null;
        return false;
    }

    public T Resolve<T>() where T : class
    {
        if (TryResolve<T>(out var service) && service is not null)
        {
            return service;
        }

        throw new InvalidOperationException($"[{ErrorCodes.BootConfigCorrupt}] Service not registered: {typeof(T).FullName}");
    }
}
