using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Draws the reconstructed horizon: real terrain, its forest, and a generated sky with
/// procedural cloud cover, where the painted skybox was.
/// </summary>
/// <remarks>
/// <para>
/// The buffers, the textures and the four pipelines. What is drawn — the mesh, the forest,
/// which trees are near enough to be models, and the two constant blocks a frame is drawn
/// with — is <see cref="TerrainPlan"/>, which both backends share, and the stages themselves
/// are <see cref="Shaders.TerrainShaders"/>. Nothing about the horizon's recipe is here.
/// </para>
/// <para>
/// When this draws, the painted cubemap does not: its mountains are baked into the picture
/// and would double-expose against the reconstructed ridge. The sky here is an atmosphere
/// and cloud layer with the scene's own sun in it, near-black when the hour has no sun, and
/// the cubemap survives only as the fallback for a backdrop that would not build.
/// </para>
/// <para>
/// Four pipelines against one descriptor set: the ground, the impostor forest and the near
/// band of modelled trees all read the same six textures and the same push block, and the
/// sky reads neither. The modelled trees take a second set as well, for the one sheet a part
/// is painted with.
/// </para>
/// </remarks>
public sealed unsafe class TerrainPipeline : IDisposable
{
    /// <summary>Floats per placed tree: where it is, how big, which way round, which shape.</summary>
    private const int Stride = TerrainPlan.Stride;

    private readonly Vk _vk;
    private readonly VulkanContext _context;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private ShaderModule _treeVertexModule;
    private ShaderModule _treeFragmentModule;
    private ShaderModule _skyVertexModule;
    private ShaderModule _skyFragmentModule;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private PipelineLayout _skyLayout;
    private Pipeline _pipeline;
    private Pipeline _treePipeline;
    private Pipeline _skyPipeline;
    private VulkanBuffer? _vertices;
    private VulkanBuffer? _indices;
    private uint _indexCount;
    private VulkanBuffer? _treeVertices;
    private VulkanBuffer? _treeIndices;
    private VulkanBuffer? _treeInstances;
    private ShaderModule _modelVertexModule;
    private ShaderModule _modelFragmentModule;
    private DescriptorSetLayout _sheetLayout;
    private DescriptorPool _sheetPool;
    private PipelineLayout _modelLayout;
    private Pipeline _modelPipeline;
    private VulkanBuffer? _modelVertices;
    private VulkanBuffer? _modelIndices;
    private VulkanBuffer? _modelInstances;
    private readonly List<VulkanTexture> _sheets = [];
    private readonly List<DescriptorSet> _sheetSets = [];
    private readonly VulkanTexture?[] _textures = new VulkanTexture?[6];

    /// <summary>The backdrop itself, which is not a device thing. See <see cref="Plan"/>.</summary>
    private TerrainPlan _plan = null!;

    private TerrainPipeline(VulkanContext context)
    {
        _context = context;
        _vk = context.Api;
    }

    /// <summary>
    /// The backdrop's own arithmetic: its meshes, its forest, and what a frame is drawn
    /// with. Everything tunable about the horizon lives on it.
    /// </summary>
    public TerrainPlan Plan => _plan;

    /// <summary>Creates the pipeline for one scene's backdrop.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="colorFormat">Colour target format.</param>
    /// <param name="depthFormat">Depth target format.</param>
    /// <param name="compiler">Shader compiler.</param>
    /// <param name="backdrop">The terrain, forest and layers to build and draw.</param>
    /// <returns>The pipeline.</returns>
    public static TerrainPipeline Create(
        VulkanContext context,
        Format colorFormat,
        Format depthFormat,
        ShaderCompiler compiler,
        TerrainBackdrop backdrop)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(backdrop);

        var pipeline = new TerrainPipeline(context)
        {
            _plan = TerrainPlan.Create(backdrop, backdrop.TreeTextures.Count),
        };

        try
        {
            pipeline._vertexModule = pipeline.CreateModule(compiler.Compile(
                TerrainShaders.Vertex, ShaderStage.Vertex, "terrain.vert", "main", ShaderLanguage.Glsl));
            pipeline._fragmentModule = pipeline.CreateModule(compiler.Compile(
                TerrainShaders.Fragment, ShaderStage.Fragment, "terrain.frag", "main", ShaderLanguage.Glsl));
            pipeline._treeVertexModule = pipeline.CreateModule(compiler.Compile(
                TerrainShaders.TreeVertex, ShaderStage.Vertex, "trees.vert", "main", ShaderLanguage.Glsl));
            pipeline._treeFragmentModule = pipeline.CreateModule(compiler.Compile(
                TerrainShaders.TreeFragment, ShaderStage.Fragment, "trees.frag", "main", ShaderLanguage.Glsl));
            pipeline._skyVertexModule = pipeline.CreateModule(compiler.Compile(
                TerrainShaders.SkyVertex, ShaderStage.Vertex, "horizon-sky.vert", "main", ShaderLanguage.Glsl));
            pipeline._skyFragmentModule = pipeline.CreateModule(compiler.Compile(
                TerrainShaders.SkyFragment, ShaderStage.Fragment, "horizon-sky.frag", "main", ShaderLanguage.Glsl));

            pipeline._modelVertexModule = pipeline.CreateModule(compiler.Compile(
                TerrainShaders.TreeModelVertex, ShaderStage.Vertex, "horizon-tree-model.vert", "main",
                ShaderLanguage.Glsl));

            pipeline._modelFragmentModule = pipeline.CreateModule(compiler.Compile(
                TerrainShaders.TreeModelFragment, ShaderStage.Fragment, "horizon-tree-model.frag",
                "main", ShaderLanguage.Glsl));

            pipeline.UploadMesh();
            pipeline.UploadTrees();

            // The tiles repeat and are colour; the splat is data and must not be
            // sRGB-decoded or wrapped; the tint is colour but clamped like the splat.
            //
            // **All six carry a mip chain, and the last two are why the ridges used to
            // crawl.** A thousand-cell splat map is stretched over a kilometre and a half
            // of terrain, so a mountain at the far edge of it puts twenty cells inside one
            // pixel. Sampled from the top level with no chain to fall back on, that pixel
            // takes whichever cell it happens to land in — rock here, forest at the
            // neighbouring pixel, rock again at the next — and a hillside a kilometre away
            // comes out as a shimmering grey-and-green weave that moves with the camera.
            // It is the most visible thing in an outdoor scene and it is one flag.
            pipeline._textures[0] = VulkanTexture.Create(context, backdrop.TileForest);
            pipeline._textures[1] = VulkanTexture.Create(context, backdrop.TileRock);
            pipeline._textures[2] = VulkanTexture.Create(context, backdrop.TileGrass);
            pipeline._textures[3] = VulkanTexture.Create(context, backdrop.TileDirt);
            //
            // Blocks where the pack holds them, which is the same picture with its chain
            // already built and no PNG decode in front of it. The linear/sRGB choice moves
            // into the block format there — BC7_UNORM for the weights, BC7_UNORM_SRGB for
            // the tint — so it is stated once either way.
            pipeline._textures[4] = backdrop.SplatBlocks is { } splat
                ? VulkanTexture.Create(context, splat, SamplerAddressMode.ClampToEdge)
                : VulkanTexture.Create(
                    context, backdrop.Splat, mipmaps: true,
                    SamplerAddressMode.ClampToEdge, linear: true);

            pipeline._textures[5] = backdrop.TintBlocks is { } tint
                ? VulkanTexture.Create(context, tint, SamplerAddressMode.ClampToEdge)
                : VulkanTexture.Create(
                    context, backdrop.Tint, mipmaps: true, SamplerAddressMode.ClampToEdge);

            pipeline.CreateDescriptors();

            // Before the pipelines, because the models' own descriptor layout is one of
            // the two the pipeline that draws them is built against.
            pipeline.UploadTreeModels(backdrop);
            pipeline.BuildPipelines(colorFormat, depthFormat);

            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    /// <summary>Records the backdrop: terrain, forest, then the sky behind them.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="camera">Where the player is looking from, in room units.</param>
    /// <param name="width">Viewport width.</param>
    /// <param name="height">Viewport height.</param>
    public void Record(CommandBuffer command, Camera camera, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (_vertices is null || _indices is null || width <= 0 || height <= 0)
        {
            return;
        }

        // Where the camera stands in the backdrop, which trees are near enough to be models,
        // and the two blocks the stages read. None of that is a device question, so none of
        // it is answered here — see TerrainPlan.
        TerrainFrame frame = _plan.Frame(camera, width, height);
        TerrainConstants push = frame.Ground;

        if (frame.Reselected && _modelInstances is not null)
        {
            _modelInstances.Write<float>(
                _plan.ModelInstanceData.AsSpan(0, (int)_plan.ModelCount * Stride));
        }

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);

        DescriptorSet set = _set;
        _vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);
        _vk.CmdPushConstants(
            command, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0,
            (uint)Marshal.SizeOf<TerrainConstants>(), &push);

        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline);
        Silk.NET.Vulkan.Buffer vertexBuffer = _vertices.Handle;
        ulong offsetZero = 0;
        _vk.CmdBindVertexBuffers(command, 0, 1, in vertexBuffer, in offsetZero);
        _vk.CmdBindIndexBuffer(command, _indices.Handle, 0, IndexType.Uint32);
        _vk.CmdDrawIndexed(command, _indexCount, 1, 0, 0, 0);

        if (_treeInstances is not null && _plan.TreeCount > 0)
        {
            _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _treePipeline);

            Silk.NET.Vulkan.Buffer* treeStreams = stackalloc Silk.NET.Vulkan.Buffer[2]
            {
                _treeVertices!.Handle,
                _treeInstances.Handle,
            };
            ulong* treeOffsets = stackalloc ulong[2] { 0, 0 };
            _vk.CmdBindVertexBuffers(command, 0, 2, treeStreams, treeOffsets);
            _vk.CmdBindIndexBuffer(command, _treeIndices!.Handle, 0, IndexType.Uint16);

            for (int kind = 0; kind < _plan.Stands.Length; kind++)
            {
                if (_plan.Stands[kind].Count == 0)
                {
                    continue;
                }

                (uint firstIndex, int vertexOffset, uint indexCount) =
                    _plan.ImpostorRanges[kind];

                _vk.CmdDrawIndexed(
                    command, indexCount, _plan.Stands[kind].Count,
                    firstIndex, vertexOffset, _plan.Stands[kind].First);
            }
        }

        // And the near band as real trees. After the impostors rather than before, so the
        // cheap pass has already put its depth down and the alpha-tested cards — which are
        // the expensive fragments here — are rejected wherever a cone is already nearer.
        if (_modelPipeline.Handle != 0 && _modelInstances is not null && _plan.ModelCount > 0)
        {
            _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _modelPipeline);

            DescriptorSet ground = _set;
            _vk.CmdBindDescriptorSets(
                command, PipelineBindPoint.Graphics, _modelLayout, 0, 1, in ground, 0, null);
            _vk.CmdPushConstants(
                command, _modelLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0, (uint)Marshal.SizeOf<TerrainConstants>(), &push);

            Silk.NET.Vulkan.Buffer* modelStreams = stackalloc Silk.NET.Vulkan.Buffer[2]
            {
                _modelVertices!.Handle,
                _modelInstances.Handle,
            };
            ulong* modelOffsets = stackalloc ulong[2] { 0, 0 };
            _vk.CmdBindVertexBuffers(command, 0, 2, modelStreams, modelOffsets);
            _vk.CmdBindIndexBuffer(command, _modelIndices!.Handle, 0, IndexType.Uint32);

            int bound = -1;

            for (int model = 0; model < _plan.Models.Length; model++)
            {
                if (_plan.ModelStands[model].Count == 0)
                {
                    continue;
                }

                foreach ((int sheet, uint firstIndex, uint indexCount) in
                    _plan.Models[model].Parts)
                {
                    if (sheet != bound)
                    {
                        DescriptorSet painted = _sheetSets[sheet];
                        _vk.CmdBindDescriptorSets(
                            command, PipelineBindPoint.Graphics, _modelLayout, 1, 1,
                            in painted, 0, null);

                        bound = sheet;
                    }

                    _vk.CmdDrawIndexed(
                        command, indexCount, _plan.ModelStands[model].Count, firstIndex,
                        _plan.Models[model].VertexOffset, _plan.ModelStands[model].First);
                }
            }
        }

        // The sky last, at the far plane, over exactly the pixels nothing claimed.
        TerrainSkyConstants skyPush = frame.Sky;

        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _skyPipeline);
        _vk.CmdPushConstants(
            command, _skyLayout, ShaderStageFlags.FragmentBit, 0,
            (uint)Marshal.SizeOf<TerrainSkyConstants>(), &skyPush);
        _vk.CmdDraw(command, 3, 1, 0, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        DestroyPipeline(ref _pipeline);
        DestroyPipeline(ref _treePipeline);
        DestroyPipeline(ref _skyPipeline);

        if (_layout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_context.Device, _layout, null);
            _layout = default;
        }

        if (_skyLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_context.Device, _skyLayout, null);
            _skyLayout = default;
        }

        if (_pool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_context.Device, _pool, null);
            _pool = default;
        }

        if (_setLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
            _setLayout = default;
        }

        DestroyModule(ref _vertexModule);
        DestroyModule(ref _fragmentModule);
        DestroyModule(ref _treeVertexModule);
        DestroyModule(ref _treeFragmentModule);
        DestroyModule(ref _skyVertexModule);
        DestroyModule(ref _skyFragmentModule);

        _vertices?.Dispose();
        _vertices = null;
        _indices?.Dispose();
        _indices = null;
        _treeVertices?.Dispose();
        _treeVertices = null;
        _treeIndices?.Dispose();
        _treeIndices = null;
        _modelVertices?.Dispose();
        _modelVertices = null;
        _modelIndices?.Dispose();
        _modelIndices = null;
        _modelInstances?.Dispose();
        _modelInstances = null;

        foreach (VulkanTexture sheet in _sheets)
        {
            sheet.Dispose();
        }

        _sheets.Clear();
        _sheetSets.Clear();

        if (_modelPipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_context.Device, _modelPipeline, null);
            _modelPipeline = default;
        }

        if (_modelLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_context.Device, _modelLayout, null);
            _modelLayout = default;
        }

        if (_sheetPool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_context.Device, _sheetPool, null);
            _sheetPool = default;
        }

        if (_sheetLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_context.Device, _sheetLayout, null);
            _sheetLayout = default;
        }

        if (_modelVertexModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_context.Device, _modelVertexModule, null);
            _modelVertexModule = default;
        }

        if (_modelFragmentModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_context.Device, _modelFragmentModule, null);
            _modelFragmentModule = default;
        }
        _treeInstances?.Dispose();
        _treeInstances = null;

        for (int i = 0; i < _textures.Length; i++)
        {
            _textures[i]?.Dispose();
            _textures[i] = null;
        }
    }

    private void DestroyPipeline(ref Pipeline pipeline)
    {
        if (pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_context.Device, pipeline, null);
            pipeline = default;
        }
    }

    private void DestroyModule(ref ShaderModule module)
    {
        if (module.Handle != 0)
        {
            _vk.DestroyShaderModule(_context.Device, module, null);
            module = default;
        }
    }

    /// <summary>Puts the ground the plan worked out onto the device.</summary>
    private void UploadMesh()
    {
        _vertices = VulkanBuffer.CreateDeviceLocal<TerrainVertex>(
            _context, _plan.Vertices, BufferUsageFlags.VertexBufferBit);
        _indices = VulkanBuffer.CreateDeviceLocal<uint>(
            _context, _plan.Indices, BufferUsageFlags.IndexBufferBit);
        _indexCount = (uint)_plan.Indices.Length;
    }

    /// <summary>Puts the impostor shapes and the whole forest onto the device.</summary>
    private void UploadTrees()
    {
        if (_plan.TreeCount == 0)
        {
            return;
        }

        _treeVertices = VulkanBuffer.CreateDeviceLocal<TerrainVertex>(
            _context, _plan.TreeVertices, BufferUsageFlags.VertexBufferBit);
        _treeIndices = VulkanBuffer.CreateDeviceLocal<ushort>(
            _context, _plan.TreeIndices, BufferUsageFlags.IndexBufferBit);
        _treeInstances = VulkanBuffer.CreateDeviceLocal<float>(
            _context, _plan.TreeInstances, BufferUsageFlags.VertexBufferBit);
    }

    /// <summary>
    /// Puts the modelled trees, and the sheets they are painted with, onto the device.
    /// </summary>
    /// <param name="backdrop">The backdrop, for the textures the plan does not hold.</param>
    /// <remarks>
    /// One texture and one descriptor set apiece. There are four of them at most — a trunk
    /// and three sprays — so a set each is simpler than an array of samplers and asks
    /// nothing of the device that a 1.0 driver does not already offer.
    /// </remarks>
    private void UploadTreeModels(TerrainBackdrop backdrop)
    {
        if (_plan.Models.Length == 0)
        {
            return;
        }

        foreach (Formats.Bitmaps.DecodedImage image in backdrop.TreeTextures)
        {
            _sheets.Add(VulkanTexture.Create(_context, image));
        }

        if (_sheets.Count > 0)
        {
            CreateSheetSets();
        }

        _modelVertices = VulkanBuffer.CreateDeviceLocal<TerrainTreeVertex>(
            _context, _plan.ModelVertices, BufferUsageFlags.VertexBufferBit);
        _modelIndices = VulkanBuffer.CreateDeviceLocal<uint>(
            _context, _plan.ModelIndices, BufferUsageFlags.IndexBufferBit);

        // Written every time the near band is reselected, which is why it is host-visible
        // and sized for the widest band the budget could ever ask for.
        _modelInstances = VulkanBuffer.CreateHostVisible(
            _context,
            (ulong)(_plan.ModelInstanceData.Length * sizeof(float)),
            BufferUsageFlags.VertexBufferBit);
    }

    /// <summary>A descriptor set for each of the trees' own textures.</summary>
    private void CreateSheetSets()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };

        if (_vk.CreateDescriptorSetLayout(_context.Device, in layoutInfo, null, out _sheetLayout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the tree texture descriptor layout.");
        }

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = (uint)_sheets.Count,
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = (uint)_sheets.Count,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };

        if (_vk.CreateDescriptorPool(_context.Device, in poolInfo, null, out _sheetPool)
            != Result.Success)
        {
            throw new VulkanException("Could not create the tree texture descriptor pool.");
        }

        foreach (VulkanTexture texture in _sheets)
        {
            DescriptorSetLayout layout = _sheetLayout;
            var allocate = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _sheetPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };

            if (_vk.AllocateDescriptorSets(_context.Device, in allocate, out DescriptorSet set)
                != Result.Success)
            {
                throw new VulkanException("Could not allocate a tree texture descriptor set.");
            }

            var image = new DescriptorImageInfo
            {
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                ImageView = texture.View,
                Sampler = texture.Sampler,
            };

            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &image,
            };

            _vk.UpdateDescriptorSets(_context.Device, 1, in write, 0, null);
            _sheetSets.Add(set);
        }
    }

    private ShaderModule CreateModule(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };

            if (_vk.CreateShaderModule(_context.Device, in createInfo, null, out ShaderModule module)
                != Result.Success)
            {
                throw new VulkanException("Could not create a terrain shader module.");
            }

            return module;
        }
    }

    private void CreateDescriptors()
    {
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[6];

        for (uint i = 0; i < 6; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 6,
            PBindings = bindings,
        };

        if (_vk.CreateDescriptorSetLayout(_context.Device, in layoutInfo, null, out _setLayout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the terrain descriptor layout.");
        }

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 6,
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };

        if (_vk.CreateDescriptorPool(_context.Device, in poolInfo, null, out _pool) != Result.Success)
        {
            throw new VulkanException("Could not create the terrain descriptor pool.");
        }

        DescriptorSetLayout setLayout = _setLayout;
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };

        if (_vk.AllocateDescriptorSets(_context.Device, in allocate, out _set) != Result.Success)
        {
            throw new VulkanException("Could not allocate the terrain descriptor set.");
        }

        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[6];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[6];

        for (int i = 0; i < 6; i++)
        {
            VulkanTexture texture = _textures[i]!;
            images[i] = new DescriptorImageInfo
            {
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                ImageView = texture.View,
                Sampler = texture.Sampler,
            };

            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = images + i,
            };
        }

        _vk.UpdateDescriptorSets(_context.Device, 6, writes, 0, null);
    }

    private void BuildPipelines(Format colorFormat, Format depthFormat)
    {
        DescriptorSetLayout setLayout = _setLayout;

        var pushConstants = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<TerrainConstants>(),
        };

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstants,
        };

        if (_vk.CreatePipelineLayout(_context.Device, in layoutInfo, null, out _layout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the terrain pipeline layout.");
        }

        var skyPush = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<TerrainSkyConstants>(),
        };

        var skyLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &skyPush,
        };

        if (_vk.CreatePipelineLayout(_context.Device, in skyLayoutInfo, null, out _skyLayout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the horizon sky pipeline layout.");
        }

        // Terrain: one 24-byte stream of position and normal.
        var terrainBinding = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = (uint)Marshal.SizeOf<TerrainVertex>(),
            InputRate = VertexInputRate.Vertex,
        };

        VertexInputAttributeDescription* terrainAttributes =
            stackalloc VertexInputAttributeDescription[2]
            {
                new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
                new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
            };

        _pipeline = BuildOne(
            colorFormat, depthFormat, _vertexModule, _fragmentModule, _layout,
            1, &terrainBinding, 2, terrainAttributes, depthWrite: true);

        // Trees: every impostor shape in stream 0, one 24-byte placement per instance in
        // stream 1. The shapes share a buffer and are drawn as ranges of it, so a hillside
        // of four species is four draws rather than four pipelines.
        VertexInputBindingDescription* treeBindings =
            stackalloc VertexInputBindingDescription[2]
            {
                new()
                {
                    Binding = 0,
                    Stride = (uint)Marshal.SizeOf<TerrainVertex>(),
                    InputRate = VertexInputRate.Vertex,
                },
                new()
                {
                    Binding = 1,
                    Stride = 6 * sizeof(float),
                    InputRate = VertexInputRate.Instance,
                },
            };

        VertexInputAttributeDescription* treeAttributes =
            stackalloc VertexInputAttributeDescription[5]
            {
                new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
                new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
                new() { Location = 2, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 0 },
                new() { Location = 3, Binding = 1, Format = Format.R32Sfloat, Offset = 16 },
                new() { Location = 4, Binding = 1, Format = Format.R32Sfloat, Offset = 20 },
            };

        _treePipeline = BuildOne(
            colorFormat, depthFormat, _treeVertexModule, _treeFragmentModule, _layout,
            2, treeBindings, 5, treeAttributes, depthWrite: true);

        // The modelled trees of the near band. Two descriptor sets rather than one: the
        // splat and the tint it shares with the ground, and the one sheet it is painted
        // with, which changes per part.
        if (_plan.Models.Length > 0 && _sheetLayout.Handle != 0)
        {
            DescriptorSetLayout* modelSets = stackalloc DescriptorSetLayout[2]
            {
                _setLayout,
                _sheetLayout,
            };

            var modelLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = modelSets,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstants,
            };

            if (_vk.CreatePipelineLayout(_context.Device, in modelLayoutInfo, null, out _modelLayout)
                != Result.Success)
            {
                throw new VulkanException("Could not create the horizon tree pipeline layout.");
            }

            VertexInputBindingDescription* modelBindings =
                stackalloc VertexInputBindingDescription[2]
                {
                    new()
                    {
                        Binding = 0,
                        Stride = (uint)Marshal.SizeOf<TerrainTreeVertex>(),
                        InputRate = VertexInputRate.Vertex,
                    },
                    new()
                    {
                        Binding = 1,
                        Stride = Stride * sizeof(float),
                        InputRate = VertexInputRate.Instance,
                    },
                };

            VertexInputAttributeDescription* modelAttributes =
                stackalloc VertexInputAttributeDescription[6]
                {
                    new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
                    new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
                    new() { Location = 2, Binding = 0, Format = Format.R32G32Sfloat, Offset = 24 },
                    new() { Location = 3, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 0 },
                    new() { Location = 4, Binding = 1, Format = Format.R32Sfloat, Offset = 16 },
                    new() { Location = 5, Binding = 1, Format = Format.R32Sfloat, Offset = 20 },
                };

            _modelPipeline = BuildOne(
                colorFormat, depthFormat, _modelVertexModule, _modelFragmentModule, _modelLayout,
                2, modelBindings, 6, modelAttributes, depthWrite: true);
        }

        // The sky: no vertex input at all, and no depth writes — it must lose to
        // everything and stop nothing.
        _skyPipeline = BuildOne(
            colorFormat, depthFormat, _skyVertexModule, _skyFragmentModule, _skyLayout,
            0, null, 0, null, depthWrite: false);
    }

    private Pipeline BuildOne(
        Format colorFormat,
        Format depthFormat,
        ShaderModule vertex,
        ShaderModule fragment,
        PipelineLayout layout,
        uint bindingCount,
        VertexInputBindingDescription* bindings,
        uint attributeCount,
        VertexInputAttributeDescription* attributes,
        bool depthWrite)
    {
        nint entryPoint = SilkMarshal.StringToPtr("main");

        try
        {
            PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertex,
                PName = (byte*)entryPoint,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragment,
                PName = (byte*)entryPoint,
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = bindingCount,
                PVertexBindingDescriptions = bindings,
                VertexAttributeDescriptionCount = attributeCount,
                PVertexAttributeDescriptions = attributes,
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };

            DynamicState* dynamicStates = stackalloc DynamicState[2]
            {
                DynamicState.Viewport,
                DynamicState.Scissor,
            };

            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };

            var viewport = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };

            // No culling: whether a grid's winding survives the world's handedness is
            // exactly the kind of thing that would otherwise be diagnosed as a black
            // screen, and a heightfield seen from above has almost no back faces anyway.
            var rasterization = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1f,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };

            var depth = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = depthWrite,
                DepthCompareOp = CompareOp.LessOrEqual,
            };

            PipelineColorBlendAttachmentState* blendAttachments =
                stackalloc PipelineColorBlendAttachmentState[(int)GBuffer.Targets];

            blendAttachments[GBuffer.Colour] = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };

            for (int i = 1; i < (int)GBuffer.Targets; i++)
            {
                blendAttachments[i] = default;
            }

            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = GBuffer.Targets,
                PAttachments = blendAttachments,
            };

            Format* colors = stackalloc Format[(int)GBuffer.Targets]
            {
                colorFormat,
                GBuffer.NormalFormat,
                GBuffer.MotionFormat,
                GBuffer.LightFormat,
            };
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = GBuffer.Targets,
                PColorAttachmentFormats = colors,
                DepthAttachmentFormat = depthFormat,
            };

            var createInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewport,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depth,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = layout,
            };

            Result created = _vk.CreateGraphicsPipelines(
                _context.Device, default, 1, in createInfo, null, out Pipeline pipeline);

            if (created != Result.Success)
            {
                throw new VulkanException($"Could not create a terrain pipeline: {created}.");
            }

            return pipeline;
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }
    }
}
