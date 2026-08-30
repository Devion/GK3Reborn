using GK3Reborn.Formats.Ui;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Direct3D12;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System.Numerics;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The passes drawn over the room: the sky behind it, the interface on top, the film and the
/// fade over both.
/// </summary>
/// <remarks>
/// None of these appear in a reference render — the headless renderer stops at the room — so
/// this is the only place they are exercised without a window. What it proves is what the
/// debug layer can see: that each pipeline is accepted, that its root signature satisfies its
/// shader, and that a recorded frame draws without a complaint.
/// </remarks>
[Collection(GpuTests.Name)]
public sealed unsafe class D3D12ScreenTests
{
    private const int Width = 320;
    private const int Height = 200;

    private static bool CanRender()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return D3D12DeviceSelector.Survey().Selected is not null;
        }
        catch (D3D12Exception)
        {
            return false;
        }
    }

    private static DecodedImage Face(byte shade) =>
        new(4, 4, [.. Enumerable.Repeat<byte>(shade, 4 * 4 * 4)], HasAlpha: true, "face");

    /// <summary>A sheet with a few letters on it, enough to lay out a display list.</summary>
    private static OverlayAtlas Sheet()
    {
        var image = new DecodedImage(
            64, 16, [.. Enumerable.Repeat<byte>(255, 64 * 16 * 4)], HasAlpha: false, "sheet");

        return OverlayAtlas.Build(
            FontFile.Parse("Font=ABCDEFGH\n", image, "TEST", new DiagnosticBag()));
    }

    [Fact]
    public void The_sky_is_drawn_against_the_rooms_depth()
    {
        Assert.SkipUnless(CanRender(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        // Six distinguishable sides, so a cube built with its faces in the wrong order would
        // at least be reading six different subresources.
        DecodedImage[] faces = [Face(10), Face(40), Face(80), Face(120), Face(160), Face(200)];

        using D3D12SkyboxPass sky = D3D12SkyboxPass.Create(
            context, compiler, GBufferFormats.Light, GBufferFormats.Depth, faces, azimuth: 0.5f);

        using D3D12Texture colour = D3D12Texture.CreateRenderTarget(
            context, GBufferFormats.Light, Width, Height);

        using D3D12Texture depth = D3D12Texture.CreateDepthTarget(
            context, GBufferFormats.Depth, Width, Height, sampled: true);

        using D3D12DescriptorHeap targets = D3D12DescriptorHeap.Create(
            context.Device, DescriptorHeapType.Rtv, 1);

        using D3D12DescriptorHeap depths = D3D12DescriptorHeap.Create(
            context.Device, DescriptorHeapType.Dsv, 1);

        targets.Allocate();
        depths.Allocate();

        context.Device->CreateRenderTargetView(
            colour.Handle, (RenderTargetViewDesc*)null, targets.Cpu(0));

        depth.DescribeDepth(context, depths.Cpu(0));

        var camera = new Camera
        {
            Position = new Vector3(0, 60, 0),
            Target = new Vector3(0, 60, 100),
        };

        ID3D12GraphicsCommandList4* list = context.BeginOneShot();

        colour.Transition(list, ResourceStates.RenderTarget);
        depth.Transition(list, ResourceStates.DepthWrite);

        CpuDescriptorHandle target = targets.Cpu(0);
        CpuDescriptorHandle stencil = depths.Cpu(0);

        list->ClearDepthStencilView(
            stencil, ClearFlags.Depth, 1f, 0, 0, (Silk.NET.Maths.Box2D<int>*)null);

        depth.Transition(list, ResourceStates.DepthRead);
        list->OMSetRenderTargets(1, &target, false, &stencil);

        sky.Record(list, camera, Width, Height);
        context.EndOneShot();

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }

    [Fact]
    public void The_sky_points_the_camera_the_way_it_is_looking()
    {
        var camera = new Camera
        {
            Position = new Vector3(0, 0, 0),
            Target = new Vector3(0, 0, 100),
        };

        SkyboxConstants straight = SkyboxShaders.Describe(camera, 0f, 320, 200);

        // Looking down positive z, so that is where forward points and right is at a right
        // angle to it. A basis read out of the wrong rows of the view matrix gives a sky that
        // is plausible until the camera turns.
        Assert.Equal(1f, new Vector3(straight.Forward.X, straight.Forward.Y, straight.Forward.Z).Z, 3);
        Assert.Equal(0f, Vector3.Dot(
            new Vector3(straight.Forward.X, straight.Forward.Y, straight.Forward.Z),
            new Vector3(straight.Right.X, straight.Right.Y, straight.Right.Z)), 3);

        // Wider than it is tall, so the horizontal half-angle is the larger of the two.
        Assert.True(straight.Right.W > straight.Up.W);
        Assert.Equal(320f, straight.Viewport.X);
        Assert.Equal(200f, straight.Viewport.Y);

        // A quarter turn moves the sky and not the camera, so forward comes out somewhere
        // else entirely — which is the whole point of the azimuth.
        SkyboxConstants turned = SkyboxShaders.Describe(camera, MathF.PI / 2f, 320, 200);
        Assert.NotEqual(straight.Forward.X, turned.Forward.X, 3);
    }

    [Fact]
    public void The_interface_draws_its_letters_and_its_pictures()
    {
        Assert.SkipUnless(CanRender(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        using D3D12OverlayPass overlay = D3D12OverlayPass.Create(
            context, compiler, GBufferFormats.Picture, frames: 2);

        overlay.SetAtlas(Sheet());

        int picture = overlay.AddPicture(Face(128));
        Assert.Equal(1, picture);
        Assert.Equal(1, overlay.Pictures);

        using D3D12Texture colour = D3D12Texture.CreateRenderTarget(
            context, GBufferFormats.Picture, Width, Height);

        using D3D12DescriptorHeap targets = D3D12DescriptorHeap.Create(
            context.Device, DescriptorHeapType.Rtv, 1);

        targets.Allocate();

        context.Device->CreateRenderTargetView(
            colour.Handle, (RenderTargetViewDesc*)null, targets.Cpu(0));

        // A panel, then a picture, then a panel again: three runs, which is what a screen
        // showing a map costs and the case where binding the wrong descriptor shows up.
        var list = new Overlay(Sheet());
        list.Begin(Width, Height);
        list.Rect(0, 0, 32, 32, Vector4.One);
        list.Picture(picture, 40, 0, 32, 32, Vector4.One);
        list.Rect(80, 0, 32, 32, Vector4.One);

        Assert.Equal(3, list.Quads.Count);

        // Twice, so both slots of the ring are written and read.
        for (uint frame = 0; frame < 2; frame++)
        {
            ID3D12GraphicsCommandList4* commands = context.BeginOneShot();

            colour.Transition(commands, ResourceStates.RenderTarget);
            overlay.Prepare(list, frame);
            overlay.Record(commands, targets.Cpu(0), Width, Height);

            context.EndOneShot();
        }

        Assert.Equal(3, overlay.Rectangles);

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("fade")]
    public void The_passes_over_the_room_agree_with_their_shaders(string which)
    {
        Assert.SkipUnless(CanRender(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        bool film = which == "movie";

        using D3D12ScreenPass pass = D3D12ScreenPass.Create(
            context,
            compiler,
            film ? MovieShaders.Vertex : FadeShaders.Vertex,
            film ? MovieShaders.Fragment : FadeShaders.Fragment,
            which,

            // The film reads its frame; the fade reads nothing at all and is the cheapest
            // pass in the renderer for exactly that reason.
            inputs: film ? 1u : 0u,
            constantBytes: 32,
            [GBufferFormats.Picture],
            blend: !film);

        Assert.True(pass.Signature.PushConstantParameter >= 0);
        Assert.Equal(film ? 1u : 0u, pass.Signature.ViewDescriptorCount);

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }
}
