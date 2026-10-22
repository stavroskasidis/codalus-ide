using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing.Design;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Windows.Forms.Design;

namespace Codalus.WinForms.Designer;

/// <summary>
/// The design surface's UI service (#66), as the classic IDE gives its designers one: what a designer would show as an
/// error or a message (a container refusing a tool, a tab control with no page) is collected for the change's
/// <c>error</c> and written to the Windows Forms Designer output, never a modal message box in the hidden host; a
/// dialog a designer would open is refused. The styles are the ones designers read without checking for them.
/// </summary>
internal sealed class DesignerUIService(IWin32Window owner) : IUIService
{
    private readonly Hashtable _styles = new()
    {
        ["DialogFont"] = SystemFonts.MessageBoxFont ?? Control.DefaultFont,
        ["HighlightColor"] = SystemColors.Highlight,
    };

    /// <summary>What the designers said since the last <see cref="Clear"/>.</summary>
    public List<string> Messages { get; } = [];

    public IDictionary Styles => _styles;

    public void Clear() => Messages.Clear();

    public bool CanShowComponentEditor(object? component) => false;

    public IWin32Window GetDialogOwnerWindow() => owner;

    public void SetUIDirty()
    {
    }

    public bool ShowComponentEditor(object? component, IWin32Window? parent) => false;

    public DialogResult ShowDialog(Form? form) => DialogResult.Cancel;

    public void ShowError(string? message) => Add(message);

    public void ShowError(Exception? ex) => Add(MessageOf(ex));

    public void ShowError(Exception? ex, string? message) => Add(string.IsNullOrWhiteSpace(message) ? MessageOf(ex) : message);

    public void ShowMessage(string? message) => Add(message);

    public void ShowMessage(string? message, string? caption) => Add(message);

    public DialogResult ShowMessage(string? message, string? caption, MessageBoxButtons buttons)
    {
        Add(message);
        return buttons switch
        {
            MessageBoxButtons.OK => DialogResult.OK,
            MessageBoxButtons.YesNo => DialogResult.No,
            _ => DialogResult.Cancel,
        };
    }

    public bool ShowToolWindow(Guid toolWindow) => false;

    private static string? MessageOf(Exception? ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex?.Message;

    private void Add(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Messages.Add(message.Trim());
            Console.Error.WriteLine("[designer] " + message.Trim());
        }
    }
}

/// <summary>
/// The design surface's type resolution (#66): a Toolbox item finds its type (the project's own controls among them)
/// the way the form's code found it, deterministically, instead of loading assemblies by name.
/// </summary>
internal sealed class DesignerTypeResolution(DesignerTypes types) : ITypeResolutionService
{
    public Assembly? GetAssembly(AssemblyName name) => GetAssembly(name, throwOnError: false);

    public Assembly? GetAssembly(AssemblyName name, bool throwOnError)
    {
        var loaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => !a.IsDynamic && string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
        if (loaded is not null)
        {
            return loaded;
        }

        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(name);
        }
        catch (Exception ex) when (!throwOnError && ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    public Type? GetType(string name) => GetType(name, throwOnError: false, ignoreCase: false);

    public Type? GetType(string name, bool throwOnError) => GetType(name, throwOnError, ignoreCase: false);

    public Type? GetType(string name, bool throwOnError, bool ignoreCase)
    {
        var type = types.Find(name) ?? Type.GetType(name, throwOnError: false, ignoreCase);
        if (type is null && throwOnError)
        {
            throw new TypeLoadException($"The type {name} wasn't found (build the project?).");
        }

        return type;
    }

    public void ReferenceAssembly(AssemblyName name)
    {
    }

    public string? GetPathOfAssembly(AssemblyName name) => GetAssembly(name)?.Location is { Length: > 0 } location ? location : null;
}

/// <summary>
/// The classic IDE's Toolbox creation (#66): the type's own Toolbox item (its <see cref="ToolboxItemAttribute"/>), and
/// the container designer's <c>CreateToolCore</c>, which a container overrides to route the tool (a tab control to its
/// selected page, a split container to its panel) and whose drag and drop handler creates it with the defaults the
/// designers act on (<c>Parent</c>, <c>Location</c>, <c>Size</c>).
/// </summary>
internal static class ToolInvoker
{
    /// <summary>The classic IDE's item for labels (the attribute on Label and LinkLabel), which .NET's Windows Forms names but doesn't ship.</summary>
    private const string AutoSizeToolboxItemName = "System.Windows.Forms.Design.AutoSizeToolboxItem";

    /// <summary>ParentControlDesigner's protected <c>CreateToolCore</c> (stable since .NET Framework 2.0; the host runs on the app's own Windows Forms), invoked with virtual dispatch.</summary>
    private static readonly MethodInfo? s_createToolCore = typeof(ParentControlDesigner).GetMethod(
        "CreateToolCore", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(ToolboxItem), typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool)], null);

    /// <summary>The Toolbox item the classic IDE creates <paramref name="type"/> with: its <see cref="ToolboxItemAttribute"/>'s, else a plain one.</summary>
    public static ToolboxItem ToolFor(Type type, DesignerTypes types)
    {
        if (TypeDescriptor.GetAttributes(type)[typeof(ToolboxItemAttribute)] is not ToolboxItemAttribute { ToolboxItemTypeName: { Length: > 0 } typeName } attribute)
        {
            return new ToolboxItem(type);
        }

        Type? itemType = null;
        try
        {
            itemType = attribute.ToolboxItemType;
        }
        catch (ArgumentException)
        {
            // Not loadable by its assembly-qualified name (System.Design is a facade on .NET): by its full name below.
        }

        var name = typeName.Split(',')[0].Trim();
        itemType ??= name == AutoSizeToolboxItemName ? typeof(AutoSizeToolboxItem)
            : types.Find(name) ?? typeof(ParentControlDesigner).Assembly.GetType(name, throwOnError: false);
        if (itemType is not null && typeof(ToolboxItem).IsAssignableFrom(itemType) && itemType.GetConstructor([typeof(Type)]) is not null)
        {
            try
            {
                return (ToolboxItem)Activator.CreateInstance(itemType, type)!;
            }
            catch (TargetInvocationException ex)
            {
                Console.Error.WriteLine($"[designer] {name} couldn't be created for {type.FullName}: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        return new ToolboxItem(type);
    }

    /// <summary>
    /// <paramref name="designer"/>'s <c>CreateToolCore</c>: at a screen <paramref name="x"/>, <paramref name="y"/> when
    /// <paramref name="hasLocation"/>, drawn at a size when <paramref name="hasSize"/>, else at its default place. What
    /// it creates may be null (a container that routed the tool to another designer returns nothing).
    /// </summary>
    public static IComponent[]? Create(ParentControlDesigner designer, ToolboxItem tool, int x, int y, int width, int height, bool hasLocation, bool hasSize)
    {
        var method = s_createToolCore ?? throw new InvalidOperationException("This Windows Forms has no ParentControlDesigner.CreateToolCore: the designer can't create Toolbox items.");
        try
        {
            return (IComponent[]?)method.Invoke(designer, [tool, x, y, width, height, hasLocation, hasSize]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }
}

/// <summary>
/// The classic IDE's <c>AutoSizeToolboxItem</c>, which Label's and LinkLabel's <see cref="ToolboxItemAttribute"/> name:
/// a control created from the Toolbox sizes itself to its text (<c>label1.AutoSize = true;</c>).
/// </summary>
internal sealed class AutoSizeToolboxItem(Type toolType) : ToolboxItem(toolType)
{
    protected override IComponent[]? CreateComponentsCore(IDesignerHost? host)
    {
        var components = base.CreateComponentsCore(host);
        if (components is [Control control, ..] && TypeDescriptor.GetProperties(control)["AutoSize"] is { PropertyType: var type, IsReadOnly: false } autoSize && type == typeof(bool))
        {
            autoSize.SetValue(control, true);
        }

        return components;
    }
}
