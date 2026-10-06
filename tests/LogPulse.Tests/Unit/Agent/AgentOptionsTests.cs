using System.ComponentModel.DataAnnotations;
using LogPulse.Agent.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace LogPulse.Tests.Unit.Agent;

public class AgentOptionsTests
{
    private static AgentOptions Valid() => new()
    {
        ApiBaseUrl = new Uri("https://logpulse.example.com/"),
        ServerName = "web-01",
        ApiKey = "lp_real_key",
        LogFiles = [new LogFileOptions { Path = "/var/log/app.log" }],
    };

    private static List<string> Errors(AgentOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results.Select(r => r.ErrorMessage ?? string.Empty).ToList();
    }

    private static HostingEnvironment Environment(string name) => new() { EnvironmentName = name };

    [Fact]
    public void Valid_options_pass()
    {
        Assert.Empty(Errors(Valid()));
    }

    [Theory]
    [InlineData("ftp://logpulse.example.com/")]
    [InlineData("/relative/path")]
    public void The_api_address_must_be_absolute_http_or_https(string url)
    {
        var options = Valid();
        options.ApiBaseUrl = new Uri(url, UriKind.RelativeOrAbsolute);

        Assert.NotEmpty(Errors(options));
    }

    [Fact]
    public void Invalid_values_are_reported()
    {
        var options = Valid();
        options.ServerName = "web 01/x";
        options.MaxBatchBytes = 1024; // below one maximum-size entry

        Assert.Equal(2, Errors(options).Count);
    }

    [Fact]
    public void Inconsistent_settings_are_reported()
    {
        var options = Valid();
        options.IdleMetricsInterval = TimeSpan.FromSeconds(1); // shorter than MetricsInterval
        options.LogFiles = [new LogFileOptions { Path = " " }];

        Assert.Equal(2, Errors(options).Count);
    }

    [Fact]
    public void Development_accepts_the_development_key_and_plain_http()
    {
        var options = Valid();
        options.ApiKey = "lp_dev_demo_agent_key_not_secret";
        options.ApiBaseUrl = new Uri("http://192.168.1.20:5080");

        AgentStartupChecks.Check(options, Environment(Environments.Development));
    }

    [Fact]
    public void Outside_development_the_development_key_is_refused()
    {
        var options = Valid();
        options.ApiKey = "lp_dev_demo_agent_key_not_secret";

        Assert.Throws<InvalidOperationException>(() => AgentStartupChecks.Check(options, Environment(Environments.Production)));
    }

    [Fact]
    public void Outside_development_plain_http_is_refused_unless_the_api_is_local()
    {
        var remote = Valid();
        remote.ApiBaseUrl = new Uri("http://logpulse.example.com");
        var local = Valid();
        local.ApiBaseUrl = new Uri("http://localhost:5080");

        Assert.Throws<InvalidOperationException>(() => AgentStartupChecks.Check(remote, Environment(Environments.Production)));
        AgentStartupChecks.Check(local, Environment(Environments.Production));
    }
}
