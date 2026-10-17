# 0069. WCF: service references through dotnet-svcutil, and a test client of our own

- **Status:** accepted (M42, 2026-10-16)
- **Date:** 2026-10-16
- **Deciders:** maintainer

## Context

M42 ([roadmap](../roadmap.md#m42--wcf)) brings WCF to every OS: Add Service Reference for WCF clients, CoreWCF services, a test client, and the `system.serviceModel` configuration schema. Nothing in it is Windows-only: WCF's client libraries (`System.ServiceModel.*`, dotnet/wcf), `dotnet-svcutil` and CoreWCF are MIT and run on .NET everywhere. Visual Studio's WCF Test Client and Service Configuration Editor are proprietary.

The constraints:
- **The shell is Native AOT** (CLAUDE.md). `System.ServiceModel`'s client builds its proxies with reflection and run-time code generation (`ChannelFactory<T>`, `RealProxy`-style dispatch), which AOT can't compile; a test client that calls arbitrary contracts can't have compiled proxies anyway.
- **Generating a client runs the user's code**: `dotnet-svcutil` builds the project to reuse its types (a bootstrapper project that references it). As with the scaffolders ([ADR 0041](0041-scaffolding-and-service-references.md)), that only happens out of process, from the build host, in trusted folders (ADR 0004).
- **The tools are the user's** (CLAUDE.md): one process per operation, the project's own version.
- References must move between Visual Studio, the command line and Codalus unchanged.

## Options

| Question | Options | Verdict |
|---|---|---|
| Client generation | `dotnet-svcutil` (MIT, the tool Visual Studio's "WCF Web Service Reference" is built on); our own generator from the WSDL | **`dotnet-svcutil`**, the user's, as a local tool (`.config/dotnet-tools.json` or `dotnet-tools.json`) or a global one, run as `dotnet svcutil` in the project's folder by the build host, as M15c runs `dotnet-aspnet-codegenerator`. **Install** asks first, then adds it as a local tool (a manifest first) or restores the manifest's tools. |
| Where the reference lives | svcutil's default (`ServiceReference/` with `dotnet-svcutil.params.json`); Visual Studio's (`Connected Services/<name>/` with `ConnectedService.json`) | **Visual Studio's**: svcutil generates into `Connected Services/<name>/Reference.cs`, and the `dotnet-svcutil.params.json` it writes becomes Visual Studio's `ConnectedService.json`: the same options under `ExtendedData` (`inputs` first, then the others alphabetically), `ProviderId` `Microsoft.VisualStudio.ConnectedService.Wcf`, `Version` `15.0.40203.910`. `dotnet-svcutil --update` reads that file too (its `WCFCSUpdateOptions`), so **Update** writes the changed options into it and runs `--update` on the folder. References in svcutil's own layout are listed and updated in place. |
| Options | All of svcutil's; Visual Studio's dialog | **Visual Studio's**: namespace (default: the reference's name), collection and dictionary types (Visual Studio's lists), synchronous operations, type reuse, internal access. |
| The test client's engine | `System.ServiceModel` with dynamic proxies; SOAP over `HttpClient` with WSDL/XSD read by `System.Xml.Linq` | **`HttpClient` + `System.Xml.Linq`**, AOT-clean: `WsdlReader` follows `wsdl:import`/`xsd:import`/`xsd:include` (WCF's `?wsdl` splits a service into `?wsdl=wsdl0` and `?xsd=xsd0`…), `XsdSchemaSet` reads what a form needs, `SoapRequest` builds the envelope, `SoapClient` posts it. No `XmlSchemaSet` either, which isn't needed and pulls in more than it gives. |
| Where it runs | A new host; an existing host (the debug or data host); the workbench, in the shell's process | **The workbench** (`src/Codalus.Workbench/Wcf/`). It is plain HTTP and XML parsing with no user code: nothing to isolate, nothing heavy (documents are capped at 16 MB, 64 per description), and every request is cancellable on the thread pool. A host would add a process, a protocol and a start-up for no isolation gain. |
| Bindings | Every binding; HTTP SOAP bindings without message security | **BasicHttpBinding** (SOAP 1.1, `text/xml` and `SOAPAction`) and **WSHttpBinding without security** (SOAP 1.2, `application/soap+xml` with the action, and WS-Addressing 1.0 headers when the binding's policy says `UsingAddressing`), plus any other SOAP binding over HTTP without WS-Security. An endpoint with message security, reliable sessions or a non-HTTP address is listed with the reason it can't be called; duplex callbacks are reported. |
| Running services | Ask the user for the address; find it | **Find it, then let the user edit it**: a CoreWCF project's endpoint paths come from its `AddServiceEndpoint` calls, its addresses from the running program's "Now listening on" lines (Run, or the debugged program), and each is asked for `?wsdl` on loopback only. |
| CoreWCF templates | Our own; CoreWCF's `CoreWCF.Templates` | **CoreWCF's**, offered in New Project like Avalonia's: "Install the CoreWCF templates…" runs `dotnet new install CoreWCF.Templates` when picked. |
| Configuration | A hand-written element catalog; a schema | **A schema of our own** (`Schemas/system.serviceModel.xsd` in the markup host), compiled once: completion, hover and validation (as warnings, of the section only) come from it. Less common elements accept any attribute so the schema doesn't flag what it doesn't model. |

## Decision

**Build host** (`SolutionSession.Wcf.cs`, `ProjectManagement/WcfConnectedServices.cs`; [project-protocol.md](../project-protocol.md#wcf-references)): `wcfReferences/status`, `install`, `list`, `add`, `update`, `remove`, and the `wcfReferences/output` notification. One svcutil run at a time, its output streamed, its process tree killed on cancel, telemetry off (`DOTNET_SVCUTIL_TELEMETRY_OPTOUT`). A project file svcutil changed (it adds the `System.ServiceModel.*` packages) is evaluated again and restored. Remove sends the folder to the trash; the packages stay. `CODALUS_SVCUTIL` names a stand-in for tests.

**Workbench** (`Wcf/`): `WcfReferenceService` (the references through the host, the Service References output), `WcfTestClient` (`WsdlReader`, `XsdSchemaSet`, `SoapRequest`, `SoapClient`, `SoapResponse`, `WcfServiceDiscovery`).

**Shell**: the Service References document gets a **WCF** kind (address, Browse, Discover, name, namespace, collection types, the three switches, svcutil's state and Install), and a WCF reference's options can be changed (Update generates it again), opened, removed or opened in the test client. The **WCF Test Client** is a document: the address (a URL, a WSDL file, or a running service), the endpoints and operations, a form generated from the operation's input message (simple values, choices, nested types, repeated elements with Add and Remove, nil), the XML (it follows the form until edited, then is sent as edited), Invoke, and the response as a tree and as formatted XML, faults with their code and reason. Commands `wcf.addServiceReference` and `wcf.testClient`.

## Consequences

- References are Visual Studio's and svcutil's own: either tool can update what the other made. svcutil decides the packages (8.0.0 adds `System.ServiceModel.*` 4.10.* for `net10.0`, even beside newer ones); they're the user's dependencies.
- Generating a reference builds the project (and, on update with type reuse, a bootstrapper project): seconds to a minute on a cold machine, with its output in the Service References channel.
- The test client calls what HTTP and plain SOAP reach. `TODO(issue)`: message security (WS-Security, `TransportWithMessageCredential`), reliable sessions, duplex contracts, NetTcp and named pipes, MTOM, and MEX-only metadata (WS-MetadataExchange); custom headers in the form.
- The configuration schema is ours and partial (custom binding elements, credentials and less common behaviors are open). `TODO(issue)`: grow it with M43's .NET Framework projects, and namespaced `web.config` files (`xmlns` on `<configuration>`) aren't checked.
