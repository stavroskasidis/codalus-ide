using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Globalization;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;

namespace Codalus.WinForms.Designer;

/// <summary>
/// The collections the designer edits (M41b, ADR 0073): a list's items, a table's styles, a list view's columns and
/// items, a tab control's pages, a strip's or a menu item's items, a grid's columns, an image list's images. Their
/// editors (and a menu's Type Here) send the whole new list; the session makes the real collection say it, creating
/// and destroying component items through the designer host, and the state's difference writes it.
/// </summary>
internal sealed partial class DesignerSession
{
    /// <summary>The collections a component has editors for (the most derived declaration of each property).</summary>
    private IEnumerable<(DesignerCollectionInfo Info, object Collection)> CollectionsOf(object component)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in DesignerCollections.All)
        {
            if (_types.Find(info.OwnerType) is not { } owner || !owner.IsInstanceOfType(component) || !seen.Add(info.Property))
            {
                continue;
            }

            object? collection;
            try
            {
                collection = component.GetType().GetProperty(info.Property)?.GetValue(component);
            }
            catch (System.Reflection.TargetInvocationException)
            {
                continue;
            }

            if (collection is not null)
            {
                yield return (info, collection);
            }
        }
    }

    private string[]? CollectionKeys(object component)
    {
        var keys = CollectionsOf(component).Select(c => c.Info.Key).ToArray();
        return keys.Length == 0 ? null : keys;
    }

    /// <summary>A collection as the code says it: its items as values or references, the locals it holds; a tab control's pages are its controls (no collection).</summary>
    private CollectionState? CollectionStateOf(DesignerCollectionInfo info, object collection)
    {
        if (info.Method == "Controls.Add")
        {
            return null;
        }

        var items = new List<DesignerValue>();
        var locals = new List<DesignerLocal>();
        switch (info.Kind)
        {
            case DesignerCollections.Values:
                foreach (var item in (IEnumerable)collection)
                {
                    if (_values.Serialize(item) is not { } value)
                    {
                        return null;
                    }

                    items.Add(value);
                }

                break;
            case DesignerCollections.Components:
                foreach (var item in (IEnumerable)collection)
                {
                    if (item is IComponent component && NameOf(component) is { Length: > 0 } name)
                    {
                        items.Add(DesignerValue.ReferenceTo(name));
                    }
                }

                break;
            case DesignerCollections.Locals:
                foreach (var item in (IEnumerable)collection)
                {
                    if (NameOf(item) is not { Length: > 0 } name || _values.Serialize(item) is not { } value)
                    {
                        return null;
                    }

                    items.Add(DesignerValue.ReferenceTo(name));
                    locals.Add(new DesignerLocal(name, value));
                }

                break;
            case DesignerCollections.Images when collection is ImageList.ImageCollection images:
                foreach (var key in images.Keys)
                {
                    items.Add(DesignerValue.String(key ?? string.Empty));
                }

                break;
        }

        return new CollectionState(info, [.. items], [.. locals]);
    }

    private (DesignerCollectionInfo Info, object Collection) FindCollection(string component, string property)
    {
        var target = Target(component) ?? throw Unknown(component);
        var found = CollectionsOf(target).FirstOrDefault(c => c.Info.Property == property);
        return found.Info is null ? throw new InvalidOperationException($"{component} has no {property} the designer edits.") : found;
    }

    /// <summary>A collection's items for its editor.</summary>
    public DesignerCollectionResult Collection(DesignerCollectionParams parameters)
    {
        var (info, collection) = FindCollection(parameters.Component, parameters.Property);
        var items = new List<DesignerCollectionItem>();
        if (collection is ImageList.ImageCollection images)
        {
            foreach (var key in images.Keys)
            {
                items.Add(new DesignerCollectionItem { Name = key, Display = key });
            }

            return new DesignerCollectionResult(info.Key, [.. items]);
        }

        foreach (var item in (IEnumerable)collection)
        {
            switch (info.Kind)
            {
                case DesignerCollections.Components when item is IComponent component && NameOf(component) is { Length: > 0 } name:
                    items.Add(new DesignerCollectionItem { Name = name, Type = DesignerTypes.NameOf(component.GetType()), Text = TextOf(component), Display = name });
                    break;
                case DesignerCollections.Locals when item is ListViewItem listItem:
                    items.Add(new DesignerCollectionItem { Name = NameOf(listItem), Text = listItem.Text, Display = listItem.Text });
                    break;
                case DesignerCollections.Values:
                    items.Add(new DesignerCollectionItem { Value = _values.Serialize(item), Text = item as string, Display = DisplayOf(item) });
                    break;
            }
        }

        return new DesignerCollectionResult(info.Key, [.. items]);
    }

    private static string? TextOf(IComponent component) => component switch
    {
        DataGridViewColumn column => column.HeaderText,
        _ => TypeDescriptor.GetProperties(component)["Text"]?.GetValue(component) as string,
    };

    private static string DisplayOf(object? item) => item switch
    {
        null => string.Empty,
        TableLayoutStyle { SizeType: SizeType.AutoSize } style => style.SizeType.ToString(),
        ColumnStyle style => string.Create(CultureInfo.InvariantCulture, $"{style.SizeType} {style.Width}"),
        RowStyle style => string.Create(CultureInfo.InvariantCulture, $"{style.SizeType} {style.Height}"),
        _ => Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>Makes a collection hold <paramref name="parameters"/>' items, in that order: new component items created, the others' removed.</summary>
    public DesignerChangeResult SetCollection(DesignerSetCollectionParams parameters)
    {
        var before = Capture();
        var owner = Target(parameters.Component) ?? throw Unknown(parameters.Component);
        var (info, collection) = FindCollection(parameters.Component, parameters.Property);
        switch (info.Kind)
        {
            case DesignerCollections.Values:
                SetValues(owner, info, (IList)collection, parameters.Items);
                break;
            case DesignerCollections.Components:
                SetComponents(info, collection, parameters.Items);
                break;
            case DesignerCollections.Locals when owner is ListView listView:
                SetLocals(listView, parameters.Items);
                break;
            case DesignerCollections.Images when owner is ImageList imageList:
                SetImages(imageList, parameters.Items);
                break;
            default:
                throw new InvalidOperationException($"{parameters.Component}'s {parameters.Property} can't be edited here.");
        }

        return Result(before);
    }

    private void SetValues(object owner, DesignerCollectionInfo info, IList list, DesignerCollectionItem[] items)
    {
        var values = items.Select(i => i.Value is { } value ? _values.Evaluate(value) : i.Text ?? string.Empty).ToList();
        list.Clear();
        foreach (var value in values)
        {
            list.Add(value);
        }

        // A table's columns and rows follow its styles, as its editor keeps them.
        if (owner is TableLayoutPanel table)
        {
            if (info.Property == "ColumnStyles")
            {
                table.ColumnCount = Math.Max(1, values.Count);
            }
            else
            {
                table.RowCount = Math.Max(1, values.Count);
            }
        }
    }

    private void SetComponents(DesignerCollectionInfo info, object collection, DesignerCollectionItem[] items)
    {
        var current = ((IEnumerable)collection).Cast<object>().ToList();
        var sited = current.OfType<IComponent>().Where(c => NameOf(c) is { Length: > 0 }).ToList();
        var designTime = current.Where(o => o is not IComponent c || NameOf(c) is not { Length: > 0 }).ToList();
        var wanted = new List<IComponent>();
        foreach (var item in items)
        {
            if (item.Name is { } name && Target(name) is IComponent existing && sited.Contains(existing))
            {
                wanted.Add(existing);
                continue;
            }

            var type = _types.Find(item.Type ?? info.ItemTypes[0]) ?? throw new InvalidOperationException($"The type {item.Type} wasn't found.");
            var component = Host.CreateComponent(type, item.Name ?? NewComponentName(type));
            try
            {
                if (Host.GetDesigner(component) is IComponentInitializer initializer)
                {
                    initializer.InitializeNewComponent(new Hashtable());
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Some item designers expect Visual Studio's own tool strip editing: the item works without it.
                Console.Error.WriteLine($"{component.Site?.Name}'s designer didn't initialize it: {ex.Message}");
            }

            if (item.Text is { } text && (TypeDescriptor.GetProperties(component)["Text"] ?? TypeDescriptor.GetProperties(component)["HeaderText"]) is { IsReadOnly: false } textProperty)
            {
                textProperty.SetValue(component, text);
            }

            wanted.Add(component);
        }

        foreach (var gone in sited.Where(c => !wanted.Contains(c)).ToList())
        {
            foreach (var (_, inner) in CollectionsOf(gone))
            {
                foreach (var child in ((IEnumerable)inner).OfType<IComponent>().Where(c => NameOf(c) is { Length: > 0 }).ToList())
                {
                    Host.DestroyComponent(child);
                }
            }

            Host.DestroyComponent(gone);
        }

        switch (collection)
        {
            case ToolStripItemCollection strip:
                strip.Clear();
                strip.AddRange([.. wanted.Cast<ToolStripItem>()]);
                foreach (var node in designTime.OfType<ToolStripItem>())
                {
                    strip.Add(node);
                }

                break;
            case TabControl.TabPageCollection pages:
                pages.Clear();
                pages.AddRange([.. wanted.Cast<TabPage>()]);
                break;
            case ListView.ColumnHeaderCollection columns:
                columns.Clear();
                columns.AddRange([.. wanted.Cast<ColumnHeader>()]);
                break;
            case DataGridViewColumnCollection gridColumns:
                gridColumns.Clear();
                gridColumns.AddRange([.. wanted.Cast<DataGridViewColumn>()]);
                break;
            case IList list:
                list.Clear();
                foreach (var component in wanted)
                {
                    list.Add(component);
                }

                break;
        }
    }

    private void SetLocals(ListView listView, DesignerCollectionItem[] items)
    {
        var wanted = new List<ListViewItem>();
        var previous = listView.Items.Cast<ListViewItem>().ToList();
        foreach (var item in items)
        {
            if (item.Name is { } name && _locals.TryGetValue(name, out var local) && local is ListViewItem existing)
            {
                if (item.Text is { } text && text != existing.Text)
                {
                    existing.Text = text;
                }

                wanted.Add(existing);
                continue;
            }

            var created = new ListViewItem(item.Text ?? string.Empty);
            _locals[item.Name ?? NewLocalName("listViewItem")] = created;
            wanted.Add(created);
        }

        listView.Items.Clear();
        listView.Items.AddRange([.. wanted]);
        foreach (var gone in previous.Where(p => !wanted.Contains(p)))
        {
            if (NameOf(gone) is { } name)
            {
                _locals.Remove(name);
            }
        }
    }

    private void SetImages(ImageList imageList, DesignerCollectionItem[] items)
    {
        var images = new List<(string Key, Image Image)>();
        foreach (var item in items)
        {
            if (item.Bytes is { } bytes)
            {
                images.Add((item.Name ?? "image", Image.FromStream(new MemoryStream(Convert.FromBase64String(bytes)))));
            }
            else if (item.Name is { } key && imageList.Images.ContainsKey(key) && imageList.Images[key] is { } image)
            {
                images.Add((key, image));
            }
        }

        imageList.Images.Clear();
        foreach (var (key, image) in images)
        {
            imageList.Images.Add(key, image);
        }

        _imageListVersions[imageList] = _imageListVersions.GetValueOrDefault(imageList) + 1;
    }

    /// <summary>Visual Studio's name for a new component: its type's name, first letter lowercased, and the first free number.</summary>
    private string NewComponentName(Type type) => NewLocalName(char.ToLowerInvariant(type.Name[0]) + type.Name[1..]);

    /// <summary>The first free name of a stem and a number (<c>listViewItem3</c>).</summary>
    private string NewLocalName(string stem)
    {
        for (var i = 1; ; i++)
        {
            var name = stem + i.ToString(CultureInfo.InvariantCulture);
            if (Host.Container.Components[name] is null && !_locals.ContainsKey(name))
            {
                return name;
            }
        }
    }
}
