using PhoneBox.Agent;
using Xunit;

public sealed class CodexBarTests
{
    [Fact]
    public void ParsesPrimarySecondaryAndSupplementalWindowsWithoutPlanMetadata()
    {
        var p = Assert.Single(CodexBar.Parse("""
            [{"provider":"claude","usage":{"primary":{"usedPercent":0,"windowMinutes":300},
            "secondary":{"usedPercent":80,"windowMinutes":10080},
            "extraRateWindows":[{"title":"Model pool","window":{"usedPercent":25}}]}}]
            """));
        Assert.Equal(3, p.Limits.Count);
        Assert.Equal(0, p.Limits[0].Percent);
        Assert.Equal("Weekly", p.Limits[1].Label);
        Assert.Equal("Model pool", p.Limits[2].Label);
        Assert.Null(p.Error);
    }

    [Fact]
    public void SyntheticAndMalformedWindowsAreSkippedWithoutDroppingLaterProviders()
    {
        var p = CodexBar.Parse("""
            [{"provider":"claude","usage":{"primary":{"usedPercent":0,"isSyntheticPlaceholder":true},"secondary":{"usedPercent":"unknown"}}},
            {"provider":"antigravity","usage":{"secondary":{"usedPercent":0}}}]
            """);
        Assert.Empty(p[0].Limits);
        Assert.NotNull(p[0].Error);
        Assert.Equal("Secondary limit", Assert.Single(p[1].Limits).Label);
    }

    [Fact]
    public void UsesLocalFallbackOnProviderErrorAndKeepsItFresh()
    {
        var external = Assert.Single(CodexBar.Parse("""[{"provider":"codex","error":{"message":"Signed out"}}]"""));
        var first = new QuotaProvider("codex", "Codex", "Plan", [new("Weekly", 10, null, null, "normal")], null, 1, null);
        var next = first with { Limits = [new("Weekly", 40, null, null, "normal")], UpdatedAt = 2 };
        Assert.Equal(10, CodexBar.Select(first, external, 2)!.Limits[0].Percent);
        Assert.Equal(40, CodexBar.Select(next, external, 3)!.Limits[0].Percent);
        Assert.Equal("Signed out", CodexBar.Select(null, external, 3)!.Error);
        var healthy = next with { Source = "codexbar", UpdatedAt = 3 };
        Assert.Equal("codexbar", CodexBar.Select(first, healthy, 4)!.Source);
        Assert.Equal("local", CodexBar.Select(first, healthy, 600004)!.Source);
    }

    [Fact]
    public void InvalidRootIsAnError() => Assert.Throws<System.Text.Json.JsonException>(() => CodexBar.Parse("{}"));

    [Fact]
    public void AFailedProviderDoesNotDiscardOtherProvidersFromANonzeroExit()
    {
        // CodexBar 0.84.0 merges provider outputs but returns failure if any provider fails.
        var providers = CodexBar.Parse("""
            [{"provider":"claude","error":{"message":"Not signed in"}},
             {"provider":"codex","usage":{"primary":{"usedPercent":32,"windowMinutes":300}}}]
            """, exitCode: 1);
        Assert.Equal(2, providers.Count);
        Assert.Equal("Not signed in", providers[0].Error);
        Assert.Null(providers[1].Error);
        Assert.Equal(32, Assert.Single(providers[1].Limits).Percent);
    }

    [Fact]
    public void FatalCliErrorsRemainFailures() => Assert.Throws<IOException>(() => CodexBar.Parse(
        """[{"provider":"cli","error":{"message":"Invalid arguments"}}]""", exitCode: 1));

    [Fact]
    public void TertiaryWindowsAreRetainedAlongsidePrimaryAndSecondary()
    {
        var provider = Assert.Single(CodexBar.Parse("""
            [{"provider":"claude","rateWindowLabels":{"tertiary":"Opus"},"usage":{
            "primary":{"usedPercent":10},"secondary":{"usedPercent":20},"tertiary":{"usedPercent":30}}}]
            """));
        Assert.Equal(3, provider.Limits.Count);
        Assert.Equal("Opus", provider.Limits[2].Label);
        Assert.Equal(30, provider.Limits[2].Percent);
    }

    [Fact]
    public void ParsesCostOverviewAndAggregatesTopModelsAcrossDays()
    {
        var overview = CodexBar.ParseCost("""
            [
              {
                "provider": "claude",
                "last30DaysCostUSD": 42.5,
                "last30DaysTokens": 1250000,
                "sessionCostUSD": 1.25,
                "sessionTokens": 35000,
                "historyDays": 30,
                "daily": [
                  {
                    "date": "2026-04-17",
                    "totalCost": 20.0,
                    "totalTokens": 600000,
                    "modelBreakdowns": [
                      { "modelName": "claude-sonnet-4-5", "cost": 15.0, "totalTokens": 500000 },
                      { "modelName": "claude-opus-4-6", "cost": 5.0, "totalTokens": 100000 }
                    ]
                  },
                  {
                    "date": "2026-04-18",
                    "totalCost": 22.5,
                    "totalTokens": 650000,
                    "modelBreakdowns": [
                      { "modelName": "claude-sonnet-4-5", "cost": 22.5, "totalTokens": 650000 }
                    ]
                  }
                ]
              },
              {
                "provider": "codex",
                "totals": { "totalCost": 18.0, "totalTokens": 800000 }
              }
            ]
            """);

        Assert.NotNull(overview);
        Assert.Equal(2, overview.ConnectedCount);
        Assert.Equal(60.5, overview.TotalCost30dUsd);
        Assert.Equal(2050000, overview.TotalTokens30d);
        Assert.Equal("claude", overview.Subscriptions[0].Id);
        Assert.Equal(42.5, overview.Subscriptions[0].Cost30dUsd);
        Assert.Equal("codex", overview.Subscriptions[1].Id);
        Assert.Equal(18.0, overview.Subscriptions[1].Cost30dUsd);
        Assert.Equal(2, overview.TopModels.Count);
        Assert.Equal("claude-sonnet-4-5", overview.TopModels[0].ModelName);
        Assert.Equal(37.5, overview.TopModels[0].Cost30dUsd);
        Assert.Equal(1150000, overview.TopModels[0].Tokens30d);
    }
}
