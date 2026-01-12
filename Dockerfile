# Use the official .NET SDK image for building the application
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies
COPY ["WebWatchParty/WebWatchParty.csproj", "WebWatchParty/"]
RUN dotnet restore "WebWatchParty/WebWatchParty.csproj"

# Copy the rest of the files and build the application
COPY . .
WORKDIR "/src/WebWatchParty"
RUN dotnet build "WebWatchParty.csproj" -c Release -o /app/build

# Publish the application
FROM build AS publish
RUN dotnet publish "WebWatchParty.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Use the ASP.NET runtime image for the final stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "WebWatchParty.dll"]

