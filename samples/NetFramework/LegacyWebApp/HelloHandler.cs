using System.Web;

namespace LegacyWebApp
{
    /// <summary>An ASP.NET (System.Web) handler: the project needs the classic IDE's web application targets to build.</summary>
    public class HelloHandler : IHttpHandler
    {
        public bool IsReusable => true;

        public void ProcessRequest(HttpContext context)
        {
            context.Response.ContentType = "text/plain";
            context.Response.Write("Hello from System.Web");
        }
    }
}
