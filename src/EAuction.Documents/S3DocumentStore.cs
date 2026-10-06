using Amazon.S3;
using Amazon.S3.Model;

namespace EAuction.Documents;

public sealed record S3Options
{
    public required string ServiceUrl { get; init; }
    public required string AccessKey { get; init; }
    public required string SecretKey { get; init; }
    public string Bucket { get; init; } = "eauction-documents";
}

/// <summary>
/// The object store, on anything that speaks S3. MinIO, in compose.
///
/// The metadata rides on the object as S3 user metadata (<c>x-amz-meta-*</c>),
/// which is why this service has no database. One consequence worth naming: S3
/// user metadata is ASCII-only, so a file called <c>كراسة الشروط.pdf</c> cannot be
/// stored verbatim in a header. It is URL-encoded on the way in and decoded on the
/// way out — the first version of this silently dropped Arabic filenames to
/// question marks, which is exactly the kind of thing that is noticed by a user
/// and not by a test.
/// </summary>
public sealed class S3DocumentStore : IDocumentStore, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public S3DocumentStore(S3Options options)
    {
        _bucket = options.Bucket;
        _s3 = new AmazonS3Client(
            options.AccessKey, options.SecretKey,
            new AmazonS3Config
            {
                ServiceURL = options.ServiceUrl,

                // MinIO serves one host with the bucket in the path. Virtual-host
                // addressing would resolve eauction-documents.minio, which does
                // not exist.
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
            });
    }

    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        var buckets = await _s3.ListBucketsAsync(ct);
        if (buckets.Buckets.Any(b => b.BucketName == _bucket)) return;

        await _s3.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, ct);
    }

    public async Task PutAsync(DocumentMetadata metadata, Stream bytes, CancellationToken ct)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = Key(metadata.Id),
            InputStream = bytes,
            ContentType = metadata.ContentType,

            // Payload signing stays on.
            //
            // Turning it off looks like a saving — the SDK hashes the stream a
            // second time, after this service has already hashed it — but the SDK
            // refuses to send an unsigned payload over plain HTTP, and MinIO in
            // compose is plain HTTP. With it off, every upload in a deployed stack
            // failed at the signer with "the request must be sent over HTTPS".
            // Found by running against a real endpoint; the in-memory store cannot
            // have this bug.
        };

        request.Metadata.Add("filename", Uri.EscapeDataString(metadata.FileName));
        request.Metadata.Add("access", metadata.Access.ToString());
        request.Metadata.Add("owner", metadata.OwnerSubject.ToString());
        request.Metadata.Add("sha256", metadata.Sha256);
        request.Metadata.Add("uploaded", metadata.UploadedAt.ToUnixTimeMilliseconds().ToString());

        await _s3.PutObjectAsync(request, ct);
    }

    public async Task<DocumentMetadata?> HeadAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var response = await _s3.GetObjectMetadataAsync(_bucket, Key(id), ct);
            return Read(id, response.Metadata, response.Headers.ContentType,
                response.Headers.ContentLength);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<DocumentContent?> GetAsync(Guid id, CancellationToken ct)
    {
        GetObjectResponse response;
        try
        {
            response = await _s3.GetObjectAsync(_bucket, Key(id), ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        var metadata = Read(id, response.Metadata, response.Headers.ContentType,
            response.Headers.ContentLength);

        return new DocumentContent(metadata, response.ResponseStream);
    }

    private static string Key(Guid id) => id.ToString("N");

    private static DocumentMetadata Read(
        Guid id, MetadataCollection metadata, string? contentType, long size)
    {
        var uploaded = long.TryParse(metadata["uploaded"], out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.UnixEpoch;

        return new DocumentMetadata
        {
            Id = id,
            FileName = Uri.UnescapeDataString(metadata["filename"] ?? ""),
            ContentType = contentType ?? "application/octet-stream",
            SizeBytes = size,
            Access = DocumentAccessValues.Parse(metadata["access"]),
            OwnerSubject = Guid.TryParse(metadata["owner"], out var owner) ? owner : Guid.Empty,
            Sha256 = metadata["sha256"] ?? "",
            UploadedAt = uploaded,
        };
    }

    public void Dispose() => _s3.Dispose();
}
