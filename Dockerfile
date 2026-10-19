# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["DiscordRoleBot.csproj", "./"]
RUN dotnet restore "DiscordRoleBot.csproj"
COPY . .
RUN dotnet publish "DiscordRoleBot.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DiscordRoleBot.dll"]

