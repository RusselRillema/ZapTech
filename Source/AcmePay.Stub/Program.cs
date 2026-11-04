using AcmePay.Stub;

if (File.Exists(".env"))
{
    DotNetEnv.Env.NoClobber().Load(".env");
}

await StubApplication.Build(args).RunAsync();
