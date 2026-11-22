using System.Drawing;
using Microsoft.DotNet.DesignTools.Protocol.DataPipe;

// The replacement of Microsoft.DotNet.DesignTools.Protocol's API (#461, ADR 0106): what control libraries' designers use
// of the classic IDE's designer protocol, its channel between the designer's process and the IDE's. Codalus's designer
// host has no such channel: a vendor's messages are declared here so its assemblies load, and the server's
// IRequestSender answers its requests with nothing (Microsoft.DotNet.DesignTools.Server).

namespace Microsoft.DotNet.DesignTools.Protocol.DataPipe
{
    /// <summary>Reads a message's properties by name.</summary>
    public interface IDataPipeReader
    {
        byte ReadByte(string name);

        int ReadInt32OrDefault(string name, int defaultValue);

        string ReadString(string name);

        string? ReadStringOrDefault(string name, string? defaultValue);

        T[] ReadArray<T>(string name, Func<IDataPipeReader, T> readItem);

        T[]? ReadArrayOrNull<T>(string name, Func<IDataPipeReader, T> readItem);
    }

    /// <summary>Writes a message's properties by name.</summary>
    public interface IDataPipeWriter
    {
        void Write(byte value);

        void Write(string value);

        void Write(string name, string? value);

        void WriteIfNotDefault(string name, int value, int defaultValue);

        void WriteIfNotDefault(string name, string? value, string? defaultValue);

        void WriteArray<T>(string name, IReadOnlyList<T> items, Action<IDataPipeWriter, T> writeItem);

        void WriteArrayIfNotNull<T>(string name, IReadOnlyList<T>? items, Action<IDataPipeWriter, T> writeItem);
    }

    /// <summary>Typed reads. Codalus never carries a message, so nothing calls these.</summary>
    public static class DataPipeReaderExtensions
    {
        public static bool ReadBooleanOrFalse(this IDataPipeReader reader, string name) => throw NoChannel();

        public static object ReadObject(this IDataPipeReader reader, string name) => throw NoChannel();

        public static object? ReadObjectOrNull(this IDataPipeReader reader, string name) => throw NoChannel();

        public static Rectangle ReadRectangleOrDefault(this IDataPipeReader reader, string name, Rectangle defaultValue) => throw NoChannel();

        internal static NotSupportedException NoChannel() => new("Codalus's Windows Forms designer has no channel to the classic IDE's process: designer protocol messages aren't carried (ADR 0106).");
    }

    /// <summary>Typed writes. Codalus never carries a message, so nothing calls these.</summary>
    public static class DataPipeWriterExtensions
    {
        public static void WriteIfNotDefault(this IDataPipeWriter writer, string name, Rectangle value, Rectangle defaultValue) => throw DataPipeReaderExtensions.NoChannel();

        public static void WriteIfNotFalse(this IDataPipeWriter writer, string name, bool value) => throw DataPipeReaderExtensions.NoChannel();

        public static void WriteObject(this IDataPipeWriter writer, object? value) => throw DataPipeReaderExtensions.NoChannel();

        public static void WriteObject(this IDataPipeWriter writer, string name, object? value) => throw DataPipeReaderExtensions.NoChannel();

        public static void WriteObjectIfNotNull(this IDataPipeWriter writer, string name, object? value) => throw DataPipeReaderExtensions.NoChannel();
    }
}

namespace Microsoft.DotNet.DesignTools.Protocol.Endpoints
{
    /// <summary>A request's data.</summary>
    public abstract class Request
    {
        protected Request()
        {
        }

        protected Request(IDataPipeReader reader) => ReadProperties(reader);

        protected virtual void ReadProperties(IDataPipeReader reader)
        {
        }

        protected virtual void WriteProperties(IDataPipeWriter writer)
        {
        }
    }

    /// <summary>A response's data.</summary>
    public abstract class Response
    {
        protected Response()
        {
        }

        protected Response(IDataPipeReader reader) => ReadProperties(reader);

        protected virtual void ReadProperties(IDataPipeReader reader)
        {
        }

        protected virtual void WriteProperties(IDataPipeWriter writer)
        {
        }
    }

    /// <summary>A named request and its response.</summary>
    public abstract class Endpoint<TRequest, TResponse>
        where TRequest : Request
        where TResponse : Response
    {
        public virtual string Name => GetType().Name;

        protected virtual TRequest CreateRequest(IDataPipeReader reader) => throw DataPipeReaderExtensions.NoChannel();

        protected virtual TResponse CreateResponse(IDataPipeReader reader) => throw DataPipeReaderExtensions.NoChannel();
    }

    /// <summary>Marks an endpoint for the classic IDE's composition.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ExportEndpointAttribute : Attribute
    {
    }

    /// <summary>Sends a request to the other side of the designer protocol.</summary>
    public interface IRequestSender
    {
        TResponse SendRequest<TRequest, TResponse>(string endpointName, TRequest request)
            where TRequest : Request
            where TResponse : Response;
    }
}

namespace Microsoft.DotNet.DesignTools.Protocol.Notifications
{
    /// <summary>A notification's data.</summary>
    public abstract class NotificationData
    {
        protected NotificationData()
        {
        }

        protected NotificationData(IDataPipeReader reader) => ReadProperties(reader);

        protected virtual void ReadProperties(IDataPipeReader reader)
        {
        }

        protected virtual void WriteProperties(IDataPipeWriter writer)
        {
        }
    }

    /// <summary>A named notification.</summary>
    public abstract class Notification<TData>
        where TData : NotificationData
    {
        public virtual string Name => GetType().Name;

        protected virtual TData CreateNotificationData(IDataPipeReader reader) => throw DataPipeReaderExtensions.NoChannel();
    }

    /// <summary>Marks a notification for the classic IDE's composition.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ExportNotificationAttribute : Attribute
    {
    }
}
