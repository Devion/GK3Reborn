using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

[Collection(GpuTests.Name)]
public sealed class VulkanUploadBudgetTests
{
    [Fact]
    public void Real_texture_sizes_trigger_cache_eviction_after_room_release()
    {
        DeviceReport report = VulkanDeviceSelector.Survey();
        Assert.SkipUnless(report.Available && report.Adapters.Count > 0, "no Vulkan device");
        using VulkanContext context = VulkanContext.CreateHeadless();
        using var compiler = new GK3Reborn.Rendering.Shaders.ShaderCompiler(
            GK3Reborn.Rendering.Shaders.ShaderCompiler.DefaultCacheDirectory);
        using var pipeline = MeshPipeline.Create(context, Format.R8G8B8A8Unorm, Format.D32Sfloat, compiler);
        var device = new VulkanGeometryDevice(context, pipeline);
        using var cache = new GK3Reborn.Rendering.Geometry.TextureCache(device,
            new GK3Reborn.Formats.Bitmaps.DecodedImage(1, 1, [255, 255, 255, 255], false, "fallback"));
        cache.MaximumRetainedBytes = 64;
        using (var room = GK3Reborn.Rendering.Geometry.SceneGeometry.Create(device, cache))
        {
            cache.Add("test", new GK3Reborn.Formats.Bitmaps.DecodedImage(4, 4, new byte[64], false, "test"));
            Assert.Equal(84, cache.DeviceBytes);
        }
        Assert.Equal(0, cache.DeviceBytes);
        Assert.False(cache.Has("test"));
    }

    [Fact]
    public unsafe void Chunked_submissions_preserve_every_copy_on_the_device()
    {
        DeviceReport report = VulkanDeviceSelector.Survey();
        Assert.SkipUnless(report.Available && report.Adapters.Count > 0, "no Vulkan device");
        using VulkanContext context = VulkanContext.CreateHeadless();
        var buffers = new List<VulkanBuffer>();
        try
        {
            using (var batch = new BufferUploads(context))
            {
                uint[] data = new uint[1024 * 1024 / sizeof(uint)];
                for (uint i = 0; i < 180; i++)
                {
                    data[0] = 0x60000000 + i;
                    buffers.Add(VulkanBuffer.CreateDeviceLocal<uint>(context,
                        i < 40 ? data : data.AsSpan(0, 1), BufferUsageFlags.TransferSrcBit, batch));
                    Assert.InRange(batch.Count, 1, BufferUploads.MaximumCopies);
                }
                batch.Submit();
                Assert.Equal(0, batch.Count);
            }
            using VulkanBuffer readback = VulkanBuffer.CreateHostVisible(context, (ulong)buffers.Count * sizeof(uint), BufferUsageFlags.TransferDstBit);
            CommandBuffer commands = context.BeginOneShot();
            for (int i = 0; i < buffers.Count; i++)
            {
                var region = new BufferCopy { Size = sizeof(uint), DstOffset = (ulong)i * sizeof(uint) };
                context.Api.CmdCopyBuffer(commands, buffers[i].Handle, readback.Handle, 1, in region);
            }
            var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.HostReadBit };
            context.Api.CmdPipelineBarrier(commands, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit,
                0, 1, &barrier, 0, null, 0, null);
            context.EndOneShot(commands);
            void* mapped = null;
            Assert.Equal(Result.Success, context.Api.MapMemory(context.Device, readback.Memory, 0, readback.Size, 0, &mapped));
            try
            {
                var values = new ReadOnlySpan<uint>(mapped, buffers.Count);
                for (int i = 0; i < values.Length; i++) { Assert.Equal(0x60000000u + (uint)i, values[i]); }
            }
            finally { context.Api.UnmapMemory(context.Device, readback.Memory); }
        }
        finally
        {
            foreach (VulkanBuffer buffer in buffers) { buffer.Dispose(); }
        }
    }
}
