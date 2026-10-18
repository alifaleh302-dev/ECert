# =========================================================
# ECert Training Center Management System
# Docker image for Railway deployment
# =========================================================

# ===== Build stage =====
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file first and restore dependencies (better layer caching)
COPY ECert/ECert.csproj ECert/
RUN dotnet restore ECert/ECert.csproj

# Copy the rest of the source and publish a Release build
COPY ECert/ ECert/
RUN dotnet publish ECert/ECert.csproj -c Release -o /app/publish

# ===== Runtime stage =====
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Runtime folders for user uploads and QR codes
RUN mkdir -p /app/wwwroot/uploads /app/wwwroot/qrcodes

COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod 755 /usr/local/bin/docker-entrypoint.sh

# Railway injects the assigned port into the PORT env var.
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=10000
ENV DOTNET_USE_POLLING_FILE_WATCHER=1
EXPOSE 10000

# The container starts as root only so the entrypoint can take ownership of the
# Railway volume mount (the platform mounts volumes as root). It immediately drops
# to the unprivileged `app` user before the application process is exec'd, so the
# app itself never runs as root.
RUN chown -R app:app /app

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["dotnet", "ECert.dll"]
