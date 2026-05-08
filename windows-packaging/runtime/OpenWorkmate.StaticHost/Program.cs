using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.Pack.json"),
    optional: true,
    reloadOnChange: false);

var port = int.TryParse(builder.Configuration["StaticHost:Port"], out var p) ? p : 3000;
var officeRoot = builder.Configuration["StaticHost:OfficeRoot"] ?? "";
var wpsRoot = builder.Configuration["StaticHost:WpsRoot"] ?? "";
var chromeRoot = builder.Configuration["StaticHost:ChromeRoot"] ?? "";

if (string.IsNullOrWhiteSpace(officeRoot) || !Directory.Exists(officeRoot))
{
    TryWriteStaticHostDiag("StaticHost:OfficeRoot 未设置或目录不存在。");
    return 1;
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port, listen =>
    {
        var certPath = builder.Configuration["StaticHost:CertPfxPath"];
        var certPassword = builder.Configuration["StaticHost:CertPfxPassword"];
        if (!string.IsNullOrWhiteSpace(certPath) && File.Exists(certPath))
        {
            listen.UseHttps(certPath, certPassword ?? "");
        }
        else
        {
            listen.UseHttps(https =>
            {
                https.ServerCertificate = GetDevCertificate();
            });
        }
    });
});

static void TryWriteStaticHostDiag(string message)
{
    try
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenWorkmate", "logs");
        Directory.CreateDirectory(dir);
        File.AppendAllText(
            Path.Combine(dir, "static-host.txt"),
            $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
    }
    catch
    {
        /* ignore */
    }
}

static X509Certificate2 GetDevCertificate()
{
    using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
    store.Open(OpenFlags.ReadOnly);
    var found = store.Certificates.Find(X509FindType.FindBySubjectName, "localhost", validOnly: false);
    var cert = found.OfType<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey);
    if (cert is null)
        throw new InvalidOperationException(
            "未找到本机 localhost 开发证书。请运行: dotnet dev-certs https --trust");
    return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), ReadOnlySpan<char>.Empty);
}

var app = builder.Build();

var defaultFiles = new DefaultFilesOptions();
defaultFiles.DefaultFileNames.Clear();
defaultFiles.DefaultFileNames.Add("taskpane.html");
app.UseDefaultFiles(defaultFiles);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(officeRoot)),
    RequestPath = ""
});

if (!string.IsNullOrWhiteSpace(wpsRoot) && Directory.Exists(wpsRoot))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(wpsRoot)),
        RequestPath = "/wps"
    });
}

if (!string.IsNullOrWhiteSpace(chromeRoot) && Directory.Exists(chromeRoot))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(chromeRoot)),
        RequestPath = "/OpenWorkmate-chrome"
    });
}

var bootMsg =
    $"OpenWorkmate.StaticHost HTTPS https://localhost:{port}/ （Office 根：{officeRoot}）";
if (Directory.Exists(wpsRoot)) bootMsg += $"{Environment.NewLine}  WPS 静态：/wps → {wpsRoot}";
if (Directory.Exists(chromeRoot)) bootMsg += $"{Environment.NewLine}  Chrome 更新：/OpenWorkmate-chrome → {chromeRoot}";
TryWriteStaticHostDiag(bootMsg);
app.Logger.LogInformation("{Boot}", bootMsg);

app.Run();
return 0;
