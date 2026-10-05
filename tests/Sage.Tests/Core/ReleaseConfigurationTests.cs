#nullable enable
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Sage.Simulation;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// `-c Release` is what `dotnet publish` defaults to. It behaves as Shipping, so it has to write
// bin/Shipping and be found as {config} = Shipping (issue #294): the packed Player has no Release host.
// The game's project is evaluated, not built: MSBuild says where the dll would go.
public class ReleaseConfigurationTests
{
    private static (string Target, string Config, string Defines) Evaluate(string configuration)
    {
        string project = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Hello", "Hello.csproj");
        var psi = new ProcessStartInfo("dotnet", $"msbuild \"{project}\" -p:Configuration={configuration} -getProperty:TargetPath,SageConfiguration,DefineConstants")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        using var doc = JsonDocument.Parse(output);
        var props = doc.RootElement.GetProperty("Properties");
        return (props.GetProperty("TargetPath").GetString()!.Replace('\\', '/'),
                props.GetProperty("SageConfiguration").GetString()!,
                props.GetProperty("DefineConstants").GetString()!);
    }

    [Fact]
    public void AReleaseBuildIsShippingAndWritesTheShippingFolder()
    {
        var (target, config, defines) = Evaluate("Release");
        Assert.Equal("Shipping", config);
        Assert.EndsWith("/games/Hello/bin/Shipping/Hello.dll", target);
        Assert.Contains("SAGE_SHIPPING", defines);
    }

    [Fact]
    public void OtherConfigurationsKeepTheirOwnFolder()
    {
        Assert.EndsWith("/bin/Debug/Hello.dll", Evaluate("Debug").Target);
        Assert.EndsWith("/bin/Shipping/Hello.dll", Evaluate("Shipping").Target);
    }

    [Fact]
    public void TheEnginesConfigurationNameIsOneOfTheFoldersABuildWrites()
    {
        // {config} resolves from BuildInfo.ConfigurationName; it must never be the word Release.
        Assert.NotEqual("Release", BuildInfo.ConfigurationName);
    }
}
