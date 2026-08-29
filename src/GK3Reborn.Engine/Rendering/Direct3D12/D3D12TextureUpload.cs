using GK3Reborn.Formats.Bitmaps;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Puts a picture on the device, in whichever form it arrived.
/// </summary>
/// <remarks>
/// <para>
/// Two ways in, and the difference between them is a quarter of the video memory. A
/// <see cref="CompressedImage"/> is what the content pipeline produced: the blocks go from
/// the file to a staging buffer to the texture exactly as they are, one copy a level, with
/// the mip chain the compressor already built. A <see cref="DecodedImage"/> is a picture
/// that never went through the pipeline — a screen's own artwork, a frame of film, a
/// generated texture — and it is uploaded as eight-bit colour with its mips made on the
/// device.
/// </para>
/// <para>
/// Making them on the device is where Direct3D and Vulkan part company. Vulkan blits each
/// level from the one above with <c>vkCmdBlitImage</c>; Direct3D has no blit at all. The
/// mips are made by a compute shader instead — see <see cref="D3D12MipChain"/> — which is
/// written in the same GLSL as everything else and goes through the same translation.
/// </para>
/// <para>
/// The row pitch is the thing to get right. A texture is copied out of a buffer whose rows
/// are padded to a multiple of two hundred and fifty-six bytes, which for most widths is
/// not the width of the picture, and a copy that assumes otherwise produces an image that
/// shears further with every row. <c>GetCopyableFootprints</c> is asked rather than the
/// arithmetic being repeated, because it knows the rule for the block formats too, where a
/// row is a row of blocks.
/// </para>
/// </remarks>
public static unsafe class D3D12TextureUpload
{
    /// <summary>Which DXGI format a block format is.</summary>
    /// <param name="format">The block format.</param>
    /// <returns>The DXGI format.</returns>
    /// <remarks>
    /// BC1 through BC7 are required of every Direct3D 12 device, so unlike the Vulkan path
    /// there is no case here for expanding the blocks on the host. That path exists on the
    /// other backend only for Apple silicon, which has no Direct3D.
    /// </remarks>
    public static Format FormatOf(BlockFormat format) => format switch
    {
        BlockFormat.Bc7Srgb => Format.FormatBC7UnormSrgb,
        BlockFormat.Bc7Unorm => Format.FormatBC7Unorm,
        BlockFormat.Bc5Unorm => Format.FormatBC5Unorm,
        BlockFormat.Bc4Unorm => Format.FormatBC4Unorm,
        _ => Format.FormatBC7Unorm,
    };

    /// <summary>Puts a decoded picture on the device.</summary>
    /// <param name="context">The device.</param>
    /// <param name="source">The picture.</param>
    /// <param name="mipmaps">Whether to build a mip chain for it.</param>
    /// <param name="linear">Whether the picture is data rather than colour.</param>
    /// <param name="into">An open batch to record into, or null to submit on its own.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="D3D12Exception">It could not be created or filled.</exception>
    /// <remarks>
    /// <para>
    /// The pipeline shades in linear space, so a colour texture is declared sRGB and the
    /// hardware converts on read. Doing it in the shader instead is a common source of
    /// double-corrected, washed-out output.
    /// </para>
    /// <para>
    /// A normal map is not a colour. Its channels are a direction, and putting one through
    /// the sRGB path bends every normal towards flat — which reads as a weak, waxy surface
    /// rather than as the colour-space mistake it is. That is what <paramref name="linear"/>
    /// is for.
    /// </para>
    /// <para>
    /// <b>A packed atlas must pass <paramref name="mipmaps"/> as false.</b> Each coarser
    /// level averages texels across tile boundaries, so by the third level a tile is
    /// visibly contaminated by its neighbours.
    /// </para>
    /// </remarks>
    public static D3D12Texture Create(
        D3D12Context context,
        DecodedImage source,
        bool mipmaps = true,
        bool linear = false,
        D3D12Uploads? into = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source.Pixels);

        Format format = linear ? Format.FormatR8G8B8A8Unorm : Format.FormatR8G8B8A8UnormSrgb;

        uint mips = mipmaps
            ? (uint)(Math.Floor(Math.Log2(Math.Max(source.Width, source.Height))) + 1)
            : 1;

        // Room for the mips to be written into, which they cannot be through a shader
        // resource view. The top level is copied in and the rest are computed.
        D3D12Texture texture = D3D12Texture.CreateSampled(
            context, format, source.Width, source.Height, mips, writable: mips > 1);

        try
        {
            Fill(context, texture, [source.Pixels], 1, into);

            if (mips > 1)
            {
                D3D12MipChain.Build(context, texture);
            }

            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    /// <summary>Puts an already-compressed picture on the device.</summary>
    /// <param name="context">The device.</param>
    /// <param name="source">The compressed levels, as the file holds them.</param>
    /// <param name="into">An open batch to record into, or null to submit on its own.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="D3D12Exception">It could not be created or filled.</exception>
    /// <remarks>
    /// The cheap path, and the one worth taking wherever the content pipeline has produced
    /// a file for. Nothing is decoded, nothing is filtered, and a quarter of the memory is
    /// asked for.
    /// </remarks>
    public static D3D12Texture Create(
        D3D12Context context, CompressedImage source, D3D12Uploads? into = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (source.Blocks.IsEmpty)
        {
            throw new D3D12Exception($"The compressed texture {source.Name} has no blocks.");
        }

        Format format = FormatOf(source.Format);
        uint mips = (uint)Math.Max(1, source.Mips);

        D3D12Texture texture = D3D12Texture.CreateSampled(
            context, format, source.Width, source.Height, mips, writable: false);

        try
        {
            var levels = new List<byte[]>((int)mips);

            int bytesPerBlock = CompressedImage.BytesPerBlock(source.Format);
            int at = 0;

            for (uint level = 0; level < mips; level++)
            {
                int width = Math.Max(1, source.Width >> (int)level);
                int height = Math.Max(1, source.Height >> (int)level);
                int blocks = ((width + 3) / 4) * ((height + 3) / 4) * bytesPerBlock;

                if (at + blocks > source.Blocks.Length)
                {
                    // The file claims more levels than it carries. Stopping is better than
                    // reading past the end, and the levels that are there still make a
                    // usable texture.
                    mips = level;
                    break;
                }

                levels.Add(source.Blocks.Slice(at, blocks).ToArray());
                at += blocks;
            }

            Fill(context, texture, levels, mips, into);
            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    /// <summary>Copies levels of pixels or blocks into a texture.</summary>
    private static void Fill(
        D3D12Context context,
        D3D12Texture texture,
        List<byte[]> levels,
        uint mips,
        D3D12Uploads? into)
    {
        if (levels.Count == 0)
        {
            return;
        }

        bool own = into is null;
        D3D12Uploads batch = into ?? D3D12Uploads.Begin(context);

        try
        {
            ResourceDesc description = texture.Handle->GetDesc();

            var footprints = new PlacedSubresourceFootprint[mips];
            var rows = new uint[mips];
            var rowBytes = new ulong[mips];
            ulong total = 0;

            fixed (PlacedSubresourceFootprint* placed = footprints)
            fixed (uint* rowCounts = rows)
            fixed (ulong* rowSizes = rowBytes)
            {
                context.Device->GetCopyableFootprints(
                    &description, 0, mips, 0, placed, rowCounts, rowSizes, &total);
            }

            ComPtr<ID3D12Resource> staging = context.CreateBuffer(total, HeapType.Upload);
            batch.Keep(staging);

            void* mapped;
            var nothing = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
            D3D12Exception.ThrowIfFailed(staging.Map(0, &nothing, &mapped), "map a texture staging buffer");

            try
            {
                for (int level = 0; level < levels.Count && level < mips; level++)
                {
                    PlacedSubresourceFootprint footprint = footprints[level];
                    byte[] source = levels[level];

                    // Row by row, because the source is packed and the destination is
                    // padded. Copying the whole level in one go is the mistake that shears
                    // every texture whose width is not a multiple of sixty-four.
                    uint sourcePitch = (uint)(rowBytes[level]);

                    for (uint row = 0; row < rows[level]; row++)
                    {
                        ulong destination = footprint.Offset + (row * footprint.Footprint.RowPitch);
                        int from = (int)(row * sourcePitch);
                        int count = (int)Math.Min(sourcePitch, (uint)(source.Length - from));

                        if (count <= 0)
                        {
                            break;
                        }

                        source.AsSpan(from, count)
                            .CopyTo(new Span<byte>((byte*)mapped + destination, count));
                    }
                }
            }
            finally
            {
                staging.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
            }

            texture.Transition(batch.List, ResourceStates.CopyDest);

            for (uint level = 0; level < levels.Count && level < mips; level++)
            {
                var destination = new TextureCopyLocation
                {
                    PResource = texture.Handle,
                    Type = TextureCopyType.SubresourceIndex,
                };
                destination.Anonymous.SubresourceIndex = level;

                var origin = new TextureCopyLocation
                {
                    PResource = staging.Handle,
                    Type = TextureCopyType.PlacedFootprint,
                };
                origin.Anonymous.PlacedFootprint = footprints[level];

                batch.List->CopyTextureRegion(&destination, 0, 0, 0, &origin, (Box*)null);
            }

            // Left where the mip builder or the shaders want it. A texture whose mips are
            // still to be made goes to unordered access; one that is complete goes to being
            // read, and by every stage, because the same texture is sampled by the mesh
            // shader and by the reflection trace.
            texture.Transition(
                batch.List,
                levels.Count < mips
                    ? ResourceStates.UnorderedAccess
                    : ResourceStates.AllShaderResource);

            if (own)
            {
                batch.Submit();
            }
        }
        finally
        {
            if (own)
            {
                batch.Dispose();
            }
        }
    }
}
