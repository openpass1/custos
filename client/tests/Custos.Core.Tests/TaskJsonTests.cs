using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core.Json;
using Custos.Core.Model;

namespace Custos.Core.Tests;

public sealed class TaskJsonTests
{
    [Fact]
    public void Fields_unknown_to_this_version_survive_a_round_trip()
    {
        const string json = """
            {
              "id": "6f0c1d2e-0000-4000-8000-000000000001",
              "revision": 3,
              "name": "Docs",
              "backup_type": "incremental",
              "future_field": {"a": [1, 2]},
              "schedule": {"type": "daily", "times": ["21:30"], "jitter_s": 60},
              "destinations": [{"id": "d1", "kind": "s3", "bucket": "b", "storage_class": "cold"}]
            }
            """;

        var task = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.TaskDefinition)!;
        var written = JsonNode.Parse(JsonSerializer.Serialize(task, ConfigJsonContext.Default.TaskDefinition))!;

        Assert.Equal(2, written["future_field"]!["a"]!.AsArray().Count);
        Assert.Equal(60, written["schedule"]!["jitter_s"]!.GetValue<int>());
        Assert.Equal("cold", written["destinations"]![0]!["storage_class"]!.GetValue<string>());
        Assert.Equal("s3", written["destinations"]![0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_fields_get_the_defaults_of_appendix_a()
    {
        var task = JsonSerializer.Deserialize("""{"name": "Docs"}""", ConfigJsonContext.Default.TaskDefinition)!;

        Assert.True(task.Enabled);
        Assert.Equal(BackupTypes.Incremental, task.BackupType);
        Assert.Equal(12, task.Retention.KeepFull);
        Assert.Equal(RetentionModes.Count, task.Retention.Mode);
        Assert.True(task.Layout.SeparateFolders);
        Assert.Equal(LayoutOptions.DefaultNameTemplate, task.Layout.NameTemplate);
        Assert.Equal(["~$*", "*.tmp"], task.Filters.Exclude);
        Assert.True(task.Vss.Enabled);
        Assert.Equal(VssFailureActions.Abort, task.Vss.OnFailure);
        Assert.True(task.Schedule.CatchUp);
        Assert.Equal(1, task.Copy.ReadRetries);
    }

    [Fact]
    public void Times_are_written_in_utc_to_the_second()
    {
        var task = new TaskDefinition { Name = "Docs", UpdatedAt = new DateTimeOffset(2026, 10, 12, 23, 0, 0, TimeSpan.FromHours(2)) };

        string json = JsonSerializer.Serialize(task, ConfigJsonContext.Default.TaskDefinition);

        Assert.Contains("\"updated_at\": \"2026-10-12T21:00:00Z\"", json, StringComparison.Ordinal);
        Assert.Equal(task.UpdatedAt, JsonSerializer.Deserialize(json, ConfigJsonContext.Default.TaskDefinition)!.UpdatedAt);
    }

    [Fact]
    public void A_clone_is_independent()
    {
        var task = new TaskDefinition { Name = "Docs", Sources = [new TaskSource { Path = @"D:\Data" }] };

        var clone = task.Clone();
        clone.Sources[0].Path = @"E:\Other";

        Assert.Equal(@"D:\Data", task.Sources[0].Path);
        Assert.Equal(task.Id, clone.Id);
    }
}
