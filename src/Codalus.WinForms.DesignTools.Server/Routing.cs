using System.ComponentModel.Design;
using System.Windows.Forms;
using Microsoft.DotNet.DesignTools.Protocol.Endpoints;
using Microsoft.DotNet.DesignTools.Protocol.Notifications;

// The replacement of Microsoft.DotNet.DesignTools.Server's API (#461, ADR 0106): type routing, the server's request
// handlers, its services and its undo engine, as control libraries' designers use them.

namespace Microsoft.DotNet.DesignTools.TypeRouting
{
    /// <summary>Marks a type routing provider: a design assembly's map from the designer names its controls give to its types.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ExportTypeRoutingDefinitionProviderAttribute : Attribute
    {
    }

    /// <summary>What a name routes to.</summary>
    public enum TypeRoutingKind
    {
        Designer,
        DesignerSerializer,
    }

    /// <summary>A designer (or designer serializer) name and the type it routes to.</summary>
    public sealed class TypeRoutingDefinition
    {
        public TypeRoutingDefinition(TypeRoutingKind kind, string name, Type type)
        {
            Kind = kind;
            Name = name;
            Type = type;
        }

        public TypeRoutingKind Kind { get; }

        public string Name { get; }

        public Type Type { get; }
    }

    /// <summary>A design assembly's routes.</summary>
    public interface ITypeRoutingDefinitionProvider
    {
        IEnumerable<TypeRoutingDefinition> GetDefinitions();
    }

    /// <summary>The base of a design assembly's routes.</summary>
    public abstract class TypeRoutingDefinitionProvider : ITypeRoutingDefinitionProvider
    {
        public virtual IEnumerable<TypeRoutingDefinition> GetDefinitions() => [];

        public static TypeRoutingDefinition RerouteDesigner<T>(string name) => new(TypeRoutingKind.Designer, name, typeof(T));

        public static TypeRoutingDefinition RerouteDesignerSerializer<T>(string name) => new(TypeRoutingKind.DesignerSerializer, name, typeof(T));
    }
}

namespace Microsoft.DotNet.DesignTools.Protocol.Endpoints
{
    /// <summary>Marks a request handler for the classic IDE's composition.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ExportRequestHandlerAttribute : Attribute
    {
        public ExportRequestHandlerAttribute(string endpointName) => EndpointName = endpointName;

        public string EndpointName { get; }
    }

    /// <summary>
    /// Answers a request from the other side of the designer protocol. The classic IDE's process sends them; Codalus's
    /// designer host has no such process, so a vendor's handlers are never called.
    /// </summary>
    public abstract class RequestHandler<TRequest, TResponse>
        where TRequest : Request
        where TResponse : Response
    {
        public IServiceProvider? ServiceProvider { get; set; }

        public virtual TResponse HandleRequest(TRequest request) => throw new NotSupportedException("Codalus's Windows Forms designer carries no designer protocol requests (ADR 0106).");

        protected IDesignerHost? GetDesignerHost(System.ComponentModel.IComponent component) => component?.Site?.GetService(typeof(IDesignerHost)) as IDesignerHost;
    }
}

namespace Microsoft.DotNet.DesignTools.Services
{
    /// <summary>Sends notifications to the classic IDE's process: Codalus's designer host writes them to its log.</summary>
    public interface INotificationService
    {
        void SendNotification(string name, NotificationData data);

        void SendObjectNotification(string name, object target, NotificationData data);
    }

    /// <summary>The designer server's UI service.</summary>
    public interface IUIService
    {
        IWin32Window? GetDialogOwnerWindow();

        DialogResult ShowDialog(Form form);

        void ShowError(string message);

        void ShowMessage(string message);
    }
}

namespace Microsoft.DotNet.DesignTools.Undo
{
    /// <summary>The surface's undo engine, as designers check it. Codalus's undo is the editor's: this only carries the flags.</summary>
    public class UndoEngine : IDisposable
    {
        public bool Enabled { get; set; } = true;

        public bool UndoInProgress { get; protected set; }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }
    }
}
