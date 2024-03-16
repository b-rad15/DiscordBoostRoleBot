#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/runtime:8.0 AS base
ARG TARGETPLATFORM
WORKDIR /app

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY ["DiscordBoostRoleBot.csproj", "."]
RUN dotnet restore "./DiscordBoostRoleBot.csproj" -a $TARGETARCH
COPY . .
WORKDIR "/src/."
RUN dotnet build "DiscordBoostRoleBot.csproj" -c Release -o /app/build

FROM build AS publish
ARG TARGETARCH
RUN dotnet publish "DiscordBoostRoleBot.csproj" -c Release -a $TARGETARCH -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "DiscordBoostRoleBot.dll"]