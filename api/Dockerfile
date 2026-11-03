FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY QueryIndexAdvisor.Api/QueryIndexAdvisor.Api.csproj QueryIndexAdvisor.Api/
RUN dotnet restore QueryIndexAdvisor.Api/QueryIndexAdvisor.Api.csproj
COPY QueryIndexAdvisor.Api/ QueryIndexAdvisor.Api/
RUN dotnet publish QueryIndexAdvisor.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "QueryIndexAdvisor.Api.dll"]
