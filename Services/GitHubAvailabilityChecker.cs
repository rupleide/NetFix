#nullable enable

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NetFix.Services;

public enum GitHubAvailabilityResult
{
    Available,
    Unavailable,
    Timeout
}

public static class GitHubAvailabilityChecker
{
    private const string GitHubMainUrl = "https://github.com";
    private const string GitHubApiUrl = "https://api.github.com";

    public static async Task<GitHubAvailabilityResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        Task<GitHubAvailabilityResult> mainTask = CheckUrlAsync(GitHubMainUrl, cancellationToken);
        Task<GitHubAvailabilityResult> apiTask = CheckUrlAsync(GitHubApiUrl, cancellationToken);

        GitHubAvailabilityResult[] results = await Task.WhenAll(mainTask, apiTask);

        if (results[0] == GitHubAvailabilityResult.Available && results[1] == GitHubAvailabilityResult.Available)
        {
            return GitHubAvailabilityResult.Available;
        }

        if (results[0] == GitHubAvailabilityResult.Timeout || results[1] == GitHubAvailabilityResult.Timeout)
        {
            return GitHubAvailabilityResult.Timeout;
        }

        return GitHubAvailabilityResult.Unavailable;
    }

    private static async Task<GitHubAvailabilityResult> CheckUrlAsync(string url, CancellationToken cancellationToken)
    {
        const int maxAttempts = 2;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(4));

                using HttpClient http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");

                using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    return GitHubAvailabilityResult.Available;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == maxAttempts)
                {
                    return GitHubAvailabilityResult.Timeout;
                }
            }
            catch
            {
                if (attempt == maxAttempts)
                {
                    return GitHubAvailabilityResult.Unavailable;
                }
            }
        }

        return GitHubAvailabilityResult.Unavailable;
    }
}
