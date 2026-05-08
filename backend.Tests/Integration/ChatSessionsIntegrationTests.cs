using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenWorkmate.Server;
using Xunit;

namespace backend.Tests.Integration;

public class ChatSessionsIntegrationTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _userConfigPath;
    private readonly string _scheduledTasksDir;
    private readonly string _chatSessionsDir;

    public ChatSessionsIntegrationTests()
    {
        _userConfigPath = Path.Combine(
            Path.GetTempPath(),
            "OpenWorkmate.user-config-test-" + Guid.NewGuid().ToString("N") + ".json");
        _scheduledTasksDir = Path.Combine(
            Path.GetTempPath(),
            "OpenWorkmate.scheduled-tasks-test-" + Guid.NewGuid().ToString("N"));
        _chatSessionsDir = Path.Combine(
            Path.GetTempPath(),
            "OpenWorkmate.chat-sessions-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_chatSessionsDir);

        IntegrationTestUserConfigWriter.Write(_userConfigPath, _scheduledTasksDir, webSocketAuthToken: "");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenWorkmate:UserConfigPath"] = _userConfigPath,
                    ["OpenWorkmate:ChatSessionsDirectory"] = _chatSessionsDir,
                });
            });
        });
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try
        {
            if (File.Exists(_userConfigPath))
                File.Delete(_userConfigPath);
        }
        catch
        {
            /* ignore */
        }

        try
        {
            if (Directory.Exists(_scheduledTasksDir))
                Directory.Delete(_scheduledTasksDir, true);
        }
        catch
        {
            /* ignore */
        }

        try
        {
            if (Directory.Exists(_chatSessionsDir))
                Directory.Delete(_chatSessionsDir, true);
        }
        catch
        {
            /* ignore */
        }
    }

    [Fact]
    public async Task GetChatSessions_EmptyDirectory_ReturnsOkAndEmptyItems()
    {
        var res = await _client.GetAsync("/api/chat-sessions?skip=0&take=10");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetChatSessions_WithSeededDb_ListsPaginatesMessagesDelete()
    {
        const string sid = "testsess12ab";
        // 触达 API 以创建 chat-sessions.db 与表结构（与 SqliteChatSessionStore 一致）
        var initRes = await _client.GetAsync("/api/chat-sessions?skip=0&take=10");
        initRes.EnsureSuccessStatusCode();

        var dbPath = Path.Combine(_chatSessionsDir, "chat-sessions.db");
        Assert.True(File.Exists(dbPath), "chat-sessions.db should exist after first list call.");

        await using (var conn = new SqliteConnection("Data Source=" + dbPath))
        {
            await conn.OpenAsync();
            await using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync();

            await using (var insSess = conn.CreateCommand())
            {
                insSess.CommandText =
                    "INSERT INTO chat_sessions (session_id, updated_at_utc, title_preview, message_count) VALUES ($sid, $upd, $title, $cnt);";
                insSess.Parameters.AddWithValue("$sid", sid);
                insSess.Parameters.AddWithValue("$upd", "2026-01-15T10:00:00.0000000Z");
                insSess.Parameters.AddWithValue("$title", "hello");
                insSess.Parameters.AddWithValue("$cnt", 2);
                await insSess.ExecuteNonQueryAsync();
            }

            await using (var m0 = conn.CreateCommand())
            {
                m0.CommandText =
                    "INSERT INTO chat_session_messages (session_id, sort_order, role, text, created_at_utc) VALUES ($sid, 0, $r, $t, $c);";
                m0.Parameters.AddWithValue("$sid", sid);
                m0.Parameters.AddWithValue("$r", "user");
                m0.Parameters.AddWithValue("$t", "hi");
                m0.Parameters.AddWithValue("$c", "2026-01-15T10:00:00.0000000Z");
                await m0.ExecuteNonQueryAsync();
            }

            await using (var m1 = conn.CreateCommand())
            {
                m1.CommandText =
                    "INSERT INTO chat_session_messages (session_id, sort_order, role, text, created_at_utc) VALUES ($sid, 1, $r, $t, $c);";
                m1.Parameters.AddWithValue("$sid", sid);
                m1.Parameters.AddWithValue("$r", "assistant");
                m1.Parameters.AddWithValue("$t", "yo");
                m1.Parameters.AddWithValue("$c", "2026-01-15T10:00:01.0000000Z");
                await m1.ExecuteNonQueryAsync();
            }
        }

        var listRes = await _client.GetAsync("/api/chat-sessions?skip=0&take=10");
        listRes.EnsureSuccessStatusCode();
        var listBody = await listRes.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(listBody))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("hasMore").GetBoolean());
            var items = doc.RootElement.GetProperty("items");
            Assert.Equal(1, items.GetArrayLength());
            Assert.Equal(sid, items[0].GetProperty("sessionId").GetString());
        }

        var msgRes = await _client.GetAsync("/api/chat-sessions/" + sid + "/messages");
        msgRes.EnsureSuccessStatusCode();
        var msgBody = await msgRes.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(msgBody))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var msgs = doc.RootElement.GetProperty("messages");
            Assert.Equal(2, msgs.GetArrayLength());
            Assert.Equal("user", msgs[0].GetProperty("role").GetString());
            Assert.Equal("hi", msgs[0].GetProperty("text").GetString());
        }

        var delRes = await _client.DeleteAsync("/api/chat-sessions/" + sid);
        delRes.EnsureSuccessStatusCode();

        var after = await _client.GetAsync("/api/chat-sessions/" + sid + "/messages");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        var errJson = await after.Content.ReadAsStringAsync();
        using var errDoc = JsonDocument.Parse(errJson);
        Assert.False(errDoc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task GetChatSessions_WhenAuthRequired_WithoutHeader_Returns401()
    {
        var userPath = Path.Combine(
            Path.GetTempPath(),
            "OpenWorkmate.user-config-auth-chatsess-" + Guid.NewGuid().ToString("N") + ".json");
        var schedDir = Path.Combine(Path.GetTempPath(), "OpenWorkmate.st-chatsess-" + Guid.NewGuid().ToString("N"));
        var chatDir = Path.Combine(Path.GetTempPath(), "OpenWorkmate.chat-chatsess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(chatDir);
        IntegrationTestUserConfigWriter.Write(userPath, schedDir, webSocketAuthToken: "chatsess-secret");

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenWorkmate:UserConfigPath"] = userPath,
                    ["OpenWorkmate:ChatSessionsDirectory"] = chatDir,
                });
            });
        });
        var client = factory.CreateClient();
        try
        {
            var res = await client.GetAsync("/api/chat-sessions");
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        finally
        {
            client.Dispose();
            try
            {
                if (File.Exists(userPath)) File.Delete(userPath);
            }
            catch
            {
                /* ignore */
            }

            try
            {
                if (Directory.Exists(schedDir)) Directory.Delete(schedDir, true);
            }
            catch
            {
                /* ignore */
            }

            try
            {
                if (Directory.Exists(chatDir)) Directory.Delete(chatDir, true);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    [Fact]
    public async Task GetChatSessions_FilterByAgentProfileId_ReturnsMatchingRowsOnly()
    {
        await _client.GetAsync("/api/chat-sessions?skip=0&take=10");

        var dbPath = Path.Combine(_chatSessionsDir, "chat-sessions.db");
        Assert.True(File.Exists(dbPath));

        const string sidA = "filtertestaa01";
        const string sidB = "filtertestbb02";

        await using (var conn = new SqliteConnection("Data Source=" + dbPath))
        {
            await conn.OpenAsync();
            await using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync();

            foreach (var tuple in new (string Sid, string Aid, string Title)[] { (sidA, "alpha", "a-title"), (sidB, "beta", "b-title") })
            {
                await using (var insSess = conn.CreateCommand())
                {
                    insSess.CommandText =
                        """
                        INSERT INTO chat_sessions (session_id, updated_at_utc, title_preview, message_count, agent_profile_id)
                        VALUES ($sid, $upd, $title, $cnt, $aid);
                        """;
                    insSess.Parameters.AddWithValue("$sid", tuple.Sid);
                    insSess.Parameters.AddWithValue("$upd", "2026-02-01T12:00:00.0000000Z");
                    insSess.Parameters.AddWithValue("$title", tuple.Title);
                    insSess.Parameters.AddWithValue("$cnt", 1);
                    insSess.Parameters.AddWithValue("$aid", tuple.Aid);
                    await insSess.ExecuteNonQueryAsync();
                }

                await using (var m0 = conn.CreateCommand())
                {
                    m0.CommandText =
                        """
                        INSERT INTO chat_session_messages (session_id, sort_order, role, text, created_at_utc)
                        VALUES ($sid, 0, $r, $t, $c);
                        """;
                    m0.Parameters.AddWithValue("$sid", tuple.Sid);
                    m0.Parameters.AddWithValue("$r", "user");
                    m0.Parameters.AddWithValue("$t", "hi");
                    m0.Parameters.AddWithValue("$c", "2026-02-01T12:00:00.0000000Z");
                    await m0.ExecuteNonQueryAsync();
                }
            }
        }

        var alphaRes = await _client.GetAsync("/api/chat-sessions?skip=0&take=10&agentProfileId=alpha");
        alphaRes.EnsureSuccessStatusCode();
        var alphaJson = await alphaRes.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(alphaJson))
        {
            var items = doc.RootElement.GetProperty("items");
            Assert.Equal(1, items.GetArrayLength());
            Assert.Equal(sidA, items[0].GetProperty("sessionId").GetString());
            Assert.Equal("alpha", items[0].GetProperty("agentProfileId").GetString());
        }

        var allRes = await _client.GetAsync("/api/chat-sessions?skip=0&take=10");
        allRes.EnsureSuccessStatusCode();
        var allJson = await allRes.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(allJson))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("items").GetArrayLength());
        }
    }
}
