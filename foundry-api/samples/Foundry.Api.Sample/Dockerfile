FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy all solution csproj files to match visual directory layout
COPY src/Foundry.Api/Foundry.Api.csproj src/Foundry.Api/
COPY src/Foundry.Api.SourceGenerators/Foundry.Api.SourceGenerators.csproj src/Foundry.Api.SourceGenerators/
COPY samples/Foundry.Api.Sample/Foundry.Api.Sample.csproj samples/Foundry.Api.Sample/
COPY foundry-mongo/src/FoundryMongo.csproj foundry-mongo/src/

# Restore dependencies
RUN dotnet restore samples/Foundry.Api.Sample/Foundry.Api.Sample.csproj

# Copy remaining source code files
COPY src/ src/
COPY samples/ samples/
COPY foundry-mongo/ foundry-mongo/

# Publish target app
RUN dotnet publish samples/Foundry.Api.Sample/Foundry.Api.Sample.csproj -c Release -o /app/publish

# Production runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Foundry.Api.Sample.dll"]
