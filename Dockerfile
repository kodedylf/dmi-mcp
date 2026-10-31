FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY src/DmiMcp/DmiMcp.csproj ./
RUN dotnet restore
COPY src/DmiMcp/ ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
# tzdata for Europe/Copenhagen local times in tool output.
RUN apk add --no-cache tzdata
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 3000
HEALTHCHECK CMD wget -qO- http://localhost:3000/health || exit 1
ENTRYPOINT ["dotnet", "DmiMcp.dll"]
