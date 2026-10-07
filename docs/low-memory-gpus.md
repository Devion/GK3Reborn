# Scene loading on GPUs with limited memory

The reported RX 580 (4 GiB) failure occurred while submitting geometry uploads
when leaving the hotel after chasing Mallory and Macdougal. The hotel's
`LBY_ALL.NVC` front-door action changes location to `RC1`. The report does not
identify an exact save or timeblock.

Vulkan uploads now submit in batches of at most 32 MiB or 128 copies, except that
one oversized resource may occupy a batch by itself. Completed batches release
staging resources immediately. Host-visible allocations prefer non-device-local
memory, and allocation retries compatible memory types on out-of-memory results.
Failed submissions release staging resources only when they are no longer in use.

Scene texture caches now have a retention threshold, checked after the last room
releases them. Above that threshold, cached room textures and CPU sampling data
are cleared; permanent fallback textures survive. The usual threshold is 1 GiB.

For GPUs reporting at most 4 GiB, scene loading automatically limits texture
dimensions to 1024, reduces the relief triangle budget to 250,000, and reduces
cache retention to 256 MiB. At most 2 GiB uses 512-pixel textures. Compressed
textures select an existing smaller mip without recompression; decoded textures
are downsampled. Texture accounting uses uploaded resource payload sizes.

These limits reduce peaks and retained memory; they are not a guarantee that all
assets, render resolutions or driver configurations fit in 4 GiB. Payload counts
exclude allocation alignment, framebuffer resources and driver overhead.

Repeated `LBY -> RC1 -> LBY -> RC1` loads at timeblock `202P` completed on the
development NVIDIA GPU using the 4 GiB profile and packaged enhanced textures.
Resident texture payloads were 278.5 MiB for `LBY` and 433.6 MiB for `RC1`, with
the cache cleared on each room release. This exercises the destination and cache
lifetime, not the exact reported save or a physical 4 GiB allocation limit.

Automated tests check batching thresholds, actual GPU copy/readback across multiple
batches, mip selection, downsampling, and cache lifetime. Confirmation of the
reported crash fix still requires the user's RX 580 and save.
