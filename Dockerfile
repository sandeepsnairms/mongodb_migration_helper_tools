# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["MongoTestTools.csproj", "./"]
RUN dotnet restore "MongoTestTools.csproj"

# Copy all source files and build
COPY . .
RUN dotnet build "MongoTestTools.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "MongoTestTools.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Create a non-root user
RUN useradd -m -u 1000 appuser && chown -R appuser:appuser /app
USER appuser

# Copy published files
COPY --from=publish /app/publish .

# Expose port
EXPOSE 8080

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Start the application
ENTRYPOINT ["dotnet", "MongoTestTools.dll"]
