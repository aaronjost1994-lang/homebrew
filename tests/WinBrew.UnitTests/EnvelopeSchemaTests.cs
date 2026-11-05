using System.Text.Json;
using WinBrew.Cli.Output;

namespace WinBrew.UnitTests;

/// <summary>
/// Structural conformance with schemas/cli-output.v1.schema.json (A16 JSON
/// envelope tests). The schema file is the authoring-time contract; these
/// tests assert the runtime envelope never violates its invariants:
/// exactly one JSON document; ok=true → data, no error; ok=false → error
/// {code,name,message,details}, no data; warnings always present.
/// </summary>
public class EnvelopeSchemaTests
{
    [Fact]
    public void SuccessEnvelope_HasDataAndWarnings_NoError()
    {
        var json = JsonSerializer.Serialize(
            JsonEnvelope.Success("doctor", new VersionData("0.1.0", "win-x64")),
            CliJsonContext.Default.JsonEnvelope);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("doctor", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.False(root.TryGetProperty("error", out _)); // schema: error forbidden when ok
    }

    [Fact]
    public void NullData_OmittedFromWire_SchemaValid() // data is not required for ok=true
    {
        var json = JsonSerializer.Serialize(
            JsonEnvelope.Success("config list", null),
            CliJsonContext.Default.JsonEnvelope);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("data", out _)); // omitted, not null-typed
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void FailureEnvelope_HasErrorObject_NoData()
    {
        var json = JsonSerializer.Serialize(
            JsonEnvelope.Failure(
                "config get",
                code: 3,
                name: "NotFound",
                message: "Unknown configuration key 'x'.",
                details: new Dictionary<string, object?> { ["key"] = "x" }),
            CliJsonContext.Default.JsonEnvelope);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.False(root.TryGetProperty("data", out _)); // schema: data forbidden when not ok
        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);

        var error = root.GetProperty("error");
        Assert.Equal(3, error.GetProperty("code").GetInt32());
        Assert.Equal("NotFound", error.GetProperty("name").GetString());
        Assert.Equal("Unknown configuration key 'x'.", error.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Object, error.GetProperty("details").ValueKind);
    }

    [Fact]
    public void Envelope_IsExactlyOneJsonDocument() // A8: stdout carries exactly one document
    {
        var json = JsonSerializer.Serialize(
            JsonEnvelope.Success("list", null),
            CliJsonContext.Default.JsonEnvelope);

        using var doc = JsonDocument.Parse(json); // throws on trailing content
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void DoctorReport_SerializesWithStringEnumStatuses() // ADR-0006.5
    {
        var json = JsonSerializer.Serialize(
            JsonEnvelope.Success("doctor", new WinBrew.Application.Doctor.DoctorReport(
                "0.1.0",
                "/root",
                [new WinBrew.Application.Doctor.DoctorCheck("check", WinBrew.Application.Doctor.DoctorStatus.Warn, "detail")])),
            CliJsonContext.Default.JsonEnvelope);

        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement.GetProperty("data").GetProperty("checks")[0].GetProperty("status");
        Assert.Equal(JsonValueKind.String, status.ValueKind);
        Assert.Equal("Warn", status.GetString());
    }
}
