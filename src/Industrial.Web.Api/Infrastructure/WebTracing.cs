using System.Diagnostics;

namespace Industrial.Web.Api.Infrastructure;

public static class WebTracing
{
    public const string SourceName = "Industrial.Web.Api";

    public static readonly ActivitySource Source = new(SourceName);
}
