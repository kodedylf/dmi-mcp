using System.Globalization;
using System.Net;
using System.Text.Json;
using ModelContextProtocol;

namespace DmiMcp;

/// <summary>Thin wrapper around DMI's open data API (no key needed) and Open-Meteo's geocoder.</summary>
public sealed class DmiClient(HttpClient http)
{
    private const string Base = "https://opendataapi.dmi.dk";
    // The forecast EDR service frequently answers 429 "Server is busy" for a few seconds at a time.
    private static readonly TimeSpan[] RetryDelays = [.. Enumerable.Repeat(TimeSpan.FromSeconds(2), 8)];

    private IReadOnlyList<Station>? _stations;

    public async Task<JsonElement> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        var url = pathAndQuery.StartsWith("http") ? pathAndQuery : Base + pathAndQuery;
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(url, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new McpException($"DMI did not answer within {http.Timeout.TotalSeconds:0} seconds. The service is probably overloaded; try again shortly.");
            }
            using var _ = response;
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                && attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], ct);
                continue;
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new McpException($"DMI returned {(int)response.StatusCode} for {url}: {Truncate(body, 300)}");
            return JsonDocument.Parse(body).RootElement.Clone();
        }
    }

    /// <summary>Accepts "lat,lon" or a Danish place name / postcode.</summary>
    public async Task<Location> ResolveAsync(string location, CancellationToken ct)
    {
        var parts = location.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            return new Location(location, lat, lon);

        var json = await GetAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=da&countryCode=DK&name={Uri.EscapeDataString(location)}", ct);
        if (!json.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            throw new McpException($"Could not find a Danish place called '{location}'. Try another spelling or 'lat,lon'.");
        var r = results[0];
        var name = r.GetProperty("name").GetString()!;
        if (r.TryGetProperty("admin2", out var admin2)) name += $" ({admin2.GetString()})";
        return new Location(name, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble());
    }

    public async Task<IReadOnlyList<Station>> GetStationsAsync(CancellationToken ct)
    {
        if (_stations is not null) return _stations;

        var json = await GetAsync("/v2/metObs/collections/station/items?status=Active&limit=10000", ct);
        // The list contains historic versions of each station; keep the current one (validTo == null) when present.
        _stations = json.GetProperty("features").EnumerateArray()
            .Select(f =>
            {
                var p = f.GetProperty("properties");
                var coords = f.GetProperty("geometry").GetProperty("coordinates");
                return (Current: p.GetProperty("validTo").ValueKind == JsonValueKind.Null,
                    Station: new Station(
                        p.GetProperty("stationId").GetString()!,
                        p.GetProperty("name").GetString()!,
                        p.GetProperty("type").GetString()!,
                        coords[1].GetDouble(),
                        coords[0].GetDouble(),
                        p.GetProperty("parameterId").EnumerateArray().Select(x => x.GetString()!).ToHashSet()));
            })
            .GroupBy(x => x.Station.Id)
            .Select(g => g.OrderByDescending(x => x.Current).First().Station)
            .ToList();
        return _stations;
    }

    public async Task<IEnumerable<(Station Station, double DistanceKm)>> NearestStationsAsync(
        Location location, string? requiredParameter, CancellationToken ct) =>
        (await GetStationsAsync(ct))
            .Where(s => requiredParameter is null || s.Parameters.Contains(requiredParameter))
            .Select(s => (s, DistanceKm(location.Lat, location.Lon, s.Lat, s.Lon)))
            .OrderBy(x => x.Item2);

    private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double d) => d * Math.PI / 180;
        var a = Math.Pow(Math.Sin(Rad(lat2 - lat1) / 2), 2)
                + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(Rad(lon2 - lon1) / 2), 2);
        return 6371 * 2 * Math.Asin(Math.Sqrt(a));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public sealed record Location(string Name, double Lat, double Lon);

public sealed record Station(string Id, string Name, string Type, double Lat, double Lon, IReadOnlySet<string> Parameters);
