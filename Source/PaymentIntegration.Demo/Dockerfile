FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Source/PaymentIntegration.Demo/PaymentIntegration.Demo.csproj Source/PaymentIntegration.Demo/
RUN dotnet restore Source/PaymentIntegration.Demo/PaymentIntegration.Demo.csproj
COPY Source/PaymentIntegration.Demo/ Source/PaymentIntegration.Demo/
RUN dotnet publish Source/PaymentIntegration.Demo/PaymentIntegration.Demo.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "PaymentIntegration.Demo.dll"]
CMD ["--interactive"]
