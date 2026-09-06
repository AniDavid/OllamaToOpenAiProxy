FROM mcr.microsoft.com/dotnet/net:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 80

FROM mcr.microsoft.com/dotnet/net:10.0-sdk AS build
WORKDIR /src
COPY ["OllamaToOpenAiProxy.csproj", "."]
RUN dotnet restore "./OllamaToOpenAiProxy.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "OllamaToOpenAiProxy.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "OllamaToOpenAiProxy.csproj" -c Release -o /app/publish /p:PublishTrimmed=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OllamaToOpenAiProxy.dll"]