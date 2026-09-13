# syntax=docker/dockerfile:1
# Pin the build stage to linux/amd64 so dotnet always runs natively.
# The published DLLs are platform-agnostic MSIL and run on both amd64 and arm64
# without any QEMU emulation. The final runtime stage still uses the correct
# platform-specific ASP.NET image for whichever architecture is being built.
# ── Stylesheet build stage ───────────────────────────────────────────────────
# The web UI's Tailwind CSS is compiled here rather than loaded from
# cdn.tailwindcss.com at runtime. Building it in the image (instead of
# committing the generated file) guarantees the stylesheet can never drift out
# of sync with the classes used in index.html.
FROM --platform=linux/amd64 node:20-alpine AS css
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY build/ build/
COPY Rock.CloudPrint.Service/wwwroot/index.html Rock.CloudPrint.Service/wwwroot/
RUN npm run css

FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files first so dotnet restore is cached independently of source changes.
COPY Rock.CloudPrint.Service/Rock.CloudPrint.Service.csproj     Rock.CloudPrint.Service/
COPY Rock.CloudPrint.Shared/Rock.CloudPrint.Shared.csproj        Rock.CloudPrint.Shared/
COPY Rock.CloudPrint.Shared.Common/Rock.CloudPrint.Shared.Common.projitems  Rock.CloudPrint.Shared.Common/
COPY Rock.CloudPrint.Shared.Common/Rock.CloudPrint.Shared.Common.shproj     Rock.CloudPrint.Shared.Common/

RUN dotnet restore Rock.CloudPrint.Service/Rock.CloudPrint.Service.csproj

# Copy source and publish.
COPY Rock.CloudPrint.Service/      Rock.CloudPrint.Service/
COPY Rock.CloudPrint.Shared/       Rock.CloudPrint.Shared/
COPY Rock.CloudPrint.Shared.Common/ Rock.CloudPrint.Shared.Common/

# Bring in the compiled stylesheet from the css stage.
COPY --from=css /src/Rock.CloudPrint.Service/wwwroot/app.css Rock.CloudPrint.Service/wwwroot/app.css

RUN dotnet publish Rock.CloudPrint.Service/Rock.CloudPrint.Service.csproj \
    -c Release \
    -o /app/publish

# ── Runtime image ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Pre-create the config directory. When operators bind-mount a host directory
# here (e.g. ./config or a TrueNAS dataset) Docker uses this as the mount
# point. Without it Docker would create the directory as root, which can cause
# permission issues on some hosts.
RUN mkdir -p /app/config

# Run as a non-root user (UID 1000 matches the host volume owner so the
# config directory bind-mount remains writable without privilege escalation).
RUN adduser --disabled-password --no-create-home --gecos '' --uid 1000 appuser \
 && chown -R appuser /app
USER appuser

# Port the web UI listens on (also set in appsettings.json "Urls").
EXPOSE 8080

ENTRYPOINT ["dotnet", "Rock.CloudPrint.Service.dll"]
