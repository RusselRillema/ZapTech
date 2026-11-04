FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Source/AcmePay.Stub/AcmePay.Stub.csproj Source/AcmePay.Stub/
RUN dotnet restore Source/AcmePay.Stub/AcmePay.Stub.csproj
COPY Source/AcmePay.Stub/ Source/AcmePay.Stub/
RUN dotnet publish Source/AcmePay.Stub/AcmePay.Stub.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "AcmePay.Stub.dll"]
