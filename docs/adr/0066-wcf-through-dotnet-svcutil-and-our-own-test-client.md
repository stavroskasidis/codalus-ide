# 0066. WCF through dotnet-svcutil and our own test client

- **Status:** accepted (M42)
- **Date:** 2026-10-14
- **Deciders:** maintainer (the roadmap's M42: "Nothing here is Windows-only", so it doesn't wait for D31)

## Context

WCF on modern .NET is:
- **clients:** the `System.ServiceModel.*` packages (dotnet/wcf, MIT), with code generated from a service's metadata by `dotnet-svcutil` (MIT);
- **services:** CoreWCF (MIT), on ASP.NET Core.

All three run on every OS. Visual Studio adds:
- the WCF Web Service Reference provider (Connected Services), which runs `dotnet-svcutil` and writes `ConnectedService.json`;
- the WCF Test Client and the Service Configuration Editor, both proprietary.

Generating the client needs the project's references resolved: `dotnet-svcutil` builds the project to reuse its types.

## Decision

**References through the user's own `dotnet-svcutil`, as a child of the build host**, like the code generator (ADR 0041):
- `wcf/add` runs `dotnet svcutil <input> --outputDir "Connected Services/<name>" --outputFile Reference.cs --namespace "*,<namespace>" --projectFile <project>`, plus the options Visual Studio's dialog has:
  - which operations are generated (async, both, or sync only);
  - the collection type;
  - internal types;
  - the serializer;
  - type reuse;
  - message contracts;
  - data binding.
- It runs in the build host because the tool builds the project, so it runs where the project is (a remote window's machine), and only in trusted solutions.
- `wcf/tool` finds the tool (local, then global). With `install`, it restores the manifest or installs `dotnet-svcutil` as a local tool, after the shell asks.
- `wcf/update` regenerates. As recorded, it runs `--update <folder>`. With new options or a new input, it generates again into the same folder.
- `wcf/remove` sends the folder to the trash. The packages stay, as for OpenAPI and gRPC.
- The tool's output goes to a Service References output channel (`scaffold/output` with `source: wcf`).

**Both options files.**
- `dotnet-svcutil` writes `dotnet-svcutil.params.json`.
- We add `ConnectedService.json` with the same options under `ExtendedData` and `ProviderId: Microsoft.VisualStudio.ConnectedService.Wcf`, the file Visual Studio's provider reads.
- Either file is enough for `dotnet-svcutil --update` (checked), so a reference made in either IDE updates in the other.
- `serviceReferences/list` reads both. WCF references are folders, not items, so they show next to the OpenAPI and gRPC ones without a project item.

**Our own WCF Test Client** (`WcfTestClient`, workbench `Wcf/`):
- Reads the metadata: a WSDL file, a `?wsdl` address, or the service's address, where it tries `?singleWsdl` and then `?wsdl`. Imports are fetched from the same service only.
- `WsdlDocument` parses what a request needs: endpoints (SOAP 1.1 or 1.2, WS-Addressing), document/literal wrapped operations, and their parameters from the XML Schema (sequences, base types, references, enumerations, arrays).
- Builds a sample request body with typed values for the user to edit, sends it over HTTP with the SOAP action (and WS-Addressing headers where the binding uses them), and shows the response body or the fault.
- It never runs the service's code. A request is only what the user sends.

**CoreWCF services:**
- New Project offers `CoreWCF.Templates` (`dotnet new install`) like Avalonia's, while no `corewcf` template is installed.
- A running service's metadata is its `?wsdl`, which the test client and Add Service Reference read.

**Configuration:** the markup host knows `<system.serviceModel>` in `.config` files: completion of elements, attributes and values (binding and behavior configuration names from the file), hover, unknown elements, and values outside a choice. It is written from the configuration reference, not an XSD (Visual Studio's is proprietary).

## Consequences

- **Packages.** `dotnet-svcutil` 8 adds `System.ServiceModel.*` at `4.10.*`. In a project with central package management, the tool writes versions into the project, which NuGet rejects; that is the tool's behavior, and moving the versions to `Directory.Packages.props` is a TODO.
- **Gaps:** message security, duplex contracts and transports other than HTTP (net.tcp, named pipes) in the test client. The endpoint or operation says why it can't be tested.
- **Fixture and tests.** `samples/Wcf`: a CoreWCF calculator service, a client, the service's WSDL in `contracts/`, and `dotnet-svcutil` 8.0.0 in the tool manifest. The tests run the real tool, build the generated client, and start the service on loopback to call it.
