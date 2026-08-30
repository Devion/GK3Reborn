// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>Where the camera is pointing, and how wide it sees.</summary>
/// <param name="Forward">xyz: where the camera looks, already turned by the azimuth.</param>
/// <param name="Right">xyz: its right; w: the tangent of half the horizontal field of view.</param>
/// <param name="Up">xyz: its up; w: the tangent of half the vertical field of view.</param>
/// <param name="Viewport">xy: the size in pixels.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SkyboxConstants(
    Vector4 Forward, Vector4 Right, Vector4 Up, Vector4 Viewport);

/// <summary>The sky, sampled from a cube through the ray each pixel looks along.</summary>
/// <remarks>
/// One triangle and a cubemap. Nothing is passed between the stages: the direction to sample
/// is worked out in the fragment stage from where the fragment is, which is the one input
/// that was ever demonstrably reaching it.
/// </remarks>
public static class SkyboxShaders
{
    /// <summary>Works out where the camera is pointing and how wide it sees.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="azimuth">How far the sky is turned, in radians about the vertical.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The block the fragment stage reads.</returns>
    /// <remarks>
    /// <para>
    /// Shared, because it is the one place a sky can be wrong in a way that looks nearly
    /// right. The basis is built the way <c>CreateLookAtLeftHanded</c> builds it — this
    /// world is left-handed — rather than read out of the view matrix: the rows of a view
    /// matrix are the basis of the inverse, so taking them gives a sky that is plausible
    /// until the camera turns and then points the wrong way down every axis.
    /// </para>
    /// <para>
    /// Turning the sky by its azimuth is turning the ray the other way, which is why the
    /// rotation is negative. It is applied to three vectors rather than as a matrix through
    /// the whole pass.
    /// </para>
    /// </remarks>
    public static SkyboxConstants Describe(
        Camera camera, float azimuth, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(camera.Up, forward));
        Vector3 up = Vector3.Cross(forward, right);

        Matrix4x4 turn = Matrix4x4.CreateRotationY(-azimuth);

        float tanY = MathF.Tan(camera.FieldOfView / 2f);
        float tanX = tanY * width / Math.Max(1, height);

        return new SkyboxConstants(
            new Vector4(Vector3.TransformNormal(forward, turn), 0f),
            new Vector4(Vector3.TransformNormal(right, turn), tanX),
            new Vector4(Vector3.TransformNormal(up, turn), tanY),
            new Vector4(width, height, 0f, 0f));
    }

    /// <summary>The vertex stage.</summary>
    public const string Vertex = """
        #version 450

        // One triangle covering the screen, from the vertex index alone. No vertex buffer,
        // no attributes and nothing passed to the fragment stage: the direction to sample
        // is worked out from where the fragment is, which is the one input that was ever
        // demonstrably reaching it.
        void main()
        {
            vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);

            // Depth at the far plane, so the sky loses to anything the room drew. Written
            // as clip coordinates outright, which is also why nothing here can be clipped
            // by a near plane.
            gl_Position = vec4((corner * 2.0) - 1.0, 1.0, 1.0);
        }
        """;

    /// <summary>The fragment stage.</summary>
    public const string Fragment = """
        #version 450

        layout(binding = 0) uniform samplerCube sky;

        layout(push_constant) uniform Push
        {
            vec4 forward;   // xyz: where the camera looks, already turned by the azimuth
            vec4 right;     // xyz: its right, scaled by nothing; w: tan of half the horizontal fov
            vec4 up;        // xyz: its up;                      w: tan of half the vertical fov
            vec4 viewport;  // xy: size in pixels
        } push;

        layout(location = 0) out vec4 outColor;

        void main()
        {
            // The ray through this pixel, built from the camera's own basis rather than by
            // inverting a projection. It is the same arithmetic the projection does, run
            // forwards: an inverse is a thing that can be ill-conditioned or wrong in a way
            // that is invisible until every pixel comes back with the same answer.
            vec2 ndc = ((gl_FragCoord.xy / push.viewport.xy) * 2.0) - 1.0;

            // gl_FragCoord counts down the screen and up counts up it.
            vec3 direction = push.forward.xyz
                           + (push.right.xyz * (ndc.x * push.right.w))
                           - (push.up.xyz * (ndc.y * push.up.w));

            outColor = vec4(texture(sky, normalize(direction)).rgb, 1.0);
        }
        """;
}
