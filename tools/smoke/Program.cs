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
var documentsUrl = Env("SMOKE_DOCUMENTS_URL", "http://localhost:5107");
var notificationsUrl = Env("SMOKE_NOTIFICATIONS_URL", "http://localhost:5108");
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
    Topics.Lifecycle, Topics.CurrentWinner, Topics.BidsRejected,
    Topics.ParticipantPayments, Topics.Settlements
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

    // Real files, through the document service, and the ids it returns.
    //
    // Uploaded before being attached, in that order, because the id auction-admin
    // records has to be an id that resolves — the other order leaves an auction
    // pointing at a document that does not exist and looking complete.
    var adminDocs = new Caller(http, documentsUrl, adminToken, "admin");

    var bookletDoc = await adminDocs.UploadAsync(
        "/documents", "كراسة الشروط.pdf", BookletPdf(), "application/pdf", "Restricted");
    var coverDoc = await adminDocs.UploadAsync(
        "/documents", "cover.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00], "image/jpeg", "Public");

    var bookletDocId = bookletDoc.GetProperty("id").GetGuid();
    var coverDocId = coverDoc.GetProperty("id").GetGuid();

    await admin.PostAsync($"/auctions/{auctionId}/booklet", new { documentId = bookletDocId });
    await admin.PostAsync($"/auctions/{auctionId}/cover-image", new { documentId = coverDocId });

    // The Arabic filename came back intact, which is not a formality: S3 user
    // metadata is ASCII-only, so this is the case that turns a booklet's name into
    // question marks.
    if (bookletDoc.GetProperty("fileName").GetString() == "كراسة الشروط.pdf")
        n.Step("booklet (كراسة الشروط) and cover uploaded and attached",
            $"sha256 {bookletDoc.GetProperty("sha256").GetString()?[..12]}…");
    else
        n.Fail("booklet (كراسة الشروط) and cover uploaded and attached",
            $"the filename came back as {bookletDoc.GetProperty("fileName").GetString()}");

    // The cover is Public: a citizen reads the catalogue before registering, so it
    // has to open with no token at all.
    var anonDocs = new Caller(http, documentsUrl, "", "anonymous");
    var (coverStatus, _) = await anonDocs.TryGetAsync($"/documents/{coverDocId}");

    if (coverStatus == System.Net.HttpStatusCode.OK)
        n.Step("the cover image is public", "no token, as the catalogue needs");
    else
        n.Fail("the cover image is public", $"got {(int)coverStatus}");

    // The booklet is Restricted: no role opens it, not even the administrator who
    // uploaded it. Only a grant, and only from the service that knows who paid.
    var (bookletStatus, _) = await adminDocs.TryGetAsync($"/documents/{bookletDocId}");
    var (anonBooklet, _) = await anonDocs.TryGetAsync($"/documents/{bookletDocId}");

    if (bookletStatus == System.Net.HttpStatusCode.NotFound
        && anonBooklet == System.Net.HttpStatusCode.NotFound)
        n.Step("the booklet is not readable by role", "404 — not even for its uploader");
    else
        n.Fail("the booklet is not readable by role",
            $"admin got {(int)bookletStatus}, anonymous got {(int)anonBooklet}");

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
        var strong = bidderId == sara ? saraStrong : khalidStrong;

        await who.PostAsync($"/auctions/{auctionId}/subscriptions", new { bidderId });
        await QualifyAsync(who, strong, auctionId, bidderId);

        var state = await who.GetAsync(sub);
        var stage = state.GetProperty("status").GetString();

        if (stage != "Eligible")
        {
            n.Fail($"{who.Who} is eligible to bid", $"stage is {stage}");
            continue;
        }

        // Asserted on the topic rather than on the subscription, for two reasons.
        //
        // The gateway's reference is not in the API response and should not be: a
        // payment reference is the bidder's own and the endpoint is readable by
        // administrators too. And the topic is stronger evidence anyway — it is
        // where the money actually was recorded, and it proves the settlement
        // crossed Kafka rather than being produced inside one process.
        //
        // This is the hole the payment service closed. Before it, both endpoints
        // took a reference the caller invented, so this walk-through qualified two
        // bidders for a state land auction without a riyal moving, and reported it
        // as a pass.
        foreach (var purpose in new[] { "Booklet", "Deposit" })
        {
            var settlement = await watcher.WaitForAsync(
                Topics.Settlements, "PaymentSettled", auctionId,
                p => p.Contains($"\"purpose\":\"{purpose}\"")
                     && p.Contains(bidderId.ToString()));

            if (!settlement.Contains("\"outcome\":\"Charged\""))
                n.Fail($"{purpose.ToLowerInvariant()} charged for {who.Who}", settlement);
        }

        n.Step($"{who.Who} is eligible to bid",
            $"both charges settled on {Topics.Settlements}");
    }

    await watcher.WaitForAsync(
        Topics.Participants, "", auctionId, p => p.Contains(sara.ToString()));
    n.Step($"eligibility published to {Topics.Participants}", "this is how the catcher learns");

    // كراسة الشروط, which sara has now paid for.
    //
    // The document is Restricted, so no role opens it — the step above proved that
    // even its uploader gets a 404. The only way in is a grant, and the participant
    // service mints one for exactly one reason: the booklet fee settled.
    var saraGrant = await saraParticipant.GetAsync(
        $"/auctions/{auctionId}/subscriptions/{sara}/booklet-grant");

    var grantedDoc = saraGrant.GetProperty("documentId").GetGuid();
    var grant = saraGrant.GetProperty("grant").GetString()!;

    var saraDocs = new Caller(http, documentsUrl, saraToken, "sara");
    var (opened, bookletBody) = await saraDocs.TryGetAsync(
        $"/documents/{grantedDoc}?grant={Uri.EscapeDataString(grant)}");

    if (opened == System.Net.HttpStatusCode.OK && bookletBody.Contains("كراسة الشروط"))
        n.Step("sara reads the booklet she paid for", "a grant, not a role");
    else
        n.Fail("sara reads the booklet she paid for", $"got {(int)opened}");

    // The same grant in khalid's hands. It names sara in the part that is signed,
    // so a forwarded link is no use to whoever it reaches.
    var khalidDocs = new Caller(http, documentsUrl, khalidToken, "khalid");
    var (borrowed, _) = await khalidDocs.TryGetAsync(
        $"/documents/{grantedDoc}?grant={Uri.EscapeDataString(grant)}");

    if (borrowed == System.Net.HttpStatusCode.NotFound)
        n.Step("a forwarded grant is useless to anyone else", "404 — the subject is signed in");
    else
        n.Fail("a forwarded grant is useless to anyone else", $"got {(int)borrowed}");

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
    var khalidLastBidId = Guid.Empty;
    var saraLastOffset = -1L;
    var saraLastReceiptSignature = "";
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
        if (bidder == khalid) khalidLastBidId = clientBidId;
        if (bidder == sara)
        {
            saraLastBidId = clientBidId;

            // Kept so the certificate can be checked against the receipt handed
            // over at the time, which is the only thing that makes a receipt saved
            // on the day worth anything later.
            var receipt = JsonDocument.Parse(bidBody).RootElement;
            saraLastOffset = receipt.GetProperty("offset").GetInt64();
            saraLastReceiptSignature = receipt.GetProperty("signature").GetString() ?? "";
        }
    }

    // Reported on what happened, not unconditionally: this step used to claim five
    // accepted bids even when the loop had broken on a refusal.
    if (accepted == ladder.Length)
        n.Step("five accepted bids",
            $"202 each — recorded, not yet judged; last {(opening + 4 * increment) / 100:N0} SAR");
    else
        n.Fail("five accepted bids", $"only {accepted} of {ladder.Length} were accepted");

    // --- the certificate (شهادة مزايدة) ------------------------------------
    //
    // Issued from the append-only log, not from what the caller presents: the
    // service seeks to the offset, reads the frame back and reports what is in the
    // record. That is why it can be asked for months later from a saved receipt.
    var certificate = await saraCatcher.GetAsync(
        $"/auctions/{auctionId}/bids/{saraLastOffset}/certificate");

    if (certificate.GetProperty("signature").GetString() == saraLastReceiptSignature
        && certificate.GetProperty("amountMinorUnits").GetInt64() == opening + 4 * increment
        && certificate.GetProperty("bidderId").GetGuid() == sara
        && certificate.GetProperty("channel").GetString() == "Online")
        n.Step("sara's bid has a certificate that matches her receipt",
            certificate.GetProperty("reference").GetString() ?? "");
    else
        n.Fail("sara's bid has a certificate that matches her receipt", certificate.ToString());

    // Another bidder's certificate carries their amount and their identity.
    var (othersStatus, _) = await khalidCatcher.TryGetAsync(
        $"/auctions/{auctionId}/bids/{saraLastOffset}/certificate");
    if (othersStatus == System.Net.HttpStatusCode.Forbidden)
        n.Step("khalid cannot fetch sara's certificate", "403");
    else
        n.Fail("khalid cannot fetch sara's certificate", $"got {(int)othersStatus}");

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
        // Accepted into the log, to be judged. Which of the two refused it is a
        // race by design — the catcher's price view is eventually consistent — so
        // what is asserted here is only that it got in, and the winner check below
        // is what proves it never won. Waiting here for a specific BidRejected made
        // this step depend on which side of the race the run landed on.
        n.Step("a bid below the current price is refused",
            "202 at the catcher; the processor is the judge (D-03)");
        n.Note("the catcher screens on an eventually-consistent price; the processor decides");
    }
    else
    {
        n.Fail("a bid below the current price is refused", $"got {(int)staleStatus} {staleBody}");
    }

    // A bid the PROCESSOR is certain to refuse, whatever the catcher's price view
    // happens to be at this instant.
    //
    // The stale bid above cannot do this job: the catcher may refuse it at the edge,
    // in which case the processor never sees it and never publishes a verdict. The
    // push-channel assertion further down used to depend on that race and failed
    // whenever the catcher's price view had caught up — the test passing was a
    // matter of timing rather than of anything working.
    //
    // A repeated clientBidId is deterministic instead: the catcher does not dedupe,
    // and the processor refuses a repeat as DuplicateBidId before it looks at the
    // amount. Sent at a winning amount so the catcher's own floor cannot refuse it,
    // and it still cannot win, because the duplicate check comes first.
    var replayed = BidFrame.BuildClientFrame(
        auctionId, khalid, opening + 9 * increment, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        khalidLastBidId, 4, khalidSecret);

    var (replayStatus, replayBody) = await khalidCatcher.TryPostBytesAsync("/bids", replayed);
    if (replayStatus == System.Net.HttpStatusCode.Accepted)
        n.Step("a repeated bid id is accepted at the edge and refused by the processor",
            "idempotency belongs where the ladder is");
    else
        n.Fail("a repeated bid id is accepted at the edge and refused by the processor",
            $"got {(int)replayStatus} {replayBody}");

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
        e => e.GetProperty("clientBidId").GetGuid() == khalidLastBidId
             && !e.GetProperty("accepted").GetBoolean());

    if (verdict.GetProperty("reason").GetString() == "DuplicateBidId")
        n.Step("khalid is told why his bid was refused",
            verdict.GetProperty("reason").GetString() ?? "?");
    else
        n.Fail("khalid is told why his bid was refused", verdict.ToString());

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

    // -----------------------------------------------------------------------
    n.Section("10. المدفوعات — the end of this auction's money");
    // -----------------------------------------------------------------------
    //
    // Three different things happen to three sums, and none of them is a refund of
    // everything: the winner owes brokerage, the winner's own deposit is set against
    // the price rather than returned, and the loser's comes back. Reporting the
    // winner's deposit as a refund would overstate what went back to bidders by the
    // largest deposit in the auction.

    // مبلغ السعي, 2.5% of the price actually won at.
    var expectedBrokerage = (long)Math.Round(pendingAmount * 2.5m / 100m,
        MidpointRounding.AwayFromZero);

    var brokerage = await watcher.WaitForAsync(
        Topics.Settlements, "PaymentSettled", auctionId,
        p => p.Contains("\"purpose\":\"Brokerage\"") && p.Contains(sara.ToString()));

    if (brokerage.Contains($"\"amountMinorUnits\":{expectedBrokerage}")
        && brokerage.Contains("\"outcome\":\"Charged\""))
        n.Step("brokerage charged to the winner",
            $"{expectedBrokerage / 100m:N2} SAR — 2.5% of {pendingAmount / 100:N0}");
    else
        n.Fail("brokerage charged to the winner",
            $"wanted {expectedBrokerage} halala, got {brokerage}");

    // Settling the award is what releases the deposits — never at the gavel, while
    // the cascade can still reach a losing bidder (§8.3).
    await committee.PostAsync($"/auctions/{auctionId}/settle");

    var applied = await watcher.WaitForAsync(
        Topics.Settlements, "PaymentSettled", auctionId,
        p => p.Contains("\"outcome\":\"AppliedToPurchase\"") && p.Contains(sara.ToString()));
    n.Step("the winner's deposit applied to the price", "not refunded, and not reported as one");

    var refunded = await watcher.WaitForAsync(
        Topics.Settlements, "PaymentSettled", auctionId,
        p => p.Contains("\"outcome\":\"Refunded\"") && p.Contains(khalid.ToString()));

    if (refunded.Contains("RFND-"))
        n.Step("the losing bidder's deposit refunded", "against the original charge");
    else
        n.Fail("the losing bidder's deposit refunded", refunded);

    // The winner must not be refunded as well as credited: that would hand back a
    // hundred thousand riyals the municipality has already set against the price.
    var doubleRefund = await watcher.TryFindAsync(
        Topics.Settlements, "PaymentSettled",
        p => p.Contains(auctionId.ToString()) && p.Contains(sara.ToString())
             && p.Contains("\"outcome\":\"Refunded\""),
        TimeSpan.FromSeconds(3));

    if (doubleRefund is null)
        n.Step("the winner is not refunded as well", "one outcome per deposit");
    else
        n.Fail("the winner is not refunded as well", doubleRefund);

    _ = applied;

    // -----------------------------------------------------------------------
    n.Section("11. الإشعارات — what each bidder was actually told");
    // -----------------------------------------------------------------------
    //
    // The in-product inbox is the delivered channel: SMS needs an aggregator
    // contract that does not exist (P-7), and a bidder who closed the tab still
    // has to find out they were outbid.
    //
    // What is asserted here is mostly what a bidder was NOT told. Sara and khalid
    // took opposite sides of this auction, so the two inboxes should differ in
    // exactly the ways D-22 requires.

    var saraInbox = new Caller(http, notificationsUrl, saraToken, "sara");
    var khalidInbox = new Caller(http, notificationsUrl, khalidToken, "khalid");

    var saraNotices = await WaitForNoticesAsync(saraInbox, "Awarded");
    var khalidNotices = await WaitForNoticesAsync(khalidInbox, "Outbid");

    static string[] Kinds(JsonElement inbox) =>
        inbox.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("kind").GetString() ?? "")
            .ToArray();

    var saraKinds = Kinds(saraNotices);
    var khalidKinds = Kinds(khalidNotices);

    if (saraKinds.Contains("Eligible") && saraKinds.Contains("AuctionStarted"))
        n.Step("sara was told she is eligible and the auction opened",
            string.Join(", ", saraKinds.Distinct()));
    else
        n.Fail("sara was told she is eligible and the auction opened",
            string.Join(", ", saraKinds));

    // The winner is told she won. The loser is not told who did — that would undo
    // the masking by notification (D-22).
    if (khalidKinds.Contains("Outbid") && !khalidKinds.Contains("Awarded"))
        n.Step("khalid was told he was outbid, and not who won", "D-22 holds in the inbox");
    else
        n.Fail("khalid was told he was outbid, and not who won",
            string.Join(", ", khalidKinds));

    // And one bidder cannot read another's inbox: it is a list of which auctions
    // they are in and what they bid.
    var firstNotice = saraNotices.GetProperty("items")[0].GetProperty("id").GetGuid();
    var (borrowedRead, _) = await khalidInbox.TryPostAsync($"/notifications/{firstNotice}/read");

    if (borrowedRead == System.Net.HttpStatusCode.NotFound)
        n.Step("one bidder cannot read another's notifications", "404");
    else
        n.Fail("one bidder cannot read another's notifications", $"got {(int)borrowedRead}");

    // -----------------------------------------------------------------------
    n.Section("12. قاعة المزاد — the hall, where a clerk enters the bids");
    // -----------------------------------------------------------------------
    //
    // A second auction, run the other way (§29). Everything a bidder does is
    // unchanged — register, booklet, terms, deposit — and everything after the
    // hammer is unchanged too. What differs is the middle: a clerk types the bids
    // for people in the room, moves the end time, and brings the hammer down.

    var clerkToken = await Keycloak.TokenAsync(http, issuer, "admin-web", "clerk-user", password);
    var clerk = Keycloak.Subject(clerkToken);
    var clerkAdmin = new Caller(http, adminUrl, clerkToken, "clerk");
    var clerkCatcher = new Caller(http, catcherUrl, clerkToken, "clerk");

    var hallStarts = DateTimeOffset.UtcNow.AddSeconds(10);
    var hallId = (await admin.PostAsync("/auctions", new
    {
        createdByUserId = Keycloak.Subject(adminToken), nameAr = "قاعة", nameEn = "Hall"
    })).GetProperty("id").GetGuid();

    await admin.PutAsync($"/auctions/{hallId}", new
    {
        nameAr = $"مخطط السعيد — قاعة {DateTimeOffset.UtcNow:HHmmss}",
        nameEn = "Al-Saeed hall",
        channel = "Onsite",
        bidderVisibility = "Masked",
        startsAt = hallStarts,
        endsAt = hallStarts.AddSeconds(20),
        openingPriceMinorUnits = opening,
        reservePriceMinorUnits = reserve,
        minIncrementMinorUnits = increment,
        depositMinorUnits = 100_000_00,
        brokerageFeePercent = 2.5m,
        bookletPriceMinorUnits = 1_000_00,
        quietPeriodSeconds = (int?)null,
        maxExtensions = 3,
        phase = "Hall"
    });
    await admin.PostAsync($"/auctions/{hallId}/plots",
        new { deedNumber = "2020/1", areaSqm = 900.0m });
    var hallBooklet = await adminDocs.UploadAsync(
        "/documents", "كراسة القاعة.pdf", BookletPdf(), "application/pdf", "Restricted");

    await admin.PostAsync($"/auctions/{hallId}/booklet",
        new { documentId = hallBooklet.GetProperty("id").GetGuid() });
    await admin.PostAsync($"/auctions/{hallId}/cover-image", new { documentId = coverDocId });

    // The clerk goes on the floor before approval here, but the domain allows it
    // after as well — a clerk falls ill and a shift changes, and an auction cannot
    // be re-approved to deal with that.
    await admin.PutAsync($"/auctions/{hallId}/clerk", new { clerkUserId = clerk });
    n.Step("a clerk is put on the floor", "operator role, assigned to this auction only");

    await admin.PostAsync($"/auctions/{hallId}/submit");
    await committee.PostAsync($"/auctions/{hallId}/approve");

    // Nobody but the assigned clerk gets the key that signs for the room.
    var (keyToStranger, _) = await new Caller(http, adminUrl, adminToken, "auction-admin")
        .TryGetAsync($"/auctions/{hallId}/clerk-key");
    if (keyToStranger is System.Net.HttpStatusCode.Forbidden)
        n.Step("not even an administrator can take the clerk's key", "403");
    else
        n.Fail("not even an administrator can take the clerk's key", $"got {(int)keyToStranger}");

    var clerkKey = (await clerkAdmin.GetAsync($"/auctions/{hallId}/clerk-key"))
        .GetProperty("secretHex").GetString()!;
    var clerkSecret = Convert.FromHexString(clerkKey);

    // The participant service learns the auction's terms from auctions.upcoming, so
    // a subscription posted the instant after approval races the relay. Waited for
    // rather than slept through, because a fixed sleep is a flake with a timer.
    await watcher.WaitForAsync(Topics.Upcoming, "AuctionApproved", hallId);
    await WaitForTermsAsync(saraParticipant, hallId, sara);

    // The bidders qualify exactly as they did online.
    foreach (var (who, strong, bidderId) in new[]
             {
                 (saraParticipant, saraStrong, sara),
                 (khalidParticipant, khalidStrong, khalid)
             })
    {
        await who.PostAsync($"/auctions/{hallId}/subscriptions", new { bidderId });
        await QualifyAsync(who, strong, hallId, bidderId);
    }
    n.Step("both bidders qualify for the hall auction", "same booklet → terms → deposit");

    await WaitUntil(hallStarts, "the hall auction to open");

    // The catcher has to have both the auction and the clerk assignment before it
    // can accept anything; both arrive on compacted topics it replays.
    JsonElement hallBid = default;
    var hallAccepted = false;
    for (var attempt = 0; attempt < 40 && !hallAccepted; attempt++)
    {
        // At the reserve: below it the right outcome is an exhausted ladder and no
        // candidate at all, which would be a weaker thing for this walk-through to
        // prove than the committee being handed the hall's winner.
        var frame = BidFrame.BuildClientFrame(
            hallId, sara, reserve, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Guid.NewGuid(), RandomNumberGenerator.GetInt32(int.MaxValue), clerkSecret);

        var (hallStatus, body) = await clerkCatcher.TryPostBytesAsync("/bids", frame);
        if (hallStatus == System.Net.HttpStatusCode.Accepted)
        {
            hallBid = JsonDocument.Parse(body).RootElement.Clone();
            hallAccepted = true;
            break;
        }

        await Task.Delay(500);
    }

    if (hallAccepted)
        n.Step("the clerk enters a bid for a bidder in the room",
            $"signed with the clerk's key, recorded as sara's at offset {hallBid.GetProperty("offset").GetInt64()}");
    else
        n.Fail("the clerk enters a bid for a bidder in the room", "never accepted");

    // The bidder is in the room with a paddle, not a keyboard. She has a perfectly
    // valid signing key for this auction and it does not help her: the refusal is
    // about the channel, not about the signature.
    var saraHallSecret = Convert.FromHexString(
        (await saraParticipant.GetAsync(
            $"/auctions/{hallId}/subscriptions/{sara}/signing-key"))
        .GetProperty("secretHex").GetString()!);

    var saraHallFrame = BidFrame.BuildClientFrame(
        hallId, sara, reserve + increment, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Guid.NewGuid(), RandomNumberGenerator.GetInt32(int.MaxValue), saraHallSecret);
    var (saraDirect, _) = await saraCatcher.TryPostBytesAsync("/bids", saraHallFrame);
    if (saraDirect == System.Net.HttpStatusCode.Forbidden)
        n.Step("a bidder cannot bid directly in a hall auction", "403 — the room would not know");
    else
        n.Fail("a bidder cannot bid directly in a hall auction", $"got {(int)saraDirect}");

    // The record names both parties, which is the whole evidential story onsite —
    // and it is the bidder's certificate, not the clerk's. A clerk who entered a bid
    // is not thereby entitled to read it back: they are staff who typed it, and the
    // proof belongs to the person whose money is on it.
    var (clerkReadsIt, _) = await clerkCatcher.TryGetAsync(
        $"/auctions/{hallId}/bids/{hallBid.GetProperty("offset").GetInt64()}/certificate");
    if (clerkReadsIt == System.Net.HttpStatusCode.Forbidden)
        n.Step("the clerk cannot read back the certificate they created", "403 — it is the bidder's");
    else
        n.Fail("the clerk cannot read back the certificate they created", $"got {(int)clerkReadsIt}");

    var hallCertificate = await saraCatcher.GetAsync(
        $"/auctions/{hallId}/bids/{hallBid.GetProperty("offset").GetInt64()}/certificate");
    if (hallCertificate.GetProperty("channel").GetString() == "Onsite"
        && hallCertificate.GetProperty("enteredByUserId").GetGuid() == clerk
        && hallCertificate.GetProperty("bidderId").GetGuid() == sara)
        n.Step("the record names the bidder and the clerk who entered it",
            "an onsite bid claims less than an online one, and says so");
    else
        n.Fail("the record names the bidder and the clerk who entered it",
            hallCertificate.ToString());

    // The clerk's two verbs.
    await clerkAdmin.PostAsync($"/auctions/{hallId}/extend", new { seconds = 120 });
    n.Step("the clerk moves the end time", "the auctioneer decides, not a clock");

    var (strangerClose, _) = await new Caller(http, adminUrl, committeeToken, "award-committee")
        .TryPostAsync($"/auctions/{hallId}/close");
    if (strangerClose is System.Net.HttpStatusCode.Forbidden)
        n.Step("only this auction's clerk can bring the hammer down", "403");
    else
        n.Fail("only this auction's clerk can bring the hammer down", $"got {(int)strangerClose}");

    await clerkAdmin.PostAsync($"/auctions/{hallId}/close");
    n.Step("the hammer falls", "the only thing that ends a hall auction");

    var (hallWinner, hallAmount) = await WaitForCandidateAsync(committee, hallId);
    if (hallWinner == sara && hallAmount == reserve)
        n.Step("the committee is offered the hall's candidate",
            $"{hallAmount / 100:N0} SAR — the same الترسية workflow as online");
    else
        n.Fail("the committee is offered the hall's candidate", $"{hallWinner} at {hallAmount}");
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

/// <summary>
/// A few bytes that begin like a PDF.
///
/// Not a real document: what the walk-through proves about a booklet is that it
/// round-trips byte for byte with its name and its hash, and a 40MB file would
/// prove that no better and take longer.
/// </summary>
static byte[] BookletPdf() =>
    System.Text.Encoding.UTF8.GetBytes(
        "%PDF-1.7\n% كراسة الشروط — مخطط السعيد\n%%EOF\n");

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

/// <summary>
/// Walks one bidder from a bare subscription to eligible, waiting on the payment
/// service at each of the two steps that cost money.
///
/// Both of those steps are asynchronous and return 202: the request crosses the
/// outbox to <c>participants.payments</c>, the payment service charges the gateway
/// and answers on <c>payments.settlements</c>, and the participant service applies
/// that. Nothing here can be asserted immediately, which is the honest shape of
/// taking money and the reason this helper exists.
/// </summary>
static async Task QualifyAsync(Caller who, Caller strong, Guid auctionId, Guid bidderId)
{
    var sub = $"/auctions/{auctionId}/subscriptions/{bidderId}";

    // The booklet fee. Stepped up, because it is money.
    await strong.PostAsync($"{sub}/booklet");
    await WaitForStageAsync(who, sub, "BookletPurchased", "the booklet fee");

    await who.PostAsync($"{sub}/terms");
    await who.PostAsync($"{sub}/deposit-method", new { method = "Payment" });

    await strong.PostAsync($"{sub}/deposit");
    await WaitForStageAsync(who, sub, "Eligible", "the deposit");
}

/// <summary>
/// Waits for a subscription to reach a stage, and says which gateway refusal it saw
/// if it does not. A bare timeout here would send someone reading Kafka logs for
/// something the subscription was already recording.
/// </summary>
static async Task WaitForStageAsync(Caller who, string sub, string stage, string what)
{
    for (var i = 0; i < 120; i++)
    {
        var state = await who.GetAsync(sub);
        if (state.GetProperty("status").GetString() == stage) return;

        if (state.TryGetProperty("paymentFailureReason", out var reason)
            && reason.ValueKind == JsonValueKind.String)
        {
            throw new SmokeException(
                $"the gateway refused {what} for {who.Who}: {reason.GetString()}");
        }

        await Task.Delay(500);
    }

    throw new SmokeException(
        $"{what} never settled for {who.Who}; is the payment service running and "
        + "consuming " + Topics.ParticipantPayments + "?");
}

/// <summary>
/// Waits until a bidder's inbox holds a notice of the given kind.
///
/// Notifications are produced by a consumer following six topics, so they arrive
/// after the thing they are about rather than with it — which is the whole shape of
/// this channel and worth waiting for rather than sleeping through.
/// </summary>
static async Task<JsonElement> WaitForNoticesAsync(Caller who, string kind)
{
    for (var i = 0; i < 60; i++)
    {
        var inbox = await who.GetAsync("/notifications?take=50");

        if (inbox.GetProperty("items").EnumerateArray()
            .Any(item => item.GetProperty("kind").GetString() == kind))
            return inbox;

        await Task.Delay(500);
    }

    throw new SmokeException(
        $"{who.Who} was never told {kind}; is the notification service running?");
}

/// <summary>
/// Waits until the participant service has an auction's terms, by trying the thing
/// that needs them. A 404 here means the relay has not caught up yet, not that the
/// auction does not exist — the admin service approved it moments ago.
/// </summary>
static async Task WaitForTermsAsync(Caller who, Guid auctionId, Guid bidderId)
{
    for (var i = 0; i < 60; i++)
    {
        var (status, _) = await who.TryPostAsync(
            $"/auctions/{auctionId}/subscriptions", new { bidderId });

        // Created, or already there from a previous attempt: either way the terms
        // have arrived.
        if (status != System.Net.HttpStatusCode.NotFound) return;
        await Task.Delay(500);
    }

    throw new SmokeException(
        $"the participant service never learned auction {auctionId}; is it consuming "
        + Topics.Upcoming + "?");
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
