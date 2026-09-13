using System.Text;
using Foundry.FileIO;
using MongoDB.Bson;
using Xunit;

namespace Foundry.FileIO.Tests;

/// <summary>
/// CSV export/import of records containing <see cref="ObjectId"/> properties. Pins the defect where
/// CsvHelper, lacking a registered converter for <see cref="ObjectId"/>, treated it as a complex
/// object and auto-mapped its own public members (<c>Timestamp</c>, <c>CreationTime</c>) in place of
/// the id value.
/// </summary>
public sealed class ObjectIdCsvTests
{
    public sealed class SingleIdRow
    {
        public ObjectId ResourceId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class TwoIdRow
    {
        public ObjectId ResourceId { get; set; }
        public ObjectId ProjectId { get; set; }
        public string RoleOnProject { get; set; } = string.Empty;
    }

    private static async Task<string> Export<T>(params T[] rows)
    {
        var output = new MemoryStream();
        await new CsvDataExporter<T>().ExportAsync(ToAsync(rows), output);

        output.Position = 0;
        return await new StreamReader(output).ReadToEndAsync();
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            await Task.CompletedTask;
        }
    }

    private static async Task<List<T>> Parse<T>(string csv)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var results = new List<T>();

        await foreach (var row in new CsvDataParser<T>().ParseAsync(stream))
        {
            results.Add(row);
        }

        return results;
    }

    [Fact]
    public async Task ExportUsesThePropertyNameAsTheHeaderForAnObjectIdColumn()
    {
        var id = ObjectId.GenerateNewId();
        var csv = await Export(new SingleIdRow { ResourceId = id, Name = "Test" });

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("ResourceId", lines[0]);

        // Pins the defect: without a registered converter, ObjectId's own public members Timestamp
        // and CreationTime are auto-mapped as columns in place of the ResourceId value.
        Assert.DoesNotContain("Timestamp", lines[0]);
        Assert.DoesNotContain("CreationTime", lines[0]);
    }

    [Fact]
    public async Task ExportedObjectIdValueIsThe24CharacterHexString()
    {
        var id = ObjectId.GenerateNewId();
        var csv = await Export(new SingleIdRow { ResourceId = id, Name = "Test" });

        Assert.Contains(id.ToString(), csv);
    }

    [Fact]
    public async Task ATypeWithTwoObjectIdPropertiesExportsTwoDistinctlyNamedColumnsWithNoDuplicates()
    {
        var resourceId = ObjectId.GenerateNewId();
        var projectId = ObjectId.GenerateNewId();
        var csv = await Export(new TwoIdRow { ResourceId = resourceId, ProjectId = projectId, RoleOnProject = "Developer" });

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var headerColumns = lines[0].Split(',', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("ResourceId", headerColumns);
        Assert.Contains("ProjectId", headerColumns);

        // Pins the duplicate-header defect: with no converter, each ObjectId property expanded into
        // its own Timestamp/CreationTime pair, so two ObjectId properties produced two identically
        // named "Timestamp,CreationTime" pairs.
        Assert.Equal(headerColumns.Length, headerColumns.Distinct().Count());
        Assert.DoesNotContain("Timestamp", lines[0]);
    }

    [Fact]
    public async Task AnObjectIdSurvivesAnExportThenParseRoundTrip()
    {
        var id = ObjectId.GenerateNewId();
        var csv = await Export(new SingleIdRow { ResourceId = id, Name = "Test" });

        var parsed = Assert.Single(await Parse<SingleIdRow>(csv));
        Assert.Equal(id, parsed.ResourceId);
    }
}
