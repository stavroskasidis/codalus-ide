# See https://aka.ms/customizecontainer to learn how to customize your container and how the IDE uses this Dockerfile to build images faster.

# The base stage: the image the app runs on. Fast mode (Debug) runs the build output on it.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# The build stage: builds the project.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Front/Front.csproj", "Front/"]
RUN dotnet restore "./Front/Front.csproj"
COPY . .
WORKDIR "/src/Front"
RUN dotnet build "./Front.csproj" -c $BUILD_CONFIGURATION -o /app/build

# The publish stage: publishes the project.
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Front.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# The final stage: the published app on the base image (Release, and fast mode turned off).
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Front.dll"]
