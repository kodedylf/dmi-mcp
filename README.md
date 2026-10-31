# dmi-mcp

MCP server (streamable HTTP, stateless, .NET 10) for weather data from DMI's free open data API. No API key needed.

| Tool | Source | What |
|---|---|---|
| `get_forecast` | `forecastedr` (HARMONIE DINI) | Hourly forecast up to 60 h: temperature, wind, gusts, precipitation, cloud, humidity |
| `get_current_weather` | `metObs` | Latest 10-minute observations from the nearest Synop station |
| `get_climate_history` | `climateData` | Quality-controlled day/month/year values (e.g. `mean_temp`, `acc_precip`) |
| `find_stations` | `metObs` | Nearest active stations and what they measure |

Locations are Danish place names or postcodes (geocoded via Open-Meteo), or `lat,lon`.

## Configuration

| Env | Default | |
|---|---|---|
| `PORT` | `3000` | |
| `MCP_PATH` | `/mcp` | Set a hard-to-guess path when public — it is the only access control |

`GET /health` returns `ok`.

## Deploy

```bash
docker build -t dmi-mcp .
docker run -d --restart unless-stopped -p 3000:3000 -e MCP_PATH=/<random>/mcp dmi-mcp
```

Put it behind a TLS-terminating reverse proxy, then add `https://<host>/<random>/mcp` as a custom connector
on claude.ai (it syncs to the phone app), or for Claude Code:
`claude mcp add --scope user --transport http dmi https://<host>/<random>/mcp`.

## Run locally

```bash
dotnet run --project src/DmiMcp   # http://localhost:3000/mcp
```

In Git Bash, set `MSYS_NO_PATHCONV=1` before passing `MCP_PATH`, or it is rewritten into a Windows path.

## Notes

- DMI's forecast EDR service often answers `429 Server is busy` and point queries can take 20+ seconds.
  The client retries 429/503 for ~16 s and uses a 90 s timeout.
- Fair-use limit is 500 requests per 5 seconds. API docs: <https://www.dmi.dk/friedata/dokumentation>
- Data is CC BY 4.0 — credit DMI when you reuse it.
