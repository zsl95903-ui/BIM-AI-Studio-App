using System.IO;
using System.Text.Json;
using RevitAi.Core.Agent;

namespace RevitAi.Addin.Services;

// Port of the previous AIChatSessionManager storage layout:
//   %LOCALAPPDATA%\RevitAi\sessions\session_<id>.json
//   %LOCALAPPDATA%\RevitAi\sessions\session_index.json
// The previous used %LOCALAPPDATA%\Ext.Tools\AIChat\sessions; we keep the same
// file naming and index shape under our own folder.
internal sealed class AgentSessionStore
{
    private const string IndexFileName = "session_index.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly string _indexPath;

    public AgentSessionStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitAi",
            "sessions");
        _indexPath = Path.Combine(_directory, IndexFileName);
        System.IO.Directory.CreateDirectory(_directory);
    }

    public string Directory => _directory;

    public void Save(AgentSessionState session)
    {
        var dto = ToDto(session);
        File.WriteAllText(SessionPath(session.SessionId), JsonSerializer.Serialize(dto, SerializerOptions));
        UpdateIndex(session);
    }

    public AgentSessionState? Load(string sessionId)
    {
        var path = SessionPath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<SessionDto>(
                File.ReadAllText(path),
                SerializerOptions);
            return dto is null ? null : FromDto(dto);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<AgentSessionIndexItem> List()
    {
        if (!File.Exists(_indexPath))
        {
            return [];
        }

        try
        {
            var index = JsonSerializer.Deserialize<SessionIndexDto>(
                File.ReadAllText(_indexPath),
                SerializerOptions);
            return index?.Sessions
                .OrderByDescending(item => item.UpdatedAt)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public bool Delete(string sessionId)
    {
        var path = SessionPath(sessionId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var sessions = List().Where(item => item.SessionId != sessionId).ToList();
        WriteIndex(sessions);
        return true;
    }

    private void UpdateIndex(AgentSessionState session)
    {
        var sessions = List()
            .Where(item => item.SessionId != session.SessionId)
            .ToList();
        sessions.Add(new AgentSessionIndexItem
        {
            SessionId = session.SessionId,
            Title = session.Title,
            UpdatedAt = session.UpdatedAt,
            CreatedAt = session.CreatedAt,
            MessageCount = session.Messages.Count,
            RoundCount = session.RoundCount,
            HasSummary = !string.IsNullOrEmpty(session.AccumulatedSummary)
        });
        sessions = sessions
            .OrderByDescending(item => item.UpdatedAt)
            .ToList();
        WriteIndex(sessions);
    }

    private void WriteIndex(IReadOnlyList<AgentSessionIndexItem> sessions)
    {
        var dto = new SessionIndexDto
        {
            Version = 1,
            Sessions = sessions.ToList()
        };
        File.WriteAllText(_indexPath, JsonSerializer.Serialize(dto, SerializerOptions));
    }

    private string SessionPath(string sessionId) =>
        Path.Combine(_directory, $"session_{sessionId}.json");

    private static SessionDto ToDto(AgentSessionState session) => new()
    {
        SessionId = session.SessionId,
        Title = session.Title,
        CreatedAt = session.CreatedAt,
        UpdatedAt = session.UpdatedAt,
        RoundCount = session.RoundCount,
        AccumulatedSummary = session.AccumulatedSummary,
        SummaryMessageCount = session.SummaryMessageCount,
        Messages = session.Messages.Select(message => new MessageDto
        {
            Role = message.Role,
            Content = message.Content,
            ToolCallId = message.ToolCallId,
            Timestamp = message.Timestamp,
            ToolCalls = message.ToolCalls?.Select(call => new ToolCallDto
            {
                CallId = call.CallId,
                ToolName = call.ToolName,
                ArgumentsJson = call.ArgumentsJson
            }).ToList()
        }).ToList()
    };

    private static AgentSessionState FromDto(SessionDto dto)
    {
        var session = new AgentSessionState
        {
            SessionId = dto.SessionId,
            Title = dto.Title,
            CreatedAt = dto.CreatedAt,
            UpdatedAt = dto.UpdatedAt,
            RoundCount = dto.RoundCount,
            AccumulatedSummary = dto.AccumulatedSummary,
            SummaryMessageCount = dto.SummaryMessageCount
        };
        foreach (var message in dto.Messages)
        {
            session.Messages.Add(new AgentSessionMessage
            {
                Role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role,
                Content = message.Content,
                ToolCallId = message.ToolCallId,
                Timestamp = message.Timestamp,
                ToolCalls = message.ToolCalls?.Select(call => new AgentToolCallRecord
                {
                    CallId = call.CallId,
                    ToolName = call.ToolName,
                    ArgumentsJson = call.ArgumentsJson
                }).ToList()
            });
        }

        return session;
    }

    private sealed class SessionIndexDto
    {
        public int Version { get; set; } = 1;

        public List<AgentSessionIndexItem> Sessions { get; set; } = [];
    }

    private sealed class SessionDto
    {
        public string SessionId { get; set; } = string.Empty;

        public string Title { get; set; } = "新对话";

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public int RoundCount { get; set; }

        public string? AccumulatedSummary { get; set; }

        public int SummaryMessageCount { get; set; }

        public List<MessageDto> Messages { get; set; } = [];
    }

    private sealed class MessageDto
    {
        public string Role { get; set; } = "user";

        public string? Content { get; set; }

        public string? ToolCallId { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.Now;

        public List<ToolCallDto>? ToolCalls { get; set; }
    }

    private sealed class ToolCallDto
    {
        public string CallId { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public string? ArgumentsJson { get; set; }
    }
}

public sealed class AgentSessionIndexItem
{
    public string SessionId { get; set; } = string.Empty;

    public string Title { get; set; } = "新对话";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public int MessageCount { get; set; }

    public int RoundCount { get; set; }

    public bool HasSummary { get; set; }
}
