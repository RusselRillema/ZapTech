using AcmePay.Stub;

// Container health probe; it uses only the existing HTTP liveness endpoint.
if (args is ["--health-check"])
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await client.GetAsync("http://127.0.0.1:8080/health");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
    {
        return 1;
    }
}

if (File.Exists(".env"))
{
    DotNetEnv.Env.NoClobber().Load(".env");
}

await StubApplication.Build(args).RunAsync();
return 0;
