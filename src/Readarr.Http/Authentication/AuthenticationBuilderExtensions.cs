using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Diacritical;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;

namespace Readarr.Http.Authentication
{
    public static class AuthenticationBuilderExtensions
    {
        private static readonly Regex CookieNameRegex = new Regex(@"[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static AuthenticationBuilder AddApiKey(this AuthenticationBuilder authenticationBuilder, string name, Action<ApiKeyAuthenticationOptions> options)
        {
            return authenticationBuilder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(name, options);
        }

        public static AuthenticationBuilder AddBasic(this AuthenticationBuilder authenticationBuilder, string name)
        {
            return authenticationBuilder.AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(name, options => { });
        }

        public static AuthenticationBuilder AddNone(this AuthenticationBuilder authenticationBuilder, string name)
        {
            return authenticationBuilder.AddScheme<AuthenticationSchemeOptions, NoAuthenticationHandler>(name, options => { });
        }

        public static AuthenticationBuilder AddExternal(this AuthenticationBuilder authenticationBuilder, string name)
        {
            return authenticationBuilder.AddScheme<AuthenticationSchemeOptions, NoAuthenticationHandler>(name, options => { });
        }

        public static AuthenticationBuilder AddAppAuthentication(this IServiceCollection services)
        {
            services.AddOptions<CookieAuthenticationOptions>(AuthenticationType.Forms.ToString())
                .Configure<IConfigFileProvider>((options, configFileProvider) =>
                {
                    // Replace diacritics and replace non-word characters to ensure cookie name doesn't contain any valid URL characters not allowed in cookie names
                    var instanceName = configFileProvider.InstanceName;
                    instanceName = instanceName.RemoveDiacritics();
                    instanceName = CookieNameRegex.Replace(instanceName, string.Empty);

                    options.Cookie.Name = $"{instanceName}Auth";
                    options.AccessDeniedPath = "/login?loginFailed=true";
                    options.LoginPath = "/login";
                    options.ExpireTimeSpan = TimeSpan.FromDays(7);
                    options.SlidingExpiration = true;
                    options.ReturnUrlParameter = "returnUrl";
                    options.Events.OnRedirectToLogin = context => EventOnRedirectCookiesLogin(context, 401);
                    options.Events.OnRedirectToAccessDenied = context => EventOnRedirectCookiesLogin(context, 403);
                });

            return services.AddAuthentication()
                .AddNone(AuthenticationType.None.ToString())
                .AddExternal(AuthenticationType.External.ToString())
                .AddBasic(AuthenticationType.Basic.ToString())
                .AddCookie(AuthenticationType.Forms.ToString())
                .AddApiKey("API", options =>
                {
                    options.HeaderName = "X-Api-Key";
                    options.QueryName = "apikey";
                })
                .AddApiKey("SignalR", options =>
                {
                    options.HeaderName = "X-Api-Key";
                    options.QueryName = "access_token";
                });
        }

        // Before .NET 10 only AJAX requests got a 401 from the cookie handler and everything else was redirected to
        // the login page. .NET 10 returns 401 for anything it takes to be an API endpoint, which includes the page
        // request for the UI, so a browser got a blank 401 instead of the login screen. Keep the old behaviour.
        private static Task EventOnRedirectCookiesLogin(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
        {
            if (string.Equals(context.Request.Query[HeaderNames.XRequestedWith], "XMLHttpRequest", StringComparison.Ordinal) ||
                string.Equals(context.Request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.Ordinal))
            {
                context.Response.Headers.Location = context.RedirectUri;
                context.Response.StatusCode = statusCode;
            }
            else
            {
                context.Response.Redirect(context.RedirectUri);
            }

            return Task.CompletedTask;
        }
    }
}
