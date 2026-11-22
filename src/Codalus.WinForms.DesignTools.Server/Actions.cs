using System.Collections;
using System.ComponentModel;
using Codalus.WinForms.DesignTools;
using Microsoft.WinForms.Utilities.Shared;
using Microsoft.DotNet.DesignTools.Designers.Actions;
using Swf = System.ComponentModel.Design;

// The replacement of the designer server's designer actions (#461, ADR 0106): a smart tag's lists and items. A list is
// Windows Forms' own DesignerActionList (through DesignerActionListBridge), so the surface's DesignerActionService and
// the host's smart-tag code read it as any list: its items come back as Windows Forms' items, and its properties and
// methods are the list's own members, found by name as Windows Forms finds them.

namespace Microsoft.DotNet.DesignTools.Designers.Actions
{
    /// <summary>Which of a component's action lists to get.</summary>
    public enum ComponentActionsType
    {
        All,
        Component,
        Service,
    }

    /// <summary>An item of a smart tag.</summary>
    public abstract class DesignerActionItem
    {
        private IDictionary? _properties;

        protected DesignerActionItem(string? displayName, string? category, string? description)
        {
            DisplayName = displayName?.Replace("&", string.Empty, StringComparison.Ordinal);
            Category = category;
            Description = description;
        }

        public bool AllowAssociate { get; set; }

        public virtual string? Category { get; }

        public virtual string? Description { get; }

        public virtual string? DisplayName { get; }

        public IDictionary Properties => _properties ??= new Hashtable();

        public bool ShowInSourceView { get; set; } = true;

        /// <summary>Windows Forms' item for this one, for the surface (made once).</summary>
        internal Swf.DesignerActionItem? Bridged { get; set; }
    }

    /// <summary>A smart tag's heading.</summary>
    public class DesignerActionTextItem : DesignerActionItem
    {
        public DesignerActionTextItem(string? displayName, string? category)
            : base(displayName, category, null)
        {
        }
    }

    /// <summary>A smart tag's group heading.</summary>
    public sealed class DesignerActionHeaderItem : DesignerActionTextItem
    {
        public DesignerActionHeaderItem(string displayName)
            : base(displayName, displayName)
        {
        }

        public DesignerActionHeaderItem(string displayName, string category)
            : base(displayName, category)
        {
        }
    }

    /// <summary>A smart tag's property: the list's own property named <see cref="MemberName"/>.</summary>
    public sealed class DesignerActionPropertyItem : DesignerActionItem
    {
        public DesignerActionPropertyItem(string memberName, string displayName)
            : this(memberName, displayName, null, null)
        {
        }

        public DesignerActionPropertyItem(string memberName, string displayName, string? category)
            : this(memberName, displayName, category, null)
        {
        }

        public DesignerActionPropertyItem(string memberName, string displayName, string? category, string? description)
            : base(displayName, category, description) => MemberName = memberName;

        public string MemberName { get; }

        public IComponent? RelatedComponent { get; set; }
    }

    /// <summary>A smart tag's link: the list's own method named <see cref="MemberName"/>, or what <see cref="Invoke"/> does.</summary>
    public class DesignerActionMethodItem : DesignerActionItem
    {
        private readonly DesignerActionList? _actionList;

        public DesignerActionMethodItem(DesignerActionList actionList, string memberName, string displayName)
            : this(actionList, memberName, displayName, null, null, false)
        {
        }

        public DesignerActionMethodItem(DesignerActionList actionList, string memberName, string displayName, bool includeAsDesignerVerb)
            : this(actionList, memberName, displayName, null, null, includeAsDesignerVerb)
        {
        }

        public DesignerActionMethodItem(DesignerActionList actionList, string memberName, string displayName, string? category)
            : this(actionList, memberName, displayName, category, null, false)
        {
        }

        public DesignerActionMethodItem(DesignerActionList actionList, string memberName, string displayName, string? category, bool includeAsDesignerVerb)
            : this(actionList, memberName, displayName, category, null, includeAsDesignerVerb)
        {
        }

        public DesignerActionMethodItem(DesignerActionList actionList, string memberName, string displayName, string? category, string? description)
            : this(actionList, memberName, displayName, category, description, false)
        {
        }

        public DesignerActionMethodItem(DesignerActionList actionList, string memberName, string displayName, string? category, string? description, bool includeAsDesignerVerb)
            : base(displayName, category, description)
        {
            _actionList = actionList;
            MemberName = memberName;
            IncludeAsDesignerVerb = includeAsDesignerVerb;
        }

        public virtual bool IncludeAsDesignerVerb { get; }

        public virtual string MemberName { get; }

        public IComponent? RelatedComponent { get; set; }

        /// <summary>Calls the list's method named <see cref="MemberName"/>.</summary>
        public virtual void Invoke()
        {
            if (_actionList is null || MemberName is null)
            {
                throw new InvalidOperationException("The action has no list or no method to invoke.");
            }

            var method = _actionList.GetType().GetMethod(MemberName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                ?? throw new InvalidOperationException($"The action list {_actionList.GetType().FullName} has no method {MemberName}.");
            method.Invoke(_actionList, null);
        }
    }

    /// <summary>A smart tag's items.</summary>
    public class DesignerActionItemCollection : NonNullCollection<DesignerActionItem>
    {
    }

    /// <summary>
    /// A smart tag's list of items. It is Windows Forms' own <see cref="Swf.DesignerActionList"/> (through
    /// <see cref="DesignerActionListBridge"/>), so the surface shows it as it shows any list.
    /// </summary>
    public class DesignerActionList : DesignerActionListBridge
    {
        public DesignerActionList(IComponent? component)
            : base(component)
        {
        }

        public new IComponent? Component => base.Component;

        public override bool AutoShow { get; set; }

        /// <summary>The service from the component's designer host, as the vendor's designer sees it (its designers, not the surface's adapters).</summary>
        public new object? GetService(Type serviceType) => VendorServices.Get(Component, serviceType, () => base.GetService(serviceType));

        public T? GetService<T>() => (T?)GetService(typeof(T));

        public new virtual DesignerActionItemCollection GetSortedActionItems() => [];

        internal sealed override DesignerActionItemCollection VendorItems() => GetSortedActionItems();
    }

    /// <summary>A component's smart tag lists.</summary>
    public class DesignerActionListCollection : NonNullCollection<DesignerActionList>
    {
        public DesignerActionListCollection()
        {
        }

        public DesignerActionListCollection(IEnumerable<DesignerActionList> lists)
            : base(lists)
        {
        }
    }

    /// <summary>The smart tag lists the surface has for a component: the replacement's lists among Windows Forms' service's.</summary>
    public class DesignerActionService : DisposableObject
    {
        private readonly Swf.DesignerActionService? _inner;

        internal DesignerActionService(Swf.DesignerActionService? inner) => _inner = inner;

        public DesignerActionListCollection GetComponentActions(IComponent component) => GetComponentActions(component, ComponentActionsType.All);

        public virtual DesignerActionListCollection GetComponentActions(IComponent component, ComponentActionsType type)
        {
            var lists = new DesignerActionListCollection();
            if (_inner is not null)
            {
                foreach (var list in _inner.GetComponentActions(component))
                {
                    if (list is DesignerActionList vendor)
                    {
                        lists.Add(vendor);
                    }
                }
            }

            return lists;
        }

        public void Clear() => _inner?.Clear();

        public void Remove(IComponent component) => _inner?.Remove(component);
    }

    /// <summary>Shows, hides and refreshes smart tags: Windows Forms' service's, which Codalus's shell asks again.</summary>
    public class DesignerActionUIService : DisposableObject
    {
        private readonly Swf.DesignerActionUIService? _inner;

        internal DesignerActionUIService(Swf.DesignerActionUIService? inner) => _inner = inner;

        public void HideUI(IComponent component) => _inner?.HideUI(component);

        public void Refresh(IComponent component) => _inner?.Refresh(component);

        public void ShowUI(IComponent component) => _inner?.ShowUI(component);
    }
}

namespace Codalus.WinForms.DesignTools
{
    /// <summary>
    /// Codalus's bridge between the replacement's <see cref="DesignerActionList"/> and Windows Forms': Windows Forms reads
    /// a list's items through its own <c>GetSortedActionItems</c>, which this turns from the vendor's items into Windows
    /// Forms' (a method item that calls the vendor's <c>Invoke</c>), and the vendor's <c>GetSortedActionItems</c> (another
    /// signature) is the replacement's. Public only because the replacement's list derives from it.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract class DesignerActionListBridge : Swf.DesignerActionList
    {
        private protected DesignerActionListBridge(IComponent? component)
            : base(component)
        {
        }

        internal abstract DesignerActionItemCollection VendorItems();

        public sealed override Swf.DesignerActionItemCollection GetSortedActionItems()
        {
            var items = new Swf.DesignerActionItemCollection();
            foreach (var item in VendorItems())
            {
                items.Add(item.Bridged ??= Bridge(item));
            }

            return items;
        }

        private Swf.DesignerActionItem Bridge(DesignerActionItem item)
        {
            Swf.DesignerActionItem bridged = item switch
            {
                DesignerActionHeaderItem header => new Swf.DesignerActionHeaderItem(header.DisplayName ?? string.Empty, header.Category ?? string.Empty),
                DesignerActionTextItem text => new Swf.DesignerActionTextItem(text.DisplayName ?? string.Empty, text.Category ?? string.Empty),
                DesignerActionPropertyItem property => new Swf.DesignerActionPropertyItem(property.MemberName, property.DisplayName ?? property.MemberName, property.Category, property.Description) { RelatedComponent = property.RelatedComponent },
                DesignerActionMethodItem method => new MethodItem(this, method) { RelatedComponent = method.RelatedComponent },
                _ => new Swf.DesignerActionTextItem(item.DisplayName ?? string.Empty, item.Category ?? string.Empty),
            };
            bridged.AllowAssociate = item.AllowAssociate;
            bridged.ShowInSourceView = item.ShowInSourceView;
            foreach (DictionaryEntry entry in item.Properties)
            {
                bridged.Properties[entry.Key] = entry.Value;
            }

            return bridged;
        }

        /// <summary>A vendor's method item, which may override <c>Invoke</c>, as Windows Forms' item.</summary>
        private sealed class MethodItem(Swf.DesignerActionList list, DesignerActionMethodItem vendor)
            : Swf.DesignerActionMethodItem(list, vendor.MemberName, vendor.DisplayName ?? vendor.MemberName, vendor.Category, vendor.Description, vendor.IncludeAsDesignerVerb)
        {
            public override void Invoke() => vendor.Invoke();
        }
    }
}
