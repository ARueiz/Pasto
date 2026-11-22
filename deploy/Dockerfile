FROM node:24.22.2-bookworm-slim AS frontend
WORKDIR /client
COPY src/Pasto.Client/package.json src/Pasto.Client/package-lock.json ./
RUN npm ci
COPY src/Pasto.Client/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /source
COPY global.json ./
COPY src/Pasto.Server/Pasto.Server.csproj src/Pasto.Server/packages.lock.json ./src/Pasto.Server/
RUN dotnet restore src/Pasto.Server/Pasto.Server.csproj --locked-mode
COPY src/Pasto.Server/ ./src/Pasto.Server/
COPY --from=frontend /client/dist/ ./src/Pasto.Server/wwwroot/
RUN dotnet publish src/Pasto.Server/Pasto.Server.csproj -c Release --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13
WORKDIR /app
COPY --from=build /publish/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Pasto.Server.dll"]
