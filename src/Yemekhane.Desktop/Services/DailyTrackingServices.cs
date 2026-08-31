using System.IO;
using System.Media;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Yemekhane.Application.DailyTracking;

namespace Yemekhane.Desktop.Services;

public interface IDailyTrackingApiClient
{
    Task<DailyTrackingPage> GetAsync(DailyTrackingQuery query, CancellationToken cancellationToken = default);
}

public sealed class DailyTrackingApiClient(HttpClient client, IJwtSession session) : IDailyTrackingApiClient
{
    public async Task<DailyTrackingPage> GetAsync(DailyTrackingQuery query, CancellationToken cancellationToken = default)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var values = new List<string> { $"pageSize={query.PageSize}" };
        Add("decision", query.Decision); Add("mealTypeId", query.MealTypeId); Add("deviceId", query.DeviceId);
        Add("classId", query.ClassId); Add("search", query.Search); Add("cursorTimestamp", query.CursorTimestamp);
        Add("cursorOperationId", query.CursorOperationId); Add("sinceTimestamp", query.SinceTimestamp);
        Add("sinceOperationId", query.SinceOperationId);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/daily-tracking?" + string.Join('&', values));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DailyTrackingPage>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Günlük takip yanıtı boş döndü.");

        void Add(string name, object? value)
        {
            if (value is null) return;
            var text = value is DateTimeOffset timestamp ? timestamp.ToString("O") : value.ToString();
            values.Add($"{name}={Uri.EscapeDataString(text!)}");
        }
    }
}

public interface IDailyTrackingPreferences
{
    bool SoundEnabled { get; set; }
}

public sealed class FileDailyTrackingPreferences : IDailyTrackingPreferences
{
    private readonly string path;
    private bool soundEnabled;

    public FileDailyTrackingPreferences(string? path = null)
    {
        this.path = path ?? Path.Combine(Yemekhane.Infrastructure.Persistence.ApplicationDataPath.Resolve(), "daily-tracking.json");
        try
        {
            if (File.Exists(this.path)) soundEnabled = JsonSerializer.Deserialize<Settings>(File.ReadAllText(this.path))?.SoundEnabled ?? false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { soundEnabled = false; }
    }

    public bool SoundEnabled
    {
        get => soundEnabled;
        set
        {
            if (soundEnabled == value) return;
            soundEnabled = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(new Settings(value)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record Settings(bool SoundEnabled);
}

public interface ITrackingSoundPlayer
{
    ValueTask PlayAsync(string decision, CancellationToken cancellationToken = default);
}

public sealed class SystemTrackingSoundPlayer : ITrackingSoundPlayer
{
    public ValueTask PlayAsync(string decision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Task.Run(() => (decision == "DENY" ? SystemSounds.Hand : SystemSounds.Asterisk).Play(), cancellationToken);
        return ValueTask.CompletedTask;
    }
}
