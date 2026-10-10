using System.IO;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Web21
{
    /// <summary>ASP.NET Core 2.1: the web host and a Startup class (the old hosting model).</summary>
    public static class Program
    {
        public static void Main(string[] args) => CreateWebHostBuilder(args).Build().Run();

        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost.CreateDefaultBuilder(args).UseStartup<Startup>();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRouting();
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            var runtime = Path.GetFileName(Path.GetDirectoryName(typeof(object).Assembly.Location));
            app.Run(context => context.Response.WriteAsync("Web21 on " + runtime + " in " + env.EnvironmentName)); // [request]
        }
    }
}
