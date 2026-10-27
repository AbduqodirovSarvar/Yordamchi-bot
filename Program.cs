using YordamchiBot.Userbot;

var builder = Host.CreateApplicationBuilder(args);

// reloadOnChange is on by default for appsettings.json -> IOptionsMonitor sees edits instantly.
builder.Services.Configure<UserbotConfig>(builder.Configuration.GetSection(UserbotConfig.SectionName));
builder.Services.AddSingleton<UserbotMessageHandler>();
builder.Services.AddHostedService<UserbotWorker>();

// A fatal error (e.g. missing credentials) must stop the process so Docker's restart policy can react.
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);

await builder.Build().RunAsync();
