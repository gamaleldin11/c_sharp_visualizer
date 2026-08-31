# Build and run the trace API.
#
# Note what this image does NOT need: no Docker socket, no privileged flags, no seccomp
# profile, no gVisor. The interpreter never executes user code, so a submitted program is
# data being walked rather than a process being contained. That is what makes the app
# deployable on any free host - the hardened-sandbox requirement is what would have ruled
# them all out.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, against just the project files, so the layer is reused whenever only source
# changed - which is almost every build.
COPY backend/Directory.Packages.props backend/
COPY backend/CsViz.slnx backend/
COPY backend/src/CsViz.Ai/CsViz.Ai.csproj backend/src/CsViz.Ai/
COPY backend/src/CsViz.Analysis/CsViz.Analysis.csproj backend/src/CsViz.Analysis/
COPY backend/src/CsViz.Api/CsViz.Api.csproj backend/src/CsViz.Api/
COPY backend/src/CsViz.Core/CsViz.Core.csproj backend/src/CsViz.Core/
COPY backend/src/CsViz.Trace/CsViz.Trace.csproj backend/src/CsViz.Trace/
RUN dotnet restore backend/src/CsViz.Api/CsViz.Api.csproj

COPY backend/ backend/
RUN dotnet publish backend/src/CsViz.Api/CsViz.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Unprivileged by default. Nothing here needs root, and a container that does not need it
# should not have it.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin csviz
USER 10001

COPY --from=build --chown=10001:10001 /app .

# The narration cache is the one piece of durable state. Mounting a volume here makes cached
# explanations survive a restart; without one the app still works and simply re-earns them.
ENV CSVIZ_AI_CACHE=/app/data/csviz-ai-cache.db
VOLUME ["/app/data"]

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD ["/bin/sh", "-c", "wget --spider -q http://127.0.0.1:8080/healthz || exit 1"]

ENTRYPOINT ["dotnet", "CsViz.Api.dll"]
