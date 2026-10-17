using CalculatorService;
using CoreWCF;
using CoreWCF.Configuration;
using CoreWCF.Description;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceModelServices();
builder.Services.AddServiceModelMetadata();
builder.Services.AddSingleton<IServiceBehavior, UseRequestHeadersForMetadataAddressBehavior>();

var app = builder.Build();
app.UseServiceModel(services =>
{
    services.AddService<CalculatorService.CalculatorService>();
    // SOAP 1.1 (BasicHttpBinding), and SOAP 1.2 with WS-Addressing (WSHttpBinding without security).
    services.AddServiceEndpoint<CalculatorService.CalculatorService, ICalculatorService>(new BasicHttpBinding(), "/CalculatorService.svc");
    services.AddServiceEndpoint<CalculatorService.CalculatorService, ICalculatorService>(new WSHttpBinding(SecurityMode.None), "/CalculatorServiceWs.svc");
    app.Services.GetRequiredService<ServiceMetadataBehavior>().HttpGetEnabled = true;
});

app.Run();
