// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using Silk.NET.Vulkan;

using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>One compute stage: its module, its layout and the pipeline itself.</summary>
/// <remarks>
/// Every stage of the denoiser is this same shape — one shader, one descriptor set, and a
/// small push constant — so they share one class rather than one file each.
/// </remarks>
internal sealed unsafe class ComputePipeline : IDisposable
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly ShaderModule _module;

    private ComputePipeline(
        Vk vk,
        Device device,
        ShaderModule module,
        DescriptorSetLayout setLayout,
        PipelineLayout layout,
        Pipeline handle)
    {
        _vk = vk;
        _device = device;
        _module = module;
        SetLayout = setLayout;
        Layout = layout;
        Handle = handle;
    }

    /// <summary>The pipeline.</summary>
    public Pipeline Handle { get; }

    /// <summary>Its layout, for binding sets and pushing constants.</summary>
    public PipelineLayout Layout { get; }

    /// <summary>The layout of set zero, for allocating sets against.</summary>
    public DescriptorSetLayout SetLayout { get; }

    /// <summary>Compiles a stage and builds everything it needs to run.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Compiler for the source.</param>
    /// <param name="source">GLSL for the stage.</param>
    /// <param name="bindings">What set zero holds.</param>
    /// <param name="pushConstantBytes">Size of the push constant range, or zero.</param>
    /// <returns>The stage, ready to dispatch.</returns>
    public static ComputePipeline Create(
        VulkanContext context,
        ShaderCompiler compiler,
        string source,
        ReadOnlySpan<DescriptorSetLayoutBinding> bindings,
        uint pushConstantBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        Vk vk = context.Api;
        Device device = context.Device;

        byte[] code = compiler.Compile(
            source, ShaderStage.Compute, "denoiser", "main", ShaderLanguage.Glsl);
        ShaderModule module;

        fixed (byte* spirv = code)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)spirv,
            };

            if (vk.CreateShaderModule(device, in moduleInfo, null, out module) != Result.Success)
            {
                throw new VulkanException("Could not create a compute shader module.");
            }
        }

        DescriptorSetLayout setLayout;

        fixed (DescriptorSetLayoutBinding* declared = bindings)
        {
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = declared,
            };

            if (vk.CreateDescriptorSetLayout(device, in layoutInfo, null, out setLayout) !=
                Result.Success)
            {
                vk.DestroyShaderModule(device, module, null);
                throw new VulkanException("Could not create a compute descriptor layout.");
            }
        }

        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Size = pushConstantBytes,
        };

        DescriptorSetLayout local = setLayout;

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &local,
            PushConstantRangeCount = pushConstantBytes > 0 ? 1u : 0u,
            PPushConstantRanges = pushConstantBytes > 0 ? &range : null,
        };

        if (vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out PipelineLayout layout) !=
            Result.Success)
        {
            vk.DestroyDescriptorSetLayout(device, setLayout, null);
            vk.DestroyShaderModule(device, module, null);
            throw new VulkanException("Could not create a compute pipeline layout.");
        }

        byte* entryPoint = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };

        var createInfo = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = entryPoint,
            },
            Layout = layout,
        };

        if (vk.CreateComputePipelines(device, default, 1, in createInfo, null, out Pipeline handle) !=
            Result.Success)
        {
            vk.DestroyPipelineLayout(device, layout, null);
            vk.DestroyDescriptorSetLayout(device, setLayout, null);
            vk.DestroyShaderModule(device, module, null);
            throw new VulkanException("Could not create a compute pipeline.");
        }

        return new ComputePipeline(vk, device, module, setLayout, layout, handle);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _vk.DestroyPipeline(_device, Handle, null);
        _vk.DestroyPipelineLayout(_device, Layout, null);
        _vk.DestroyDescriptorSetLayout(_device, SetLayout, null);
        _vk.DestroyShaderModule(_device, _module, null);
    }
}
