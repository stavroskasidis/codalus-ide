using System.CodeDom;
using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Reflection;

// The replacement of the designer server's CodeDOM serialization and collection editor (#461, ADR 0106). Codalus's
// designer doesn't serialize through CodeDOM: it runs InitializeComponent's statements itself and writes code from the
// surface's state (ADR 0073). A vendor's serializers derive from these so its assemblies load; nothing calls them, and a
// call says so.

namespace Microsoft.DotNet.DesignTools.Serialization
{
    public abstract class CodeDomSerializerBase
    {
        internal static NotSupportedException NotUsed() => new("Codalus's Windows Forms designer writes the code itself; control libraries' CodeDOM serializers aren't used (ADR 0106).");

        protected virtual object DeserializeInstance(IDesignerSerializationManager manager, Type type, object?[]? parameters, string? name, bool addToContainer) => throw NotUsed();

        protected object? DeserializeExpression(IDesignerSerializationManager manager, string? name, CodeExpression expression) => throw NotUsed();

        protected void DeserializePropertiesFromResources(IDesignerSerializationManager manager, object value, Attribute[]? filter) => throw NotUsed();

        protected CodeExpression? GetExpression(IDesignerSerializationManager manager, object value) => throw NotUsed();

        protected string GetUniqueName(IDesignerSerializationManager manager, object value) => throw NotUsed();

        protected bool IsSerialized(IDesignerSerializationManager manager, object value) => throw NotUsed();

        protected CodeExpression? SerializeCreationExpression(IDesignerSerializationManager manager, object value, out bool isComplete) => throw NotUsed();

        protected void SerializeEvents(IDesignerSerializationManager manager, CodeStatementCollection statements, object value, params Attribute[]? filter) => throw NotUsed();

        protected void SerializeProperties(IDesignerSerializationManager manager, CodeStatementCollection statements, object value, Attribute[]? filter) => throw NotUsed();

        protected void SerializePropertiesToResources(IDesignerSerializationManager manager, CodeStatementCollection statements, object value, Attribute[]? filter) => throw NotUsed();

        protected void SerializeResource(IDesignerSerializationManager manager, string resourceName, object? value) => throw NotUsed();

        protected void SerializeResourceInvariant(IDesignerSerializationManager manager, string resourceName, object? value) => throw NotUsed();

        protected CodeExpression? SerializeToExpression(IDesignerSerializationManager manager, object? value) => throw NotUsed();

        protected CodeExpression? SerializeToResourceExpression(IDesignerSerializationManager manager, object? value) => throw NotUsed();

        protected void SetExpression(IDesignerSerializationManager manager, object value, CodeExpression expression) => throw NotUsed();

        protected void SetExpression(IDesignerSerializationManager manager, object value, CodeExpression expression, bool isPreset) => throw NotUsed();
    }

    public class CodeDomSerializer : CodeDomSerializerBase
    {
        public virtual object? Deserialize(IDesignerSerializationManager manager, object codeObject) => throw NotUsed();

        public virtual object? Serialize(IDesignerSerializationManager manager, object value) => throw NotUsed();

        public virtual CodeStatementCollection SerializeMember(IDesignerSerializationManager manager, object owningObject, MemberDescriptor member) => throw NotUsed();
    }

    public class CollectionCodeDomSerializer : CodeDomSerializer
    {
        protected bool MethodSupportsSerialization(MethodInfo method) => throw NotUsed();

        protected virtual object? SerializeCollection(IDesignerSerializationManager manager, CodeExpression targetExpression, Type targetType, ICollection originalCollection, ICollection valuesToSerialize) => throw NotUsed();
    }

    public class TypeCodeDomSerializer : CodeDomSerializerBase
    {
        public virtual CodeTypeDeclaration Serialize(IDesignerSerializationManager manager, object root, ICollection? members) => throw NotUsed();
    }

    public class DesignerSerializationManager
    {
        public DesignerSerializationManager(IServiceProvider provider)
        {
        }

        public bool PreserveNames { get; set; }

        public object? PropertyProvider { get; set; }

        public bool RecycleInstances { get; set; }

        public IDisposable CreateSession() => throw CodeDomSerializerBase.NotUsed();
    }

    public sealed class ExpressionContext
    {
        public ExpressionContext(CodeExpression expression, Type expressionType, object owner)
        {
            Expression = expression;
            ExpressionType = expressionType;
            Owner = owner;
        }

        public CodeExpression Expression { get; }

        public Type ExpressionType { get; }

        public object Owner { get; }
    }

    public sealed class RootContext
    {
        public RootContext(CodeExpression expression, object value)
        {
            Expression = expression;
            Value = value;
        }

        public CodeExpression Expression { get; }

        public object Value { get; }
    }

    public sealed class SerializeAbsoluteContext
    {
        public SerializeAbsoluteContext()
        {
        }

        public SerializeAbsoluteContext(MemberDescriptor? member) => Member = member;

        public MemberDescriptor? Member { get; }

        public bool ShouldSerialize(MemberDescriptor? member) => Member is null || Member == member;
    }
}

namespace Microsoft.DotNet.DesignTools.Editors
{
    /// <summary>
    /// The designer server's collection editor. Codalus's Properties window has its own collection editor; a vendor's
    /// editor deriving from this one is shown as Windows Forms' would be, which isn't supported yet (ADR 0106).
    /// </summary>
    public class CollectionEditor
    {
        public CollectionEditor(IServiceProvider serviceProvider, Type collectionType)
        {
            ServiceProvider = serviceProvider;
            CollectionType = collectionType;
        }

        protected Type CollectionType { get; }

        protected IServiceProvider ServiceProvider { get; }
    }
}

namespace Microsoft.DotNet.DesignTools.Serialization
{
    /// <summary>
    /// The serialization engine's cache of components' code, as Windows Forms' own engine has one. Control libraries find
    /// it by name (DevExpress's <c>ComponentCacheHelper</c>) to drop a component's entry when it changes; Codalus writes
    /// the code from the surface's state and keeps no such cache, so there is nothing to drop.
    /// </summary>
    internal sealed class ComponentCache : IDisposable
    {
        internal void RemoveEntry(object component)
        {
        }

        public void Dispose()
        {
        }
    }
}
