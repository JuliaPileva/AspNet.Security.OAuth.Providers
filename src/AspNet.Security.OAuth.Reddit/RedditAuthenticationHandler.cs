﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OAuth.Providers
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace AspNet.Security.OAuth.Reddit
{
    public class RedditAuthenticationHandler : OAuthHandler<RedditAuthenticationOptions>
    {
        public RedditAuthenticationHandler(
            [NotNull] IOptionsMonitor<RedditAuthenticationOptions> options,
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
                request.Headers.Authorization = new AuthenticationHeaderValue("bearer", tokens.AccessToken);

                // When a custom user agent is specified in the options, add it to the request headers
                // to override the default (generic) user agent used by the OAuth2 base middleware.
                if (!string.IsNullOrEmpty(Options.UserAgent))
                {
                    request.Headers.UserAgent.ParseAdd(Options.UserAgent);
                }

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

        protected override string BuildChallengeUrl(AuthenticationProperties properties, string redirectUri)
        {
            var address = base.BuildChallengeUrl(properties, redirectUri);

            // Add duration=permanent to the authorization request to get an access token that doesn't expire after 1 hour.
            // See https://github.com/reddit/reddit/wiki/OAuth2#authorization for more information.
            return QueryHelpers.AddQueryString(address, name: "duration", value: "permanent");
        }

        protected override string FormatScope()
        {
            // Note: Reddit requires a non-standard comma-separated scope.
            // See https://github.com/reddit/reddit/wiki/OAuth2#authorization
            // and http://tools.ietf.org/html/rfc6749#section-3.3.
            return string.Join(",", Options.Scope);
        }

        protected override async Task<OAuthTokenResponse> ExchangeCodeAsync([NotNull] string code, [NotNull] string redirectUri)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Options.ClientId}:{Options.ClientSecret}"));

            HttpRequestMessage request = null;
            HttpResponseMessage response = null;
            FormUrlEncodedContent formUrlEncodedContent = null;
            try
            {
                request = new HttpRequestMessage(HttpMethod.Post, Options.TokenEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

                // When a custom user agent is specified in the options, add it to the request headers
                // to override the default (generic) user agent used by the OAuth2 base middleware.
                if (!string.IsNullOrEmpty(Options.UserAgent))
                {
                    request.Headers.UserAgent.ParseAdd(Options.UserAgent);
                }

                formUrlEncodedContent = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirectUri,
                    ["code"] = code
                });
                request.Content = formUrlEncodedContent;

                response = await Backchannel.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Context.RequestAborted);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogError("An error occurred while retrieving an access token: the remote server " +
                                    "returned a {Status} response with the following payload: {Headers} {Body}.",
                                    /* Status: */ response.StatusCode,
                                    /* Headers: */ response.Headers.ToString(),
                                    /* Body: */ await response.Content.ReadAsStringAsync());

                    return OAuthTokenResponse.Failed(new Exception("An error occurred while retrieving an access token."));
                }

                var payload = JObject.Parse(await response.Content.ReadAsStringAsync());

                return OAuthTokenResponse.Success(payload);
            }
            finally
            {
                formUrlEncodedContent?.Dispose();
                request?.Dispose();
                response?.Dispose();
            }
        }
    }
}
