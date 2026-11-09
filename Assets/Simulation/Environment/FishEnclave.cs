using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace SeaSim.Environment
{
    /// <summary>
    /// An underwater enclave: the single, centred swimming volume shared by every fish school, surrounded by
    /// procedural rock cliffs, with loose boulders on a sandy floor (placeholder until the seabed step).
    ///
    /// The geometry carries the large forms (rounded limestone masses, solution pockets, alcoves, a flared base, a
    /// rounded shoulder into a plateau on top); the fine detail comes from the triplanar textures. The cliff line starts
    /// from a rounded rectangle placed <see cref="wallMargin"/> beyond the swimming volume and only wanders outward
    /// from it (bays and headlands); the rock never bulges more than <see cref="inwardRelief"/> toward the fish, which is
    /// kept below the margin: rock never enters the swimming volume. Boulders stay below it.
    ///
    /// Mesh layout: submesh 0 = rock (cliffs + boulders), submesh 1 = sand. UV0.x = cavity (0 = exposed, 1 = deep
    /// recess), used by the shaders for ambient occlusion. The mesh is rebuilt in edit mode, so it can be tuned live.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class FishEnclave : MonoBehaviour
    {
        [Header("Swimming volume (centred on this object)")]
        [SerializeField] Vector3 swimSize = new Vector3(40f, 12f, 40f);

        [Header("Cliffs")]
        [Tooltip("Gap between the swimming volume and the base line of the cliffs (m).")]
        [SerializeField, Min(0.5f)] float wallMargin = 3f;
        [Tooltip("Corner radius of the base outline (m). Larger = rounder enclave; the sides move out to keep the margin.")]
        [SerializeField, Min(0f)] float cornerRadius = 18f;
        [Tooltip("How far the cliffs rise above the swimming volume (m).")]
        [SerializeField, Min(0f)] float heightAbove = 10f;
        [Tooltip("Distance between mesh vertices (m). Keep it well below the pocket size; finer detail comes from the textures.")]
        [SerializeField, Range(0.2f, 2f)] float resolution = 0.35f;

        [Header("Outline (m)")]
        [Tooltip("How far the cliff line wanders outward from the rounded rectangle, so the enclave does not read as a box. " +
                 "Only ever pushes outward: the swimming volume stays clear.")]
        [SerializeField, Min(0f)] float outlineIrregularity = 14f;
        [Tooltip("Typical length of the bays and headlands along the outline.")]
        [SerializeField, Min(2f)] float outlineScale = 20f;

        [Header("Top of the cliffs (m)")]
        [Tooltip("Radius of the rounded shoulder where the cliff face turns into the plateau above it.")]
        [SerializeField, Min(0.5f)] float shoulderRadius = 4f;
        [Tooltip("Depth of the plateau behind the shoulder.")]
        [SerializeField, Min(0f)] float plateauWidth = 18f;
        [Tooltip("Height of the gentle swells on the plateau.")]
        [SerializeField, Min(0f)] float plateauRelief = 1.5f;

        [Header("Cliff shape (m)")]
        [Tooltip("Depth of the alcoves and of the gaps between rock masses.")]
        [SerializeField, Min(0f)] float outwardRelief = 2.5f;
        [Tooltip("How far rock may bulge toward the fish. Kept below the wall margin.")]
        [SerializeField, Min(0f)] float inwardRelief = 2.2f;
        [Tooltip("Typical size of the rounded rock masses.")]
        [SerializeField, Min(1f)] float massSize = 10f;
        [Tooltip("How much the top of the cliffs leans over the enclave.")]
        [SerializeField, Range(0f, 1f)] float overhang = 0.5f;
        [SerializeField] int seed = 7;

        [Header("Erosion (limestone)")]
        [Tooltip("Typical size of the solution pockets dissolved into the rock (m).")]
        [SerializeField, Min(0.5f)] float pocketSize = 2.2f;
        [Tooltip("Depth of the pockets (m).")]
        [SerializeField, Min(0f)] float pocketDepth = 0.35f;
        [Tooltip("Share of the rock surface covered by pockets.")]
        [SerializeField, Range(0f, 1f)] float pocketCoverage = 0.35f;
        [Tooltip("Depth of the horizontal undercut notches carved by old water levels (m).")]
        [SerializeField, Min(0f)] float notchDepth = 0.4f;

        [Header("Floor and boulders (placeholder until the seabed step)")]
        [Tooltip("Depth of the sand below the swimming volume (m).")]
        [SerializeField, Min(0f)] float floorDepth = 2f;
        [SerializeField, Min(0f)] float floorRelief = 0.7f;
        [SerializeField, Range(0, 60)] int boulderCount = 24;

        Mesh mesh;
        bool dirty = true;

        static readonly List<FishEnclave> active = new List<FishEnclave>();
        public static IReadOnlyList<FishEnclave> Active => active;

        /// <summary>The volume fish swim in (world space).</summary>
        public Bounds SwimVolume => new Bounds(transform.position, swimSize);

        float FloorY => -swimSize.y * 0.5f - floorDepth;

        void OnEnable()
        {
            active.Add(this);
            dirty = true;
            Rebuild();
        }

        void OnDisable()
        {
            active.Remove(this);
            if (mesh != null)
            {
                if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
                mesh = null;
            }
        }

        void OnValidate()
        {
            inwardRelief = math.min(inwardRelief, wallMargin * 0.8f); // rock never reaches the fish
            dirty = true;
        }

        void Update()
        {
            if (dirty) Rebuild();
        }

        void Rebuild()
        {
            dirty = false;
            if (mesh == null)
            {
                mesh = new Mesh { name = "Enclave", hideFlags = HideFlags.DontSave, indexFormat = IndexFormat.UInt32 };
                GetComponent<MeshFilter>().sharedMesh = mesh;
            }

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var rock = new List<int>();
            var sand = new List<int>();
            BuildCliffs(vertices, uvs, rock);
            BuildBoulders(vertices, uvs, rock);
            BuildFloor(vertices, uvs, sand);

            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(rock, 0);
            mesh.SetTriangles(sand, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        // ---------------------------------------------------------------- cliffs

        void BuildCliffs(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
        {
            BaseOutline(out float hx, out float hz, out float r);
            float perimeter = 2f * (2f * hx - 2f * r) + 2f * (2f * hz - 2f * r) + 2f * math.PI * r;

            float yBottom = FloorY - 2f;   // below the sand, so no gap shows
            float yTop = swimSize.y * 0.5f + heightAbove;

            // Each column follows a profile: straight up the cliff face, a quarter-circle shoulder turning away from
            // the fish, then a plateau. The profile is sampled by arc length so rows stay evenly spaced.
            float faceLength = yTop - yBottom;
            float shoulderLength = 0.5f * math.PI * shoulderRadius;
            float profileLength = faceLength + shoulderLength + plateauWidth;
            // The outline runs further out than the rounded rectangle, so the perimeter is longer: sample it more.
            int columns = math.max(8, (int)math.ceil(perimeter * (1f + outlineIrregularity / math.min(hx, hz)) / resolution));
            int rows = math.max(2, (int)math.ceil(profileLength / resolution));
            int start = vertices.Count;

            for (int c = 0; c <= columns; c++)
            {
                PathPoint(perimeter * c / columns, hx, hz, r, out float2 basePoint, out float2 outward);
                // Bays and headlands: the base line only moves outward, never toward the swimming volume.
                if (c == columns) PathPoint(0f, hx, hz, r, out basePoint, out outward); // close the loop exactly
                float2 o = basePoint / outlineScale + seed * 3.7f;
                float wander = 0.5f + 0.5f * (noise.snoise(o) * 0.7f + noise.snoise(o * 2.3f + 11f) * 0.3f);
                basePoint += outward * wander * outlineIrregularity;

                for (int row = 0; row <= rows; row++)
                {
                    float s = profileLength * row / rows;
                    float outwardOffset, y, faceWeight;
                    if (s <= faceLength)
                    {
                        outwardOffset = 0f; y = yBottom + s; faceWeight = 1f;
                    }
                    else if (s <= faceLength + shoulderLength)
                    {
                        float angle = (s - faceLength) / shoulderRadius;
                        outwardOffset = shoulderRadius * (1f - math.cos(angle));
                        y = yTop + shoulderRadius * math.sin(angle);
                        faceWeight = math.cos(angle);
                    }
                    else
                    {
                        outwardOffset = shoulderRadius + (s - faceLength - shoulderLength);
                        y = yTop + shoulderRadius;
                        faceWeight = 0f;
                    }

                    float3 p = new float3(basePoint.x, y, basePoint.y) + new float3(outward.x, 0f, outward.y) * outwardOffset;
                    // Cliff relief pushes sideways on the face and fades out over the shoulder; the plateau gets
                    // gentle vertical swells instead.
                    float carve = CliffDisplacement(p, yBottom, yTop, out float cavity);
                    p.xz -= outward * carve * faceWeight;
                    float swell = noise.snoise(p.xz * 0.08f + seed * 5.3f) * 0.7f + noise.snoise(p.xz * 0.25f + 3f) * 0.3f;
                    p.y += swell * plateauRelief * (1f - faceWeight);
                    vertices.Add(p);
                    uvs.Add(new Vector2(cavity * faceWeight, 0f));
                }
            }

            int stride = rows + 1;
            for (int c = 0; c < columns; c++)
            for (int row = 0; row < rows; row++)
            {
                int a = start + c * stride + row, b = a + stride;
                // Winding chosen so the faces look inward, toward the fish (Unity front faces are clockwise).
                triangles.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
            }
        }

        /// <summary>
        /// Inward displacement of the cliff (m), and how recessed the point is (0..1).
        /// Eroded reef limestone: rounded, billowing masses (no preferred direction), solution pockets dissolved into
        /// them (cellular noise: round hollows separated by sharp rims), a few horizontal undercut notches, rare deep
        /// alcoves, an overhanging top and a base that flares out onto the sand. The texture adds the fine detail.
        /// </summary>
        float CliffDisplacement(float3 p, float yBottom, float yTop, out float cavity)
        {
            float3 s = p + seed * 17.3f;
            float f = 1f / massSize;
            float height01 = math.saturate((p.y - yBottom) / (yTop - yBottom));

            // Rounded masses: isotropic noise with a soft "pillow" profile, plus a smaller set on top.
            // (A plain, smooth profile: sharper "pillow" shapes read as caves.)
            float mass = noise.snoise(s * f) + 0.25f * noise.snoise(s * f * 2.1f + 5.1f);
            mass /= 1.25f;                                                                // ~[-1, 1]
            float t = 0.55f + 0.42f * mass;

            // Alcoves: rare, deep recesses near the bottom half.
            float alcoveMask = math.smoothstep(0.55f, 0.8f, noise.snoise(new float3(s.x * 0.05f, s.y * 0.12f, s.z * 0.05f)));
            alcoveMask *= 1f - math.smoothstep(0.2f, 0.65f, height01);
            t -= 0.6f * alcoveMask;

            float carve = math.lerp(-outwardRelief, inwardRelief, math.saturate(t));

            // Solution pockets: distance to the nearest cell centre; small distance = inside a round hollow.
            float2 cells = noise.cellular(s / pocketSize);
            // Squared profile: round-bottomed hollows with fairly sharp rims between them.
            float pocket = 1f - math.smoothstep(0.05f, 0.6f, cells.x);
            pocket *= pocket;
            pocket *= math.smoothstep(1f - pocketCoverage - 0.1f, 1f - pocketCoverage + 0.1f,
                                      0.5f + 0.5f * noise.snoise(s * 0.11f + 40f));
            // Smaller pockets inside the big ones' rims.
            float2 small = noise.cellular(s / (pocketSize * 0.4f) + 13f);
            pocket += 0.35f * (1f - math.smoothstep(0.1f, 0.6f, small.x)) * pocketCoverage;
            carve -= pocket * pocketDepth;

            // Undercut notches: one or two wavy horizontal grooves, deepest where the rock sticks out.
            float notchY = p.y + noise.snoise(new float3(s.x * 0.08f, 0f, s.z * 0.08f)) * 1.5f;
            float notch = math.exp(-math.pow((notchY - (FloorY + 4.5f)) / 1.6f, 2f))
                        + 0.6f * math.exp(-math.pow((notchY - (FloorY + 11f)) / 1.3f, 2f));
            carve -= notch * notchDepth * math.saturate(0.4f + 0.6f * mass);

            // Overhang at the top, flared base at the bottom (both pushed inward, still capped by inwardRelief).
            carve += overhang * inwardRelief * math.smoothstep(0.65f, 1f, height01) * (0.6f + 0.4f * mass);
            float baseFlare = 1f - math.smoothstep(FloorY - 1f, FloorY + 2.5f, p.y);
            carve = math.lerp(carve, math.max(carve, inwardRelief * 0.7f), baseFlare);
            carve = math.clamp(carve, -outwardRelief - pocketDepth - notchDepth, inwardRelief);

            cavity = math.saturate(1f - (carve + outwardRelief) / (outwardRelief + inwardRelief)) * 0.7f
                   + math.saturate(pocket) * 0.3f;
            return carve;
        }

        /// <summary>
        /// Rounded rectangle the cliff line starts from (half sizes and corner radius). Its sides are pushed out far
        /// enough that even the rounded corners stay <see cref="wallMargin"/> away from the corners of the swimming
        /// volume: the distance from a box corner to the arc is r - sqrt(2) * (r - d), where d is the side offset, so
        /// d must be at least r - (r - margin) / sqrt(2).
        /// </summary>
        void BaseOutline(out float hx, out float hz, out float r)
        {
            float sx = swimSize.x * 0.5f, sz = swimSize.z * 0.5f;
            r = math.clamp(cornerRadius, wallMargin, math.min(sx, sz) + wallMargin);
            float d = math.max(wallMargin, r - (r - wallMargin) / math.SQRT2);
            hx = sx + d;
            hz = sz + d;
        }

        /// <summary>Point and outward normal on a rounded rectangle, at distance s along its perimeter.</summary>
        static void PathPoint(float s, float hx, float hz, float r, out float2 point, out float2 outward)
        {
            float lx = 2f * hx - 2f * r, lz = 2f * hz - 2f * r, arc = 0.5f * math.PI * r;
            if (s < lx) { point = new float2(-hx + r + s, -hz); outward = new float2(0, -1); return; }
            s -= lx;
            if (s < arc) { Corner(new float2(hx - r, -hz + r), -0.5f * math.PI + s / r, r, out point, out outward); return; }
            s -= arc;
            if (s < lz) { point = new float2(hx, -hz + r + s); outward = new float2(1, 0); return; }
            s -= lz;
            if (s < arc) { Corner(new float2(hx - r, hz - r), s / r, r, out point, out outward); return; }
            s -= arc;
            if (s < lx) { point = new float2(hx - r - s, hz); outward = new float2(0, 1); return; }
            s -= lx;
            if (s < arc) { Corner(new float2(-hx + r, hz - r), 0.5f * math.PI + s / r, r, out point, out outward); return; }
            s -= arc;
            if (s < lz) { point = new float2(-hx, hz - r - s); outward = new float2(-1, 0); return; }
            s -= lz;
            Corner(new float2(-hx + r, -hz + r), math.PI + math.min(s, arc) / r, r, out point, out outward);
        }

        static void Corner(float2 center, float angle, float r, out float2 point, out float2 outward)
        {
            outward = new float2(math.cos(angle), math.sin(angle));
            point = center + outward * r;
        }

        // ---------------------------------------------------------------- boulders

        /// <summary>
        /// Loose boulders half-buried in the sand, mostly near the cliffs. Their size is capped so their top stays
        /// below the swimming volume.
        /// </summary>
        void BuildBoulders(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
        {
            if (boulderCount == 0) return;
            BuildIcosphere(2, out var unitVerts, out var unitTris);
            var rng = new Unity.Mathematics.Random((uint)(seed * 7919 + 1));
            float maxRadius = math.max(0.3f, (floorDepth - 0.3f) / 1.2f);
            BaseOutline(out float hx, out float hz, out _);

            for (int b = 0; b < boulderCount; b++)
            {
                // Pick a spot, biased toward the cliffs.
                float2 dir = math.normalize(rng.NextFloat2Direction());
                float edge = math.pow(rng.NextFloat(), 0.35f);
                float2 xz = new float2(dir.x * hx, dir.y * hz) * math.lerp(0.25f, 0.92f, edge);
                float radius = rng.NextFloat(0.35f, maxRadius) * math.lerp(0.6f, 1f, edge);
                // Sit on the actual sand surface (lowest point under the stone, so it never floats over a hollow).
                float ground = math.min(SandHeight(xz.x, xz.y),
                               math.min(math.min(SandHeight(xz.x + radius, xz.y), SandHeight(xz.x - radius, xz.y)),
                                        math.min(SandHeight(xz.x, xz.y + radius), SandHeight(xz.x, xz.y - radius))));
                // Where the sand rises, shrink the stone so its top still stays below the swimming volume.
                float headroom = FloorY + floorDepth - 0.3f - ground;
                radius = math.min(radius, math.max(0.2f, headroom / 1.6f));
                float3 scale = new float3(rng.NextFloat(0.8f, 1.3f), rng.NextFloat(0.55f, 0.85f), rng.NextFloat(0.8f, 1.3f)) * radius;
                quaternion rot = quaternion.Euler(rng.NextFloat(0, 6.28f), rng.NextFloat(0, 6.28f), rng.NextFloat(0, 6.28f));
                float3 center = new float3(xz.x, ground + radius * 0.15f, xz.y);
                float noiseSeed = rng.NextFloat(0, 100);

                int start = vertices.Count;
                foreach (var v in unitVerts)
                {
                    float3 n = v;
                    // Lumpy, slightly flattened stone, pitted with solution pockets like the cliffs.
                    float lump = 1f + 0.22f * noise.snoise(n * 1.3f + noiseSeed) + 0.08f * noise.snoise(n * 3.1f + noiseSeed);
                    lump -= 0.12f * (1f - math.smoothstep(0.1f, 0.6f, noise.cellular(n * 2.2f + noiseSeed).x));
                    float3 local = math.mul(rot, n * lump) * scale;
                    vertices.Add(center + local);
                    uvs.Add(new Vector2(0.15f, 0f));
                }
                foreach (int t in unitTris) triangles.Add(start + t);
            }
        }

        /// <summary>Unit icosphere (outward-facing, clockwise winding).</summary>
        static void BuildIcosphere(int subdivisions, out List<float3> verts, out List<int> tris)
        {
            float t = (1f + math.sqrt(5f)) * 0.5f;
            verts = new List<float3>
            {
                new float3(-1, t, 0), new float3(1, t, 0), new float3(-1, -t, 0), new float3(1, -t, 0),
                new float3(0, -1, t), new float3(0, 1, t), new float3(0, -1, -t), new float3(0, 1, -t),
                new float3(t, 0, -1), new float3(t, 0, 1), new float3(-t, 0, -1), new float3(-t, 0, 1),
            };
            for (int i = 0; i < verts.Count; i++) verts[i] = math.normalize(verts[i]);
            tris = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };
            var cache = new Dictionary<long, int>();
            for (int s = 0; s < subdivisions; s++)
            {
                var next = new List<int>();
                for (int i = 0; i < tris.Count; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    int ab = Midpoint(a, b, verts, cache), bc = Midpoint(b, c, verts, cache), ca = Midpoint(c, a, verts, cache);
                    next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                tris = next;
            }
            // Make the faces look outward: in Unity the front-face normal is cross(b - a, c - a).
            float3 v0 = verts[tris[0]], v1 = verts[tris[1]], v2 = verts[tris[2]];
            if (math.dot(math.cross(v1 - v0, v2 - v0), v0) < 0f)
                for (int i = 0; i < tris.Count; i += 3) (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
        }

        static int Midpoint(int a, int b, List<float3> verts, Dictionary<long, int> cache)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (cache.TryGetValue(key, out int index)) return index;
            verts.Add(math.normalize((verts[a] + verts[b]) * 0.5f));
            cache[key] = verts.Count - 1;
            return verts.Count - 1;
        }

        // ---------------------------------------------------------------- sand

        /// <summary>Half sizes of the sand floor: it reaches under the deepest bays of the outline.</summary>
        void FloorExtents(out float hx, out float hz)
        {
            BaseOutline(out hx, out hz, out _);
            hx += outlineIrregularity + 2f;
            hz += outlineIrregularity + 2f;
        }

        /// <summary>Soft dunes, slightly elongated, rising a little toward the edges (unitless, times floorRelief).</summary>
        float DuneHeight(float px, float pz, float hx, float hz)
        {
            float2 q = new float2(px, pz) * 0.07f + seed * 7.31f;
            float h = noise.snoise(q) * 0.6f + noise.snoise(q * new float2(2.4f, 1.2f)) * 0.3f;
            float edge = math.max(math.abs(px) / hx, math.abs(pz) / hz);
            return h + math.smoothstep(0.75f, 1f, edge) * 1.2f;
        }

        /// <summary>Height of the sand surface at a point (local space).</summary>
        float SandHeight(float px, float pz)
        {
            FloorExtents(out float hx, out float hz);
            return FloorY + DuneHeight(px, pz, hx, hz) * floorRelief;
        }

        void BuildFloor(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
        {
            FloorExtents(out float hx, out float hz);
            int nx = math.max(2, (int)math.ceil(2f * hx / resolution));
            int nz = math.max(2, (int)math.ceil(2f * hz / resolution));
            int start = vertices.Count;
            for (int z = 0; z <= nz; z++)
            for (int x = 0; x <= nx; x++)
            {
                float px = math.lerp(-hx, hx, (float)x / nx);
                float pz = math.lerp(-hz, hz, (float)z / nz);
                float h = DuneHeight(px, pz, hx, hz);
                vertices.Add(new Vector3(px, FloorY + h * floorRelief, pz));
                uvs.Add(new Vector2(math.saturate(0.5f - 0.5f * h), 1f));
            }
            int stride = nx + 1;
            for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                int a = start + z * stride + x, b = a + stride;
                triangles.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
            }
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 1f, 0.6f, 0.6f);
            Gizmos.DrawWireCube(transform.position, swimSize);
        }
    }
}
