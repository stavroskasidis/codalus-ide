using CalculatorClient.Calculator;

// The address from the command line, else the service's launch profile.
var address = args.Length > 0 ? args[0] : "http://localhost:5280/CalculatorService.svc";
var client = new CalculatorServiceClient(CalculatorServiceClient.EndpointConfiguration.BasicHttpBinding_ICalculatorService, address);
try
{
    Console.WriteLine($"2 + 3 = {await client.AddAsync(2, 3)}");
    Console.WriteLine(await client.GreetAsync(new Person { Name = "Ada", Age = 36, Mood = Mood.Happy, Tags = ["math"] }));
    foreach (var person in await client.FindPeopleAsync("A", 10))
    {
        Console.WriteLine($"{person.Name} ({person.Age})");
    }

    await client.CloseAsync();
}
catch (Exception)
{
    client.Abort();
    throw;
}
