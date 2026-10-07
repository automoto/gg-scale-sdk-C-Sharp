using System;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GGScale.IntegrationTests
{
    /// <summary>Fresh players for tests that change or destroy the player they run as.</summary>
    internal static class Throwaway
    {
        public static async Task<GGScaleClient> PlayerAsync()
        {
            var c = new GGScaleClient(new GGScaleClientOptions { BaseUrl = ItFixture.BaseUrl, ApiKey = ItFixture.PublishableKey });
            await c.LoginAsync(new AnonymousAuth(c.Transport, ItFixture.PublishableKey));
            return c;
        }
    }

    [Collection("integration")]
    public class PartiesIntegrationTests
    {
        // A party of two fills a match at once. The game mode keeps these
        // entries away from the other matchmaker tests.
        private static readonly MatchRequest Settings = new MatchRequest
        {
            Mode = MatchMode.MatchOnly,
            MinCount = 2,
            MaxCount = 2,
            GameMode = "party-it-cs",
        };

        /// <summary>A fresh leader, and a fresh member who joined by code. The party is as the member sees it.</summary>
        private static async Task<(GGScaleClient Leader, GGScaleClient Member, Party Party)> PartyOfTwoAsync()
        {
            var leader = await Throwaway.PlayerAsync();
            var member = await Throwaway.PlayerAsync();
            var party = await leader.Parties.CreateAsync(Settings);
            var code = await leader.Parties.CreateCodeAsync(party.Id, party.Version, 1);
            party = await member.Parties.JoinByCodeAsync(code.Code);
            return (leader, member, party);
        }

        [Fact]
        public async Task Queue_matches_both_members_with_the_party_on_every_roster_entry()
        {
            var (leader, member, party) = await PartyOfTwoAsync();
            using var _l = leader;
            using var _m = member;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            party = await leader.Parties.SetReadyAsync(party.Id, party.Version, true, null, cts.Token);
            party = await member.Parties.SetReadyAsync(party.Id, party.Version, true, null, cts.Token);

            var leaderWait = leader.Parties.WaitForMatchAsync(party.Id, null, cts.Token);
            var memberWait = member.Parties.WaitForMatchAsync(party.Id, null, cts.Token);
            await leader.Parties.QueueAsync(party.Id, party.Version, null, cts.Token);
            var a = await leaderWait;
            var b = await memberWait;

            Assert.Equal(a.MatchId, b.MatchId);
            Assert.All(a.Users, u => Assert.Equal(party.Id, u.PartyId));
        }

        [Fact]
        public async Task Update_with_a_stale_version_conflicts()
        {
            var (leader, member, party) = await PartyOfTwoAsync();
            using var _l = leader;
            using var _m = member;

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => leader.Parties.UpdateAsync(party.Id, party.Version - 1, Settings));

            Assert.True(ex.IsStaleVersion, ex.Message);
        }

        [Fact]
        public async Task Kicked_member_loses_access()
        {
            var (leader, member, party) = await PartyOfTwoAsync();
            using var _l = leader;
            using var _m = member;

            await leader.Parties.KickAsync(party.Id, party.Version, member.Session!.PlayerId);
            var ex = await Assert.ThrowsAsync<GGScaleException>(() => member.Parties.GetAsync(party.Id));

            Assert.True(ex.IsNotFound, ex.Message);
        }

        [Fact]
        public async Task Member_leaves_the_party()
        {
            var (leader, member, party) = await PartyOfTwoAsync();
            using var _l = leader;
            using var _m = member;

            await member.Parties.LeaveAsync(party.Id, party.Version);
            var ex = await Assert.ThrowsAsync<GGScaleException>(() => member.Parties.CurrentAsync());

            Assert.True(ex.IsNotFound, ex.Message);
        }

        [Fact]
        public async Task Disband_removes_the_members()
        {
            var (leader, member, party) = await PartyOfTwoAsync();
            using var _l = leader;
            using var _m = member;

            await leader.Parties.DisbandAsync(party.Id, party.Version);
            var ex = await Assert.ThrowsAsync<GGScaleException>(() => member.Parties.GetAsync(party.Id));

            Assert.True(ex.IsNotFound, ex.Message);
        }

        [Fact]
        public async Task Wrong_codes_start_a_cooldown_with_retry_after()
        {
            using var c = await Throwaway.PlayerAsync();

            // The default player limit is 10 wrong codes; the call after it is 429.
            GGScaleException? last = null;
            for (var i = 0; i < 12; i++)
            {
                last = await Assert.ThrowsAsync<GGScaleException>(() => c.Parties.JoinByCodeAsync(new string('Z', 16)));
                if (last.IsCodeCooldown)
                {
                    break;
                }
            }

            Assert.True(last!.IsCodeCooldown && last.RetryAfter > TimeSpan.Zero, last.Message);
        }
    }

    [Collection("integration")]
    public class RealtimeTicketIntegrationTests
    {
        [Fact]
        public async Task Ticket_works_once()
        {
            using var c = await Throwaway.PlayerAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ticket = await c.Realtime.CreateTicketAsync(cts.Token);
            var url = GGScaleClient.TicketUri(new Uri(ItFixture.BaseUrl.Replace("http", "ws", StringComparison.Ordinal) + "/v1/ws"), ticket.Ticket);

            using (var first = new ClientWebSocket())
            {
                await first.ConnectAsync(url, cts.Token);
                await first.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
            }
            using var second = new ClientWebSocket();
            second.Options.CollectHttpResponseDetails = true;
            await Assert.ThrowsAsync<WebSocketException>(() => second.ConnectAsync(url, cts.Token));

            Assert.Equal(HttpStatusCode.Unauthorized, second.HttpStatusCode);
        }
    }

    [Collection("integration")]
    public class DeleteIntegrationTests
    {
        private const string Password = "correct-horse-battery-staple";

        [Fact]
        public async Task Request_delete_then_cancel_with_credentials()
        {
            using var c = await Throwaway.PlayerAsync();
            var email = "delete-" + Guid.NewGuid().ToString("N") + "@example.com";
            await c.Auth.LinkEmailAsync(email, Password);

            var pending = await c.Auth.RequestDeleteAsync(Password);
            Assert.True(pending.ScheduledPurgeAt > pending.DeleteRequestedAt, "the purge is a grace period out");
            Assert.Null(c.Session);

            await c.Auth.CancelDeleteAsync(email, Password);
            var again = await Assert.ThrowsAsync<GGScaleException>(() => c.Auth.CancelDeleteAsync(email, Password));

            Assert.True(again.IsNotFound, "no deletion is pending after the cancel: " + again.Message);
        }

        [Fact]
        public async Task Request_delete_revokes_the_anonymous_session()
        {
            using var c = await Throwaway.PlayerAsync();
            var revoked = c.Session!.RefreshToken;

            await c.Auth.RequestDeleteAsync(null);
            var ex = await Assert.ThrowsAsync<GGScaleException>(() => c.Auth.RefreshAsync(revoked));

            Assert.True(ex.IsUnauthorized, ex.Message);
        }
    }
}
