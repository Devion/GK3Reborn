// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Rendering.Shaders;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

internal static class VulkanBindings
{
    /// <summary>The descriptor set layout bindings a shader layout describes.</summary>
    /// <param name="layout">What the pipeline binds.</param>
    /// <param name="set">Which set to take, for a layout that spans more than one.</param>
    /// <returns>The bindings, in the order the layout gives them.</returns>
    public static DescriptorSetLayoutBinding[] Of(ShaderLayout layout, uint set = 0)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return
        [
            .. layout.Bindings
                .Where(binding => binding.Set == set)
                .Select(binding => new DescriptorSetLayoutBinding
                {
                    Binding = binding.Binding,
                    DescriptorType = TypeOf(binding.Kind),
                    DescriptorCount = binding.Count,
                    StageFlags = StagesOf(binding.Stages),
                }),
        ];
    }

    /// <summary>How a binding's kind is spelled in Vulkan.</summary>
    private static DescriptorType TypeOf(ShaderBindingKind kind) => kind switch
    {
        ShaderBindingKind.UniformBuffer => DescriptorType.UniformBuffer,
        ShaderBindingKind.ReadOnlyStorageBuffer => DescriptorType.StorageBuffer,
        ShaderBindingKind.StorageBuffer => DescriptorType.StorageBuffer,
        ShaderBindingKind.CombinedImageSampler => DescriptorType.CombinedImageSampler,
        ShaderBindingKind.SampledImage => DescriptorType.SampledImage,
        ShaderBindingKind.StorageImage => DescriptorType.StorageImage,
        ShaderBindingKind.Sampler => DescriptorType.Sampler,
        ShaderBindingKind.AccelerationStructure => DescriptorType.AccelerationStructureKhr,
        _ => throw new VulkanException($"No Vulkan descriptor type for {kind}."),
    };

    /// <summary>Which stages may read a binding.</summary>
    private static ShaderStageFlags StagesOf(ShaderStages stages)
    {
        ShaderStageFlags flags = ShaderStageFlags.None;

        if (stages.HasFlag(ShaderStages.Vertex))
        {
            flags |= ShaderStageFlags.VertexBit;
        }

        if (stages.HasFlag(ShaderStages.Fragment))
        {
            flags |= ShaderStageFlags.FragmentBit;
        }

        if (stages.HasFlag(ShaderStages.Compute))
        {
            flags |= ShaderStageFlags.ComputeBit;
        }

        return flags;
    }
}
