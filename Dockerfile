# Stage 1: Build environment
FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build-env
WORKDIR /App

# Copy everything
COPY . ./

# Restore as distinct layers
RUN dotnet restore

# Build and publish a release
RUN dotnet publish -c Release -o out

# Stage 2: Runtime image
FROM mcr.microsoft.com/dotnet/sdk:7.0
WORKDIR /App

# Copy the published output from the build stage
COPY --from=build-env /App/out .

# Add a volume for the SQLite database file
VOLUME /App/users.db

# Set the entry point
ENTRYPOINT ["dotnet", "Adminbot.dll"]
