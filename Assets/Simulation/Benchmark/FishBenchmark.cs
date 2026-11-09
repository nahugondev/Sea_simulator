using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using SeaSim.Fish;
using UnityEngine;

namespace SeaSim.Benchmark
{
    /// <summary>
    /// Measures the fish simulation in a player build, where editor safety checks are off.
    /// Does nothing unless the player is started with <c>-fishbench</c>:
    /// <code>SeaSimBenchmark.exe -fishbench 1500,10000,50000 -benchout results.csv</code>
    /// For each fish count it re-spawns the school, lets the schools form for <see cref="warmupFrames"/> frames,
    /// then records <see cref="measuredFrames"/> frames and writes one CSV line. The simulation runs on a fixed
    /// time step so every run simulates the same amount of time, then the player quits.
    /// </summary>
    public class FishBenchmark : MonoBehaviour
    {
        [SerializeField] FishSchool school;
        [SerializeField, Min(1)] int warmupFrames = 1500;
        [SerializeField, Min(1)] int measuredFrames = 300;
        [Tooltip("Fixed simulated time per frame (s), so results do not depend on the frame rate.")]
        [SerializeField, Min(0.001f)] float stepSeconds = 0.02f;

        void Start()
        {
            if (!TryGetArgument("-fishbench", out string countsArg)) return;
            TryGetArgument("-benchout", out string outPath);
            if (string.IsNullOrEmpty(outPath)) outPath = Path.Combine(Application.persistentDataPath, "fishbench.csv");
            StartCoroutine(Run(countsArg, outPath));
        }

        IEnumerator Run(string countsArg, string outPath)
        {
            Application.runInBackground = true; // otherwise the player pauses when its window loses focus
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Time.captureDeltaTime = stepSeconds; // fixed simulated time per frame, run as fast as possible

            var csv = new StringBuilder("fish,frame_ms_avg,frame_ms_p95,simulation_ms_avg,simulation_ms_p95\n");
            foreach (var token in countsArg.Split(','))
            {
                if (!int.TryParse(token, out int count)) continue;
                school.SetFishCount(count);
                for (int f = 0; f < warmupFrames; f++) yield return null;

                var frame = new float[measuredFrames];
                var simulation = new float[measuredFrames];
                float last = Time.realtimeSinceStartup;
                for (int f = 0; f < measuredFrames; f++)
                {
                    yield return null;
                    float now = Time.realtimeSinceStartup;
                    frame[f] = (now - last) * 1000f;
                    simulation[f] = school.LastSimulationMilliseconds;
                    last = now;
                }
                csv.AppendLine(string.Join(",",
                    count.ToString(CultureInfo.InvariantCulture),
                    F(Average(frame)), F(Percentile(frame, 0.95f)),
                    F(Average(simulation)), F(Percentile(simulation, 0.95f))));
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
