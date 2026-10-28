# Bridge relay: forwards end-to-end encrypted Bridge connections between a PC and a phone
# that aren't on the same network. Build from the windows/ folder:
#   docker build -f Bridge.Relay/Dockerfile -t bridge-relay .
#   docker run -d --restart unless-stopped -p 47830:47830 -v bridge-relay:/data bridge-relay relay.example.com
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Bridge.Protocol/ Bridge.Protocol/
COPY Bridge.Security/ Bridge.Security/
COPY Bridge.Network/ Bridge.Network/
COPY Bridge.Relay/ Bridge.Relay/
RUN dotnet publish Bridge.Relay/Bridge.Relay.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out .
ENV BRIDGE_RELAY_DATA=/data
RUN mkdir -p /data && chown app /data
VOLUME /data
EXPOSE 47830
USER app
ENTRYPOINT ["dotnet", "Bridge.Relay.dll"]
