using System.Net;
using System.Text;

namespace MvsBuddy.Tests;

/// <summary>An HTTP handler that answers from a table of paths (with query), and records what was asked.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> answers = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    public Func<CancellationToken, Task>? Delay { get; set; }

    public FakeGitHub Json(string pathAndQuery, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        answers[pathAndQuery] = () => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        return this;
    }

    public FakeGitHub Answer(string pathAndQuery, Func<HttpResponseMessage> answer)
    {
        answers[pathAndQuery] = answer;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Delay != null)
        {
            await Delay(cancellationToken);
        }
        string key = request.RequestUri!.PathAndQuery;
        return answers.TryGetValue(key, out var answer)
            ? answer()
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") };
    }
}
