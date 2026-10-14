namespace Codalus.Markup.Host.Xml;

/// <summary>An element of a configuration schema: its documentation, attributes (with their values when they are a choice) and children.</summary>
internal sealed class ConfigElement(string name, string? description = null)
{
    public string Name { get; } = name;

    public string? Description { get; } = description;

    public Dictionary<string, string[]> Attributes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, ConfigElement> Children { get; } = new(StringComparer.Ordinal);

    public ConfigElement Attr(string name, params string[] values)
    {
        Attributes[name] = values;
        return this;
    }

    public ConfigElement Add(params ConfigElement[] children)
    {
        foreach (var child in children)
        {
            Children[child.Name] = child;
        }

        return this;
    }
}

/// <summary>
/// WCF's <c>&lt;system.serviceModel&gt;</c> section of <c>app.config</c> and <c>web.config</c> (M42): the elements,
/// attributes and values that WCF clients, CoreWCF's configuration support and .NET Framework services read, for
/// completion, hover and checks. Written from the configuration reference; covers the common bindings, behaviors,
/// endpoints and hosting settings, not every extension.
/// </summary>
internal static class ServiceModelKnowledge
{
    public const string Section = "system.serviceModel";

    private static readonly string[] s_bool = ["true", "false"];

    /// <summary>The standard bindings, as elements of <c>&lt;bindings&gt;</c> and values of an endpoint's <c>binding</c>.</summary>
    public static readonly string[] Bindings =
    [
        "basicHttpBinding", "basicHttpsBinding", "wsHttpBinding", "ws2007HttpBinding", "wsDualHttpBinding", "wsFederationHttpBinding",
        "ws2007FederationHttpBinding", "netTcpBinding", "netNamedPipeBinding", "netHttpBinding", "netHttpsBinding", "netMsmqBinding",
        "webHttpBinding", "customBinding", "mexHttpBinding", "mexHttpsBinding", "mexTcpBinding", "mexNamedPipeBinding",
    ];

    public static ConfigElement Root { get; } = Build();

    private static ConfigElement Build()
    {
        var timeouts = new[] { "closeTimeout", "openTimeout", "receiveTimeout", "sendTimeout" };
        ConfigElement Binding()
        {
            var binding = new ConfigElement("binding", "A binding configuration, referred to by an endpoint's `bindingConfiguration`.")
                .Attr("name")
                .Attr("maxReceivedMessageSize").Attr("maxBufferSize").Attr("maxBufferPoolSize")
                .Attr("transferMode", "Buffered", "Streamed", "StreamedRequest", "StreamedResponse")
                .Attr("messageEncoding", "Text", "Mtom")
                .Attr("textEncoding", "utf-8", "utf-16BE", "utf-16")
                .Attr("allowCookies", s_bool).Attr("bypassProxyOnLocal", s_bool).Attr("useDefaultWebProxy", s_bool).Attr("proxyAddress")
                .Attr("hostNameComparisonMode", "StrongWildcard", "Exact", "WeakWildcard")
                .Attr("transactionFlow", s_bool).Attr("portSharingEnabled", s_bool).Attr("maxConnections").Attr("listenBacklog");
            foreach (var timeout in timeouts)
            {
                binding.Attr(timeout);
            }

            return binding.Add(
                new ConfigElement("security", "The binding's security: none, transport (TLS), message, or both.")
                    .Attr("mode", "None", "Transport", "Message", "TransportWithMessageCredential", "TransportCredentialOnly")
                    .Add(
                        new ConfigElement("transport", "Transport security: how the client authenticates to the transport.")
                            .Attr("clientCredentialType", "None", "Basic", "Digest", "Ntlm", "Windows", "Certificate", "InheritedFromHost")
                            .Attr("proxyCredentialType", "None", "Basic", "Digest", "Ntlm", "Windows")
                            .Attr("protectionLevel", "None", "Sign", "EncryptAndSign")
                            .Attr("realm"),
                        new ConfigElement("message", "Message security: how the client authenticates in the message.")
                            .Attr("clientCredentialType", "None", "Windows", "UserName", "Certificate", "IssuedToken")
                            .Attr("algorithmSuite", "Default", "Basic256", "Basic192", "Basic128", "TripleDes", "Basic256Sha256", "Basic128Sha256")
                            .Attr("establishSecurityContext", s_bool).Attr("negotiateServiceCredential", s_bool)),
                new ConfigElement("readerQuotas", "Limits on the size and depth of the messages read.")
                    .Attr("maxDepth").Attr("maxStringContentLength").Attr("maxArrayLength").Attr("maxBytesPerRead").Attr("maxNameTableCharCount"),
                new ConfigElement("reliableSession", "WS-ReliableMessaging.")
                    .Attr("enabled", s_bool).Attr("ordered", s_bool).Attr("inactivityTimeout"));
        }

        var bindings = new ConfigElement("bindings", "Binding configurations, by binding.");
        foreach (var name in Bindings.Where(b => !b.StartsWith("mex", StringComparison.Ordinal)))
        {
            bindings.Add(new ConfigElement(name, $"Configurations of `{name}`.").Add(Binding()));
        }

        ConfigElement Endpoint(string description) => new ConfigElement("endpoint", description)
            .Attr("address").Attr("binding", Bindings).Attr("bindingConfiguration").Attr("contract").Attr("name")
            .Attr("behaviorConfiguration").Attr("kind", "mexEndpoint", "webHttpEndpoint", "udpDiscoveryEndpoint", "workflowControlEndpoint")
            .Attr("endpointConfiguration").Attr("isSystemEndpoint", s_bool).Attr("listenUri").Attr("listenUriMode", "Explicit", "Unique")
            .Add(new ConfigElement("identity", "The service's identity the client expects.")
                .Add(new ConfigElement("dns").Attr("value"), new ConfigElement("userPrincipalName").Attr("value"), new ConfigElement("servicePrincipalName").Attr("value")),
                new ConfigElement("headers", "Address headers sent with every message."));

        var dataContractSerializer = new ConfigElement("dataContractSerializer", "The data contract serializer's limits.")
            .Attr("maxItemsInObjectGraph").Attr("ignoreExtensionDataObject", s_bool);
        var serviceBehavior = new ConfigElement("behavior", "A service behavior, referred to by a service's `behaviorConfiguration`.")
            .Attr("name")
            .Add(
                new ConfigElement("serviceMetadata", "Publishes the service's metadata (WSDL), for Add Service Reference and the WCF Test Client.")
                    .Attr("httpGetEnabled", s_bool).Attr("httpsGetEnabled", s_bool).Attr("httpGetUrl").Attr("httpsGetUrl")
                    .Attr("policyVersion", "Default", "Policy12", "Policy15"),
                new ConfigElement("serviceDebug", "Exception details in faults and the help page.")
                    .Attr("includeExceptionDetailInFaults", s_bool).Attr("httpHelpPageEnabled", s_bool).Attr("httpsHelpPageEnabled", s_bool),
                new ConfigElement("serviceThrottling", "Limits on concurrent calls, sessions and instances.")
                    .Attr("maxConcurrentCalls").Attr("maxConcurrentSessions").Attr("maxConcurrentInstances"),
                new ConfigElement("serviceCredentials", "The service's credentials and how it validates clients'.")
                    .Add(new ConfigElement("serviceCertificate").Attr("findValue").Attr("storeLocation", "CurrentUser", "LocalMachine").Attr("storeName", "My", "Root", "TrustedPeople", "CertificateAuthority").Attr("x509FindType", "FindBySubjectName", "FindByThumbprint", "FindBySubjectDistinguishedName"),
                        new ConfigElement("userNameAuthentication").Attr("userNamePasswordValidationMode", "Windows", "MembershipProvider", "Custom").Attr("customUserNamePasswordValidatorType")),
                new ConfigElement("serviceAuthorization").Attr("principalPermissionMode", "None", "UseWindowsGroups", "UseAspNetRoles", "Custom", "Always"),
                new ConfigElement("serviceTimeouts").Attr("transactionTimeout"),
                dataContractSerializer);
        var endpointBehavior = new ConfigElement("behavior", "An endpoint behavior, referred to by an endpoint's `behaviorConfiguration`.")
            .Attr("name")
            .Add(
                new ConfigElement("webHttp", "REST-style (WebHttp) programming model for the endpoint.")
                    .Attr("helpEnabled", s_bool).Attr("defaultOutgoingResponseFormat", "Xml", "Json")
                    .Attr("defaultBodyStyle", "Bare", "Wrapped", "WrappedRequest", "WrappedResponse")
                    .Attr("automaticFormatSelectionEnabled", s_bool).Attr("faultExceptionEnabled", s_bool),
                new ConfigElement("clientCredentials", "The client's credentials."),
                new ConfigElement("callbackDebug").Attr("includeExceptionDetailInFaults", s_bool),
                new ConfigElement("clientVia").Attr("viaUri"),
                new ConfigElement("enableWebScript"),
                dataContractSerializer);

        return new ConfigElement(Section, "WCF's configuration: services, clients, bindings and behaviors.").Add(
            new ConfigElement("services", "The services this application hosts.").Add(
                new ConfigElement("service", "A service: its implementation type and endpoints.")
                    .Attr("name").Attr("behaviorConfiguration")
                    .Add(Endpoint("An endpoint of the service: its address, binding and contract."),
                        new ConfigElement("host").Add(
                            new ConfigElement("baseAddresses").Add(new ConfigElement("add").Attr("baseAddress")),
                            new ConfigElement("timeouts").Attr("closeTimeout").Attr("openTimeout")))),
            new ConfigElement("client", "The endpoints this application calls.").Add(Endpoint("A client endpoint: the address, binding and contract of a service to call."), new ConfigElement("metadata")),
            bindings,
            new ConfigElement("behaviors", "Service and endpoint behaviors.").Add(
                new ConfigElement("serviceBehaviors").Add(serviceBehavior),
                new ConfigElement("endpointBehaviors").Add(endpointBehavior)),
            new ConfigElement("serviceHostingEnvironment", "How services are hosted in IIS and ASP.NET.")
                .Attr("aspNetCompatibilityEnabled", s_bool).Attr("multipleSiteBindingsEnabled", s_bool).Attr("minFreeMemoryPercentageToActivateService")
                .Add(new ConfigElement("serviceActivations").Add(new ConfigElement("add", "A service activated without a .svc file.").Attr("relativeAddress").Attr("service").Attr("factory"))),
            new ConfigElement("protocolMapping", "The default binding of each scheme.").Add(new ConfigElement("add").Attr("scheme", "http", "https", "net.tcp", "net.pipe", "net.msmq").Attr("binding", Bindings).Attr("bindingConfiguration")),
            new ConfigElement("diagnostics", "Message logging and performance counters.")
                .Attr("performanceCounters", "Off", "ServiceOnly", "All", "Default").Attr("wmiProviderEnabled", s_bool)
                .Add(new ConfigElement("messageLogging")
                    .Attr("logEntireMessage", s_bool).Attr("logMalformedMessages", s_bool).Attr("logMessagesAtServiceLevel", s_bool)
                    .Attr("logMessagesAtTransportLevel", s_bool).Attr("maxMessagesToLog").Attr("maxSizeOfMessageToLog")),
            new ConfigElement("extensions", "Custom behaviors, binding elements and bindings."),
            new ConfigElement("standardEndpoints", "Standard endpoint configurations."));
    }

    /// <summary>
    /// The element at the end of <paramref name="path"/> (the open elements, outermost first), when it is inside
    /// <c>&lt;system.serviceModel&gt;</c> and known; null otherwise.
    /// </summary>
    public static ConfigElement? Find(IReadOnlyList<string> path)
    {
        var start = -1;
        for (var i = 0; i < path.Count; i++)
        {
            if (path[i] == Section)
            {
                start = i;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var current = Root;
        for (var i = start + 1; i < path.Count; i++)
        {
            if (!current.Children.TryGetValue(path[i], out var child))
            {
                return null;
            }

            current = child;
        }

        return current;
    }

    /// <summary>A configuration file WCF reads.</summary>
    public static bool IsConfigFile(string? path) =>
        path is not null && (path.EndsWith(".config", StringComparison.OrdinalIgnoreCase));
}
