using EAuction.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace EAuction.Tests;

/// <summary>
/// Which issuers a service accepts.
///
/// The one setting where a mistake does not degrade the service, it closes it: every
/// endpoint behind <c>RequireAuthorization</c> answers 401 to every caller, including
/// the ones that are entirely correct. These assert the shape of the accepted set
/// rather than any one value, because the failure mode is an empty or wrong set.
/// </summary>
public class JwtIssuerTests
{
    private const string Internal = "http://keycloak:8080/realms/eauction";
    private const string External = "http://localhost:8080/realms/eauction";

    private static string[] AcceptedIssuers(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            // http, so the handler's own post-configure check has to be told this is
            // not a deployment. Every issuer here is a Compose address, which is the
            // situation these tests are about.
            .AddInMemoryCollection(settings
                .Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))
                .Append(new KeyValuePair<string, string?>("Jwt:RequireHttpsMetadata", "false")))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEAuctionJwt(configuration, new FakeEnvironment());

        // Read back what the handler was actually configured with. Asserting on
        // JwtOptions instead would test the record rather than the validation.
        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        // The union of both properties, because IdentityModel accepts either and this
        // is a test about which issuers get in — not about which field holds them. A
        // test that read only the plural would fail the moment the shape changed,
        // whatever the behaviour, and would pass a singular that held the wrong value.
        var parameters = options.TokenValidationParameters;
        return (parameters.ValidIssuers ?? [])
            .Concat(parameters.ValidIssuer is null ? [] : new[] { parameters.ValidIssuer })
            .ToArray();
    }

    [Fact]
    public void An_empty_issuer_setting_does_not_replace_the_authority()
    {
        // The regression. `Jwt__Issuer:` with nothing after it in YAML, or a Helm
        // value that resolved to nothing, reaches configuration as "" and not as
        // null — so `options.Issuer ?? options.Authority` kept the empty string and
        // every token in the service was refused with IDX10204, an error naming
        // neither the setting nor the service.
        var accepted = AcceptedIssuers(
            ("Jwt:Authority", Internal),
            ("Jwt:Issuer", ""));

        Assert.Equal([Internal], accepted);
    }

    [Fact]
    public void Whitespace_is_treated_the_same_as_empty()
    {
        var accepted = AcceptedIssuers(
            ("Jwt:Authority", Internal),
            ("Jwt:Issuer", "   "));

        Assert.Equal([Internal], accepted);
    }

    [Fact]
    public void Both_hostnames_are_accepted_when_the_two_differ()
    {
        // Keycloak stamps iss with the host it was called on, so the same realm
        // issues both forms: localhost to a browser, keycloak:8080 to a service on
        // the Compose network. Accepting only one rejects the other's tokens.
        var accepted = AcceptedIssuers(
            ("Jwt:Authority", Internal),
            ("Jwt:Issuer", External));

        Assert.Equal(2, accepted.Length);
        Assert.Contains(Internal, accepted);
        Assert.Contains(External, accepted);
    }

    [Fact]
    public void The_authority_alone_is_accepted_when_no_issuer_is_configured()
    {
        // Deployment, where Keycloak is reached by one name from everywhere.
        Assert.Equal([Internal], AcceptedIssuers(("Jwt:Authority", Internal)));
    }

    [Fact]
    public void An_issuer_equal_to_the_authority_is_not_listed_twice()
    {
        // Harmless, but a duplicate in the accepted set is the kind of thing that
        // makes a later reader wonder which one is winning.
        Assert.Equal(
            [Internal],
            AcceptedIssuers(("Jwt:Authority", Internal), ("Jwt:Issuer", Internal)));
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        // Development, so the Authority guard does not throw: these tests are about
        // which issuers are accepted, not about the guard that demands one.
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = nameof(JwtIssuerTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
