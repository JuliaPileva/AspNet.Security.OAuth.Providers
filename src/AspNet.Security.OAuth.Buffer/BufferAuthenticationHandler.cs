/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OAuth.Providers
 * for more information concerning the license and the contributors participating to this project.
 */

using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace AspNet.Security.OAuth.Buffer
{
    public class BufferAuthenticationHandler : OAuthHandler<BufferAuthenticationOptions>
    {
        public BufferAuthenticationHandler(
            [NotNull] IOptionsMonitor<BufferAuthenticationOptions> options,
            [NotNull] ILoggerFactory logger,
            [NotNull] UrlEncoder encoder,
            [NotNull] ISystemClock clock)
            : base(options, logger, encoder, clock)
        {
        }

        protected override async Task<AuthenticationTicket> CreateTicketAsync([NotNull] ClaimsIdentity identity,
            [NotNull] AuthenticationProperties properties, [NotNull] OAuthTokenResponse tokens)
        {
            HttpRequestMessage request = null;
            HttpResponseMessage response = null;
            try
            {
                request = new HttpRequestMessage(HttpMethod.Get, Options.UserInformationEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

                response = await Backchannel.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Context.RequestAborted);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogError("An error occurred while retrieving the user profile: the remote server " +
                                    "returned a {Status} response with the following payload: {Headers} {Body}.",
                                    /* Status: */ response.StatusCode,
                                    /* Headers: */ response.Headers.ToString(),
                                    /* Body: */ await response.Content.ReadAsStringAsync());

                    throw new HttpRequestException("An error occurred while retrieving the user profile.");
                }

                var payload = JObject.Parse(await response.Content.ReadAsStringAsync());

                var principal = new ClaimsPrincipal(identity);
                var context = new OAuthCreatingTicketContext(principal, properties, Context, Scheme, Options, Backchannel, tokens, payload);
                context.RunClaimActions(payload);

                await Options.Events.CreatingTicket(context);
                return new AuthenticationTicket(context.Principal, context.Properties, Scheme.Name);
            }
            finally
            {
                request?.Dispose();
                response?.Dispose();
            }
        }
    }
}
