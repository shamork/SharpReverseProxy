using System;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace SharpReverseProxy
{
    public class ProxyMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly HttpClient _httpClient;
        private readonly ProxyOptions _options;

        public ProxyMiddleware(RequestDelegate next, IOptions<ProxyOptions> options)
        {
            _next = next;
            _options = options.Value;
            if (options.Value.HttpClientFactory == null)
            {
                _httpClient = new HttpClient(_options.BackChannelMessageHandler ?? new HttpClientHandler
                {
                    AllowAutoRedirect = _options.FollowRedirects
                });
            }
        }

        public async Task Invoke(HttpContext context)
        {
            var uri = GeRequestUri(context);
            var resultBuilder = new ProxyResultBuilder(uri);

            var matchedRule = _options.ProxyRules.FirstOrDefault(r => r.Matcher.Invoke(context.Request));
            if (matchedRule == null)
            {
                await _next(context);
                _options.Reporter.Invoke(resultBuilder.NotProxied(context.Response.StatusCode));
                return;
            }

            if (matchedRule.RequiresAuthentication && !UserIsAuthenticated(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                _options.Reporter.Invoke(resultBuilder.NotAuthenticated());
                return;
            }

            var proxyRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), uri);
            SetProxyRequestBody(proxyRequest, context);
            SetProxyRequestHeaders(proxyRequest, context);
            HttpResponseMessage response = null;
            if (matchedRule.Modifier != null)
            {
                response = await matchedRule.Modifier.Invoke(proxyRequest, context.User);
            }
            if (_options.HttpClientFactory == null)
            {
                proxyRequest.Headers.Host = proxyRequest.RequestUri.IsDefaultPort
                ? proxyRequest.RequestUri.Host :
                $"{proxyRequest.RequestUri.Host}:{proxyRequest.RequestUri.Port}";
            }


            try
            {
                await ProxyTheRequest(context, proxyRequest, matchedRule, response);
            }
            catch (TimeoutException ex)
            {
                await ReturnContent(context, new HttpResponseMessage(System.Net.HttpStatusCode.GatewayTimeout)
                {
                    Content = new StringContent($"forward failed, host:{proxyRequest.Headers.Host}\r\nuri:{proxyRequest.RequestUri}\r\nheaders:{proxyRequest.Headers}\r\nException:{ex.Message}", System.Text.Encoding.UTF8, "text/plain")
                });
            }
            catch (TaskCanceledException ex)
            {
                await ReturnContent(context, new HttpResponseMessage(System.Net.HttpStatusCode.GatewayTimeout)
                {
                    Content = new StringContent($"forward failed, host:{proxyRequest.Headers.Host}\r\nuri:{proxyRequest.RequestUri}\r\nheaders:{proxyRequest.Headers}\r\nException:{ex.Message}", System.Text.Encoding.UTF8, "text/plain")
                });
            }
            catch (SocketException ex)
            {
                await ReturnContent(context, new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
                {
                    Content = new StringContent($"forward failed, host:{proxyRequest.Headers.Host}\r\nuri:{proxyRequest.RequestUri}\r\nheaders:{proxyRequest.Headers}\r\nException:{ex.Message}", System.Text.Encoding.UTF8, "text/plain")
                });
            }
            catch (HttpRequestException ex)
            {
                await ReturnContent(context, new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
                {
                    Content = new StringContent($"forward failed, host:{proxyRequest.Headers.Host}\r\nuri:{proxyRequest.RequestUri}\r\nheaders:{proxyRequest.Headers}\r\nException:{ex.Message}", System.Text.Encoding.UTF8, "text/plain")
                });
            }
            catch (Exception ex)
            {
                await ReturnContent(context, new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent($"forward failed, host:{proxyRequest.Headers.Host}\r\nuri:{proxyRequest.RequestUri}\r\nheaders:{proxyRequest.Headers}\r\nException:{ex}", System.Text.Encoding.UTF8, "text/plain")
                });
            }
            _options.Reporter.Invoke(resultBuilder.Proxied(proxyRequest.RequestUri, context.Response.StatusCode));
        }

        private async Task ProxyTheRequest(HttpContext context, HttpRequestMessage proxyRequest, ProxyRule proxyRule, HttpResponseMessage response)
        {
            var hc = (_options.HttpClientFactory == null ? _httpClient : (_options.HttpClientFactory(proxyRequest)));
            if (hc == null)
            {
                await ReturnContent(context, new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent($"no available httpClient,host:{proxyRequest.Headers.Host}\r\nuri:{proxyRequest.RequestUri}\r\nheaders:{proxyRequest.Headers}", System.Text.Encoding.UTF8, "text/plain")
                });
            }
            using (var responseMessage = response ?? await hc.SendAsync(proxyRequest,
                                                                      HttpCompletionOption.ResponseHeadersRead,
                                                                      context.RequestAborted))
            {

                if (proxyRule.PreProcessResponse || proxyRule.ResponseModifier == null)
                {
                    await ReturnContent(context, responseMessage);
                }

                if (proxyRule.ResponseModifier != null)
                {
                    await proxyRule.ResponseModifier.Invoke(responseMessage, context);
                }
                if (!(proxyRule.PreProcessResponse || proxyRule.ResponseModifier == null))
                {
                    await ReturnContent(context, responseMessage);
                }
            }
        }

        private static async Task ReturnContent(HttpContext context, HttpResponseMessage responseMessage)
        {
            context.Response.StatusCode = (int)responseMessage.StatusCode;
            context.Response.ContentType = responseMessage.Content?.Headers.ContentType?.MediaType;
            foreach (var header in responseMessage.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            // SendAsync removes chunking from the response. 
            // This removes the header so it doesn't expect a chunked response.
            context.Response.Headers.Remove("transfer-encoding");

            if (responseMessage.Content != null)
            {
                foreach (var contentHeader in responseMessage.Content.Headers)
                {
                    context.Response.Headers[contentHeader.Key] = contentHeader.Value.ToArray();
                }
                await responseMessage.Content.CopyToAsync(context.Response.Body);
            }
        }

        private static Uri GeRequestUri(HttpContext context)
        {
            var request = context.Request;
            var uriString = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
            return new Uri(uriString);
        }

        private static void SetProxyRequestBody(HttpRequestMessage requestMessage, HttpContext context)
        {
            var requestMethod = context.Request.Method;
            if (HttpMethods.IsGet(requestMethod) ||
                HttpMethods.IsHead(requestMethod) ||
                HttpMethods.IsDelete(requestMethod) ||
                HttpMethods.IsTrace(requestMethod))
            {
                return;
            }
            requestMessage.Content = new StreamContent(context.Request.Body);
        }

        private void SetProxyRequestHeaders(HttpRequestMessage requestMessage, HttpContext context)
        {
            foreach (var header in context.Request.Headers)
            {
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }
        }

        private bool UserIsAuthenticated(HttpContext context)
        {
            return context.User.Identities.FirstOrDefault()?.IsAuthenticated ?? false;
        }
    }

}
