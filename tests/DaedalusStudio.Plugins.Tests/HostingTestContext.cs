using System.Text.Json;
using DaedalusStudio.Plugins.Proposed;

namespace DaedalusStudio.Plugins.Tests;

/// <summary>
/// A plugin context for calling a hosting provider directly (ADR 0031): settings from a dictionary, secrets in memory,
/// and the device code the provider shows. Everything else throws: providers only use these.
/// </summary>
internal sealed class HostingTestContext : IPluginContext, IProposedApiProvider, IProposedApi, ISettings, IPluginLog, ISecrets, IHosting, IDeviceCodePrompt
{
    private readonly Dictionary<string, JsonElement> _settings = new(StringComparer.Ordinal);

    public HostingTestContext(params (string Key, string Value)[] settings)
    {
        foreach (var (key, value) in settings)
        {
            _settings[key] = JsonSerializer.SerializeToElement(value);
        }
    }

    public Dictionary<string, string> StoredSecrets { get; } = new(StringComparer.Ordinal);

    public List<DeviceCode> Codes { get; } = [];

    public List<string> Logged { get; } = [];

    public PluginInfo Plugin { get; } = new("daedalus.test", "1.0.0", AppContext.BaseDirectory);

    public ICommands Commands => throw new NotSupportedException();

    public IWindow Window => throw new NotSupportedException();

    public ISettings Settings => this;

    public IPluginLog Log => this;

    public IList<IDisposable> Subscriptions { get; } = [];

    IProposedApi? IProposedApiProvider.Proposed => this;

    public ITests Tests => throw new NotSupportedException();

    public IWorkspace Workspace => throw new NotSupportedException();

    public IBuild Build => throw new NotSupportedException();

    public IDebug Debug => throw new NotSupportedException();

    public ITerminals Terminals => throw new NotSupportedException();

    public IHosting Hosting => this;

    public ISecrets Secrets => this;

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public void Set(string key, string value)
    {
        _settings[key] = JsonSerializer.SerializeToElement(value);
        Changed?.Invoke(this, new SettingsChangedEventArgs([key]));
    }

    public T GetValue<T>(string key, T fallback) =>
        _settings.TryGetValue(key, out var value) ? value.Deserialize<T>() ?? fallback : fallback;

    public void Write(PluginLogLevel level, string message) => Logged.Add($"{level}: {message}");

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(StoredSecrets.GetValueOrDefault(key));

    public Task StoreAsync(string key, string value, CancellationToken cancellationToken)
    {
        StoredSecrets[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        StoredSecrets.Remove(key);
        return Task.CompletedTask;
    }

    public IDisposable RegisterProvider(HostingProvider provider) => throw new NotSupportedException();

    public Task ShowAsync(DeviceCode code, CancellationToken cancellationToken)
    {
        Codes.Add(code);
        return Task.CompletedTask;
    }
}
