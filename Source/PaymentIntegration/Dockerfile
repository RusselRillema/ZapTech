FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Source/PaymentIntegration/PaymentIntegration.csproj Source/PaymentIntegration/
RUN dotnet restore Source/PaymentIntegration/PaymentIntegration.csproj
COPY Source/PaymentIntegration/ Source/PaymentIntegration/
RUN dotnet publish Source/PaymentIntegration/PaymentIntegration.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "PaymentIntegration.dll"]
