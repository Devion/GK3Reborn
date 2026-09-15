using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering;

namespace GK3Reborn;

/// <summary>Recording a run to numbered frames on a fixed clock, with the camera on a rail.</summary>
public static partial class Application
{
    /// <summary>One point on a camera rail, in the scene file's own terms.</summary>
    /// <param name="Time">Seconds from the first recorded frame.</param>
    /// <param name="Position">Where the camera stands.</param>
    /// <param name="Angle">Heading and pitch in degrees, as a .SIF camera writes them: positive pitch looks down.</param>
    /// <param name="Fov">Vertical field of view in degrees.</param>
    /// <param name="Cut">Jump here rather than travel from the key before.</param>
    /// <param name="Ease">Ease in and out of the move that ends here.</param>
    private readonly record struct RailKey(double Time, Vector3 Position, Vector2 Angle, float Fov, bool Cut, bool Ease);

    /// <summary>One point on a model's path.</summary>
    /// <param name="Time">Seconds from the first recorded frame.</param>
    /// <param name="Position">Where the model's pivot stands.</param>
    /// <param name="Heading">Degrees it has turned from how the room placed it.</param>
    private readonly record struct PathKey(double Time, Vector3 Position, float Heading);

    /// <summary>A placed model driven along keyed points, such as a van arriving.</summary>
    /// <param name="Name">The model or its noun.</param>
    /// <param name="Pivot">The point, in the room as placed, that the keys move and turn about.</param>
    /// <param name="Keys">The points, in time order.</param>
    /// <param name="Until">Optional last time the path controls the model, before normal movement resumes.</param>
    private sealed record ModelPath(string Name, Vector3 Pivot, PathKey[] Keys, double? Until);

    /// <summary>--record DIR [--rail FILE]: a fixed step per frame, every frame written as a PNG, and the view taken from the rail.</summary>
    private sealed class FilmRecording : IDisposable
    {
        private readonly RecordingFrames _frames;
        private readonly RailKey[] _keys;
        private readonly ModelPath[] _paths;
        private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

        private FilmRecording(string directory, int fps, int warmup, RailKey[] keys, ModelPath[] paths)
        {
            _frames = new RecordingFrames(directory);
            _keys = keys;
            _paths = paths;
            Fps = fps;
            Warmup = warmup;
        }

        /// <summary>Frames a second, which is also the clock the room runs on.</summary>
        public int Fps { get; }

        /// <summary>Frames drawn and thrown away first, so the denoisers have settled before anything is kept.</summary>
        public int Warmup { get; }

        /// <summary>How long each frame lasts in scene time.</summary>
        public float Step => 1f / Fps;

        /// <summary>Reads --record, --rail, --record-fps and --record-warmup.</summary>
        /// <returns>The recording, or null when --record is absent.</returns>
        /// <param name="args">The command line.</param>
        public static FilmRecording? From(string[] args)
        {
            if (Option(args, "--record") is not { Length: > 0 } directory)
            {
                return null;
            }

            int fps = 30;
            int warmup = 0;
            RailKey[] keys = [];
            ModelPath[] paths = [];

            if (Option(args, "--rail") is { Length: > 0 } railPath)
            {
                using JsonDocument rail = JsonDocument.Parse(File.ReadAllText(railPath));
                JsonElement root = rail.RootElement;

                fps = root.TryGetProperty("fps", out JsonElement f) ? f.GetInt32() : fps;
                warmup = root.TryGetProperty("warmup", out JsonElement w) ? w.GetInt32() : warmup;
                keys = [.. root.GetProperty("keys").EnumerateArray().Select(Key).OrderBy(k => k.Time)];
                paths = root.TryGetProperty("models", out JsonElement models) ? [.. models.EnumerateArray().Select(PathOf)] : [];
            }

            fps = int.TryParse(Option(args, "--record-fps"), CultureInfo.InvariantCulture, out int askedFps) && askedFps > 0 ? askedFps : fps;
            warmup = int.TryParse(Option(args, "--record-warmup"), CultureInfo.InvariantCulture, out int askedWarmup) ? askedWarmup : warmup;

            Directory.CreateDirectory(directory);
            Log.Info($"Recording: {fps} fps into {directory}, {warmup} warm-up frames, {keys.Length} rail keys, {paths.Length} model paths");

            return new FilmRecording(directory, fps, warmup, keys, paths);
        }

        /// <summary>Puts every model with a path where its path has it on this frame.</summary>
        /// <param name="frame">The frame about to be drawn, counting warm-up.</param>
        /// <param name="world">The room, for finding the models by name.</param>
        /// <param name="geometry">Where they are drawn.</param>
        public void Move(int frame, Game.SceneUpdate world, Rendering.Geometry.SceneGeometry geometry)
        {
            double time = (double)(frame - Warmup) / Fps;

            foreach (ModelPath path in _paths)
            {
                // Release passengers to their normal animation and walking after a vehicle arrives.
                if (path.Until is { } until && time > until)
                {
                    continue;
                }

                if (world.ModelNamed(path.Name) is not { } model)
                {
                    if (_missing.Add(path.Name))
                    {
                        Log.Warning($"Recording: no model {path.Name} in the room to move");
                    }

                    continue;
                }

                PathKey[] keys = path.Keys;
                int next = Array.FindIndex(keys, k => k.Time > time);

                (Vector3 at, float heading) = next switch
                {
                    -1 => (keys[^1].Position, keys[^1].Heading),
                    0 => (keys[0].Position, keys[0].Heading),
                    _ => Along(keys[next - 1], keys[next], time),
                };

                Matrix4x4 moved = model.Transform * Matrix4x4.CreateTranslation(-path.Pivot) *
                    Matrix4x4.CreateRotationY(heading * MathF.PI / 180f) * Matrix4x4.CreateTranslation(at);

                geometry.MoveModel(model.Placement, moved);
            }
        }

        /// <summary>The view the rail puts the camera at on this frame.</summary>
        /// <returns>The view, or null when there is no rail.</returns>
        /// <param name="frame">The frame about to be drawn, counting warm-up.</param>
        /// <param name="drawn">The view the room would have used, for its planes and light.</param>
        public Camera? View(int frame, Camera drawn)
        {
            if (_keys.Length == 0)
            {
                return null;
            }

            double time = (double)(frame - Warmup) / Fps;
            int next = Array.FindIndex(_keys, k => k.Time > time);

            (Vector3 position, Vector2 angle, float fov) = next switch
            {
                -1 => (_keys[^1].Position, _keys[^1].Angle, _keys[^1].Fov),
                0 => (_keys[0].Position, _keys[0].Angle, _keys[0].Fov),
                _ => Between(_keys[next - 1], _keys[next], time),
            };

            float yaw = angle.X * MathF.PI / 180f;
            float pitch = angle.Y * MathF.PI / 180f;
            var forward = new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), -MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw));

            return new Camera
            {
                Position = position,
                Target = position + forward,
                Up = Vector3.UnitY,
                FieldOfView = fov * MathF.PI / 180f,
                NearPlane = drawn.NearPlane,
                FarPlane = drawn.FarPlane,
                LightDirection = drawn.LightDirection,
                Background = drawn.Background,
            };
        }

        /// <summary>Writes the frame just presented, unless it is still warming up.</summary>
        /// <param name="renderer">What drew it.</param>
        /// <param name="frame">The frame just drawn, counting warm-up.</param>
        public void Write(IRenderer renderer, int frame)
        {
            if (frame < Warmup || renderer.Capture() is not { } picture)
            {
                return;
            }

            _frames.Write(frame - Warmup, picture);
        }

        public void Complete() => _frames.Complete();

        public void Dispose() => _frames.Dispose();

        // A key held until the next one's time when that one is a cut, otherwise travelled to, headings the short way round.
        private static (Vector3, Vector2, float) Between(RailKey from, RailKey to, double time)
        {
            if (to.Cut)
            {
                return (from.Position, from.Angle, from.Fov);
            }

            float t = (float)Math.Clamp((time - from.Time) / Math.Max(1e-6, to.Time - from.Time), 0, 1);
            t = to.Ease ? t * t * (3f - (2f * t)) : t;

            float turn = ((((to.Angle.X - from.Angle.X) % 360f) + 540f) % 360f) - 180f;
            var angle = new Vector2(from.Angle.X + (turn * t), float.Lerp(from.Angle.Y, to.Angle.Y, t));

            return (Vector3.Lerp(from.Position, to.Position, t), angle, float.Lerp(from.Fov, to.Fov, t));
        }

        private static (Vector3, float) Along(PathKey from, PathKey to, double time)
        {
            float t = (float)Math.Clamp((time - from.Time) / Math.Max(1e-6, to.Time - from.Time), 0, 1);

            return (Vector3.Lerp(from.Position, to.Position, t), float.Lerp(from.Heading, to.Heading, t));
        }

        private static ModelPath PathOf(JsonElement model)
        {
            float[] pivot = [.. model.GetProperty("pivot").EnumerateArray().Select(v => v.GetSingle())];

            PathKey[] keys = [.. model.GetProperty("keys").EnumerateArray().Select(k =>
            {
                float[] pos = [.. k.GetProperty("pos").EnumerateArray().Select(v => v.GetSingle())];
                return new PathKey(k.GetProperty("t").GetDouble(), new Vector3(pos[0], pos[1], pos[2]), k.GetProperty("heading").GetSingle());
            }).OrderBy(k => k.Time)];

            return new ModelPath(model.GetProperty("name").GetString() ?? string.Empty, new Vector3(pivot[0], pivot[1], pivot[2]), keys,
                model.TryGetProperty("until", out JsonElement until) ? until.GetDouble() : null);
        }

        private static RailKey Key(JsonElement key)
        {
            float[] pos = [.. key.GetProperty("pos").EnumerateArray().Select(v => v.GetSingle())];
            float[] angle = [.. key.GetProperty("angle").EnumerateArray().Select(v => v.GetSingle())];

            return new RailKey( key.GetProperty("t").GetDouble(), new Vector3(pos[0], pos[1], pos[2]), new Vector2(angle[0], angle[1]),
                key.TryGetProperty("fov", out JsonElement fov) ? fov.GetSingle() : 60f, key.TryGetProperty("cut", out JsonElement cut) && cut.GetBoolean(),
                key.TryGetProperty("ease", out JsonElement ease) && ease.GetBoolean());
        }
    }
}
