using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.Bff;
using Duende.Bff.Yarp;
using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Configuration;
using EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Services;

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CustomerOnboardingBffOptions>(
    builder.Configuration.GetSection(CustomerOnboardingBffOptions.SectionName));

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
// builder.Services.AddControllers();
builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-CO-Bff-CSRF";
    options.Cookie.HttpOnly = false;
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

builder.Services.AddBff().AddRemoteApis();
builder.Services.AddOpenIdConnectAccessTokenManagement();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = "oidc";
        options.DefaultSignOutScheme = "oidc";
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = CookieNames.MICROSERVICE_CUSTOMER_ONBOARDING_HOST_BFF;
        options.Cookie.Path = "/";
        options.Cookie.SameSite = SameSiteMode.None;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    })
    .AddOpenIdConnect("oidc", options =>
    {
        options.Authority = IDP.AUTHORITY;
        options.ClientId = CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP;
        options.ClientSecret = CustomerOnboardingMicroservice.CLIENT_SECRET_FOR_IDP;
        options.ResponseType = "code";
        options.ResponseMode = "query";
        options.UsePkce = true;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Scope.Add("roles");
        options.Scope.Add("offline_access");
        options.Scope.Add("customer-onboarding.read");
        options.Scope.Add("customer-onboarding.write");
        options.ClaimActions.MapJsonKey("role", "role", "role");
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "role";

        options.CorrelationCookie.SameSite = SameSiteMode.None;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.SameSite = SameSiteMode.None;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;

        options.Events.OnRedirectToIdentityProvider = context =>
        {
            if (context.Properties.Items.TryGetValue("prompt", out var prompt))
            {
                context.ProtocolMessage.Prompt = prompt;
            }
            return Task.CompletedTask;
        };
    });

builder.Services.AddTransient<TransientGetRetryHandler>();
builder.Services.AddSingleton<IM2MAccessTokenService, M2MAccessTokenService>();

builder.Services.AddHttpClient("CustomerOnboardingApi", (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CustomerOnboardingBffOptions>>().Value;
    client.BaseAddress = new Uri(options.CustomerOnboardingApiBaseUrl);
})
.AddUserAccessTokenHandler()
.AddHttpMessageHandler<TransientGetRetryHandler>();

builder.Services.AddHttpClient("DocumentsManagementApi", (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CustomerOnboardingBffOptions>>().Value;
    client.BaseAddress = new Uri(options.DocumentsManagementApiBaseUrl);
})
.AddHttpMessageHandler<TransientGetRetryHandler>();

builder.Services.AddHttpClient("IdentityServerTokenClient", (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CustomerOnboardingBffOptions>>().Value;
    client.BaseAddress = new Uri(options.IdentityServerAuthority);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
//app.UseAuthorization();
//app.UseBff();
app.UseBff();
app.UseAuthorization();


app.MapControllers();

app.MapBffManagementEndpoints();

app.Run();
