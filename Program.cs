using System.Runtime.Loader;
using HirschNotify.Data;
using HirschNotify.Services;
using HirschNotify.Services.Health;
using HirschNotify.Services.Health.Sources;
using HirschNotify.Workers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("Logs/HirschNotify-.log", rollingInterval: RollingInterval.Day)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseWindowsService();
    builder.Services.Configure<HostOptions>(options =>
        options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Console()
        .WriteTo.File("Logs/HirschNotify-.log", rollingInterval: RollingInterval.Day));

    // Database — use SQLite if configured, otherwise MSSQL
    var dbProvider = builder.Configuration.GetValue<string>("DatabaseProvider") ?? "SqlServer";
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        if (dbProvider == "Sqlite")
            options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection"));
        else
            options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
        options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    });

    // Identity
    builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
    {
        options.Password.RequireDigit = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 6;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = true;
    });

    // Application services
    builder.Services.AddScoped<ISettingsService, SettingsService>();
    builder.Services.AddScoped<RelayUrlResolver>();
    builder.Services.AddHttpClient<IRelayClient, RelayClient>();
    builder.Services.AddHttpClient<IRelaySender, RelaySender>();
    builder.Services.AddScoped<INotificationSender, NotificationSender>();
    builder.Services.AddScoped<IContactMethodSender, EmailSender>();

    // Load contact method plugins from the plugins/ directory.
    // Any DLL containing a class implementing IContactMethodSender is registered
    // automatically. Drop a new plugin into plugins/ and restart to pick it up.
    var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
    if (Directory.Exists(pluginsDir))
    {
        foreach (var dllPath in Directory.GetFiles(pluginsDir, "*.dll"))
        {
            try
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
                var senderTypes = assembly.GetTypes()
                    .Where(t => t.IsClass && !t.IsAbstract && typeof(IContactMethodSender).IsAssignableFrom(t))
                    .ToList();

                foreach (var type in senderTypes)
                {
                    builder.Services.AddScoped(typeof(IContactMethodSender), type);
                    Log.Information("Loaded contact method plugin: {Type} from {Dll}", type.FullName, Path.GetFileName(dllPath));
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load plugin {Dll}", Path.GetFileName(dllPath));
            }
        }
    }
    else
    {
        Directory.CreateDirectory(pluginsDir);
    }
    builder.Services.AddSingleton<ConnectionState>();
    builder.Services.AddScoped<IFilterEngine, FilterEngine>();
    builder.Services.AddScoped<IThrottleManager, ThrottleManager>();
    builder.Services.AddHttpClient<IWebSocketAuthService, WebSocketAuthService>();
    builder.Services.AddSingleton<IEventProcessor, EventProcessor>();
    builder.Services.AddSingleton<IVelocityServerAccessor, VelocityServerAccessor>();

    // Health framework — SRE-facing Velocity health metrics pipeline.
    // IHealthSource implementations are registered as singletons so the worker
    // can enumerate them via DI; add new sources (event log, etc.) here.
    builder.Services.Configure<HealthSettings>(
        builder.Configuration.GetSection(HealthSettings.SectionName));
    builder.Services.AddSingleton<IHealthEventEmitter, HealthEventEmitter>();
    builder.Services.AddSingleton<IHealthSource, SdkHealthSource>();
    if (OperatingSystem.IsWindows())
    {
        builder.Services.AddSingleton<IHealthSource, WindowsServiceHealthSource>();
    }

    // Background workers
    builder.Services.AddHostedService<WebSocketWorker>();
    builder.Services.AddHostedService<VelocityAdapterWorker>();
    builder.Services.AddHostedService<VelocitySreHealthWorker>();
    builder.Services.AddHostedService<ConnectionMonitorWorker>();
    builder.Services.AddHostedService<ThrottleCleanupWorker>();
    builder.Services.AddHostedService<RelayHeartbeatWorker>();
    builder.Services.AddHostedService<RelayRegistrationPollingWorker>();

    // Razor Pages
    builder.Services.AddRazorPages();

    var app = builder.Build();

    // Auto-migrate on startup
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.Migrate();

        // Create new tables if they don't exist (for models added after last migration)
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS RecipientGroups (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Description TEXT,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS RecipientGroupMembers (
                RecipientGroupId INTEGER NOT NULL,
                RecipientId INTEGER NOT NULL,
                PRIMARY KEY (RecipientGroupId, RecipientId),
                FOREIGN KEY (RecipientGroupId) REFERENCES RecipientGroups(Id) ON DELETE CASCADE,
                FOREIGN KEY (RecipientId) REFERENCES Recipients(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS FilterRuleRecipientGroups (
                FilterRuleId INTEGER NOT NULL,
                RecipientGroupId INTEGER NOT NULL,
                PRIMARY KEY (FilterRuleId, RecipientGroupId),
                FOREIGN KEY (FilterRuleId) REFERENCES FilterRules(Id) ON DELETE CASCADE,
                FOREIGN KEY (RecipientGroupId) REFERENCES RecipientGroups(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS ContactMethods (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RecipientId INTEGER NOT NULL,
                Type TEXT NOT NULL,
                Label TEXT NOT NULL,
                Configuration TEXT NOT NULL DEFAULT '{{}}',
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (RecipientId) REFERENCES Recipients(Id) ON DELETE CASCADE
            );
        ");

        // Drop legacy notification columns from Recipients (PhoneNumber, PushoverUserKey, NotifyVia)
        // that were defined in the initial migration but never used.
        try
        {
            var columns = new[] { "PhoneNumber", "PushoverUserKey", "NotifyVia" };
            foreach (var col in columns)
            {
                // Check if column exists before trying to drop it
                var exists = db.Database.SqlQueryRaw<int>(
                    $"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Recipients') WHERE name = '{col}'")
                    .FirstOrDefault();
                if (exists > 0)
                {
                    db.Database.ExecuteSqlRaw($"ALTER TABLE Recipients DROP COLUMN {col}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not drop legacy columns from Recipients (non-fatal)");
        }

        // Apply installer settings if install-config.json exists
        var installConfigPath = Path.Combine(AppContext.BaseDirectory, "install-config.json");
        if (File.Exists(installConfigPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(installConfigPath);
                var config = System.Text.Json.JsonDocument.Parse(json).RootElement;
                var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

                if (config.TryGetProperty("eventSourceMode", out var mode))
                    await settingsService.SetAsync("EventSource:Mode", mode.GetString() ?? "VelocityAdapter");

                File.Delete(installConfigPath);
                Log.Information("Applied installer configuration and removed install-config.json");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to apply install-config.json");
            }
        }

        // Seed dev data if in Development and DB is empty
        if (app.Environment.IsDevelopment())
        {
            await HirschNotify.Data.DevSeeder.SeedAsync(scope.ServiceProvider);
        }
    }

    app.UseSerilogRequestLogging();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapRazorPages();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
