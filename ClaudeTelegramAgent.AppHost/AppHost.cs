var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.ClaudeTelegramAgent>("claudetelegramagent");

builder.Build().Run();
