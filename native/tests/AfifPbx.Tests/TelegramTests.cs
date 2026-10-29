using System.Net;
using System.Text;
using AfifPbx.Data;
using AfifPbx.Remote;
using Xunit;

namespace AfifPbx.Tests;

/// <summary>Pretends to be api.telegram.org.</summary>
internal sealed class FakeTelegram : HttpMessageHandler
{
    public readonly List<(string Path, Dictionary<string, string> Form)> Sent = new();
    public bool Down;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (Down) throw new HttpRequestException("network down");
        var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));
        lock (Sent) Sent.Add((req.RequestUri!.AbsolutePath, form));
        if (!req.RequestUri.AbsolutePath.StartsWith("/bot123456789:"))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"ok\":false,\"description\":\"Unauthorized\"}", Encoding.UTF8, "application/json") };
        if (form.GetValueOrDefault("chat_id") == "999")
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"ok\":false,\"description\":\"Bad Request: chat not found\"}", Encoding.UTF8, "application/json") };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") };
    }
}

public class TelegramTests
{
    private const string Token = "123456789:AAFakeTokenForTestsOnly_abcdefghijklmnop";

    private static (TelegramForwarder tg, FakeTelegram fake, string dir) Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "afif-tg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var fake = new FakeTelegram();
        var tg = new TelegramForwarder(dir, handler: fake) { ApiBase = "https://telegram.test" };
        return (tg, fake, dir);
    }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(100); }
        return cond();
    }

    [Fact]
    public async Task Forwards_sms_and_missed_gsm_calls_but_not_answered_or_internal_calls()
    {
        var (tg, fake, dir) = Make();
        using (tg)
        {
            tg.Configure(true, Token, new[] { "1655852624" }, true, true);
            tg.Start();
            tg.Sms("+962791112233", "مرحبا AFIF", DateTime.UtcNow);
            tg.Call(new CallRecord(0, DateTime.UtcNow, null, DateTime.UtcNow, "0791112233", "", "600", "inbound", "NO ANSWER", 0, ""));
            tg.Call(new CallRecord(0, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, "0791112233", "", "600", "inbound", "ANSWERED", 20, ""));
            tg.Call(new CallRecord(0, DateTime.UtcNow, null, DateTime.UtcNow, "100", "Afif", "101", "internal", "NO ANSWER", 0, ""));
            Assert.True(await Eventually(() => { lock (fake.Sent) return fake.Sent.Count >= 2; }));
            await Task.Delay(500);
            lock (fake.Sent)
            {
                Assert.Equal(2, fake.Sent.Count);
                Assert.All(fake.Sent, s => Assert.Equal("1655852624", s.Form["chat_id"]));
                Assert.Contains("مرحبا AFIF", fake.Sent[0].Form["text"]);
                Assert.Contains("+962791112233", fake.Sent[0].Form["text"]);
                Assert.Contains("Missed call", fake.Sent[1].Form["text"]);
            }
            // the token is stored encrypted, never in plain text
            Assert.DoesNotContain(Token, File.ReadAllText(Path.Combine(dir, "telegram.json")));
        }
    }

    [Fact]
    public async Task Keeps_messages_while_offline_and_sends_them_later()
    {
        var (tg, fake, _) = Make();
        using (tg)
        {
            fake.Down = true;
            tg.Configure(true, Token, new[] { "1655852624" }, true, true);
            tg.Start();
            tg.Sms("0791112233", "one", DateTime.UtcNow);
            tg.Sms("0791112233", "two", DateTime.UtcNow);
            Assert.True(await Eventually(() => tg.Status().LastError.Length > 0));
            Assert.Equal(2, tg.Status().Queued);
            fake.Down = false;
            tg.Configure(true, null, new[] { "1655852624" }, true, true); // wakes the sender
            Assert.True(await Eventually(() => tg.Status().Queued == 0, 15000));
            lock (fake.Sent) Assert.Equal(new[] { "one", "two" }, fake.Sent.Select(s => s.Form["text"].Split("\n\n").Last()));
        }
    }

    [Fact]
    public async Task Test_message_explains_a_wrong_chat_id()
    {
        var (tg, _, _) = Make();
        using (tg)
        {
            tg.Configure(true, Token, new[] { "999" }, true, true);
            var (ok, msg) = await tg.TestAsync();
            Assert.False(ok);
            Assert.Contains("press Start", msg);
            tg.Configure(true, null, new[] { "1655852624" }, true, true);
            Assert.True((await tg.TestAsync()).Ok);
        }
    }

    [Fact]
    public void Nothing_is_sent_when_switched_off()
    {
        var (tg, _, _) = Make();
        using (tg)
        {
            tg.Sms("0791112233", "x", DateTime.UtcNow);
            Assert.Equal(0, tg.Status().Queued);
        }
    }
}

public class MessageTextTests
{
    [Fact]
    public void Unwraps_linphone_cpim()
    {
        var body = "From: <sip:100@pbx>\r\nTo: <sip:0791112233@pbx>\r\nNS: imdn <urn:ietf:params:imdn>\r\nimdn.Message-ID: x\r\n\r\n" +
                   "Content-Type: text/plain;charset=UTF-8\r\nContent-Length: 10\r\n\r\nمرحبا\r\n";
        var (type, text) = AfifPbx.Sip.SipServer.MessageText("Message/CPIM", body);
        Assert.StartsWith("text/plain", type);
        Assert.Equal("مرحبا", text);
        var (t2, _) = AfifPbx.Sip.SipServer.MessageText("message/cpim",
            "From: <sip:a@b>\r\n\r\nContent-Type: message/imdn+xml\r\n\r\n<imdn/>");
        Assert.Equal("message/imdn+xml", t2);
        Assert.Equal(("text/plain;charset=utf-8", "hi"), AfifPbx.Sip.SipServer.MessageText("text/plain;charset=UTF-8", "hi"));
    }
}
