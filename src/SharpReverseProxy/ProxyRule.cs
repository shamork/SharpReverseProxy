using Microsoft.AspNetCore.Http;
using System;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;

namespace SharpReverseProxy
{
    public class ProxyRule
    {
        public string Name { get; set; }
        public Func<HttpRequest, bool> Matcher { get; set; } = r => false;
        public Func<HttpRequestMessage, ClaimsPrincipal, Task<HttpResponseMessage>> Modifier { get; set; } = null;
        public Func<HttpResponseMessage, HttpContext, Task> ResponseModifier { get; set; } = null;
        public bool PreProcessResponse { get; set; } = true;
        public bool RequiresAuthentication { get; set; }
    }
}
