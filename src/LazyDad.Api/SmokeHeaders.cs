namespace LazyDad.Api;

/// <summary>The headers every answer carries saying which revision and process gave it (Program.cs), for the smoke tests.</summary>
public static class SmokeHeaders
{
    public const string Revision = "X-LazyDad-Revision";
    public const string Process = "X-LazyDad-Process";
}
