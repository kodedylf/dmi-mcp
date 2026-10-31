using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DmiMcp;

[McpServerToolType]
public sealed class WeatherTools(DmiClient dmi)
{
    private const string LocationDescription = "Danish place name or postcode (e.g. 'Hillerød', '3400'), or 'lat,lon'.";
    private const string Attribution = "Data: DMI (Danish Meteorological Institute), CC BY 4.0";
    private static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    private static readonly string[] ForecastParameters =
    [
        "temperature-2m", "wind-speed-10m", "wind-dir-10m", "gust-wind-speed-10m",
        "total-precipitation", "fraction-of-cloud-cover", "relative-humidity-2m",
    ];

    [McpServerTool(Name = "get_forecast", ReadOnly = true)]
    [Description("Hourly weather forecast from DMI's HARMONIE model (about 2.5 days ahead). Times are Danish local time.")]
    public async Task<object> GetForecast(
        [Description(LocationDescription)] string location,
        [Description("Hours ahead, 1-60. Default 24.")] int hours = 24,
        CancellationToken ct = default)
    {
        var loc = await dmi.ResolveAsync(location, ct);
        hours = Math.Clamp(hours, 1, 60);

        // Precipitation is accumulated since model start, so fetch one extra hour to compute the first hourly amount.
        var now = DateTime.UtcNow;
        var from = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(hours);
        var json = await dmi.GetAsync(
            "/v1/forecastedr/collections/harmonie_dini_sf/position" +
            $"?coords=POINT({Inv(loc.Lon)}%20{Inv(loc.Lat)})&crs=crs84&f=GeoJSON" +
            $"&parameter-name={string.Join(',', ForecastParameters)}" +
            $"&datetime={Utc(from.AddHours(-1))}/{Utc(to)}", ct);

        var steps = json.GetProperty("features").EnumerateArray()
            .Select(f => f.GetProperty("properties"))
            .OrderBy(p => p.GetProperty("step").GetDateTime())
            .ToList();

        var rows = new List<object>();
        double? previousPrecip = null;
        foreach (var p in steps)
        {
            var precip = Num(p, "total-precipitation");
            var time = p.GetProperty("step").GetDateTime().ToUniversalTime();
            if (time >= from)
                rows.Add(new
                {
                    time = Local(time),
                    temperatureC = Round(Num(p, "temperature-2m") - 273.15),
                    windSpeedMs = Round(Num(p, "wind-speed-10m")),
                    windGustMs = Round(Num(p, "gust-wind-speed-10m")),
                    windFrom = Compass(Num(p, "wind-dir-10m")),
                    precipitationMm = precip is null || previousPrecip is null ? null : Round(Math.Max(0, precip.Value - previousPrecip.Value)),
                    cloudCoverPct = Round(Num(p, "fraction-of-cloud-cover") * 100, 0),
                    humidityPct = Round(Num(p, "relative-humidity-2m"), 0),
                });
            previousPrecip = precip;
        }

        return new { location = loc.Name, loc.Lat, loc.Lon, model = "HARMONIE DINI", hours = rows, attribution = Attribution };
    }

    [McpServerTool(Name = "get_current_weather", ReadOnly = true)]
    [Description("Latest observations (10-minute values) from the nearest DMI weather station(s). Values use DMI's parameter ids, e.g. temp_dry (°C), wind_speed (m/s), wind_dir (degrees), precip_past1h (mm), humidity (%), pressure_at_sea (hPa), cloud_cover (%).")]
    public async Task<object> GetCurrentWeather(
        [Description(LocationDescription)] string location,
        CancellationToken ct = default)
    {
        var loc = await dmi.ResolveAsync(location, ct);
        // Synop-type stations have the full set of sensors; try the nearest few until one has recent data.
        foreach (var (station, km) in (await dmi.NearestStationsAsync(loc, "temp_dry", ct)).Take(3))
        {
            var json = await dmi.GetAsync(
                $"/v2/metObs/collections/observation/items?stationId={station.Id}&period=latest-hour&limit=1000", ct);
            var latest = json.GetProperty("features").EnumerateArray()
                .Select(f => f.GetProperty("properties"))
                .GroupBy(p => p.GetProperty("parameterId").GetString()!)
                .ToDictionary(g => g.Key, g => g.MaxBy(p => p.GetProperty("observed").GetDateTime()));
            if (latest.Count == 0) continue;

            return new
            {
                location = loc.Name,
                station = new { station.Id, station.Name, distanceKm = Round(km, 1) },
                observed = Local(latest.Values.Max(p => p.GetProperty("observed").GetDateTime()).ToUniversalTime()),
                values = latest.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value.GetProperty("value").GetDouble()),
                attribution = Attribution,
            };
        }
        throw new McpException($"No recent observations from stations near {loc.Name}.");
    }

    [McpServerTool(Name = "get_climate_history", ReadOnly = true)]
    [Description("Historical quality-controlled climate values from the nearest DMI station, aggregated per day, month or year.")]
    public async Task<object> GetClimateHistory(
        [Description(LocationDescription)] string location,
        [Description("Start date, yyyy-MM-dd.")] string from,
        [Description("End date (exclusive), yyyy-MM-dd.")] string to,
        [Description("DMI climate parameter, e.g. mean_temp, mean_daily_max_temp, mean_daily_min_temp, max_temp_w_date, min_temp, acc_precip, bright_sunshine, mean_wind_speed, max_wind_speed_10min, mean_relative_hum, mean_pressure, snow_depth. Default mean_temp.")]
        string parameter = "mean_temp",
        [Description("day, month or year. Default day.")] string resolution = "day",
        CancellationToken ct = default)
    {
        var loc = await dmi.ResolveAsync(location, ct);
        var fromDate = ParseDate(from, nameof(from));
        var toDate = ParseDate(to, nameof(to));

        foreach (var (station, km) in (await dmi.NearestStationsAsync(loc, "temp_dry", ct)).Take(5))
        {
            // Periods start at Danish local midnight, so widen the UTC window by a day and filter on local dates below.
            var json = await dmi.GetAsync(
                $"/v2/climateData/collections/stationValue/items?stationId={station.Id}" +
                $"&parameterId={Uri.EscapeDataString(parameter)}&timeResolution={Uri.EscapeDataString(resolution)}" +
                $"&datetime={Utc(fromDate.AddDays(-1))}/{Utc(toDate.AddDays(1))}&limit=10000", ct);
            var values = json.GetProperty("features").EnumerateArray()
                .Select(f => f.GetProperty("properties"))
                .Select(p => (From: DateTimeOffset.Parse(p.GetProperty("from").GetString()!, CultureInfo.InvariantCulture),
                    Value: p.GetProperty("value").GetDouble()))
                .Where(x => x.From.Date >= fromDate && x.From.Date < toDate)
                .OrderBy(x => x.From)
                .Select(x => new { period = x.From.ToString("yyyy-MM-dd"), value = x.Value })
                .ToList();
            if (values.Count == 0) continue;

            return new
            {
                location = loc.Name,
                station = new { station.Id, station.Name, distanceKm = Round(km, 1) },
                parameter, resolution, values, attribution = Attribution,
            };
        }
        throw new McpException($"No '{parameter}' {resolution} values near {loc.Name} for that period. Check the parameter name.");
    }

    [McpServerTool(Name = "find_stations", ReadOnly = true)]
    [Description("List the nearest active DMI weather stations with the parameters each one measures.")]
    public async Task<object> FindStations(
        [Description(LocationDescription)] string location,
        [Description("Number of stations, default 5.")] int count = 5,
        CancellationToken ct = default)
    {
        var loc = await dmi.ResolveAsync(location, ct);
        return (await dmi.NearestStationsAsync(loc, null, ct))
            .Take(Math.Clamp(count, 1, 50))
            .Select(x => new
            {
                x.Station.Id, x.Station.Name, x.Station.Type, distanceKm = Round(x.DistanceKm, 1),
                parameters = x.Station.Parameters.Order(),
            });
    }

    private static double? Num(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static double? Round(double? v, int digits = 1) => v is null ? null : Math.Round(v.Value, digits);

    private static string Inv(double d) => d.ToString(CultureInfo.InvariantCulture);

    private static string Utc(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, Copenhagen).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string? Compass(double? degrees) =>
        degrees is null ? null : new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[(int)Math.Round(degrees.Value / 45) % 8];

    private static DateTime ParseDate(string s, string name) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new McpException($"'{name}' must be yyyy-MM-dd, got '{s}'.");
}
