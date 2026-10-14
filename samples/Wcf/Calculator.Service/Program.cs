using Calculator.Service;
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
    services.AddService<CalculatorService>();
    services.AddServiceEndpoint<CalculatorService, ICalculatorService>(new BasicHttpBinding(), "/CalculatorService.svc");
    // The WSDL at /CalculatorService.svc?wsdl, for Add Service Reference and the WCF Test Client.
    app.Services.GetRequiredService<ServiceMetadataBehavior>().HttpGetEnabled = true;
});
app.Run();
