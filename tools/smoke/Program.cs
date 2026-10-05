using System.Security.Cryptography;
using System.Text.Json;
using EAuction.Core;
using EAuction.Smoke;

// ---------------------------------------------------------------------------
// End-to-end smoke test.
//
// Drives the four services through one complete auction, against real Keycloak,
// real Kafka and real Postgres. Nothing is stubbed and nothing is seeded behind
// the services' backs: every piece of state arrives the way it would in
// deployment, which is the only way this test can catch the wiring faults that
// unit tests cannot see.
//
// Run it with tools/smoke/run-smoke.sh, which brings the dependencies and the
// services up first.
// ---------------------------------------------------------------------------

var issuer = Env("SMOKE_ISSUER", "http://localhost:8080/realms/eauction");
var adminUrl = Env("SMOKE_ADMIN_URL", "http://localhost:5101");
var participantUrl = Env("SMOKE_PARTICIPANT_URL", "http://localhost:5102");
var catcherUrl = Env("SMOKE_CATCHER_URL", "http://localhost:5103");
var bffUrl = Env("SMOKE_BFF_URL", "http://localhost:5105");
var bootstrap = Env("SMOKE_KAFKA", "127.0.0.1:9092");
var password = Env("SMOKE_PASSWORD", "dev-only-password");

var n = new Narrator();
using var http = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30),
    // The proxy in this environment does not serve localhost.
    DefaultRequestVersion = new Version(1, 1)
};

// A separate client for the event streams, with no timeout.
//
// HttpClient.Timeout covers the whole operation including reading the body, even
// with ResponseHeadersRead — so the ordinary 30-second timeout above severs a
// long-lived SSE stream at exactly 30 seconds, which looks like the server hanging
// up. Any .NET client of this channel needs the same treatment; a browser's fetch
// has no default timeout and does not.
using var streams = new HttpClient
{
    Timeout = Timeout.InfiniteTimeSpan,
    DefaultRequestVersion = new Version(1, 1)
};

using var watcher = new TopicWatcher(bootstrap, new[]
{
    Topics.Upcoming, Topics.Sealed, Topics.Participants,
    Topics.Lifecycle, Topics.CurrentWinner, Topics.BidsRejected
});

try
{
    // -----------------------------------------------------------------------
    n.Section("1. Identity — real Keycloak, real tokens");
    // -----------------------------------------------------------------------

    // admin-user has no second factor, so no OTP. The other three do, and a
    // password grant for a user who has one must supply it even though the
    // resulting token is still level 1.
    var otp = Keycloak.DevTotpSecret;
    var adminToken = await Keycloak.TokenAsync(http, issuer, "admin-web", "admin-user", password);
    var committeeToken = await Keycloak.TokenAsync(
        http, issuer, "admin-web", "committee-user", password, otp);
    var saraToken = await Keycloak.TokenAsync(
        http, issuer, "bidder-web", "sara", password, otp);
    var khalidToken = await Keycloak.TokenAsync(
        http, issuer, "bidder-web", "khalid", password, otp);

    // Stepped-up tokens, minted by tools/smoke/stepup-token.mjs through a real
    // browser login. Keycloak only issues a second factor through the
    // authorization-code flow, so these cannot come from a password grant.
    var saraStepUp = Env("SMOKE_SARA_STEPUP", "");
    var khalidStepUp = Env("SMOKE_KHALID_STEPUP", "");
    var committeeStepUp = Env("SMOKE_COMMITTEE_STEPUP", "");

    if (saraStepUp.Length == 0 || khalidStepUp.Length == 0 || committeeStepUp.Length == 0)
        throw new SmokeException(
            "No stepped-up tokens. Run through tools/smoke/run-smoke.sh, which mints "
            + "them; the endpoints that move money cannot be reached without one.");

    var sara = Keycloak.Subject(saraToken);
    var khalid = Keycloak.Subject(khalidToken);
    var committeeUser = Keycloak.Subject(committeeToken);

    n.Step("four tokens issued by Keycloak", $"realm {issuer.Split('/').Last()}");
    n.Note($"sara   = {sara}");
    n.Note($"khalid = {khalid}");
    n.Note("the `sub` claim IS the bidder id and the signing-key input (D-18)");

    var admin = new Caller(http, adminUrl, adminToken, "auction-admin");
    var committee = new Caller(http, adminUrl, committeeToken, "award-committee");
    var saraAdminApi = new Caller(http, adminUrl, saraToken, "sara");

    // A bidder's token must not reach the admin service at all.
    var (status, _) = await saraAdminApi.TryPostAsync("/auctions",
        new { createdByUserId = sara, nameAr = "x", nameEn = "x" });
    if (status == System.Net.HttpStatusCode.Forbidden)
        n.Step("a bidder token is refused by auction-admin", "403");
    else
        n.Fail("a bidder token is refused by auction-admin", $"got {(int)status}, wanted 403");

    // -----------------------------------------------------------------------
    n.Section("2. Prepare the auction (workflow 1)");
    // -----------------------------------------------------------------------

    var adminUser = Keycloak.Subject(adminToken);
    var created = await admin.PostAsync("/auctions", new
    {
        createdByUserId = adminUser,
        nameAr = "مخطط السعيد — المرحلة الأولى",
        nameEn = "Al-Saeed plan — phase one"
    });
    var auctionId = created.GetProperty("id").GetGuid();
    n.Step("auction created as a draft", $"{auctionId}");

    // Three plots sold as one indivisible package: bidding is on the auction, not
    // on any plot in it.
    foreach (var (deed, area) in new[] { ("1010/5", 812.5m), ("1010/6", 940.0m), ("1010/7", 756.25m) })
        await admin.PostAsync($"/auctions/{auctionId}/plots", new
        {
            deedNumber = deed, areaSqm = area,
            latitude = "21.5433", longitude = "39.1728",
            descriptionAr = $"قطعة رقم {deed}", descriptionEn = $"Plot {deed}"
        });
    n.Step("three plots added", "one indivisible package, priced as a whole");

    await admin.PostAsync($"/auctions/{auctionId}/booklet", new { documentId = Guid.NewGuid() });
    await admin.PostAsync($"/auctions/{auctionId}/cover-image", new { documentId = Guid.NewGuid() });
    n.Step("booklet (كراسة الشروط) and cover attached");

    // The auction must open and close inside this test's runtime.
    var startsAt = DateTimeOffset.UtcNow.AddSeconds(12);
    var endsAt = startsAt.AddSeconds(25);
    const long opening = 1_000_000_00;   // 1,000,000.00 SAR in halalas
    const long reserve = 1_200_000_00;
    const long increment = 50_000_00;

    await admin.PutAsync($"/auctions/{auctionId}", new
    {
        nameAr = "مخطط السعيد — المرحلة الأولى",
        nameEn = "Al-Saeed plan — phase one",
        channel = "Online",
        bidderVisibility = "Masked",   // D-22 by default; asserted end to end below
        startsAt, endsAt,
        openingPriceMinorUnits = opening,
        reservePriceMinorUnits = reserve,
        minIncrementMinorUnits = increment,
        depositMinorUnits = 100_000_00,
        brokerageFeePercent = 2.5m,
        bookletPriceMinorUnits = 1_000_00,
        quietPeriodSeconds = (int?)null,   // extension off, so the close time is predictable
        maxExtensions = 0,
        phase = "phase-1"
    });

    var validation = await admin.GetAsync($"/auctions/{auctionId}/validation");
    var problems = validation.GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToArray();
    if (problems.Length == 0) n.Step("validation clean", "opens in 12s, closes 25s later");
    else n.Fail("validation clean", string.Join("; ", problems));

    // -----------------------------------------------------------------------
    n.Section("3. Approval, and the two topics it writes");
    // -----------------------------------------------------------------------

    await admin.PostAsync($"/auctions/{auctionId}/submit");
    n.Step("submitted for review", "an auction-admin can prepare but not approve");

    var (approveStatus, _) = await admin.TryPostAsync($"/auctions/{auctionId}/approve");
    if (approveStatus == System.Net.HttpStatusCode.Forbidden)
        n.Step("auction-admin cannot approve their own auction", "403 — separation of duties");
    else
        n.Fail("auction-admin cannot approve their own auction", $"got {(int)approveStatus}, wanted 403");

    await committee.PostAsync($"/auctions/{auctionId}/approve");
    n.Step("approved by the award committee");

    // Debezium or the in-process relay carries the outbox to Kafka.
    var upcoming = await watcher.WaitForAsync(Topics.Upcoming, "AuctionApproved", auctionId);
    n.Step($"AuctionApproved on {Topics.Upcoming}", "via the transactional outbox");

    if (!upcoming.Contains("eserve", StringComparison.OrdinalIgnoreCase))
        n.Step("the reserve price is NOT on the public topic", "D-23 holds");
    else
        n.Fail("the reserve price is NOT on the public topic",
            "AuctionApproved carries the reserve — it must only be on auctions.sealed");

    var sealedPayload = await watcher.WaitForAsync(Topics.Sealed, "AuctionReserveSet", auctionId);
    if (sealedPayload.Contains(reserve.ToString()))
        n.Step($"AuctionReserveSet on {Topics.Sealed}", $"{reserve / 100:N0} SAR, ACL-restricted");
    else
        n.Fail($"AuctionReserveSet on {Topics.Sealed}", $"payload lacks the reserve: {sealedPayload}");

    // -----------------------------------------------------------------------
    n.Section("4. Register and qualify two bidders");
    // -----------------------------------------------------------------------

    // The gate, before crossing it. An ordinary sign-in — which is what a password
    // grant is, and what the rest of this walk-through uses — must not be able to
    // bind a national identity to an account.
    var (kycStatus, kycBody) = await new Caller(http, participantUrl, saraToken, "sara")
        .TryPostAsync("/bidders/register");

    if (kycStatus == System.Net.HttpStatusCode.Forbidden && kycBody.Contains("StepUpRequired"))
        n.Step("registration is refused without a second factor", "403 StepUpRequired");
    else
        n.Fail("registration is refused without a second factor",
            $"got {(int)kycStatus} {kycBody}");

    var saraParticipant = new Caller(http, participantUrl, saraToken, "sara");
    var khalidParticipant = new Caller(http, participantUrl, khalidToken, "khalid");

    // Registration and the deposit go through the stepped-up tokens; everything
    // else uses the ordinary ones, so the walk-through also demonstrates that the
    // gate is at the money and not at every click.
    var saraStrong = new Caller(http, participantUrl, saraStepUp, "sara (stepped up)");
    var khalidStrong = new Caller(http, participantUrl, khalidStepUp, "khalid (stepped up)");

    foreach (var (who, bidderId) in new[] { (saraStrong, sara), (khalidStrong, khalid) })
    {
        // No body: every field comes from the Nafath claims in the token.
        await who.PostAsync("/bidders/register");
        await who.PostAsync($"/bidders/{bidderId}/profile",
            new { phone = "+966500000001", email = $"{bidderId:N}"[..8] + "@example.sa" });
    }
    n.Step("both bidders registered with a second factor", "national_id never crosses the wire");

    foreach (var (who, bidderId) in new[] { (saraParticipant, sara), (khalidParticipant, khalid) })
    {
        var sub = $"/auctions/{auctionId}/subscriptions/{bidderId}";
        await who.PostAsync($"/auctions/{auctionId}/subscriptions", new { bidderId });
        await who.PostAsync($"{sub}/booklet", new { paymentRef = $"BKLT-{bidderId:N}"[..16] });
        await who.PostAsync($"{sub}/terms");
        await who.PostAsync($"{sub}/deposit-method", new { method = "Payment" });

        // The deposit, with the second factor.
        var strong = bidderId == sara ? saraStrong : khalidStrong;
        await strong.PostAsync($"{sub}/deposit", new { paymentRef = $"DEP-{bidderId:N}"[..16] });

        var state = await who.GetAsync(sub);
        var stage = state.GetProperty("status").GetString();
        if (stage == "Eligible") n.Step($"{who.Who} is eligible to bid", "booklet → terms → deposit");
        else n.Fail($"{who.Who} is eligible to bid", $"stage is {stage}");
    }

    await watcher.WaitForAsync(
        Topics.Participants, "", auctionId, p => p.Contains(sara.ToString()));
    n.Step($"eligibility published to {Topics.Participants}", "this is how the catcher learns");

    // -----------------------------------------------------------------------
    n.Section("5. The public catalogue — what a citizen may see");
    // -----------------------------------------------------------------------

    // Anonymous: a land auction is published before anyone registers, which is how
    // a citizen decides whether to buy the booklet at all.
    var anon = new Caller(http, bffUrl, "", "anonymous");

    var listed = await WaitForCatalogueAsync(anon, auctionId);
    if (listed.GetProperty("plotCount").GetInt32() == 3
        && listed.GetProperty("openingPriceMinorUnits").GetInt64() == opening)
        n.Step("the auction is in the public catalogue", "anonymous, no token");
    else
        n.Fail("the auction is in the public catalogue", listed.ToString());

    // The administrator's masking choice, carried on the public topic. A bidder
    // deciding whether to register is entitled to know whether their name will be
    // shown, so this is a field the read path has to publish, not one it may infer.
    if (listed.GetProperty("bidderVisibility").GetString() == "Masked")
        n.Step("the catalogue says whether bidders are named", "Masked — D-22 default");
    else
        n.Fail("the catalogue says whether bidders are named", listed.ToString());

    var detail = await anon.GetAsync($"/auctions/{auctionId}");
    var deeds = detail.GetProperty("plots").EnumerateArray()
        .Select(x => x.GetProperty("deedNumber").GetString()).ToArray();
    if (deeds.Length == 3 && deeds.Contains("1010/6"))
        n.Step("the plots are visible to a bidder", string.Join(", ", deeds));
    else
        n.Fail("the plots are visible to a bidder", $"got [{string.Join(", ", deeds)}]");

    // D-23 at the HTTP boundary, not just on the topic.
    var detailRaw = detail.ToString();
    if (!detailRaw.Contains("eserve", StringComparison.OrdinalIgnoreCase)
        && !detailRaw.Contains(reserve.ToString()))
        n.Step("the reserve price is not on the public read path", "D-23 holds end to end");
    else
        n.Fail("the reserve price is not on the public read path", "the detail response leaked it");

    // -----------------------------------------------------------------------
    n.Section("6. Signing keys — derived, never distributed");
    // -----------------------------------------------------------------------

    var saraKey = await saraParticipant.GetAsync(
        $"/auctions/{auctionId}/subscriptions/{sara}/signing-key");
    var khalidKey = await khalidParticipant.GetAsync(
        $"/auctions/{auctionId}/subscriptions/{khalid}/signing-key");

    var saraSecret = Convert.FromHexString(saraKey.GetProperty("secretHex").GetString()!);
    var khalidSecret = Convert.FromHexString(khalidKey.GetProperty("secretHex").GetString()!);

    if (!saraSecret.SequenceEqual(khalidSecret))
        n.Step("each bidder's key is their own", "HMAC(master, auctionId‖bidderId‖epoch)");
    else
        n.Fail("each bidder's key is their own", "the two bidders got the same secret");

    var (keyStatus, _) = await new Caller(http, participantUrl, khalidToken, "khalid")
        .TryPostAsync($"/auctions/{auctionId}/subscriptions/{sara}/rotate-key");
    if (keyStatus is System.Net.HttpStatusCode.Forbidden)
        n.Step("khalid cannot touch sara's key", "403");
    else
        n.Fail("khalid cannot touch sara's key", $"got {(int)keyStatus}, wanted 403");

    // -----------------------------------------------------------------------
    n.Section("7. Bidding — binary frames on the hot path");
    // -----------------------------------------------------------------------

    await WaitForReady(http, catcherUrl, n);
    await WaitUntil(startsAt, "the auction to open");

    // Subscribed before the first bid, so what follows is pushed rather than found
    // by a later read. Three viewers, because what each is told differs.
    using var saraStream = new EventStreamClient(
        streams, $"{bffUrl}/auctions/{auctionId}/stream", saraToken);
    using var khalidStream = new EventStreamClient(
        streams, $"{bffUrl}/auctions/{auctionId}/stream", khalidToken);
    using var anonStream = new EventStreamClient(
        streams, $"{bffUrl}/auctions/{auctionId}/stream", null);

    var snapshot = await saraStream.WaitForAsync("snapshot",
        e => e.GetProperty("auctionId").GetGuid() == auctionId);
    n.Step("the stream opens with a snapshot", "the truth as of now, then deltas");
    n.Note("no Last-Event-ID: a compacted read model has no history to replay");

    // Whatever the status is at this instant is the right answer: the processor may
    // not have ticked yet, and "the truth as of now" is exactly what a snapshot
    // promises. Asserting Live here would be asserting a race — and asserting that
    // it arrives as a later push is worse, because when the snapshot already says
    // Live there is no later push to wait for. What the push channel actually
    // delivers is asserted in section 8b, after the bids that cause it.
    n.Note($"snapshot status: {snapshot.GetProperty("status").GetString()}");

    var saraCatcher = new Caller(http, catcherUrl, saraToken, "sara");
    var khalidCatcher = new Caller(http, catcherUrl, khalidToken, "khalid");

    // Khalid signs a frame that names Sara as the bidder. His own key signs it
    // perfectly well; the token is what catches him.
    var impersonation = BidFrame.BuildClientFrame(
        auctionId, sara, opening + increment, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Guid.NewGuid(), RandomNumberGenerator.GetInt32(int.MaxValue), khalidSecret);
    var (impStatus, impBody) = await khalidCatcher.TryPostBytesAsync("/bids", impersonation);
    if (impStatus == System.Net.HttpStatusCode.Forbidden && impBody.Contains("BidderMismatch"))
        n.Step("khalid cannot bid as sara", "403 BidderMismatch — token vs frame");
    else
        n.Fail("khalid cannot bid as sara", $"got {(int)impStatus} {impBody}");

    // A frame signed with the wrong key.
    var forged = BidFrame.BuildClientFrame(
        auctionId, khalid, opening + increment, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Guid.NewGuid(), 1, saraSecret);
    var (forgedStatus, forgedBody) = await khalidCatcher.TryPostBytesAsync("/bids", forged);
    if (forgedStatus == System.Net.HttpStatusCode.Conflict && forgedBody.Contains("Signature"))
        n.Step("a frame signed with the wrong key is rejected", "409 InvalidSignature");
    else
        n.Fail("a frame signed with the wrong key is rejected", $"got {(int)forgedStatus} {forgedBody}");

    // The real bidding war. Sara opens, Khalid outbids, Sara takes it back.
    var ladder = new (Caller Who, byte[] Secret, Guid Bidder, long Amount)[]
    {
        (saraCatcher,   saraSecret,   sara,   opening),
        (khalidCatcher, khalidSecret, khalid, opening + increment),
        (saraCatcher,   saraSecret,   sara,   opening + 2 * increment),
        (khalidCatcher, khalidSecret, khalid, opening + 3 * increment),
        (saraCatcher,   saraSecret,   sara,   opening + 4 * increment)
    };

    // The id of the last bid, which is the one that should win. Kept so the stream
    // assertions can check that sara is told WHICH of her bids won rather than
    // inferring it from an amount.
    var saraLastBidId = Guid.Empty;
    var accepted = 0;

    foreach (var (who, secret, bidder, amount) in ladder)
    {
        var clientBidId = Guid.NewGuid();
        var frame = BidFrame.BuildClientFrame(
            auctionId, bidder, amount, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            clientBidId, RandomNumberGenerator.GetInt32(int.MaxValue), secret);

        var (bidStatus, bidBody) = await who.TryPostBytesAsync("/bids", frame);
        if (bidStatus != System.Net.HttpStatusCode.Accepted)
        {
            n.Fail($"{who.Who} bids {amount / 100:N0} SAR", $"got {(int)bidStatus} {bidBody}");
            break;
        }

        accepted++;
        if (bidder == sara) saraLastBidId = clientBidId;
    }

    // Reported on what happened, not unconditionally: this step used to claim five
    // accepted bids even when the loop had broken on a refusal.
    if (accepted == ladder.Length)
        n.Step("five accepted bids",
            $"202 each — recorded, not yet judged; last {(opening + 4 * increment) / 100:N0} SAR");
    else
        n.Fail("five accepted bids", $"only {accepted} of {ladder.Length} were accepted");

    // Below the OPENING price. The catcher knows the opening price from
    // auctions.upcoming the moment it is warm, so this is a local decision with no
    // dependence on the processor — and it is refused without one network call.
    var belowOpening = BidFrame.BuildClientFrame(
        auctionId, khalid, opening - increment, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Guid.NewGuid(), 2, khalidSecret);
    var (lowStatus, lowBody) = await khalidCatcher.TryPostBytesAsync("/bids", belowOpening);
    if (lowStatus == System.Net.HttpStatusCode.Conflict)
        n.Step("a bid below the opening price is refused", $"409 {Reason(lowBody)} — in the catcher");
    else
        n.Fail("a bid below the opening price is refused", $"got {(int)lowStatus} {lowBody}");

    // Below the CURRENT price but above the opening. The catcher's price view comes
    // from auctions.current-winner and is eventually consistent, so it MAY accept
    // this — by design. The catcher is a cheap filter; the processor is the judge
    // (D-03). Whichever of the two refuses it, the bid must never win.
    var staleBidId = Guid.NewGuid();
    var belowCurrent = BidFrame.BuildClientFrame(
        auctionId, khalid, opening + increment, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        staleBidId, 3, khalidSecret);
    var (staleStatus, staleBody) = await khalidCatcher.TryPostBytesAsync("/bids", belowCurrent);

    if (staleStatus == System.Net.HttpStatusCode.Conflict)
    {
        n.Step("a bid below the current price is refused", $"409 {Reason(staleBody)} — the catcher had the price");
    }
    else if (staleStatus == System.Net.HttpStatusCode.Accepted)
    {
        // Accepted into the log, then judged. The verdict is the processor's.
        await watcher.WaitForAsync(
            Topics.BidsRejected, "BidRejected", auctionId,
            p => p.Contains(staleBidId.ToString()));
        n.Step("a bid below the current price is refused", "202 at the catcher, BidRejected by the processor");
        n.Note("the catcher screens on an eventually-consistent price; the processor decides");
    }
    else
    {
        n.Fail("a bid below the current price is refused", $"got {(int)staleStatus} {staleBody}");
    }

    // -----------------------------------------------------------------------
    n.Section("8. The processor decides");
    // -----------------------------------------------------------------------

    await watcher.WaitForAsync(Topics.Lifecycle, "AuctionStarted", auctionId);
    n.Step("AuctionStarted", "the processor opened it, not a clock in the portal");

    var winnerPayload = await watcher.WaitForAsync(
        Topics.CurrentWinner, "CurrentWinner", auctionId,
        p => p.Contains((opening + 4 * increment).ToString()));
    var winner = JsonDocument.Parse(winnerPayload).RootElement;
    var leader = winner.GetProperty("leaderBidderId").GetGuid();
    var price = winner.GetProperty("priceMinorUnits").GetInt64();

    if (leader == sara && price == opening + 4 * increment)
        n.Step("current winner is sara at the top of the ladder", $"{price / 100:N0} SAR");
    else
        n.Fail("current winner is sara at the top of the ladder",
            $"got {leader} at {price / 100:N0} SAR");

    n.Note("the order came from Kafka partition offsets, not client timestamps (D-03)");

    // -----------------------------------------------------------------------
    n.Section("8b. The push channel — what each watcher was told");
    // -----------------------------------------------------------------------

    var pushedToSara = await saraStream.WaitForAsync("price",
        e => PriceIs(e, opening + 4 * increment));

    if (pushedToSara.GetProperty("leaderIsYou").GetBoolean())
        n.Step("sara is pushed the price and told she leads",
            $"{saraStream.CountOf("price")} delta(s), no polling");
    else
        n.Fail("sara is pushed the price and told she leads", pushedToSara.ToString());

    // The field that lets a bidder match the win to one of their own bids, instead
    // of inferring it from a price that happens to equal what they typed.
    var winningBidId = pushedToSara.GetProperty("yourWinningBidId");
    if (winningBidId.ValueKind == JsonValueKind.String
        && winningBidId.GetGuid() == saraLastBidId)
        n.Step("sara is told which of her bids won", $"{winningBidId.GetGuid()}");
    else
        n.Fail("sara is told which of her bids won", winningBidId.ToString());

    var pushedToKhalid = await khalidStream.WaitForAsync("price",
        e => PriceIs(e, opening + 4 * increment));

    var khalidSawLeader = pushedToKhalid.GetProperty("leaderIsYou").GetBoolean();
    var khalidLabel = pushedToKhalid.GetProperty("leaderLabel").GetString();
    var khalidSawWinningBid = pushedToKhalid.GetProperty("yourWinningBidId").ValueKind;

    if (!khalidSawLeader && khalidLabel is not null
        && khalidSawWinningBid == JsonValueKind.Null
        && !pushedToKhalid.ToString().Contains(sara.ToString())
        && !pushedToKhalid.ToString().Contains(saraLastBidId.ToString()))
        n.Step("khalid is pushed the price, masked", $"leader is {khalidLabel} — D-22");
    else
        n.Fail("khalid is pushed the price, masked", pushedToKhalid.ToString());

    var pushedToAnon = await anonStream.WaitForAsync("price",
        e => PriceIs(e, opening + 4 * increment));

    if (!pushedToAnon.GetProperty("leaderIsYou").GetBoolean()
        && pushedToAnon.GetProperty("yourWinningBidId").ValueKind == JsonValueKind.Null)
        n.Step("an anonymous watcher is pushed the price too", "open auction, masked identity");
    else
        n.Fail("an anonymous watcher is pushed the price too", pushedToAnon.ToString());

    // The verdict for the bid the processor refused, which until the fan-out existed
    // reached nobody: bids.rejected was written by the processor and read by nothing.
    var verdict = await khalidStream.WaitForAsync("verdict",
        e => e.GetProperty("clientBidId").GetGuid() == staleBidId);

    if (!verdict.GetProperty("accepted").GetBoolean())
        n.Step("khalid is told why his stale bid was refused",
            verdict.GetProperty("reason").GetString() ?? "?");
    else
        n.Fail("khalid is told why his stale bid was refused", verdict.ToString());

    if (saraStream.CountOf("verdict") == 0 && anonStream.CountOf("verdict") == 0)
        n.Step("the verdict reached nobody else", "a bidder's refusal is their own business");
    else
        n.Fail("the verdict reached nobody else",
            $"sara {saraStream.CountOf("verdict")}, anonymous {anonStream.CountOf("verdict")}");

    // The same verdict, as each party is allowed to see it.
    var saraPrice = await WaitForPriceAsync(
        new Caller(http, bffUrl, saraToken, "sara"), auctionId, opening + 4 * increment);
    if (saraPrice.GetProperty("leaderIsYou").GetBoolean())
        n.Step("sara is told she is leading", saraPrice.GetProperty("leaderLabel").GetString() ?? "");
    else
        n.Fail("sara is told she is leading", saraPrice.ToString());

    var khalidPrice = await new Caller(http, bffUrl, khalidToken, "khalid")
        .GetAsync($"/auctions/{auctionId}/price");
    var khalidSeesLeader = khalidPrice.GetProperty("leaderIsYou").GetBoolean();
    var label = khalidPrice.GetProperty("leaderLabel").GetString();

    if (!khalidSeesLeader && label is not null
        && !khalidPrice.ToString().Contains(sara.ToString()))
        n.Step("khalid sees the price but not who leads", $"leader is {label} — D-22");
    else
        n.Fail("khalid sees the price but not who leads", khalidPrice.ToString());

    var anonPrice = await anon.GetAsync($"/auctions/{auctionId}/price");
    if (!anonPrice.GetProperty("leaderIsYou").GetBoolean()
        && anonPrice.GetProperty("priceMinorUnits").GetInt64() == opening + 4 * increment)
        n.Step("an anonymous watcher sees the price", "open auction, masked identity");
    else
        n.Fail("an anonymous watcher sees the price", anonPrice.ToString());

    await WaitUntil(endsAt.AddSeconds(8), "the auction to close");
    await watcher.WaitForAsync(Topics.Lifecycle, "AuctionClosed", auctionId);
    n.Step("AuctionClosed", "at EndsAt + close grace");

    var offered = await watcher.WaitForAsync(Topics.Lifecycle, "CandidateOffered", auctionId);
    var candidate = JsonDocument.Parse(offered).RootElement.GetProperty("bidderId").GetGuid();
    if (candidate == sara)
        n.Step("sara offered to the committee as the candidate", "above the reserve");
    else
        n.Fail("sara offered to the committee as the candidate", $"got {candidate}");

    // -----------------------------------------------------------------------
    n.Section("9. Award (workflow 2)");
    // -----------------------------------------------------------------------

    // The processor's CandidateOffered reached auction-admin over auctions.lifecycle,
    // so the auction is waiting on the committee without the portal being told
    // anything by the processor directly.
    var (pendingBidder, pendingAmount) = await WaitForCandidateAsync(committee, auctionId);
    if (pendingBidder == sara && pendingAmount == opening + 4 * increment)
        n.Step("auction-admin is holding sara as the candidate",
            $"{pendingAmount / 100:N0} SAR, applied from auctions.lifecycle");
    else
        n.Fail("auction-admin is holding sara as the candidate",
            $"got {pendingBidder} at {pendingAmount / 100:N0} SAR");

    // The committee decides; the system never awards by itself.
    // The single most consequential act in the platform. Refused without a second
    // factor, which is asserted first so the gate is shown to hold.
    var (awardStatus, awardBody) = await committee
        .TryPostAsync($"/auctions/{auctionId}/award", new { committeeUserId = committeeUser });

    if (awardStatus == System.Net.HttpStatusCode.Forbidden && awardBody.Contains("StepUpRequired"))
        n.Step("the award is refused without a second factor", "403 StepUpRequired");
    else
        n.Fail("the award is refused without a second factor", $"got {(int)awardStatus} {awardBody}");

    var committeeStrong = new Caller(http, adminUrl, committeeStepUp, "committee (stepped up)");
    await committeeStrong.PostAsync(
        $"/auctions/{auctionId}/award", new { committeeUserId = committeeUser });

    var afterAward = await committee.GetAsync($"/auctions/{auctionId}");
    var award = afterAward.GetProperty("currentAward");
    if (award.ValueKind == JsonValueKind.Object
        && award.GetProperty("bidderId").GetGuid() == sara
        && afterAward.GetProperty("status").GetString() == "Awarded")
        n.Step("award confirmed by the committee",
            $"cascade step {award.GetProperty("cascadeStep").GetInt32()}, "
            + $"compliance by {award.GetProperty("complianceDeadline").GetDateTimeOffset():yyyy-MM-dd}");
    else
        n.Fail("award confirmed by the committee", afterAward.GetProperty("status").GetString() ?? "?");

    // Notifying before the letter exists is refused: the order is the contract.
    var (notifyEarly, _) = await committee.TryPostAsync($"/auctions/{auctionId}/award/notify");
    if (notifyEarly is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Conflict)
        n.Step("cannot notify before the letter is signed", $"{(int)notifyEarly}");
    else
        n.Fail("cannot notify before the letter is signed", $"got {(int)notifyEarly}, wanted 400/409");

    await committee.PostAsync($"/auctions/{auctionId}/award/letter", new { documentId = Guid.NewGuid() });
    await committee.PostAsync($"/auctions/{auctionId}/award/signed-letter", new { documentId = Guid.NewGuid() });
    await committee.PostAsync($"/auctions/{auctionId}/award/notify");
    n.Step("letter issued, signed, bidder notified", "in that order, enforced by the domain");

    // A bidder must not be able to drive the committee's workflow.
    var (bidderAward, _) = await new Caller(http, adminUrl, saraToken, "sara")
        .TryPostAsync($"/auctions/{auctionId}/award/notify");
    if (bidderAward == System.Net.HttpStatusCode.Forbidden)
        n.Step("the winner cannot drive their own award", "403");
    else
        n.Fail("the winner cannot drive their own award", $"got {(int)bidderAward}, wanted 403");
}
catch (SmokeException e)
{
    n.Fail("walk-through", e.Message);
}
catch (Exception e)
{
    n.Fail("walk-through", $"{e.GetType().Name}: {e.Message}");
}

return n.Summarise();

// ---------------------------------------------------------------------------

static string Env(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

/// <summary>
/// True when a pushed price equals the amount, tolerating the ones that do not.
///
/// A predicate is run against every event on the stream, including a push from the
/// auction going live, whose priceMinorUnits is null because no bid has been judged.
/// Reading it as a number threw and failed the walk-through on an event it simply
/// did not want.
/// </summary>
static bool PriceIs(JsonElement e, long amount) =>
    e.TryGetProperty("priceMinorUnits", out var price)
    && price.ValueKind == JsonValueKind.Number
    && price.GetInt64() == amount;

static string Reason(string body)
{
    try { return JsonDocument.Parse(body).RootElement.GetProperty("reason").GetString() ?? body; }
    catch { return body; }
}

static async Task WaitUntil(DateTimeOffset when, string what)
{
    var wait = when - DateTimeOffset.UtcNow;
    if (wait > TimeSpan.Zero)
    {
        Console.WriteLine($"      waiting {wait.TotalSeconds:0}s for {what}…");
        await Task.Delay(wait);
    }
}

static async Task WaitForReady(HttpClient http, string catcherUrl, Narrator n)
{
    // The catcher is not ready until it has replayed the control topics. A catcher
    // serving traffic with empty state can only reject it.
    for (var i = 0; i < 80; i++)
    {
        try
        {
            using var r = await http.GetAsync($"{catcherUrl}/health/ready");
            if (r.IsSuccessStatusCode)
            {
                n.Step("bid catcher is warm", "control topics replayed");
                return;
            }
        }
        catch (HttpRequestException) { /* still starting */ }
        await Task.Delay(500);
    }
    throw new SmokeException("the bid catcher never became ready");
}

static async Task<JsonElement> WaitForCatalogueAsync(Caller anon, Guid auctionId)
{
    // The BFF rebuilds from the compacted topics, so it is behind the approval by
    // however long the relay and the replay take.
    for (var i = 0; i < 90; i++)
    {
        var listing = await anon.GetAsync("/auctions");
        foreach (var item in listing.GetProperty("items").EnumerateArray())
            if (item.GetProperty("id").GetGuid() == auctionId)
                return item.Clone();
        await Task.Delay(500);
    }
    throw new SmokeException(
        "the auction never reached the public catalogue; is the query BFF consuming "
        + "auctions.upcoming?");
}

static async Task<JsonElement> WaitForPriceAsync(Caller who, Guid auctionId, long expected)
{
    for (var i = 0; i < 90; i++)
    {
        var price = await who.GetAsync($"/auctions/{auctionId}/price");
        if (price.TryGetProperty("priceMinorUnits", out var p)
            && p.ValueKind == JsonValueKind.Number && p.GetInt64() == expected)
            return price.Clone();
        await Task.Delay(500);
    }
    throw new SmokeException(
        $"the public price never reached {expected / 100:N0} SAR; is the query BFF "
        + "consuming auctions.current-winner?");
}

static async Task<(Guid Bidder, long Amount)> WaitForCandidateAsync(Caller committee, Guid auctionId)
{
    for (var i = 0; i < 60; i++)
    {
        var auction = await committee.GetAsync($"/auctions/{auctionId}");
        if (auction.TryGetProperty("pendingCandidateBidderId", out var pending)
            && pending.ValueKind == JsonValueKind.String)
            return (pending.GetGuid(),
                    auction.GetProperty("pendingCandidateAmountMinorUnits").GetInt64());
        await Task.Delay(500);
    }
    throw new SmokeException(
        "no candidate appeared on the auction; did CandidateOffered reach auction-admin "
        + "over auctions.lifecycle?");
}
