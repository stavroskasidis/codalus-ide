using System.Collections.ObjectModel;

// The replacement of Microsoft.WinForms.Utilities.Shared's API (#461, ADR 0106): what control libraries' designers use of it.

namespace Microsoft.WinForms.Utilities.Shared
{
    /// <summary>An object disposed once: <see cref="Dispose()"/> calls <see cref="Dispose(bool)"/> the first time.</summary>
    public abstract class DisposableObject : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }
    }

    /// <summary>A list that refuses null items.</summary>
    public class NonNullCollection<T> : Collection<T>
        where T : class
    {
        public NonNullCollection()
        {
        }

        public NonNullCollection(IEnumerable<T> items) => AddRange(items);

        public void AddRange(IEnumerable<T> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            foreach (var item in items)
            {
                Add(item);
            }
        }

        protected override void InsertItem(int index, T item)
        {
            ArgumentNullException.ThrowIfNull(item);
            base.InsertItem(index, item);
        }

        protected override void SetItem(int index, T item)
        {
            ArgumentNullException.ThrowIfNull(item);
            base.SetItem(index, item);
        }
    }
}

namespace System
{
    /// <summary>Typed service lookups.</summary>
    public static class ServiceExtensions
    {
        public static T? GetService<T>(this IServiceProvider provider) => provider is null ? default : (T?)provider.GetService(typeof(T));

        public static T GetRequiredService<T>(this IServiceProvider provider) =>
            provider.GetService<T>() ?? throw new InvalidOperationException($"The service {typeof(T).FullName} isn't available.");
    }
}
