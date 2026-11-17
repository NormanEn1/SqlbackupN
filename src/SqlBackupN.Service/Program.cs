using SqlBackupN.Core;
using SqlBackupN.Service;

// Servicio de Windows: evalúa cada 30 s qué pasos de qué jobs están vencidos y los ejecuta.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "SqlBackupN");
builder.Services.AddSqlBackupNCore();
builder.Services.AddHostedService<SchedulerWorker>();

await builder.Build().RunAsync();
