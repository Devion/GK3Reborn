# ADR 0008: Write the shading shaders in GLSL

- Status: accepted
- Date: 2026-08-19
- Amends: `Plan/01-architecture.md` section 1 (HLSL compiled with DXC)

## Context

The plan chose HLSL compiled with DXC. The compiler was already changed:
`docs/rendering.md` records that DXC ships with the Vulkan SDK, that requiring
the SDK to build the project at all is a real barrier for a contributor who only
wants to change gameplay code, and that shaderc compiles HLSL just as well and
arrives as a NuGet package. HLSL itself stood.

It no longer can. shaderc uses glslang for both languages, and glslang implements
inline ray tracing only in its GLSL front end. Its HLSL front end does not know
`RaytracingAccelerationStructure` at all — it fails at the declaration, before
reaching anything ray-related:

```
probe.frag:1: error: 'declaration' : Expected
probe.frag(1): error at column 53, HLSL parsing failed.
```

The same shader written in GLSL against `GL_EXT_ray_query` compiles. Both were
tried before anything was built on either.

That leaves three options:

1. **Adopt DXC for the shaders that ray trace.** DXC's HLSL front end does
   support `RayQuery`. It also brings back the Vulkan SDK prerequisite, or a
   second native dependency to ship and keep working on Windows and Linux — which
   is the cost the toolchain was arranged to avoid.
2. **Keep HLSL and do the ray tracing in a separate pass.** Shading and visibility
   would then be split across two shaders and an intermediate target, for no
   reason other than language. That is a real architectural cost paid to preserve
   a file extension.
3. **Write the shading shaders in GLSL.**

## Decision

The mesh shaders are GLSL. `ShaderCompiler` takes a language and compiles either.

The choice is per shader rather than global. Anything that never needs a ray query
can stay HLSL, and the compiler keeps supporting it; what forced the change is
specifically inline ray tracing.

One source serves both the raster and the ray-traced pipeline, with the ray paths
behind `RAY_TRACING`. A device without the extensions gets a shader that cannot
name an acceleration structure at all — necessary, because Vulkan requires every
statically used binding to be valid whether its branch runs or not.

## Consequences

**Good.** No new native dependency and no second compiler to ship. Ray tracing is
inline in the shading pass, so visibility is computed exactly where it is used
rather than round-tripped through a target. GLSL's ray query extension is also the
better-documented of the two, and its diagnostics from glslang are considerably
clearer than the HLSL front end's.

**Bad.** The plan's stated language no longer describes the shaders that matter,
which is a documentation cost paid every time someone reads section 1 without
this ADR. Anyone who knows HLSL and not GLSL has a small amount to learn. If DXC
is ever adopted for another reason, this decision is worth revisiting rather than
grandfathered.

**Neutral.** The SPIR-V is the same either way, and so is everything downstream of
it — the cache, the pipeline, the reflection. Nothing outside `MeshShaders` and
one parameter of `ShaderCompiler` knows which language was used.
