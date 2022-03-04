using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Caching;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Hangfire;
using Hangfire.Console;
using Hangfire.MySql;
using HealthChecks.UI.Client;
using Registry.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json.Serialization;
using Registry.Adapters;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Filters;
using Registry.Web.HealthChecks;
using Registry.Web.Middlewares;
using Registry.Web.Models.Configuration;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Serilog;
using Serilog.Events;
using IHostingEnvironment = Microsoft.Extensions.Hosting.IHostingEnvironment;
using Auth0.AspNetCore.Authentication;

namespace Registry.Web
{
    public class Startup
    {
        private const string IdentityConnectionName = "IdentityConnection";
        private const string RegistryConnectionName = "RegistryConnection";
        private const string HangfireConnectionName = "HangfireConnection";

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddCors();
            //services.AddControllers();

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Version = "v1",
                    Title = "Registry API",
                    Description = "API to manage DroneDB Registry",
                    Contact = new OpenApiContact
                    {
                        Name = "Luca Di Leo",
                        Email = "ldileo@digipa.it",
                        Url = new Uri("https://digipa.it/"),
                    },
                    License = new OpenApiLicense
                    {
                        Name = "Use under AGPLv3 License",
                        Url = new Uri("https://github.com/DroneDB/Registry/blob/master/LICENSE.md"),
                    }
                });
                c.DocumentFilter<BasePathDocumentFilter>();
            });
            services.AddSwaggerGenNewtonsoftSupport();

            services.AddMvcCore()
                .AddApiExplorer()
                .AddNewtonsoftJson();

            services.AddResponseCaching(options =>
            {
                options.MaximumBodySize = 8 * 1024 * 1024; // 8MB
                options.SizeLimit = 10 * 1024 * 1024; // 10MB
                options.UseCaseSensitivePaths = true;
            });

            services.AddSpaStaticFiles(config => { config.RootPath = "ClientApp/build"; });

            // Let's use a strongly typed class for settings
            var appSettingsSection = Configuration.GetSection("AppSettings");
            services.Configure<AppSettings>(appSettingsSection);
            var appSettings = appSettingsSection.Get<AppSettings>();

            ConfigureDbProvider<ApplicationDbContext>(services, appSettings.AuthProvider, IdentityConnectionName);

            if (!string.IsNullOrWhiteSpace(appSettings.ExternalAuthUrl))
            {
                services.AddIdentityCore<User>()
                    .AddRoles<IdentityRole>()
                    .AddEntityFrameworkStores<ApplicationDbContext>();

                services.AddScoped<ILoginManager, RemoteLoginManager>();
            }
            else
            {
                services.AddIdentityCore<User>()
                    .AddRoles<IdentityRole>()
                    .AddEntityFrameworkStores<ApplicationDbContext>()
                    .AddSignInManager();

                services.AddScoped<ILoginManager, LocalLoginManager>();
            }

            ConfigureDbProvider<RegistryContext>(services, appSettings.RegistryProvider, RegistryConnectionName);

            var key = Encoding.ASCII.GetBytes(appSettings.Secret);

            ///////////////////////////////////////////////////////////////////////
            ///https://auth0.com/docs/quickstart/webapp/aspnet-core

            ///////////////////////////////////////////////////////////////////////
            // https://auth0.com/docs/quickstart/webapp/aspnet-core-2/01-login
            /*services.AddAuthentication(auth =>
                {
                    auth.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    auth.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(jwt =>
                {
                    jwt.RequireHttpsMetadata = false;
                    jwt.SaveToken = true;
                    jwt.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(key),
                        ValidateIssuer = false,
                        ValidateAudience = false
                    };
                });*/

            // https://github.com/auth0/auth0-aspnetcore-authentication
            services.AddAuth0WebAppAuthentication(options =>
            {
              options.Domain = Configuration["Auth0:Domain"];
              options.ClientId = Configuration["Auth0:ClientId"];
              options.ClientSecret = Configuration["Auth0:ClientSecret"];
            });

            services.AddControllersWithViews();

            /*
            services.AddAuthentication(options => {
                options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie()
            .AddOpenIdConnect("Auth0", options => {
                // Set the authority to your Auth0 domain
                options.Authority = $"https://{Configuration["Auth0:Domain"]}";

                // Configure the Auth0 Client ID and Client Secret
                options.ClientId = Configuration["Auth0:ClientId"];
                options.ClientSecret = Configuration["Auth0:ClientSecret"];

                // Set response type to code
                options.ResponseType = "code"; //OpenIdConnectResponseType.Code;

                // Configure the scope
                options.Scope.Clear();
                options.Scope.Add("openid");

                // Set the callback path, so Auth0 will call back to http://localhost:3000/callback
                // Also ensure that you have added the URL as an Allowed Callback URL in your Auth0 dashboard
                options.CallbackPath = new PathString("/callback");

                // Configure the Claims Issuer to be Auth0
                options.ClaimsIssuer = "Auth0";

                //https://github.com/auth0-samples/auth0-aspnetcore-mvc-samples/blob/netcore2.1/Quickstart/01-Login/Startup.cs
                options.Events = new OpenIdConnectEvents
                {
                    // handle the logout redirection 
                    OnRedirectToIdentityProviderForSignOut = (context) =>
                    {
                        var logoutUri = $"https://{Configuration["Auth0:Domain"]}/v2/logout?client_id={Configuration["Auth0:ClientId"]}";

                        var postLogoutUri = context.Properties.RedirectUri;
                        if (!string.IsNullOrEmpty(postLogoutUri))
                        {
                            if (postLogoutUri.StartsWith("/"))
                            {
                                // transform to absolute
                                var request = context.Request;
                                postLogoutUri = request.Scheme + "://" + request.Host + request.PathBase + postLogoutUri;
                            }
                            logoutUri += $"&returnTo={ Uri.EscapeDataString(postLogoutUri)}";
                        }

                        context.Response.Redirect(logoutUri);
                        context.HandleResponse();

                        return Task.CompletedTask;
                    }
                };
            });*/

            services.Configure<IdentityOptions>(options =>
            {
                // Password settings.
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequiredLength = 1;
                options.Password.RequiredUniqueChars = 0;

                // Lockout settings.
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.AllowedForNewUsers = true;

                // User settings.
                options.User.AllowedUserNameCharacters =
                    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
                options.User.RequireUniqueEmail = false;
            });

            // Error messages that make sense
            services.Configure<ApiBehaviorOptions>(o =>
            {
                o.InvalidModelStateResponseFactory = actionContext =>
                    new BadRequestObjectResult(new ErrorResponse(actionContext.ModelState));
            });

            services.AddMemoryCache();
            RegisterCacheProvider(services, appSettings);
            RegisterHangfireProvider(services, appSettings);

            services.AddHealthChecks()
                .AddCheck<CacheHealthCheck>("Cache health check", null, new[] { "service" })
                .AddCheck<DdbHealthCheck>("DroneDB health check", null, new[] { "service" })
                .AddCheck<UserManagerHealthCheck>("User manager health check", null, new[] { "database" })
                .AddDbContextCheck<RegistryContext>("Registry database health check", null, new[] { "database" })
                .AddDbContextCheck<ApplicationDbContext>("Registry identity database health check", null,
                    new[] { "database" })
                .AddDiskSpaceHealthCheck(appSettings.StoragePath, "Ddb storage path space health check", null,
                    new[] { "storage" })
                .AddHangfire(options => { options.MinimumAvailableServers = 1; }, "Hangfire health check", null,
                    new[] { "database" });

            /*
             * NOTE about services lifetime:
             *
             * - A type should be registered as a "Singleton" only when it is fully thread-safe and is not dependent on other services or types.
             * - Scoped services are bound under a scope (request), and a new instance is created and reused inside a created "scope".
             * - If a service is defined as Transient, it is instantiated whenever invoked within a request.
             *   It is almost similar to creating an instance of the same type using "new" keyword and using it.
             *   It is also the safest option among all other service types, since we don't need to bother about the thread-safety and memory leaks.
             *
             * = In terms of lifetime, the singleton object gets the highest life per instantiation,
             *   followed by a Scoped service object and the least by a Transient object.
             */

            services.AddTransient<TokenManagerMiddleware>();
            services.AddTransient<JwtInCookieMiddleware>();
            services.AddTransient<ITokenManager, TokenManager>();
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            services.AddScoped<IUtils, WebUtils>();
            services.AddScoped<IAuthManager, AuthManager>();

            services.AddScoped<IUsersManager, UsersManager>();
            services.AddScoped<IOrganizationsManager, OrganizationsManager>();
            services.AddScoped<IDatasetsManager, DatasetsManager>();
            services.AddScoped<IObjectsManager, ObjectsManager>();
            services.AddScoped<IShareManager, ShareManager>();
            services.AddScoped<IPushManager, PushManager>();
            services.AddScoped<IDdbManager, DdbManager>();
            services.AddScoped<ISystemManager, SystemManager>();
            services.AddScoped<IBackgroundJobsProcessor, BackgroundJobsProcessor>();
            services.AddScoped<IMetaManager, Services.Managers.MetaManager>();

            services.AddSingleton<IFileSystem, FileSystem>();
            services.AddSingleton<IPasswordHasher, PasswordHasher>();
            services.AddSingleton<IBatchTokenGenerator, BatchTokenGenerator>();
            services.AddSingleton<INameGenerator, NameGenerator>();
            services.AddSingleton<ICacheManager, CacheManager>();
            services.AddSingleton<ObjectCache>(provider => new FileCache(FileCacheManagers.Hashed, 
                appSettings.CachePath, new DefaultSerializationBinder(), 
                true, appSettings.ClearCacheInterval ?? default)
            {
                PayloadReadMode = FileCache.PayloadMode.Filename,
                PayloadWriteMode = FileCache.PayloadMode.Filename
            });
            
            services.AddResponseCompression();

            if (appSettings.MaxRequestBodySize.HasValue)
            {
                services.Configure<FormOptions>(options =>
                {
                    // See https://docs.microsoft.com/it-it/aspnet/core/fundamentals/servers/kestrel?view=aspnetcore-3.1#maximum-client-connections
                    // We could put this in config "Kestrel->Limits" section
                    options.MultipartBodyLengthLimit = appSettings.MaxRequestBodySize.Value;
                });
            }

            services.AddHttpContextAccessor();

            // If using Kestrel:
            services.Configure<KestrelServerOptions>(options => { options.AllowSynchronousIO = true; });

            // If using IIS:
            services.Configure<IISServerOptions>(options => { options.AllowSynchronousIO = true; });

            // TODO: Enable when needed. Should check return object structure
            // services.AddOData();

            if (appSettings.WorkerThreads > 0)
            {
                ThreadPool.GetMinThreads(out _, out var ioCompletionThreads);
                ThreadPool.SetMinThreads(appSettings.WorkerThreads, ioCompletionThreads);
            }
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                app.UseHsts();
            }
           
            app.UseSerilogRequestLogging(options =>
            {
                // Customize the message template
                options.MessageTemplate = "Handled {RequestPath}";
    
                // Emit debug-level events instead of the defaults
                options.GetLevel = (httpContext, elapsed, ex) => LogEventLevel.Debug;
    
                // Attach additional properties to the request completion event
                options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
                {
                    diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                    diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
                };
            });
            
            app.UseDefaultFiles();

            app.UseSwagger();
            app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "Registry API"); });

            app.UseRouting();

            // We are permissive now
            app.UseCors(cors => cors
                .AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader());

            app.UseMiddleware<JwtInCookieMiddleware>();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseResponseCompression();
            app.UseResponseCaching();

            ///////////////////////////////////////////////////////////////////////
            // https://auth0.com/docs/quickstart/webapp/aspnet-core-2/01-login
            app.UseAuthentication();
            ///////////////////////////////////////////////////////////////////////

            app.UseMiddleware<TokenManagerMiddleware>();

            app.UseHangfireDashboard("/hangfire", new DashboardOptions
            {
                AsyncAuthorization = new[] { new HangfireAuthorizationFilter() }
            });
            
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                //endpoints.MapDefaultControllerRoute();

                endpoints.MapHealthChecks("/quickhealth", new HealthCheckOptions
                {
                    Predicate = _ => false
                }).RequireAuthorization();

                endpoints.MapHealthChecks("/health", new HealthCheckOptions
                {
                    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
                }).RequireAuthorization();

                endpoints.MapGet("/version",
                    async context =>
                    {
                        await context.Response.WriteAsync(
                            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "undefined");
                    });

                endpoints.MapHangfireDashboard().RequireAuthorization();

                // TODO: Enable when needed
                // endpoints.MapODataRoute("odata", "odata", GetEdmModel());
            });

            app.UseWhen(context => !context.Request.Path.StartsWithSegments("/static"), builder =>
            {
                builder.UseSpaStaticFiles(new StaticFileOptions
                {
                    ServeUnknownFileTypes = true,
                });

                builder.UseSpa(spa =>
                {
                    // To learn more about options for serving an Angular SPA from ASP.NET Core,
                    // see https://go.microsoft.com/fwlink/?linkid=864501
                    spa.Options.SourcePath = "ClientApp";

                    if (env.IsDevelopment())
                    {
                    }
                });
            });

            SetupDatabase(app);
            SetupFileCache(app);
            //SetupHangfire(app);

            PrintStartupInfo(app);
        }

        private void PrintStartupInfo(IApplicationBuilder app)
        {

            if (!Log.IsEnabled(LogEventLevel.Information))
            {
                
                var env = app.ApplicationServices.GetService<IHostEnvironment>();
                Console.WriteLine(" -> Application startup");
                Console.WriteLine(" ?> Environment: {0}", env?.EnvironmentName ?? "Unknown");
                Console.WriteLine(" ?> Version: {0}", Assembly.GetExecutingAssembly().GetName().Version);
                Console.WriteLine(" ?> Application started at {0}", DateTime.Now);
                
                var serverAddresses = app.ServerFeatures.Get<IServerAddressesFeature>()?.Addresses;
                if (serverAddresses != null)
                {
                    foreach (var address in serverAddresses)
                    {
                        Console.WriteLine($" ?> Now listening on: {address}");
                    }
                }
                
                var settings = app.ApplicationServices.GetService<IOptions<AppSettings>>();
                
                var appSettings = settings?.Value;
                if (appSettings != null && !string.IsNullOrWhiteSpace(appSettings.ExternalUrlOverride))
                {
                    Console.WriteLine($" ?> External URL: {appSettings.ExternalUrlOverride}");
                }

            }
        }

        private void SetupFileCache(IApplicationBuilder app)
        {
            var appSettingsSection = Configuration.GetSection("AppSettings");
            var appSettings = appSettingsSection.Get<AppSettings>();

            var cacheManager = app.ApplicationServices.GetService<ICacheManager>();

            Debug.Assert(cacheManager != null, nameof(cacheManager) + " != null");
            
            cacheManager.Register(MagicStrings.TileCacheSeed, parameters =>
            {
                var ddb = (DDB)parameters[0];
                var sourcePath = (string)parameters[1];
                var sourceHash = (string)parameters[2];
                var tx = (int)parameters[3];
                var ty = (int)parameters[4];
                var tz = (int)parameters[5];
                var retina = (bool)parameters[6];

                return ddb.GenerateTile(sourcePath, tz, tx, ty, retina, sourceHash);

            }, appSettings.TilesCacheExpiration);
            
            cacheManager.Register(MagicStrings.ThumbnailCacheSeed, parameters =>
            {
                var ddb = (DDB)parameters[0];
                var sourcePath = (string)parameters[1];
                var size = (int)parameters[2];

                return ddb.GenerateThumbnail(sourcePath, size);

            }, appSettings.ThumbnailsCacheExpiration);
            
        }

        // private void SetupHangfire(IApplicationBuilder app)
        // {
        //     var appSettingsSection = Configuration.GetSection("AppSettings");
        //     var appSettings = appSettingsSection.Get<AppSettings>();
        //     
        //     if (appSettings.StorageCleanupMinutes is > 0)
        //     {
        //         using var serviceScope = app.ApplicationServices
        //             .GetRequiredService<IServiceScopeFactory>()
        //             .CreateScope();
        //     
        //         var objectSystem = serviceScope.ServiceProvider.GetService<IObjectSystem>();
        //         
        //         RecurringJob.AddOrUpdate(MagicStrings.StorageCleanupJobId, () =>
        //             HangfireUtils.SyncAndCleanupWrapper(objectSystem, null),
        //             $"*/{appSettings.StorageCleanupMinutes} * * * *");
        //
        //     }
        //     else
        //     {
        //         RecurringJob.RemoveIfExists(MagicStrings.StorageCleanupJobId);
        //     }
        // }

        // NOTE: Maybe put all this as stated in https://stackoverflow.com/a/55707949
        private void SetupDatabase(IApplicationBuilder app)
        {
            using var serviceScope = app.ApplicationServices
                .GetRequiredService<IServiceScopeFactory>()
                .CreateScope();
            using var applicationDbContext = serviceScope.ServiceProvider.GetService<ApplicationDbContext>();

            if (applicationDbContext == null)
                throw new InvalidOperationException("Cannot get application db context from service provider");

            if (applicationDbContext.Database.IsSqlite())
            {
                CommonUtils.EnsureFolderCreated(Configuration.GetConnectionString(IdentityConnectionName));

                // No migrations
                applicationDbContext.Database.EnsureCreated();
            }

            if (applicationDbContext.Database.IsSqlServer())
                // No migrations
                applicationDbContext.Database.EnsureCreated();


            if (applicationDbContext.Database.IsMySql() && applicationDbContext.Database.GetPendingMigrations().Any())
                // Use migrations
                applicationDbContext.Database.Migrate();

            using var registryDbContext = serviceScope.ServiceProvider.GetService<RegistryContext>();

            if (registryDbContext == null)
                throw new InvalidOperationException("Cannot get registry db context from service provider");

            if (registryDbContext.Database.IsSqlite())
            {
                CommonUtils.EnsureFolderCreated(Configuration.GetConnectionString(RegistryConnectionName));
                // No migrations
                registryDbContext.Database.EnsureCreated();
            }

            if (registryDbContext.Database.IsSqlServer())
                // No migrations
                registryDbContext.Database.EnsureCreated();


            if (registryDbContext.Database.IsMySql() && registryDbContext.Database.GetPendingMigrations().Any())
                // Use migrations
                registryDbContext.Database.Migrate();


            CreateInitialData(registryDbContext);
            CreateDefaultAdmin(registryDbContext, serviceScope.ServiceProvider).Wait();
        }

        private void RegisterHangfireProvider(IServiceCollection services, AppSettings appSettings)
        {
            switch (appSettings.HangfireProvider)
            {
                case HangfireProvider.InMemory:

                    services.AddHangfire(configuration => configuration
                        .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                        .UseSimpleAssemblyNameTypeSerializer()
                        .UseRecommendedSerializerSettings()
                        .UseConsole()
                        .UseInMemoryStorage());

                    break;

                case HangfireProvider.Mysql:

                    services.AddHangfire(configuration => configuration
                        .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                        .UseSimpleAssemblyNameTypeSerializer()
                        .UseRecommendedSerializerSettings()
                        .UseConsole()
                        .UseStorage(new MySqlStorage(Configuration.GetConnectionString(HangfireConnectionName),
                            new MySqlStorageOptions
                            {
                                TransactionIsolationLevel = IsolationLevel.ReadCommitted,
                                QueuePollInterval = TimeSpan.FromSeconds(15),
                                JobExpirationCheckInterval = TimeSpan.FromHours(1),
                                CountersAggregateInterval = TimeSpan.FromMinutes(5),
                                PrepareSchemaIfNecessary = true,
                                DashboardJobListLimit = 50000,
                                TransactionTimeout = TimeSpan.FromMinutes(10),
                                TablesPrefix = "hangfire"
                            })));

                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported hangfire provider: '{appSettings.HangfireProvider}'");
            }

            // Add the processing server as IHostedService
            services.AddHangfireServer();
        }

        private void RegisterCacheProvider(IServiceCollection services, AppSettings appSettings)
        {
            if (appSettings.CacheProvider == null)
            {
                // Use memory caching
                services.AddDistributedMemoryCache();
                return;
            }

            switch (appSettings.CacheProvider.Type)
            {
                case CacheType.InMemory:

                    services.AddDistributedMemoryCache();

                    break;

                case CacheType.Redis:

                    var settings = appSettings.CacheProvider.Settings.ToObject<RedisProviderSettings>();

                    if (settings == null)
                        throw new ArgumentException("Invalid redis cache provider settings");

                    services.AddStackExchangeRedisCache(options =>
                    {
                        options.Configuration = settings.InstanceAddress;
                        options.InstanceName = settings.InstanceName;
                    });

                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported caching provider: '{(int)appSettings.CacheProvider.Type}'");
            }
        }

        private void ConfigureDbProvider<T>(IServiceCollection services, DbProvider provider,
            string connectionStringName) where T : DbContext
        {
            var connectionString = Configuration.GetConnectionString(connectionStringName);

            services.AddDbContext<T>(options =>
                _ = provider switch
                {
                    DbProvider.Sqlite => options.UseSqlite(connectionString),
                    DbProvider.Mysql => options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
                        builder => builder.EnableRetryOnFailure()),
                    DbProvider.Mssql => options.UseSqlServer(connectionString),
                    _ => throw new ArgumentOutOfRangeException(nameof(provider), $"Unrecognised provider: '{provider}'")
                });
        }

        private void CreateInitialData(RegistryContext context)
        {
            // If no organizations in database, let's create the public one
            if (!context.Organizations.Any())
            {
                var entity = new Organization
                {
                    Slug = MagicStrings.PublicOrganizationSlug,
                    Name = MagicStrings.PublicOrganizationSlug.ToPascalCase(false, CultureInfo.InvariantCulture),
                    CreationDate = DateTime.Now,
                    Description = "Organization",
                    IsPublic = true,
                    // NOTE: Maybe this is a good idea to flag this org as "system"
                    OwnerId = null
                };
                var ds = new Dataset
                {
                    Slug = MagicStrings.DefaultDatasetSlug,
                    CreationDate = DateTime.Now,
                    InternalRef = Guid.NewGuid()
                };
                entity.Datasets = new List<Dataset> { ds };

                context.Organizations.Add(entity);
                context.SaveChanges();
            }
        }

        private async Task CreateDefaultAdmin(RegistryContext context, IServiceProvider provider)
        {
            var usersManager = provider.GetService<UserManager<User>>();
            var roleManager = provider.GetService<RoleManager<IdentityRole>>();
            var appSettings = provider.GetService<IOptions<AppSettings>>();

            if (usersManager == null)
                throw new InvalidOperationException("Cannot get users manager from service provider");

            if (roleManager == null)
                throw new InvalidOperationException("Cannot get role manager from service provider");

            if (appSettings == null)
                throw new InvalidOperationException("Cannot get app settings from service provider");

            // If no users in database, let's create the default admin
            if (!usersManager.Users.Any())
            {
                // first we create Admin role  
                var role = new IdentityRole { Name = ApplicationDbContext.AdminRoleName };
                var r = await roleManager.CreateAsync(role);

                if (!r.Succeeded)
                    throw new InvalidOperationException("Cannot create admin role: " + r?.Errors.ToErrorString());

                var defaultAdmin = appSettings.Value.DefaultAdmin;
                var user = new User
                {
                    Email = defaultAdmin.Email,
                    UserName = defaultAdmin.UserName
                };

                var usrRes = await usersManager.CreateAsync(user, defaultAdmin.Password);
                if (!usrRes.Succeeded)
                    throw new InvalidOperationException(
                        "Cannot create default admin: " + usrRes.Errors?.ToErrorString());

                var res = await usersManager.AddToRoleAsync(user, ApplicationDbContext.AdminRoleName);
                if (!res.Succeeded)
                    throw new InvalidOperationException(
                        "Cannot add admin to admin role: " + res.Errors?.ToErrorString());

                var entity = new Organization
                {
                    Slug = defaultAdmin.UserName.ToSlug(),
                    Name = defaultAdmin.UserName + " organization",
                    CreationDate = DateTime.Now,
                    Description = null,
                    IsPublic = true,
                    // NOTE: Maybe this is a good idea to flag this org as "system"
                    OwnerId = user.Id
                };

                await context.Organizations.AddAsync(entity);
                await context.SaveChangesAsync();
            }
        }
    }
}
