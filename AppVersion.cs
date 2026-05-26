namespace NetFix;

public static class AppVersion
{
    public static string Current { get; } =
        System.Reflection.Assembly
              .GetExecutingAssembly()
              .GetName()
              .Version
              ?.ToString(3) // "1.0.8", три компонента
        ?? "1.0.0";

    public static string Display => $"v{Current}"; // "v1.0.8"
}
