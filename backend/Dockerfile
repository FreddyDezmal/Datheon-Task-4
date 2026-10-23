# Storefront API container image.
#
# Build context: the backend/ folder (it holds everything the API needs; Storefront.Api has no
# project references, and the frontend is built and hosted separately).
#
#   docker build --pull -t storefront-api backend
#
# --pull fetches the latest .NET 10 base images, so rebuilding picks up OS and runtime security
# patches; add --no-cache to also re-run the CA certificate refresh and the NuGet restore.
#
# Run (PostgreSQL connection and the other production settings come from the environment;
# see Documentation/Deployment.md):
#
#   docker run -p 10000:10000 -e Database__Provider=PostgreSql -e ConnectionStrings__Storefront=... storefront-api

# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from the project file alone, so the package layer is reused until dependencies change.
COPY Storefront.Api/Storefront.Api.csproj Storefront.Api/
RUN dotnet restore Storefront.Api/Storefront.Api.csproj

COPY Storefront.Api/ Storefront.Api/
RUN dotnet publish Storefront.Api/Storefront.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        -p:UseAppHost=false

# ---------- Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Refresh the trusted CA bundle (outbound HTTPS to FakeStoreAPI and TLS to PostgreSQL rely on it).
# A rebuild with --pull --no-cache installs whatever the distribution currently ships.
USER root
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates \
    && update-ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Production unless overridden. The base image's default HTTP port (8080) is cleared so the only
# binding is the one below; PORT is what Render sets, and 10000 is the fallback (Render's default).
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS= \
    PORT=10000
EXPOSE 10000

# The non-root user that ships with the .NET images (UID 1654). The app only writes to its home
# directory (ASP.NET Core Data Protection keys); the database is PostgreSQL, not a local file.
USER $APP_UID

# exec replaces the shell, so the API is PID 1 and receives SIGTERM directly for a graceful shutdown.
# Logs go to stdout/stderr (JSON outside Development).
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet Storefront.Api.dll --urls \"http://0.0.0.0:${PORT:-10000}\""]
