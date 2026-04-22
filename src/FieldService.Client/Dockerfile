FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && ln -sf /usr/bin/python3 /usr/bin/python \
    && rm -rf /var/lib/apt/lists/*

COPY ["FieldService.sln", "./"]
COPY ["src/FieldService.Client/FieldService.Client.csproj", "src/FieldService.Client/"]
COPY ["src/FieldService.Shared/FieldService.Shared.csproj", "src/FieldService.Shared/"]
RUN dotnet workload install wasm-tools
RUN dotnet restore "src/FieldService.Client/FieldService.Client.csproj"

COPY . .
RUN dotnet publish "src/FieldService.Client/FieldService.Client.csproj" -c Release -o /app/publish

FROM nginx:stable-alpine AS final
WORKDIR /usr/share/nginx/html
COPY ["src/FieldService.Client/nginx.conf", "/etc/nginx/conf.d/default.conf"]
COPY --from=build /app/publish/wwwroot .

EXPOSE 80
