using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Validator;
using Apex.PdfEdit.Web.Configuration;
using Apex.PdfEdit.Web.Endpoints;
using Apex.PdfEdit.Web.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, config) => config
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} | {Level:u5} | {SourceContext} : {Message:lj}{NewLine}{Exception}"));

// Bind EditOptions from the Apex:Edit configuration section.
builder.Services.Configure<EditOptions>(builder.Configuration.GetSection(EditOptions.SectionName));

// EditService is stateless per request; singleton is fine (it constructs
// SourcePdfFontResolver per call and disposes it).
builder.Services.AddSingleton<EditService>();

var app = builder.Build();

app.UseSerilogRequestLogging();

app.MapGet("/health", () => Results.Ok(new { status = "ok", phase = "D1" }));

app.MapPost("/validate", (ValidateRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.DocumentPath) || !File.Exists(req.DocumentPath))
    {
        return Results.BadRequest(new { error = "documentPath does not exist", path = req.DocumentPath });
    }
    var doc = DocumentJsonLoader.Load(req.DocumentPath);
    var schema = SchemaLoader.LoadDefault();
    var report = new TagTreeValidator(schema).Validate(doc);
    return Results.Ok(new
    {
        nodes = doc.Tree.Count,
        errors = report.ErrorCount(),
        warnings = report.WarningCount(),
        distinctErrorNodes = report.DistinctErrorNodes(),
        clean = report.IsClean()
    });
});

// POST /edit — full pipeline (see EditEndpoints).
app.MapEditEndpoints();

app.Run();

public sealed record ValidateRequest(string DocumentPath);

/// <summary>Handle exposed for WebApplicationFactory in tests.</summary>
public partial class Program;
