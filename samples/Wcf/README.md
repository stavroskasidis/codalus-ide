# WCF fixture

For the WCF tests (M42, ADR 0066).

- `Calculator.Service`: a CoreWCF service (`ICalculatorService`: `Add`, `Greet`, `Distance` with a `Point` data contract) on BasicHttpBinding at `/CalculatorService.svc`, with its metadata at `?wsdl` and `?singleWsdl`. `dotnet run` listens on http://localhost:5290.
- `Client`: a console app that the tests add WCF references to.
- `contracts/Calculator.wsdl`: the service's `?singleWsdl`, to generate a client without running it.
- `dotnet-svcutil` 8.0.0, a local tool in `.config/dotnet-tools.json`; the tests restore it.
