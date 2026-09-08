using GK3Reborn.Formats.Bitmaps;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Puts a picture on the device and reads it straight back.
/// </summary>
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
        DecodedImage read = LevelOf(source, level);

        long r = 0;
        long g = 0;
        long b = 0;

        for (int i = 0; i < read.Pixels.Length; i += 4)
        {
            r += read.Pixels[i];
            g += read.Pixels[i + 1];
            b += read.Pixels[i + 2];
        }

        int count = read.Width * read.Height;
        return (r / (255f * count), g / (255f * count), b / (255f * count));
    }

    /// <summary>Uploads a picture, builds its mips, and reads one level back as it stands.</summary>
    /// <param name="source">The picture.</param>
    /// <param name="level">Which level to read.</param>
    /// <param name="colour">
    /// Whether the picture is colour, and so uploaded sRGB-encoded as every wall and floor
    /// texture in the game is, rather than data uploaded as plain bytes.
    /// </param>
    /// <returns>The bytes of that level, which for a colour picture are still encoded.</returns>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    public DecodedImage LevelOf(DecodedImage source, uint level, bool colour = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using D3D12Texture texture = D3D12TextureUpload.Create(
            _context, source, mipmaps: true, linear: !colour);

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

        return D3D12Readback.Read(_context, one.Handle, one.State, width, height);
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
