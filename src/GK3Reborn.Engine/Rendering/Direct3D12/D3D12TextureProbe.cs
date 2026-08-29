using GK3Reborn.Formats.Bitmaps;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Puts a picture on the device and reads it straight back.
/// </summary>
/// <remarks>
/// <para>
/// There is one mistake in the texture path that hides, and this is here to find it. A
/// texture is copied out of a buffer whose rows are padded to a multiple of two hundred and
/// fifty-six bytes; a copy that treats them as packed produces an image that shears further
/// with every row. At a width of sixty-four, or a hundred and twenty-eight, or two hundred
/// and fifty-six, the padding is zero and the mistake is invisible. It is visible at a
/// width of a hundred, which is why the probe uses one.
/// </para>
/// <para>
/// The mip chain has a second one of the same shape: an odd level halved leaves an edge
/// column with nothing to average against, and reaching past the end rather than clamping
/// makes a texture creep sideways as it gets coarser. That needs a picture whose halves
/// differ, so the probe checks the average of a level rather than a single texel.
/// </para>
/// </remarks>
public sealed unsafe class D3D12TextureProbe : IDisposable
{
    private readonly D3D12Context _context;
    private bool _disposed;

    private D3D12TextureProbe(D3D12Context context) => _context = context;

    /// <summary>Name of the device being used.</summary>
    public string DeviceName => _context.DeviceName;

    /// <summary>Everything the debug layer has said since it was last asked.</summary>
    public IReadOnlyList<string> Messages => _context.DrainMessages();

    /// <summary>Creates a probe.</summary>
    /// <returns>The probe.</returns>
    /// <exception cref="D3D12Exception">There is no usable device.</exception>
    public static D3D12TextureProbe Create() =>
        new(D3D12Context.Create(enableValidation: true));

    /// <summary>Uploads a picture and reads it back.</summary>
    /// <param name="source">The picture.</param>
    /// <param name="mipmaps">Whether to build a mip chain.</param>
    /// <returns>What came back.</returns>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    /// <remarks>
    /// Uploaded as linear rather than sRGB, so that what comes back is the bytes that went
    /// in. A colour texture would be encoded on the way out and the comparison would be
    /// against a curve rather than against the picture.
    /// </remarks>
    public DecodedImage RoundTrip(DecodedImage source, bool mipmaps = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using D3D12Texture texture = D3D12TextureUpload.Create(
            _context, source, mipmaps, linear: true);

        return D3D12Readback.Read(
            _context, texture.Handle, texture.State, source.Width, source.Height);
    }

    /// <summary>Uploads a picture, builds its mips, and reads one level back.</summary>
    /// <param name="source">The picture.</param>
    /// <param name="level">Which level to read.</param>
    /// <returns>The average of that level, per channel, from zero to one.</returns>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    public (float R, float G, float B) AverageOfLevel(DecodedImage source, uint level)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using D3D12Texture texture = D3D12TextureUpload.Create(
            _context, source, mipmaps: true, linear: true);

        int width = Math.Max(1, source.Width >> (int)level);
        int height = Math.Max(1, source.Height >> (int)level);

        // The level is copied into a texture of its own, because the readback works on a
        // whole resource and reading subresource zero of the original would give the top
        // level whichever one was asked for.
        using D3D12Texture one = D3D12Texture.CreateSampled(
            _context, texture.Format, width, height);

        ID3D12GraphicsCommandList4* list = _context.BeginOneShot();

        texture.Transition(list, ResourceStates.CopySource);
        one.Transition(list, ResourceStates.CopyDest);

        var destination = new TextureCopyLocation
        {
            PResource = one.Handle,
            Type = TextureCopyType.SubresourceIndex,
        };
        destination.Anonymous.SubresourceIndex = 0;

        var origin = new TextureCopyLocation
        {
            PResource = texture.Handle,
            Type = TextureCopyType.SubresourceIndex,
        };
        origin.Anonymous.SubresourceIndex = level;

        list->CopyTextureRegion(&destination, 0, 0, 0, &origin, (Box*)null);
        _context.EndOneShot();

        DecodedImage read = D3D12Readback.Read(_context, one.Handle, one.State, width, height);

        long r = 0;
        long g = 0;
        long b = 0;

        for (int i = 0; i < read.Pixels.Length; i += 4)
        {
            r += read.Pixels[i];
            g += read.Pixels[i + 1];
            b += read.Pixels[i + 2];
        }

        int count = width * height;
        return (r / (255f * count), g / (255f * count), b / (255f * count));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _context.Dispose();
    }
}
