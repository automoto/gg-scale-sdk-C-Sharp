# Object storage for projects: open-source S3 alternatives

Research notes, 2026-08-07. Question: which open-source, self-hosted
object store should back project storage in the ggscale service?

## Summary

- MinIO Community Edition is no longer an option. Use SeaweedFS for
  production now, or Garage for a small cluster with basic needs.
- Re-evaluate RustFS in 6–12 months (around 2027-02).
- The store is an internal server decision. The C# SDK talks to the
  ggscale API, not to the store. The SDK's zero-dependency constraint
  is not affected.

## Why not MinIO

- Through 2025, MinIO moved its focus to a commercial product and
  removed features (for example, most of the management UI) from the
  community edition.
- The open-source repository was archived in early 2026 (read-only
  since February 2026). It gets no active development and no security
  patches.

## Candidates

### SeaweedFS — recommended for production now

- License: Apache 2.0. Written in Go. Active since 2012.
- ~30K GitHub stars, ~12.9K commits. Proven in medium-to-large
  production deployments.
- Good S3 API coverage. Strong performance with many small files,
  which fits a game-asset workload.
- Operational note: more components than MinIO (master, volume,
  filer).

### Garage — recommended for small, low-maintenance clusters

- License: AGPLv3. Written in Rust. From Deuxfleurs.
- Built for small self-hosted clusters (3+ nodes) on ordinary
  hardware. Geo-distributed replication is a core feature.
- Actively maintained, but S3 API coverage is narrower. For example,
  it has no object versioning.
- Pick it only if the API needs are basic put/get/list.

### RustFS — watch, do not adopt yet

- License: Apache 2.0. Written in Rust.
- Positions itself as a drop-in MinIO replacement, with MinIO
  migration tooling. Claims 2.3x MinIO speed on 4KB objects.
- Young project (~2.3K commits, ~4K stars). Community consensus in
  2026: not production-ready.

### Ceph RADOS Gateway — only at large scale

- Very mature. Scales to exabytes. High availability by design.
- High operational cost. Sensible only with dedicated infrastructure
  engineers or an existing Ceph deployment.

### Apache Ozone — niche

- S3-compatible storage from the Hadoop ecosystem. Sensible mainly if
  already in that ecosystem.

## Decision checklist

Before the final pick, confirm the store supports the S3 features the
service will use:

- [ ] Presigned URLs
- [ ] Multipart upload
- [ ] Object versioning (not in Garage)
- [ ] Object lock / retention (if needed for compliance)
- [ ] Bucket lifecycle rules

## Sources

- [Best MinIO Alternatives in 2026 (DEV Community)](https://dev.to/ethan-carter/best-minio-alternatives-in-2026-6-options-that-actually-work-17p2)
- [RustFS vs SeaweedFS vs Garage (elest.io)](https://blog.elest.io/rustfs-vs-seaweedfs-vs-garage-which-minio-alternative-should-you-pick/)
- [Self-Hosted S3 Storage in 2026 (Rilavek)](https://rilavek.com/resources/self-hosted-s3-compatible-object-storage-2026)
- [MinIO Alternatives Compared (lowcloud)](https://lowcloud.io/en/blog/minio-alternatives)
- [Self-hosted S3 after MinIO (productimpossible)](https://productimpossible.com/articles/self-hosted-s3-after-minio/)
- [Open Source MinIO Alternatives (openalternative.co)](https://openalternative.co/alternatives/minio)
