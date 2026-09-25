using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.ComponentModel;
using AnomalyStudio.Core.Modes;

namespace AnomalyStudio.Services;

/// <summary>
/// 自然文から抽出 SQL を生成する AI アシスト。設定の LLM（OpenAI 互換 API）に問い合わせる。
/// 接続テスト（GET /models）に合格するまで使えない。プロンプトは <see cref="ModeSqlPrompt"/>（表の定義・列の意味・規則・書き方の見本）。
/// API キーは接続テストに合格したとき Windows の資格情報マネージャーに保存し（OS が暗号化）、次回の起動で読み込む。
/// 設定ファイルやログには書かない。
/// </summary>
public sealed class AiSqlAssistant
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>SQL の生成は時間がかかることがあるので、接続テストより長く待つ。</summary>
    private static readonly HttpClient CompletionHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>資格情報マネージャーでの名前（「Windows 資格情報」→「汎用資格情報」に表示される）。</summary>
    public const string CredentialTarget = "AnomalyStudio:AiApiKey";

    private readonly AppSettings _settings;

    public AiSqlAssistant(AppSettings settings)
    {
        _settings = settings;
        ApiKey = ReadStoredKey() ?? string.Empty;
        HasStoredKey = ApiKey.Length > 0;

        // ベースURL・モデルを変えたら、接続テストをやり直すまで使えなくする
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.AiBaseUrl) or nameof(AppSettings.AiModel))
            {
                Invalidate();
            }
        };
    }

    public const string SamplePrompt = "30日・90日・365日のすべてで勝率60%以上、保有15分以内の時間帯を勝率順に";

    /// <summary>接続テストに合格した（設定を変えると取り消す）。</summary>
    public bool IsConnected { get; private set; }

    /// <summary>接続テストに合格した（または資格情報マネージャーから読み込んだ）API キー。</summary>
    public string ApiKey { get; private set; } = string.Empty;

    /// <summary>資格情報マネージャーに API キーを保存している。</summary>
    public bool HasStoredKey { get; private set; }

    /// <summary>
    /// 保存したキーがあってまだ確かめていなければ、接続テストをする（設定画面や AI アシストを開いたとき）。
    /// 起動のたびに外部へ通信しないよう、起動時には行わない。
    /// </summary>
    public async Task<string?> EnsureConnectedAsync(CancellationToken cancellationToken = default) =>
        IsConnected || ApiKey.Length == 0
            ? null
            : await TestConnectionAsync(_settings.AiBaseUrl, _settings.AiModel, ApiKey, cancellationToken);

    /// <summary>保存したキーを消す（メモリからも消し、接続テストを取り消す）。</summary>
    public void DeleteStoredKey()
    {
        try
        {
            CredentialStore.Delete(CredentialTarget);
        }
        catch (Win32Exception)
        {
            // 消せなくても、この起動中は使わない
        }

        ApiKey = string.Empty;
        HasStoredKey = false;
        Invalidate();
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string? ReadStoredKey()
    {
        try
        {
            return CredentialStore.Read(CredentialTarget);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>接続テストの結果が変わった。</summary>
    public event EventHandler? ConnectionChanged;

    /// <summary>設定が変わったので、接続テストをやり直すまで使えなくする。</summary>
    public void Invalidate()
    {
        if (IsConnected)
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// ベース URL の /models を API キーで取得し、応答にモデルが含まれるか確かめる。
    /// 合格すれば AI アシストを使えるようにする。結果の説明を返す。
    /// </summary>
    public async Task<string> TestConnectionAsync(string baseUrl, string model, string apiKey, CancellationToken cancellationToken = default)
    {
        Invalidate();
        if (Endpoint(baseUrl, "models") is not { } uri)
        {
            return "ベースURLが正しくありません（例 https://api.openai.com/v1）";
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "APIキーを入力してください";
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return "モデルを入力してください";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            using var response = await Http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return "APIキーが認証されませんでした";
            }

            if (!response.IsSuccessStatusCode)
            {
                return $"接続できません（HTTP {(int)response.StatusCode}）";
            }

            var ids = ReadModelIds(await response.Content.ReadAsStringAsync(cancellationToken));
            if (ids is not null && !ids.Contains(model.Trim()))
            {
                return $"モデル「{model.Trim()}」が見つかりません";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "接続できません: " + ex.Message;
        }

        ApiKey = apiKey.Trim();
        IsConnected = true;
        var saved = SaveKey();
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
        return saved ? $"接続済み（APIキーを{CredentialStore.StoreName}に保存しました）" : "接続済み（APIキーを保存できませんでした。次回は入力し直してください）";
    }

    private bool SaveKey()
    {
        try
        {
            CredentialStore.Write(CredentialTarget, "AnomalyStudio", ApiKey, "AnomalyStudio の AI アシスト（OpenAI 互換 API）の API キー");
            HasStoredKey = OperatingSystem.IsWindows();
            return HasStoredKey;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>要望から抽出 SQL を生成する。</summary>
    public async Task<string> GenerateSqlAsync(string prompt, CancellationToken cancellationToken = default) =>
        SqlFrom(await CompleteAsync(GenerationSystem, [("user", prompt)], cancellationToken));

    /// <summary>文法チェックのエラーや指示との不一致がある SQL を、問題点と一緒に送って直してもらう。</summary>
    public async Task<string> RepairSqlAsync(string prompt, string sql, string problem, CancellationToken cancellationToken = default) =>
        SqlFrom(await CompleteAsync(
            GenerationSystem,
            [("user", prompt), ("assistant", $"```sql\n{sql}\n```"), ("user", ModeSqlPrompt.RepairRequest(problem))],
            cancellationToken));

    /// <summary>
    /// 指示文と SQL が一致しているかを LLM に確かめてもらう。応答が読めなければ null（照合できなかった）。
    /// </summary>
    public async Task<SqlReview?> ReviewSqlAsync(string prompt, string sql, CancellationToken cancellationToken = default) =>
        ModeSqlPrompt.ParseReview(await CompleteAsync(
            ModeSqlPrompt.ReviewSystem(AnalysisDatabase.CandidateStatsSchema),
            [("user", ModeSqlPrompt.ReviewRequest(prompt, sql))],
            cancellationToken));

    private static string GenerationSystem => ModeSqlPrompt.System(AnalysisDatabase.CandidateStatsSchema);

    private static string SqlFrom(string content)
    {
        var sql = ModeSqlPrompt.ExtractSql(content);
        return sql.Length > 0 ? sql : throw new HttpRequestException("AI の応答に SQL がありませんでした。要望を具体的にして生成し直してください。");
    }

    /// <summary>POST /chat/completions に会話を送り、応答の本文を返す。失敗は <see cref="HttpRequestException"/>。</summary>
    private async Task<string> CompleteAsync(
        string system, IReadOnlyList<(string Role, string Content)> conversation, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("設定の「接続テスト」に合格すると AI アシストを使えます。");
        }

        var uri = Endpoint(_settings.AiBaseUrl, "chat/completions")
            ?? throw new InvalidOperationException("設定のベースURLが正しくありません。");
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = system },
        };
        foreach (var (role, content) in conversation)
        {
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
        }

        var body = new JsonObject { ["model"] = _settings.AiModel.Trim(), ["messages"] = messages };
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        using var response = await CompletionHttp.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"AI の応答がエラーです（HTTP {(int)response.StatusCode}）{ErrorDetail(text)}");
        }

        return ReadContent(text) ?? throw new HttpRequestException("AI の応答を読めませんでした。");
    }

    private static Uri? Endpoint(string baseUrl, string path) =>
        Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/" + path, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    /// <summary>OpenAI 互換の {"choices":[{"message":{"content":...}}]} から本文を読む。</summary>
    private static string? ReadContent(string json)
    {
        try
        {
            return JsonNode.Parse(json)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>エラー応答の {"error":{"message":...}} を添える（API キーは含まれない）。</summary>
    private static string ErrorDetail(string json)
    {
        try
        {
            return JsonNode.Parse(json)?["error"]?["message"]?.GetValue<string>() is { Length: > 0 } message ? ": " + message : string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>OpenAI 互換の {"data":[{"id":...}]} からモデル名を読む。形式が違えば null（モデルの確認はしない）。</summary>
    internal static HashSet<string>? ReadModelIds(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                return [.. data.EnumerateArray()
                    .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .OfType<string>()];
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
