using System.Globalization;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Extensions.NETCore.Setup;
using Amazon.SecretsManager;
using DocumentProcessor.WebForms.Components;
using DocumentProcessor.WebForms.Configuration;
using DocumentProcessor.WebForms.Data;
using DocumentProcessor.WebForms.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

// Replaces Global.asax / Global.asax.cs (Application_Start, Application_Error).

// Mirrors <httpRuntime maxRequestLength="512000"> and <requestLimits maxAllowedContentLength="524288000">:
// one upload may carry Storage.MaxFilesPerUpload files of Storage.MaxFileSizeMegabytes each.
const long MaxRequestBodyBytes = 500L * 1024L * 1024L;

var builder = WebApplication.CreateBuilder(args);

// Serve Blazor's own script (_framework/blazor.web.js) and _content/ assets even when the
// app starts from its build output outside Development.
builder.WebHost.UseStaticWebAssets();

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxRequestBodyBytes);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = MaxRequestBodyBytes);
// [PORT-NOTE] dropped <httpRuntime executionTimeout="600">: it only existed because summarizing ran
// inside the postback; summarization now runs on the background queue below.

// appsettings.json "Bedrock.*" / "Storage.*" / "Database.*" keys: static accessors for code that is
// created with 'new', plus IOptions<T> for container-created services.
AppSettings.Configure(builder.Configuration, builder.Environment);
builder.Services.AddAppSettingsOptions(builder.Configuration, builder.Environment);

// [PORT-NOTE] dropped Trace.Listeners.Add(new TextWriterTraceListener("~/App_Data/trace.log")):
// all diagnostics now go through ILogger (configured by the "Logging" section of appsettings.json).
// [PORT-NOTE] dropped ServicePointManager.SecurityProtocol |= Tls12: .NET uses the operating
// system's TLS defaults (TLS 1.2+), which every AWS endpoint accepts.

// ---- AWS clients -------------------------------------------------------------------------------
// Bedrock uses the region named by Bedrock.Region, as the legacy static client did. Secrets Manager
// keeps the SDK's default region / credential chain, like the legacy 'new AmazonSecretsManagerClient()'
// (an optional "AWS" section in appsettings.json can override it).
var awsOptions = builder.Configuration.GetAWSOptions();
builder.Services.AddDefaultAWSOptions(awsOptions);
builder.Services.AddAWSService<IAmazonSecretsManager>();
builder.Services.AddAWSService<IAmazonBedrockRuntime>(new AWSOptions
{
    Region = RegionEndpoint.GetBySystemName(AppSettings.BedrockRegion),
    Credentials = awsOptions.Credentials,
    Profile = awsOptions.Profile,
    ProfilesLocation = awsOptions.ProfilesLocation
});

// ---- Database ----------------------------------------------------------------------------------
// Resolved once at startup: Secrets Manager first (Database.UseSecretsManager), falling back to
// ConnectionStrings:DefaultConnection with a logged warning.
var connection = await DatabaseConnectionResolver.ResolveAsync(
    () => awsOptions.CreateServiceClient<IAmazonSecretsManager>());

// Replaces Application[Global.DatabaseInfoKey]; read by Components/Layout/MainLayout.razor.
builder.Services.AddSingleton(connection.Info);

// AddDbContextFactory registers both IDbContextFactory<DocumentDbContext> (used by the Default page)
// and a scoped DocumentDbContext (used by DocumentPipeline). Provider selection lives in one place so
// switching to PostgreSQL is a one-line change once the provider package is referenced.
builder.Services.AddDbContextFactory<DocumentDbContext>(
    options => UseDatabaseProvider(options, connection),
    ServiceLifetime.Scoped);

// ---- Application services ----------------------------------------------------------------------
builder.Services.AddScoped<IDocumentStorage, FileSystemDocumentStorage>();
builder.Services.AddTransient<DocumentTextExtractor>();
builder.Services.AddScoped<IDocumentSummarizer, BedrockDocumentSummarizer>();
builder.Services.AddScoped<DocumentPipeline>();

// Summarization no longer blocks the upload request: the page enqueues the stored document id and
// the worker runs DocumentPipeline in its own scope; the page polls the document status.
builder.Services.AddSingleton<DocumentProcessingQueue>();
builder.Services.AddHostedService<DocumentProcessingWorker>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (connection.Warning != null)
{
    app.Logger.LogWarning("{Warning}", connection.Warning);
}

// Creates the database and the Documents table on first run from App_Data/Schema.sql (resolved
// against the content root). No EF migrations or EnsureCreated. A failure is logged and the app
// keeps starting: the pages already report a failure to read documents.
try
{
    DatabaseInitializer.EnsureSchema(connection.ConnectionString, app.Environment);
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Could not prepare the database.");
}

// PORT-TODO
// {"was": "System.Web.Configuration.GlobalizationSection", "note": "Web.config set no <globalization> culture, so every request used the IIS server's culture. The original server's culture is not in the repository; en-US is assumed. Set Globalization:Culture in appsettings.json to the culture the legacy server ran with."}
var culture = builder.Configuration["Globalization:Culture"] ?? "en-US";
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture(culture)
    .AddSupportedCultures(culture)
    .AddSupportedUICultures(culture));

// customErrors mode="RemoteOnly" (mode="On" in Web.Release.config) with defaultRedirect="~/ErrorPage.aspx".
// Application_Error only logged the exception; the exception handler middleware logs it too.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// <error statusCode="404" redirect="~/ErrorPage.aspx?code=404" />: ErrorPage.razor reads ?code=404.
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

// [PORT-NOTE] no UseHttpsRedirection/UseHsts: the legacy site was served over plain HTTP
// (IISUrl http://localhost:44821/) and never required HTTPS.

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static void UseDatabaseProvider(DbContextOptionsBuilder options, DatabaseConnection connection)
{
    switch (connection.Info.Provider)
    {
        case DatabaseProvider.PostgreSql:
            // PORT-TODO
            // {"note": "The connection string looks like Npgsql (Host=/Username=). Reference Npgsql.EntityFrameworkCore.PostgreSQL and call options.UseNpgsql(connection.ConnectionString) here; DatabaseInitializer and App_Data/Schema.sql are SQL Server-only and need a PostgreSQL counterpart too."}
            throw new InvalidOperationException(
                "The configured connection string targets PostgreSQL, but no PostgreSQL EF Core provider is registered.");
        default:
            options.UseSqlServer(connection.ConnectionString);
            break;
    }
}
