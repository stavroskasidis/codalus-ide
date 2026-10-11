using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Web31
{
    /// <summary>ASP.NET Core 3.1: the generic host and endpoint routing.</summary>
    public static class Program
    {
        public static void Main(string[] args) => CreateHostBuilder(args).Build().Run();

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args).ConfigureWebHostDefaults(web => web.UseStartup<Startup>());
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRouting();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            var runtime = Path.GetFileName(Path.GetDirectoryName(typeof(object).Assembly.Location));
            app.UseRouting();
            app.UseEndpoints(endpoints => endpoints.MapGet("/", context => context.Response.WriteAsync("Web31 on " + runtime + " in " + env.EnvironmentName))); // [request]
        }
    }
}
