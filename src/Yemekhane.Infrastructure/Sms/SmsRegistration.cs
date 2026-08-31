using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Yemekhane.Application.Sms;

namespace Yemekhane.Infrastructure.Sms;

public static class SmsRegistration
{
    public static IServiceCollection AddYemekhaneSms(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var section = configuration.GetSection(SmsProviderOptions.SectionName);
        services.AddSingleton<IValidateOptions<SmsProviderOptions>, SmsProviderOptionsValidator>();
        services.AddOptions<SmsProviderOptions>().Bind(section).ValidateOnStart();
        services.AddScoped<SmsService>();
        services.AddSingleton<SmsDispatchRunLock>();
        services.AddScoped<SmsDispatcher>();
        services.AddHostedService<SmsBackgroundDispatcher>();

        var provider = section[nameof(SmsProviderOptions.Provider)];
        if (provider?.Equals("Mock", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Test"))
                throw new InvalidOperationException("Mock SMS provider yalnız Development veya Test ortamında kullanılabilir.");
            services.AddSingleton<ISmsProvider, MockSmsProvider>();
            return services;
        }

        services.AddSingleton<ISmsResponseParser, JsonSmsResponseParser>();
        services.AddHttpClient<ISmsProvider, HttpSmsProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
}
