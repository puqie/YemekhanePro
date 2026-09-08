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
        // Gunluk hak uyarisi zamanlayicisi: dispatcher ile ayni kayit noktasi (Program.cs'e dokunulmaz).
        services.AddHostedService<SmsAutomationWorker>();
        // Varsayilan toplu SMS sablonlari: tablo bosken acilista yazilir (Program.cs cagirir).
        services.AddScoped<SmsTemplateSeeder>();

        // Test SMS / kontor sorgusu: kayitli ayarlarla, kuyruktan bagimsiz (Mock ortaminda da calisir).
        services.AddHttpClient(LiveSmsProviderProbe.HttpClientName).ConfigurePrimaryHttpMessageHandler(NoRedirect);
        services.AddScoped<ISmsProviderProbe, LiveSmsProviderProbe>();

        var provider = section[nameof(SmsProviderOptions.Provider)];
        if (provider?.Equals("Mock", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Test"))
                throw new InvalidOperationException("Mock SMS provider yalnız Development veya Test ortamında kullanılabilir.");
            services.AddSingleton<ISmsProvider, MockSmsProvider>();
            return services;
        }

        services.AddSingleton<ISmsResponseParser, JsonSmsResponseParser>();
        services.AddHttpClient<HttpSmsProvider>().ConfigurePrimaryHttpMessageHandler(NoRedirect);
        services.AddHttpClient<MutlucellSmsProvider>().ConfigurePrimaryHttpMessageHandler(NoRedirect);
        // Kuyruk tek ISmsProvider gorur; secim her gonderimde Sms:Provider'dan okunur (Http | Mutlucell).
        services.AddTransient<ISmsProvider, SmsProviderRouter>();
        return services;
    }

    private static HttpClientHandler NoRedirect() => new() { AllowAutoRedirect = false };
}
