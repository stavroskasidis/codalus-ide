using System.ComponentModel;

namespace Codalus.WinForms.Designer;

/// <summary>
/// A type converter's context on the design surface (#68): the instance and its property's descriptor, the surface's
/// container and services (IDesignerHost, IReferenceService, the name service…). A converter reads what it needs from
/// it: ReferenceConverter lists the form's components, PaddingConverter tells an All edit from a side's,
/// FontNameConverter lists the families. A wrong one fails silently (an empty list, Padding written per side), as the
/// classic IDE's grid passes the real one.
/// </summary>
internal sealed class DesignerTypeDescriptorContext(IServiceProvider? services, IContainer? container, object? instance, PropertyDescriptor? descriptor) : ITypeDescriptorContext
{
    public IContainer? Container => container;

    public object? Instance => instance;

    public PropertyDescriptor? PropertyDescriptor => descriptor;

    public object? GetService(Type serviceType) => serviceType == typeof(ITypeDescriptorContext) ? this : services?.GetService(serviceType);

    public bool OnComponentChanging() => true;

    public void OnComponentChanged()
    {
    }
}
