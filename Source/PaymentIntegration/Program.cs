using PaymentIntegration;

if (File.Exists(".env"))
{
    DotNetEnv.Env.NoClobber().Load(".env");
}

await PaymentIntegrationApplication.Build(args).RunAsync();
