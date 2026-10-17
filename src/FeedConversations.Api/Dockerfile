FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props NuGet.Config FeedConversations.slnx ./
COPY src/FeedConversations.Api/ src/FeedConversations.Api/
COPY src/FeedConversations.ServiceDefaults/ src/FeedConversations.ServiceDefaults/
RUN dotnet restore src/FeedConversations.Api --locked-mode
RUN dotnet publish src/FeedConversations.Api -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FeedConversations.Api.dll"]
