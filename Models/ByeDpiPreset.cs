namespace NetFix.Models;

public sealed record ByeDpiPreset(string Id, string Title, string Description, string Arguments)
{
    public override string ToString() => Title;
}

public static class ByeDpiPresets
{
    public static IReadOnlyList<ByeDpiPreset> All { get; } =
    [
        new(
            "default",
            "Стандартный",
            "Параметры из поставки ByeDPI для Windows: split, disorder и запасной TLS record.",
            "--split 1 --disorder 3+s --mod-http=h,d --auto=torst --tlsrec 1+s"),
        new(
            "split-disorder",
            "Split + disorder",
            "Разбиение ClientHello. Для Windows это базовый вариант без фейковых пакетов.",
            "--split 1+s --disorder 3+s"),
        new(
            "disorder-fake",
            "Disorder + fake",
            "Фейковый первый фрагмент с перестановкой. На Windows фейк бывает нестабилен.",
            "--disorder 1 --fake -1"),
        new(
            "fake-ttl",
            "Fake TTL",
            "Фейковый пакет с коротким TTL: его должен увидеть фильтр и не должен увидеть сервер.",
            "--fake -1 --ttl 8"),
        new(
            "oob",
            "OOB в SNI",
            "Лишний байт внутри имени сервера. Подходит, если фильтр читает SNI целиком.",
            "--oob 1+s"),
        new(
            "tlsrec",
            "TLS record",
            "Делит ClientHello на отдельные TLS-записи только если соединение сбрасывают.",
            "--auto=torst --timeout 3 --tlsrec 1+s"),
    ];

    public static ByeDpiPreset Get(string? id) =>
        All.FirstOrDefault(preset => preset.Id == id) ?? All[0];
}
