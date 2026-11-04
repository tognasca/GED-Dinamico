FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Ged.Api.csproj ./
RUN dotnet restore Ged.Api.csproj

COPY . ./
# Rebuild assets inside Linux; never reuse Windows-generated obj/project.assets.json.
RUN rm -rf bin obj \
    && dotnet restore Ged.Api.csproj \
    && dotnet publish Ged.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       tesseract-ocr tesseract-ocr-por tesseract-ocr-eng poppler-utils \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ged.Api.dll"]
