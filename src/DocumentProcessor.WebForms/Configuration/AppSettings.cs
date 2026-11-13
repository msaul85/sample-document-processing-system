using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentProcessor.WebForms.Configuration
{
    /// <summary>Settings under the "Database.*" keys of appsettings.json.</summary>
    public class DatabaseOptions
    {
        public bool UseSecretsManager { get; set; }

        public string SecretDescriptionPrefix { get; set; }
    }

    /// <summary>Settings under the "Bedrock.*" keys of appsettings.json.</summary>
    public class BedrockOptions
    {
        public string Region { get; set; }

        public string SummarizationModelId { get; set; }

        public int MaxTokens { get; set; }

        /// <summary>Characters of extracted text sent to the model.</summary>
        public int MaxInputCharacters { get; set; }

        public int MaxPdfPages { get; set; }
    }

    /// <summary>Settings under the "Storage.*" keys of appsettings.json.</summary>
    public class StorageOptions
    {
        /// <summary>Configured path as written in appsettings.json (may be "~/..." relative to the content root).</summary>
        public string RootPath { get; set; }

        /// <summary><see cref="RootPath"/> resolved against IWebHostEnvironment.ContentRootPath.</summary>
        public string PhysicalRootPath { get; set; }

        public int MaxFileSizeMegabytes { get; set; }

        public long MaxFileSizeBytes
        {
            get { return MaxFileSizeMegabytes * 1024L * 1024L; }
        }

        public int MaxFilesPerUpload { get; set; }

        public string[] AllowedExtensions { get; set; }
    }

    /// <summary>
    /// Reads settings from IConfiguration (appsettings.json). No validation on startup --
    /// a bad value surfaces as a FormatException the first time the setting is touched.
    /// The static accessors stay for callers that are static or created with <c>new</c>;
    /// container-created types can take IOptions&lt;BedrockOptions&gt; / IOptions&lt;StorageOptions&gt; /
    /// IOptions&lt;DatabaseOptions&gt; registered through <see cref="AddAppSettingsOptions"/>.
    /// Keys are read in the flat "Section.Name" form used by appsettings.json, falling back
    /// to the nested "Section:Name" form.
    /// </summary>
    public static class AppSettings
    {
        private static IConfiguration _configuration;
        private static string _contentRootPath;

        /// <summary>Supplies the configuration and content root; call once at startup.</summary>
        public static void Configure(IConfiguration configuration, IWebHostEnvironment environment)
        {
            if (environment == null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            Configure(configuration, environment.ContentRootPath);
        }

        /// <summary>Supplies the configuration and content root; call once at startup.</summary>
        public static void Configure(IConfiguration configuration, string contentRootPath)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _contentRootPath = contentRootPath ?? throw new ArgumentNullException(nameof(contentRootPath));
        }

        /// <summary>
        /// Registers IOptions&lt;DatabaseOptions&gt;, IOptions&lt;BedrockOptions&gt; and IOptions&lt;StorageOptions&gt;
        /// bound from <paramref name="configuration"/>, with Storage.RootPath resolved against the content root.
        /// </summary>
        public static IServiceCollection AddAppSettingsOptions(
            this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (environment == null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            services.Configure<DatabaseOptions>(o => FillDatabase(o, configuration));
            services.Configure<BedrockOptions>(o => FillBedrock(o, configuration));
            services.Configure<StorageOptions>(o => FillStorage(o, configuration, environment.ContentRootPath));
            return services;
        }

        /// <summary>Current "Database.*" settings.</summary>
        public static DatabaseOptions Database
        {
            get
            {
                var options = new DatabaseOptions();
                FillDatabase(options, Current);
                return options;
            }
        }

        /// <summary>Current "Bedrock.*" settings.</summary>
        public static BedrockOptions Bedrock
        {
            get
            {
                var options = new BedrockOptions();
                FillBedrock(options, Current);
                return options;
            }
        }

        /// <summary>Current "Storage.*" settings.</summary>
        public static StorageOptions Storage
        {
            get
            {
                var options = new StorageOptions();
                FillStorage(options, Current, ContentRootPath);
                return options;
            }
        }

        private static IConfiguration Current
        {
            get
            {
                return _configuration ?? throw new InvalidOperationException(
                    "AppSettings.Configure was not called at startup.");
            }
        }

        private static string ContentRootPath
        {
            get
            {
                return _contentRootPath ?? throw new InvalidOperationException(
                    "AppSettings.Configure was not called at startup.");
            }
        }

        public static string ConnectionString
        {
            get
            {
                var setting = Current.GetConnectionString("DefaultConnection");
                if (setting == null)
                {
                    throw new InvalidOperationException(
                        "The 'DefaultConnection' connection string is missing from appsettings.json.");
                }

                return setting;
            }
        }

        public static bool DatabaseUseSecretsManager
        {
            get { return GetBoolean("Database.UseSecretsManager", false); }
        }

        public static string DatabaseSecretDescriptionPrefix
        {
            get { return GetString("Database.SecretDescriptionPrefix", "Password for RDS MSSQL used for MAM417."); }
        }

        public static string BedrockRegion
        {
            get { return GetString("Bedrock.Region", "us-east-1"); }
        }

        public static string BedrockSummarizationModelId
        {
            get { return GetString("Bedrock.SummarizationModelId", "global.anthropic.claude-sonnet-5"); }
        }

        public static int BedrockMaxTokens
        {
            get { return GetInt32("Bedrock.MaxTokens", 2000); }
        }

        /// <summary>Characters of extracted text sent to the model.</summary>
        public static int BedrockMaxInputCharacters
        {
            get { return GetInt32("Bedrock.MaxInputCharacters", 10000); }
        }

        public static int BedrockMaxPdfPages
        {
            get { return GetInt32("Bedrock.MaxPdfPages", 5); }
        }

        /// <summary>Configured path as written ("~/App_Data/uploads"); use <see cref="StoragePhysicalRootPath"/> for file access.</summary>
        public static string StorageRootPath
        {
            get { return GetString("Storage.RootPath", "~/App_Data/uploads"); }
        }

        /// <summary><see cref="StorageRootPath"/> resolved against IWebHostEnvironment.ContentRootPath.</summary>
        public static string StoragePhysicalRootPath
        {
            get { return ResolveContentPath(StorageRootPath, ContentRootPath); }
        }

        public static int StorageMaxFileSizeMegabytes
        {
            get { return GetInt32("Storage.MaxFileSizeMegabytes", 50); }
        }

        public static long StorageMaxFileSizeBytes
        {
            get { return StorageMaxFileSizeMegabytes * 1024L * 1024L; }
        }

        public static int StorageMaxFilesPerUpload
        {
            get { return GetInt32("Storage.MaxFilesPerUpload", 10); }
        }

        public static string[] StorageAllowedExtensions
        {
            get
            {
                var raw = GetString("Storage.AllowedExtensions", ".pdf,.txt,.log");
                return raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        private static void FillDatabase(DatabaseOptions options, IConfiguration configuration)
        {
            options.UseSecretsManager = GetBoolean(configuration, "Database.UseSecretsManager", false);
            options.SecretDescriptionPrefix = GetString(configuration, "Database.SecretDescriptionPrefix", "Password for RDS MSSQL used for MAM417.");
        }

        private static void FillBedrock(BedrockOptions options, IConfiguration configuration)
        {
            options.Region = GetString(configuration, "Bedrock.Region", "us-east-1");
            options.SummarizationModelId = GetString(configuration, "Bedrock.SummarizationModelId", "global.anthropic.claude-sonnet-5");
            options.MaxTokens = GetInt32(configuration, "Bedrock.MaxTokens", 2000);
            options.MaxInputCharacters = GetInt32(configuration, "Bedrock.MaxInputCharacters", 10000);
            options.MaxPdfPages = GetInt32(configuration, "Bedrock.MaxPdfPages", 5);
        }

        private static void FillStorage(StorageOptions options, IConfiguration configuration, string contentRootPath)
        {
            options.RootPath = GetString(configuration, "Storage.RootPath", "~/App_Data/uploads");
            options.PhysicalRootPath = ResolveContentPath(options.RootPath, contentRootPath);
            options.MaxFileSizeMegabytes = GetInt32(configuration, "Storage.MaxFileSizeMegabytes", 50);
            options.MaxFilesPerUpload = GetInt32(configuration, "Storage.MaxFilesPerUpload", 10);
            options.AllowedExtensions = GetString(configuration, "Storage.AllowedExtensions", ".pdf,.txt,.log")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>Maps "~/a/b" (or a relative path) onto the content root; absolute paths pass through.</summary>
        private static string ResolveContentPath(string path, string contentRootPath)
        {
            if (Path.IsPathRooted(path) && !path.StartsWith("~", StringComparison.Ordinal))
            {
                return path;
            }

            var relative = path.TrimStart('~').TrimStart('/', '\\')
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            return Path.GetFullPath(Path.Combine(contentRootPath, relative));
        }

        private static string Lookup(IConfiguration configuration, string key)
        {
            var value = configuration[key];
            if (string.IsNullOrEmpty(value))
            {
                value = configuration[key.Replace('.', ':')];
            }

            return value;
        }

        private static string GetString(string key, string fallback)
        {
            return GetString(Current, key, fallback);
        }

        private static int GetInt32(string key, int fallback)
        {
            return GetInt32(Current, key, fallback);
        }

        private static bool GetBoolean(string key, bool fallback)
        {
            return GetBoolean(Current, key, fallback);
        }

        private static string GetString(IConfiguration configuration, string key, string fallback)
        {
            var value = Lookup(configuration, key);
            return string.IsNullOrEmpty(value) ? fallback : value.Trim();
        }

        private static int GetInt32(IConfiguration configuration, string key, int fallback)
        {
            var value = Lookup(configuration, key);
            return string.IsNullOrEmpty(value) ? fallback : int.Parse(value.Trim());
        }

        private static bool GetBoolean(IConfiguration configuration, string key, bool fallback)
        {
            var value = Lookup(configuration, key);
            return string.IsNullOrEmpty(value) ? fallback : bool.Parse(value.Trim());
        }
    }
}
