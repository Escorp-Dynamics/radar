namespace Radar.Services;

/// <summary>Адреса распространения: репозиторий, релизы сборок и архива, поддержка и обратная связь.</summary>
public static class Distribution
{
    public const string Repo = "Escorp-Dynamics/radar";
    public const string RepoUrl = "https://github.com/" + Repo;
    /// <summary>Последний релиз сборок (тег vX.Y.Z) — проверка обновлений.</summary>
    public const string LatestReleaseApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string ReleasesUrl = RepoUrl + "/releases/latest";
    /// <summary>Архив сообщений — отдельный релиз с постоянным тегом «archive», обновляется ежедневно.</summary>
    public const string ArchiveBaseUrl = RepoUrl + "/releases/download/archive/";
    public const string DonateUrl = "https://yoomoney.ru/to/4100117878671979";
    public const string FeedbackUrl = "https://t.me/people_soft?direct";
}
