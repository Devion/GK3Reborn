using GK3Reborn.Formats.Bitmaps;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Puts a picture on the device, in whichever form it arrived.
/// </summary>
public static unsafe class D3D12TextureUpload
{

    /// <summary>Which DXGI format a block format is.</summary>
    /// <param name="format">The block format.</param>
    /// <returns>The DXGI format.</returns>
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
            Fill(context, texture, [source.Pixels.AsMemory()], 1, into);

            if (mips > 1)
            {
                D3D12MipChain.Build(context, texture, into);
            }

            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    /// <summary>Puts the six sides of a sky on the device.</summary>
    /// <param name="context">The device.</param>
    /// <param name="faces">The six sides: right, left, up, down, front, back.</param>
    /// <param name="into">An open batch to record into, or null to submit on its own.</param>
    /// <returns>The cube map.</returns>
    /// <exception cref="ArgumentException">There are not six square faces of one size.</exception>
    /// <exception cref="D3D12Exception">It could not be created or filled.</exception>
    public static D3D12Texture CreateCube(
        D3D12Context context, IReadOnlyList<DecodedImage> faces, D3D12Uploads? into = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(faces);

        if (faces.Count != 6)
        {
            throw new ArgumentException("A cube map needs exactly six faces.", nameof(faces));
        }

        int size = faces[0].Width;

        foreach (DecodedImage face in faces)
        {
            if (face.Width != size || face.Height != size)
            {
                throw new ArgumentException(
                    "A cube map's faces must all be square and the same size.", nameof(faces));
            }
        }

        D3D12Texture texture = D3D12Texture.CreateCube(
            context, Format.FormatR8G8B8A8UnormSrgb, size);

        try
        {
            // Six subresources rather than six mips of one. A texture's subresources are
            // numbered mip-fastest, so a cube with one mip a face numbers its faces nought
            // to five and the copy walks them exactly as it walks a mip chain.
            var sides = new List<ReadOnlyMemory<byte>>(6);

            foreach (DecodedImage face in faces)
            {
                sides.Add(face.Pixels.AsMemory());
            }

            Fill(context, texture, sides, 6, into);
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
            var levels = new List<ReadOnlyMemory<byte>>((int)mips);

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

                // Sliced rather than copied: the blocks are a view of a memory-mapped pack,
                // and copying each level out of it doubled what a room's textures cost.
                levels.Add(source.Blocks.Slice(at, blocks));
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

    /// <summary>Replaces the pixels of a texture that already exists.</summary>
    /// <param name="context">The device.</param>
    /// <param name="texture">The texture, which keeps its identity.</param>
    /// <param name="image">The new picture.</param>
    /// <exception cref="D3D12Exception">The copy failed.</exception>
    public static void Refresh(D3D12Context context, D3D12Texture texture, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(image.Pixels);

        if (image.Width != texture.Width || image.Height != texture.Height)
        {
            throw new D3D12Exception(
                $"A refresh is {image.Width} by {image.Height} and the texture is "
                + $"{texture.Width} by {texture.Height}.");
        }

        Fill(context, texture, [image.Pixels.AsMemory()], 1, null);
    }

    /// <summary>Copies levels of pixels or blocks into a texture.</summary>
    private static void Fill(
        D3D12Context context,
        D3D12Texture texture,
        List<ReadOnlyMemory<byte>> levels,
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

            // A range of the batch's own upload memory rather than a staging buffer each:
            // see D3D12Uploads.Reserve. It stays mapped until the batch has run, which is
            // what lets the copies below be recorded now and submitted together.
            byte* mapped = batch.Reserve(total, out ID3D12Resource* staging, out ulong staged);

            for (int level = 0; level < levels.Count && level < mips; level++)
            {
                PlacedSubresourceFootprint footprint = footprints[level];
                ReadOnlySpan<byte> source = levels[level].Span;

                // Row by row, because the source is packed and the destination is
                // padded. Copying the whole level in one go is the mistake that shears
                // every texture whose width is not a multiple of sixty-four.
                uint sourcePitch = (uint)(rowBytes[level]);

                // Unless the two pitches agree, which for a block format at a power-of-two
                // width they nearly always do: then the level is one run of bytes and
                // walking it a row at a time buys nothing.
                if (sourcePitch == footprint.Footprint.RowPitch)
                {
                    int whole = (int)Math.Min((ulong)source.Length, (ulong)rows[level] * sourcePitch);

                    source[..whole].CopyTo(new Span<byte>(mapped + footprint.Offset, whole));
                    footprints[level].Offset += staged;

                    continue;
                }

                for (uint row = 0; row < rows[level]; row++)
                {
                    ulong destination = footprint.Offset + (row * footprint.Footprint.RowPitch);
                    int from = (int)(row * sourcePitch);
                    int count = (int)Math.Min(sourcePitch, (uint)(source.Length - from));

                    if (count <= 0)
                    {
                        break;
                    }

                    source.Slice(from, count).CopyTo(new Span<byte>(mapped + destination, count));
                }

                // Where the range actually sits in the shared buffer, which is what the
                // copy has to be told; the footprints above are measured from nought
                // because that is where the host writes them.
                footprints[level].Offset += staged;
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
                    PResource = staging,
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
