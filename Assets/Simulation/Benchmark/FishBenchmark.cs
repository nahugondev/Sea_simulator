using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SeaSim.Fish;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SeaSim.Benchmark
{
    /// <summary>
    /// Measures the real scene (simulation and rendering) in a player build, where editor overhead is gone.
    /// Does nothing unless the player is started with <c>-fishbench</c>:
    /// <code>SeaSim.exe -fishbench 12550,20000 -benchout results.csv -screen-width 1920 -screen-height 1080 -screen-fullscreen 0</code>
    /// For each total fish count (spread over the scene's schools in their original proportions) and each rendering
    /// variant (everything on, fish without shadows, 2 shadow cascades, no shadows, each optional renderer feature
    /// off), it lets the scene settle, records frames and writes one CSV line: CPU and GPU frame times (from
    /// <see cref="FrameTimingManager"/>) and simulation time. Then the player quits.
    /// The simulation runs on a fixed time step, so every run simulates the same amount of time.
    /// </summary>
    public class FishBenchmark : MonoBehaviour
    {
        [Tooltip("Renderer features switched off one at a time to measure their cost (e.g. SSAO, underwater).")]
        [SerializeField] ScriptableRendererFeature[] optionalFeatures;
        [SerializeField, Min(1)] int warmupFrames = 300;
        [SerializeField, Min(1)] int measuredFrames = 300;
        [Tooltip("Fixed simulated time per frame (s), so results do not depend on the frame rate.")]
        [SerializeField, Min(0.001f)] float stepSeconds = 0.02f;

        struct Variant
        {
            public string name;
            public System.Action apply, revert;
        }

        void Start()
        {
            if (!TryGetArgument("-fishbench", out string countsArg)) return;
            TryGetArgument("-benchout", out string outPath);
            if (string.IsNullOrEmpty(outPath)) outPath = Path.Combine(Application.persistentDataPath, "fishbench.csv");
            TryGetArgument("-benchshots", out screenshotFolder);
            StartCoroutine(Run(countsArg, outPath));
        }

        // Optional (-benchshots folder): after the first warm-up, time is frozen and the same frame is captured once
        // per shadow setting (folder/<setting>.png), so settings can be compared image to image.
        string screenshotFolder;

        IEnumerator CaptureShadowSettings()
        {
            var sun = RenderSettings.sun != null ? RenderSettings.sun : FindAnyObjectByType<Light>();
            if (!sun.TryGetComponent(out UniversalAdditionalLightData sunData)) yield break;
            var simulation = FishSimulation.Instance;
            var quality = sunData.softShadowQuality;
            int lod = simulation.ShadowLod;
            var settings = new (string name, SoftShadowQuality quality, int lod)[]
            {
                ("full_mesh_low", SoftShadowQuality.Low, 0), // the settings before the shadow work
                ("full_mesh_medium", SoftShadowQuality.Medium, 0),
                ("lod1_medium", SoftShadowQuality.Medium, 1),
                ("lod2_medium", SoftShadowQuality.Medium, 2),
                ("lod1_high", SoftShadowQuality.High, 1),
                ("lod2_high", SoftShadowQuality.High, 2),
            };
            Directory.CreateDirectory(screenshotFolder);
            float timeScale = Time.timeScale;
            Time.timeScale = 0f;
            foreach (var s in settings)
            {
                sunData.softShadowQuality = s.quality;
                simulation.ShadowLod = s.lod;
                for (int f = 0; f < 3; f++) yield return null;
                ScreenCapture.CaptureScreenshot(Path.Combine(screenshotFolder, s.name + ".png"));
                yield return null;
            }
            Time.timeScale = timeScale;
            sunData.softShadowQuality = quality;
            simulation.ShadowLod = lod;
            screenshotFolder = null;
        }

        List<Variant> Variants()
        {
            var urp = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            var sun = RenderSettings.sun != null ? RenderSettings.sun : FindAnyObjectByType<Light>();
            int cascades = urp.shadowCascadeCount;
            var sunShadows = sun.shadows;
            int shadowLod = FishSimulation.Instance.ShadowLod;
            var list = new List<Variant>
            {
                new Variant { name = "all_on", apply = () => { }, revert = () => { } },
                new Variant
                {
                    name = "fish_no_shadows",
                    apply = () => { foreach (var s in FishSchool.Active) s.CastShadows = ShadowCastingMode.Off; },
                    revert = () => { foreach (var s in FishSchool.Active) s.CastShadows = ShadowCastingMode.On; },
                },
                new Variant { name = "2_cascades", apply = () => urp.shadowCascadeCount = 2, revert = () => urp.shadowCascadeCount = cascades },
                new Variant { name = "no_shadows", apply = () => sun.shadows = LightShadows.None, revert = () => sun.shadows = sunShadows },
                new Variant
                {
                    name = "full_mesh_shadows",
                    apply = () => FishSimulation.Instance.ShadowLod = 0,
                    revert = () => FishSimulation.Instance.ShadowLod = shadowLod,
                },
                new Variant
                {
                    name = "shadow_lod_1",
                    apply = () => FishSimulation.Instance.ShadowLod = 1,
                    revert = () => FishSimulation.Instance.ShadowLod = shadowLod,
                },
            };
            if (sun.TryGetComponent(out UniversalAdditionalLightData sunData))
            {
                var softQuality = sunData.softShadowQuality;
                list.Add(new Variant
                {
                    name = "soft_shadows_low",
                    apply = () => sunData.softShadowQuality = SoftShadowQuality.Low,
                    revert = () => sunData.softShadowQuality = softQuality,
                });
                list.Add(new Variant
                {
                    name = "soft_shadows_medium",
                    apply = () => sunData.softShadowQuality = SoftShadowQuality.Medium,
                    revert = () => sunData.softShadowQuality = softQuality,
                });
            }
            foreach (var feature in optionalFeatures)
            {
                if (feature == null) continue;
                var f = feature;
                list.Add(new Variant { name = "no_" + f.name, apply = () => f.SetActive(false), revert = () => f.SetActive(true) });
            }
            // Measured again last: a drift between the two all_on lines means the run order biases the results.
            list.Add(new Variant { name = "all_on_again", apply = () => { }, revert = () => { } });
            return list;
        }

        IEnumerator Run(string countsArg, string outPath)
        {
            Application.runInBackground = true; // otherwise the player pauses when its window loses focus
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Time.captureDeltaTime = stepSeconds; // fixed simulated time per frame, run as fast as possible
            yield return null; // let every school register

            var schools = new List<FishSchool>(FishSchool.Active);
            var baseCounts = new List<int>();
            int baseTotal = 0;
            foreach (var s in schools) { baseCounts.Add(s.FishCountSetting); baseTotal += s.FishCountSetting; }

            var csv = new StringBuilder("fish,variant,cpu_ms_avg,cpu_ms_p95,gpu_ms_avg,gpu_ms_p95,simulation_ms_avg\n");
            var timings = new FrameTiming[1];
            foreach (var token in countsArg.Split(','))
            {
                if (!int.TryParse(token, out int total)) continue;
                for (int i = 0; i < schools.Count; i++)
                    schools[i].SetFishCount(Mathf.Max(1, Mathf.RoundToInt(baseCounts[i] * (float)total / baseTotal)));

                foreach (var variant in Variants())
                {
                    variant.apply();
                    for (int f = 0; f < warmupFrames; f++) yield return null;
                    if (!string.IsNullOrEmpty(screenshotFolder)) yield return CaptureShadowSettings();

                    var cpu = new float[measuredFrames];
                    var gpu = new float[measuredFrames];
                    float simulation = 0f;
                    for (int f = 0; f < measuredFrames; f++)
                    {
                        yield return null;
                        FrameTimingManager.CaptureFrameTimings();
                        FrameTimingManager.GetLatestTimings(1, timings);
                        cpu[f] = (float)timings[0].cpuFrameTime;
                        gpu[f] = (float)timings[0].gpuFrameTime;
                        simulation += FishSimulation.Instance.LastSimulationMilliseconds;
                    }
                    variant.revert();

                    csv.AppendLine(string.Join(",",
                        FishSimulation.Instance.FishCount.ToString(CultureInfo.InvariantCulture), variant.name,
                        F(Average(cpu)), F(Percentile(cpu, 0.95f)),
                        F(Average(gpu)), F(Percentile(gpu, 0.95f)),
                        F(simulation / measuredFrames)));
                }
            }

            File.WriteAllText(outPath, csv.ToString());
            Application.Quit();
        }

        static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        static float Average(float[] values)
        {
            float sum = 0f;
            foreach (var v in values) sum += v;
            return sum / values.Length;
        }

        static float Percentile(float[] values, float p)
        {
            var sorted = (float[])values.Clone();
            System.Array.Sort(sorted);
            return sorted[Mathf.Clamp(Mathf.CeilToInt(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        }

        static bool TryGetArgument(string name, out string value)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != name) continue;
                value = i + 1 < args.Length ? args[i + 1] : string.Empty;
                return true;
            }
            value = null;
            return false;
        }
    }
}
