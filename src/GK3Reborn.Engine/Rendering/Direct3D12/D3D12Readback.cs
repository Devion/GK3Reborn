using Silk.NET.DXGI;
using GK3Reborn.Formats.Bitmaps;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Copies a texture off the device and into a picture.
/// </summary>
/// <remarks>
/// <para>
/// What every screenshot, every reference render and every offscreen test goes through.
/// The awkward part is not the copy but the shape of what arrives: a texture is copied into
/// a buffer with rows padded to a multiple of two hundred and fifty-six bytes, which for a
/// width that is not a multiple of sixty-four is not the width of the picture. Reading it
/// as though it were tightly packed gives a picture that shears a little further with each
/// row, which looks like a rendering bug and is not one.
/// </para>
/// <para>
/// The device is asked for the padding rather than told: <c>GetCopyableFootprints</c> knows
/// the alignment rules for every format, including the block-compressed ones where a row is
/// a row of blocks.
/// </para>
/// </remarks>
public static unsafe class D3D12Readback
{
    /// <summary>Reads a texture back as an eight-bit picture.</summary>
    /// <param name="context">The device.</param>
    /// <param name="resource">What to read.</param>
    /// <param name="state">Which state it is in, and will be left in.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height in pixels.</param>
    /// <param name="swapRedAndBlue">Whether the texture is BGRA and the picture wants RGBA.</param>
    /// <param name="wide">
    /// What the texture holds when it is not eight-bit colour, or unknown for the ordinary
    /// case. A ten-bit or half-float frame is brought back down to sRGB rather than having
    /// its bytes copied, because copying them gives the right picture with every value
    /// scrambled — which looks like a renderer bug and is not one.
    /// </param>
    /// <param name="paperWhite">Where diffuse white sat, for a wide frame.</param>
    /// <returns>The picture, with four bytes a pixel.</returns>
    /// <exception cref="D3D12Exception">The read failed.</exception>
    public static DecodedImage Read(
        D3D12Context context,
        ID3D12Resource* resource,
        ResourceStates state,
        int width,
        int height,
        bool swapRedAndBlue = false,
        Format wide = Format.FormatUnknown,
        float paperWhite = 200f)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);

        ResourceDesc description = resource->GetDesc();

        PlacedSubresourceFootprint footprint = default;
        ulong bytes = 0;
        uint rows = 0;
        ulong rowBytes = 0;

        context.Device->GetCopyableFootprints(
            &description, 0, 1, 0, &footprint, &rows, &rowBytes, &bytes);

        ComPtr<ID3D12Resource> staging = context.CreateBuffer(bytes, HeapType.Readback);

        try
        {
            ID3D12GraphicsCommandList4* list = context.BeginOneShot();

            D3D12Context.Transition(list, resource, state, ResourceStates.CopySource);

            var destination = new TextureCopyLocation
            {
                PResource = staging.Handle,
                Type = TextureCopyType.PlacedFootprint,
            };
            destination.Anonymous.PlacedFootprint = footprint;

            var origin = new TextureCopyLocation
            {
                PResource = resource,
                Type = TextureCopyType.SubresourceIndex,
            };
            origin.Anonymous.SubresourceIndex = 0;

            list->CopyTextureRegion(&destination, 0, 0, 0, &origin, (Box*)null);

            // Put it back where it was found. A capture is not supposed to be visible to
            // the rest of the frame, and a target left in CopySource is one the next pass
            // cannot draw into.
            D3D12Context.Transition(list, resource, ResourceStates.CopySource, state);

            context.EndOneShot();

            void* mapped;
            var range = new Silk.NET.Direct3D12.Range { Begin = 0, End = (nuint)bytes };

            D3D12Exception.ThrowIfFailed(staging.Map(0, &range, &mapped), "map the readback buffer");

            try
            {
                var source = new ReadOnlySpan<byte>(mapped, (int)bytes);
                uint pitch = footprint.Footprint.RowPitch;

                bool halves = wide == Format.FormatR16G16B16A16Float;
                int stride = halves ? 8 : 4;

                if (wide is Format.FormatR10G10B10A2Unorm or Format.FormatR16G16B16A16Float)
                {
                    // De-padded first, because the conversion walks pixels and the rows the
                    // device hands back are padded to its own alignment.
                    byte[] packed = new byte[width * height * stride];

                    for (int y = 0; y < height; y++)
                    {
                        source.Slice((int)(y * pitch), width * stride)
                            .CopyTo(packed.AsSpan(y * width * stride, width * stride));
                    }

                    return new DecodedImage(
                        width,
                        height,
                        HdrCapture.ToOrdinary(packed, width, height, halves, paperWhite),
                        HasAlpha: false,
                        SourceFormat: "d3d12");
                }

                byte[] pixels = new byte[width * height * 4];

                for (int y = 0; y < height; y++)
                {
                    ReadOnlySpan<byte> row = source.Slice((int)(y * pitch), width * 4);
                    Span<byte> into = pixels.AsSpan(y * width * 4, width * 4);
                    row.CopyTo(into);

                    if (!swapRedAndBlue)
                    {
                        continue;
                    }

                    for (int x = 0; x < width * 4; x += 4)
                    {
                        (into[x], into[x + 2]) = (into[x + 2], into[x]);
                    }
                }

                return new DecodedImage(width, height, pixels, HasAlpha: false, SourceFormat: "d3d12");
            }
            finally
            {
                // An empty written range: nothing was written, so nothing needs flushing
                // back to the device. Passing null instead says the whole buffer was
                // written, which on a discrete card is a pointless transfer of the picture
                // back the way it came.
                var written = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
                staging.Unmap(0, &written);
            }
        }
        finally
        {
            staging.Dispose();
        }
    }
}
