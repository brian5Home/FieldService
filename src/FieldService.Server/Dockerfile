FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["FieldService.sln", "./"]
COPY ["src/FieldService.Server/FieldService.Server.csproj", "src/FieldService.Server/"]
COPY ["src/FieldService.Shared/FieldService.Shared.csproj", "src/FieldService.Shared/"]
RUN dotnet restore "src/FieldService.Server/FieldService.Server.csproj"

COPY . .
RUN dotnet publish "src/FieldService.Server/FieldService.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "FieldService.Server.dll"]
