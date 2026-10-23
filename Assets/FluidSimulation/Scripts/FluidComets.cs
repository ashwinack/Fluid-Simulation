using System.Collections.Generic;
using UnityEngine;

namespace FluidSim
{
    /// <summary>How one style draws its single comet per pointer. Sizes are for a 1080-tall wall.</summary>
    public struct CometLook
    {
        public bool enabled;
        public float tailSeconds;   // how much of the recent path the tail covers
        public float headRadius;    // pixels
        public float tailWidth;     // pixels, at the head end; tapers to nothing
        public float intensity;
        public float coreWhite;     // white-hot centre of head and tail
        public float coast;         // how far it keeps flying after the finger lifts (0 = stops)
        public float smoothing;     // 0 = head on the finger, 1 = sweeping smooth arcs behind it
        public float coma;          // brightness of the wide soft glow round the head
    }

    /// <summary>
    /// One comet per pointer - finger, mouse or attract finger. Each pointer's recent
    /// path is recorded, and every frame it is drawn as a single ribbon that is wide
    /// and bright at the pointer and tapers to nothing at its oldest point, with a
    /// big glowing head on top. When the pointer stops the tail shrinks back into
    /// the head; when it lifts, the tail fades away behind where it was.
    /// </summary>
    public class FluidComets
    {
        struct Point
        {
            public Vector2 uv;
            public float time;
            public Color color;
        }

        class Track
        {
            public int id;
            public readonly List<Point> points = new List<Point>();   // oldest first
            public bool live;
            public bool started;
            public Vector2 head;         // smoothed head position (wall UV)
            public Vector2 vel;          // head velocity, wall UV per second
            public float lastTime;
            public Color color;
            public float coastUntil = -1f;
        }

        const int MaxPoints = 400;

        readonly Material mat;
        readonly Mesh mesh;
        readonly List<Track> tracks = new List<Track>();
        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Color> colors = new List<Color>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<Vector2> kinds = new List<Vector2>();
        readonly List<int> tris = new List<int>();

        public FluidComets(Shader shader)
        {
            mat = new Material(shader);
            mesh = new Mesh { name = "Comets" };
            mesh.MarkDynamic();
        }

        public void Clear() => tracks.Clear();

        /// <summary>Call at the start of a frame, before <see cref="Record"/>.</summary>
        public void BeginFrame()
        {
            for (int i = 0; i < tracks.Count; i++) tracks[i].live = false;
        }

        /// <summary>A pointer is down at <paramref name="uv"/> (wall UV) with this colour.</summary>
        public void Record(int id, Vector2 uv, Color color, float time, Vector2 wallPx, float smoothing)
        {
            Track t = null;
            for (int i = 0; i < tracks.Count; i++) if (tracks[i].id == id && tracks[i].coastUntil < 0f) { t = tracks[i]; break; }
            if (t == null) { t = new Track { id = id }; tracks.Add(t); }
            t.live = true;
            t.color = color;

            // Trajectory: the head chases the finger. At 0 it sits right on it; towards
            // 1 it lags and sweeps round in smooth arcs.
            float dt = t.started ? Mathf.Max(time - t.lastTime, 1e-4f) : 0f;
            Vector2 before = t.head;
            if (!t.started) { t.head = uv; t.started = true; }
            else
            {
                float rate = Mathf.Lerp(60f, 2.5f, Mathf.Clamp01(smoothing));
                t.head = Vector2.Lerp(t.head, uv, 1f - Mathf.Exp(-rate * dt));
                t.vel = Vector2.Lerp(t.vel, (t.head - before) / dt, 0.3f);
            }
            t.lastTime = time;
            AddPoint(t, t.head, color, time, wallPx);
        }

        /// <summary>
        /// After the finger lifts, a comet with <paramref name="coast"/> keeps flying
        /// along its last direction, slowing, before it fades away.
        /// </summary>
        public void Advance(float time, float coast, Vector2 wallPx)
        {
            foreach (var t in tracks)
            {
                if (t.live || !t.started) continue;
                float dt = Mathf.Max(time - t.lastTime, 0f);
                t.lastTime = time;
                if (t.coastUntil < 0f) t.coastUntil = time + 0.9f * coast;
                if (coast <= 0f || time > t.coastUntil) continue;
                t.head += t.vel * dt;
                t.vel *= Mathf.Exp(-1.2f * dt);
                AddPoint(t, t.head, t.color, time, wallPx);
            }
        }

        void AddPoint(Track t, Vector2 uv, Color color, float time, Vector2 wallPx)
        {
            // A new point only once the head has moved half a pixel, so a resting
            // finger does not pile up points on the spot.
            var pts = t.points;
            if (pts.Count > 0)
            {
                Vector2 d = Vector2.Scale(uv - pts[pts.Count - 1].uv, wallPx);
                if (d.sqrMagnitude < 0.25f)
                {
                    var last = pts[pts.Count - 1];
                    last.time = time;       // still here: the head stays fresh
                    last.color = color;
                    pts[pts.Count - 1] = last;
                    return;
                }
            }
            pts.Add(new Point { uv = uv, time = time, color = color });
            if (pts.Count > MaxPoints) pts.RemoveAt(0);
        }

        /// <summary>Drop path older than the tail, and pointers whose tail has gone.</summary>
        public void Prune(float time, float tailSeconds)
        {
            for (int i = tracks.Count - 1; i >= 0; i--)
            {
                var pts = tracks[i].points;
                int old = 0;
                while (old < pts.Count - 1 && time - pts[old].time > tailSeconds) old++;
                if (old > 0) pts.RemoveRange(0, old);
                bool coasting = tracks[i].coastUntil >= 0f && time <= tracks[i].coastUntil;
                if (!tracks[i].live && !coasting && (pts.Count == 0 || time - pts[pts.Count - 1].time > tailSeconds))
                    tracks.RemoveAt(i);
            }
        }

        /// <summary>Draw every comet into <paramref name="target"/> (additive).</summary>
        public void Draw(RenderTexture target, in CometLook look, float time)
        {
            if (target == null || tracks.Count == 0) return;

            verts.Clear(); colors.Clear(); uvs.Clear(); kinds.Clear(); tris.Clear();
            var size = new Vector2(target.width, target.height);
            float px = target.height / 1080f;
            float tail = Mathf.Max(look.tailSeconds, 0.05f);

            foreach (var t in tracks) AddComet(t, look, time, tail, size, px);
            if (verts.Count == 0) return;

            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, kinds);
            mesh.SetTriangles(tris, 0, false);

            mat.SetFloat("uCoreWhite", look.coreWhite);
            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(target);
            mat.SetPass(0);
            Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
            RenderTexture.active = prev;
        }

        void AddComet(Track t, in CometLook look, float time, float tail, Vector2 size, float px)
        {
            var pts = t.points;
            if (pts.Count == 0) return;
            var headPt = pts[pts.Count - 1];

            // Once the finger lifts (and any coasting is over), the comet fades out
            // with its tail.
            bool coasting = t.coastUntil >= 0f && time <= t.coastUntil;
            float fade = t.live || coasting ? 1f : Mathf.Clamp01(1f - (time - headPt.time) / tail);
            if (fade <= 0f) return;

            // Tail: a ribbon from the oldest point (thin, dim) to the head (wide, bright).
            if (pts.Count >= 2)
            {
                int start = verts.Count;
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector2 p = Vector2.Scale(pts[i].uv, size);
                    Vector2 prevP = Vector2.Scale(pts[Mathf.Max(i - 1, 0)].uv, size);
                    Vector2 nextP = Vector2.Scale(pts[Mathf.Min(i + 1, pts.Count - 1)].uv, size);
                    Vector2 dir = nextP - prevP;
                    dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector2.right;
                    Vector2 perp = new Vector2(-dir.y, dir.x);

                    float age = Mathf.Clamp01((time - pts[i].time) / tail);   // 0 at the head, 1 at the end
                    float k = 1f - age;
                    float half = Mathf.Max(look.tailWidth * px * 0.5f * Mathf.Pow(k, 1.3f), 0.4f);
                    float a = Mathf.Pow(k, 1.6f) * look.intensity * fade;
                    Color c = pts[i].color;
                    c.a = a;

                    AddVertex(p + perp * half, size, c, new Vector2(age, 1f), 0f);
                    AddVertex(p - perp * half, size, c, new Vector2(age, -1f), 0f);
                    if (i > 0)
                    {
                        int v = start + i * 2;
                        tris.Add(v - 2); tris.Add(v - 1); tris.Add(v);
                        tris.Add(v - 1); tris.Add(v + 1); tris.Add(v);
                    }
                }
            }

            // Head: a big round glow at the pointer. Dimmer while resting, so a
            // still cursor is a soft glow rather than a blazing star.
            float speed = 0f;
            if (pts.Count >= 2)
            {
                var before = pts[pts.Count - 2];
                float dt = Mathf.Max(headPt.time - before.time, 1e-3f);
                speed = Vector2.Scale(headPt.uv - before.uv, size).magnitude / dt / Mathf.Max(size.y, 1f);
            }
            float headA = look.intensity * fade * Mathf.Lerp(0.45f, 1f, Mathf.Clamp01(speed / 0.4f));
            float r = look.headRadius * px * 2f;        // the quad is larger than the visible glow
            Vector2 hp = Vector2.Scale(headPt.uv, size);
            Color hc = headPt.color;

            // Coma: a wide, soft glow round the head (no white core: colour only).
            if (look.coma > 0f)
            {
                Color cc = hc;
                cc.a = look.coma * fade;
                AddHead(hp, r * 2.6f, size, cc, 2f);
            }
            hc.a = headA;
            AddHead(hp, r, size, hc, 1f);
        }

        void AddHead(Vector2 centre, float r, Vector2 size, Color c, float kind)
        {
            int h = verts.Count;
            AddVertex(centre + new Vector2(-r, -r), size, c, new Vector2(-1f, -1f), kind);
            AddVertex(centre + new Vector2( r, -r), size, c, new Vector2( 1f, -1f), kind);
            AddVertex(centre + new Vector2( r,  r), size, c, new Vector2( 1f,  1f), kind);
            AddVertex(centre + new Vector2(-r,  r), size, c, new Vector2(-1f,  1f), kind);
            tris.Add(h); tris.Add(h + 1); tris.Add(h + 2);
            tris.Add(h); tris.Add(h + 2); tris.Add(h + 3);
        }

        void AddVertex(Vector2 pixel, Vector2 size, Color c, Vector2 uv, float kind)
        {
            verts.Add(new Vector3(pixel.x / size.x * 2f - 1f, pixel.y / size.y * 2f - 1f, 0f));
            colors.Add(c);
            uvs.Add(uv);
            kinds.Add(new Vector2(kind, 0f));
        }
    }
}
