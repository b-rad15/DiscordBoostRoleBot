// See https://aka.ms/new-console-template for more information

using DiscordBoostRoleBot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remora.Discord.Gateway;
using Remora.Discord.Gateway.Extensions;
using Remora.Discord.Gateway.Results;
using Remora.Results;

// Get Token from Configuration file
Configuration config = Configuration.ReadConfig();
//Enable safe exit of code
CancellationTokenSource cancellationSource = new();
Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};
//Begin Service
ServiceProvider services = new ServiceCollection()
    .AddDiscordGateway(_ => config.Token)
    .BuildServiceProvider();
DiscordGatewayClient gatewayClient = services.GetRequiredService<DiscordGatewayClient>();
Result runResult = await gatewayClient.RunAsync(cancellationSource.Token);
ILogger<Program> log = services.GetRequiredService<ILogger<Program>>();

if (!runResult.IsSuccess)
{
    switch (runResult.Error)
    {
        case ExceptionError exe:
        {
            log.LogError
            (
                exe.Exception,
                "Exception during gateway connection: {ExceptionMessage}",
                exe.Message
            );

            break;
        }
        case GatewayWebSocketError:
        case GatewayDiscordError:
        {
            log.LogError("Gateway error: {Message}", runResult.Error.Message);
            break;
        }
        default:
        {
            log.LogError("Unknown error: {Message}", runResult.Error.Message);
            break;
        }
    }
}

Console.WriteLine("Bye bye");