# syntax=docker/dockerfile:1
# Pin the build stages to the builder's own platform so dotnet and node always
# run natively. The published DLLs are platform-agnostic MSIL and run on both
# amd64 and arm64 without any QEMU emulation. The final runtime stage still uses
# the correct platform-specific ASP.NET image for whichever architecture is
# being built.
#
# $BUILDPLATFORM is set by BuildKit to the platform the build is running on.
# Hardcoding linux/amd64 here did the same thing on an amd64 builder but tripped
# a lint warning, because a hardcoded platform in FROM is usually a mistake.
# ── Stylesheet build stage ───────────────────────────────────────────────────
# The web UI's Tailwind CSS is compiled here rather than loaded from
# cdn.tailwindcss.com at runtime. Building it in the image (instead of
# committing the generated file) guarantees the stylesheet can never drift out
# of sync with the classes used in index.html.
FROM --platform=$BUILDPLATFORM node:20-alpine AS css
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY build/ build/
COPY Rock.CloudPrint.Service/wwwroot/index.html Rock.CloudPrint.Service/wwwroot/
RUN npm run css

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
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

# The version the web UI reports. CI passes the git tag, so what the UI shows and
# what the image is tagged with come from the same place and cannot drift.
#
# This must be a valid version string - the SDK validates it during restore and
# fails the build otherwise. The release workflow only ever runs on a v*.*.* tag,
# so the value it passes is always well formed. A local build reports 0.0.0-dev.
ARG VERSION=0.0.0-dev

RUN dotnet publish Rock.CloudPrint.Service/Rock.CloudPrint.Service.csproj \
    -c Release \
    -p:InformationalVersion="$VERSION" \
    -o /app/publish

# ── Runtime image ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Basic network diagnostics, so someone with a shell on this container can work
# out why a printer is unreachable without installing anything at the time -
# which on TrueNAS or Portainer is awkward and lost on every recreate.
#
# iproute2 matters most: on a bridged deployment the container has its own subnet
# and `ip route get <printer>` shows immediately whether traffic is routed out or
# wrongly treated as local. dnsutils is deliberately absent - printer addresses
# are parsed as literal IPs and never resolved, so DNS plays no part in printing.
RUN apt-get update  && apt-get install -y --no-install-recommends iputils-ping iproute2  && rm -rf /var/lib/apt/lists/*

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

# Healthy while connected to Rock, while not yet configured, and for two minutes
# after losing the connection so the reconnect backoff can do its job. See
# ProxyHealth for the rules and /healthz for what it answers.
#
# The check runs the service's own binary with --healthcheck instead of curl.
# The ASP.NET image ships neither curl nor wget, and installing curl would add
# libcurl and its TLS and HTTP/2 libraries - several megabytes, and more
# packages whose security advisories are ours to track - to make one local HTTP
# request the runtime already in the image can make. A bash /dev/tcp probe
# would need nothing installed but cannot read the status code, so a proxy that
# lost Rock would still look healthy. The cost of the approach taken is a
# short-lived dotnet process every 30 seconds.
#
# The start period covers startup only; the grace for a slow first connection
# lives in the application, where it also applies to later reconnects.
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "Rock.CloudPrint.Service.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "Rock.CloudPrint.Service.dll"]
