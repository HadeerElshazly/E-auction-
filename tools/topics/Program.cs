using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EAuction.Core;

// ---------------------------------------------------------------------------
// Provisions the control-plane topics.
//
// Deliberately a separate step, like the migrations: topic configuration is
// cluster state, it needs the right cleanup policy from the first record, and
// several service replicas creating topics concurrently is a race nobody needs.
//
//   dotnet EAuction.Topics.dll --bootstrap kafka:9092 [--replication 3] [--partitions 3]
//   dotnet EAuction.Topics.dll --bootstrap kafka:9092 --verify
//
// Auto-creation is NOT an acceptable substitute, and the broker should have
// auto.create.topics.enable=false. An auto-created topic gets cleanup.policy=delete,
// so the five compacted topics would work until their retention expired and then
// quietly lose the state the catcher and the processor rebuild from.
// ---------------------------------------------------------------------------

var argv = args;
string? Arg(string name)
{
    for (var i = 0; i < argv.Length - 1; i++)
        if (argv[i] == $"--{name}") return argv[i + 1];
    return null;
}
var has = (string name) => argv.Contains($"--{name}");

var bootstrap = Arg("bootstrap")
    ?? Environment.GetEnvironmentVariable("Kafka__BootstrapServers");
if (bootstrap is null)
{
    Console.Error.WriteLine("Pass --bootstrap host:port, or set Kafka__BootstrapServers.");
    return 2;
}

var replication = short.Parse(Arg("replication")
    ?? Environment.GetEnvironmentVariable("Kafka__ReplicationFactor") ?? "3");
var partitions = int.Parse(Arg("partitions") ?? "3");
var verifyOnly = has("verify");

// acks=all with a fixed min.insync.replicas=2 makes every produce fail on a
// single-broker cluster, so it follows the replication factor.
var minIsr = Math.Max(1, replication - 1);

using var admin = new AdminClientBuilder(
    new AdminClientConfig { BootstrapServers = bootstrap }).Build();

var metadata = admin.GetMetadata(TimeSpan.FromSeconds(15));
var existing = metadata.Topics.Where(t => t.Error.Code == ErrorCode.NoError)
    .Select(t => t.Topic).ToHashSet();

Console.WriteLine($"Cluster {bootstrap}: {metadata.Brokers.Count} broker(s), "
                  + $"replication {replication}, min.insync.replicas {minIsr}");

if (replication > metadata.Brokers.Count)
{
    Console.Error.WriteLine(
        $"Replication factor {replication} exceeds the {metadata.Brokers.Count} broker(s) "
        + "in this cluster. Topic creation would fail.");
    return 2;
}

var problems = new List<string>();

// --- verify -----------------------------------------------------------------

if (verifyOnly)
{
    var resources = ControlTopics.All
        .Where(t => existing.Contains(t.Name))
        .Select(t => new ConfigResource { Type = ResourceType.Topic, Name = t.Name })
        .ToList();

    var missing = ControlTopics.All.Where(t => !existing.Contains(t.Name)).ToList();
    foreach (var t in missing) problems.Add($"{t.Name}: does not exist");

    if (resources.Count > 0)
    {
        var configs = await admin.DescribeConfigsAsync(resources);
        foreach (var result in configs)
        {
            var topic = ControlTopics.All.First(t => t.Name == result.ConfigResource.Name);
            var want = Policy(topic.Shape);
            var got = result.Entries.TryGetValue("cleanup.policy", out var entry)
                ? entry.Value : "(unset)";

            if (got == want)
                Console.WriteLine($"  ok      {topic.Name,-28} cleanup.policy={got}");
            else
                problems.Add($"{topic.Name}: cleanup.policy is {got}, must be {want} — {topic.Why}");
        }
    }
}

// --- create -----------------------------------------------------------------

else
{
    var create = ControlTopics.All.Where(t => !existing.Contains(t.Name)).Select(t =>
        new TopicSpecification
        {
            Name = t.Name,

            // The topic's own number where the design fixes it, otherwise the
            // environment's. See ControlTopic.Partitions.
            NumPartitions = t.Partitions ?? partitions,
            ReplicationFactor = replication,
            Configs = Configs(t.Shape, minIsr)
        }).ToList();

    foreach (var t in ControlTopics.All.Where(t => existing.Contains(t.Name)))
        Console.WriteLine($"  exists  {t.Name,-28} (config not changed; run --verify)");

    if (create.Count > 0)
    {
        try
        {
            await admin.CreateTopicsAsync(create);
            foreach (var t in create)
                Console.WriteLine($"  created {t.Name,-28} cleanup.policy={t.Configs["cleanup.policy"]}");
        }
        catch (CreateTopicsException e)
        {
            foreach (var r in e.Results.Where(r => r.Error.IsError
                                                   && r.Error.Code != ErrorCode.TopicAlreadyExists))
                problems.Add($"{r.Topic}: {r.Error.Reason}");
        }
    }
}

// --- report -----------------------------------------------------------------

if (problems.Count == 0)
{
    Console.WriteLine(verifyOnly
        ? $"All {ControlTopics.All.Count} control topics are correctly configured."
        : $"All {ControlTopics.All.Count} control topics are present.");
    return 0;
}

Console.Error.WriteLine();
foreach (var p in problems) Console.Error.WriteLine($"  {p}");
return 1;

static string Policy(TopicShape shape) =>
    shape == TopicShape.CompactedState ? "compact" : "delete";

static Dictionary<string, string> Configs(TopicShape shape, int minIsr)
{
    var configs = new Dictionary<string, string>
    {
        ["cleanup.policy"] = Policy(shape),
        ["min.insync.replicas"] = minIsr.ToString()
    };

    if (shape == TopicShape.CompactedState)
    {
        // Keep a tombstone long enough that a consumer which was down over a
        // weekend still sees the delete rather than resurrecting stale state.
        configs["delete.retention.ms"] = TimeSpan.FromDays(7).TotalMilliseconds.ToString("0");
        configs["min.cleanable.dirty.ratio"] = "0.1";
    }
    else
    {
        // The lifecycle log is what the processor recovers from and what an
        // auction dispute is argued from. A year is the floor, not a target.
        configs["retention.ms"] = TimeSpan.FromDays(365).TotalMilliseconds.ToString("0");
    }

    return configs;
}
