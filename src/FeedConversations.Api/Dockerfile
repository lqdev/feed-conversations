FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /source
COPY global.json Directory.Build.props NuGet.Config FeedConversations.slnx ./
COPY src/FeedConversations.Api/ src/FeedConversations.Api/
COPY src/FeedConversations.ServiceDefaults/ src/FeedConversations.ServiceDefaults/
RUN dotnet restore src/FeedConversations.Api --locked-mode
RUN dotnet publish src/FeedConversations.Api -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FeedConversations.Api.dll"]
