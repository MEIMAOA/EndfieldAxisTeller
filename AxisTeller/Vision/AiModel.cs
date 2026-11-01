using System.IO;
using System.Text.Json;

namespace AxisTeller.Vision;

/// <summary>本地训练出来的小分类头：在 ONNX 预训练特征上做逻辑回归（二分类：这个动作的画面对不对）。</summary>
public sealed class AiModel
{
    public string Kind { get; set; } = "";
    public int RoiX { get; set; }
    public int RoiY { get; set; }
    public int RoiWidth { get; set; }
    public int RoiHeight { get; set; }
    public float[] Weights { get; set; } = Array.Empty<float>();
    public float Bias { get; set; }
    public float[] FeatureMean { get; set; } = Array.Empty<float>();
    public float[] FeatureStd { get; set; } = Array.Empty<float>();
    public int PositiveCount { get; set; }
    public int NegativeCount { get; set; }
    public double TrainAccuracy { get; set; }
    public double ValidationAccuracy { get; set; }
    public string TrainedAt { get; set; } = "";

    public static string DirectoryPath => Path.Combine(Store.Root, "ai");

    public static string PathFor(string kind) => Path.Combine(DirectoryPath, kind + ".json");

    /// <summary>特征 → 概率（0~1）。</summary>
    public double Score(float[] features)
    {
        if (Weights.Length == 0 || features.Length != Weights.Length) return 0;
        double z = Bias;
        for (var i = 0; i < features.Length; i++)
        {
            var std = FeatureStd.Length == features.Length && FeatureStd[i] > 1e-6f ? FeatureStd[i] : 1f;
            var mean = FeatureMean.Length == features.Length ? FeatureMean[i] : 0f;
            z += ((features[i] - mean) / std) * Weights[i];
        }

        return 1.0 / (1.0 + Math.Exp(-Math.Clamp(z, -60, 60)));
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            var json = JsonSerializer.Serialize(this, Json.Options);
            File.WriteAllText(PathFor(Kind), json);
        }
        catch
        {
            // 忽略保存失败
        }
    }

    public static AiModel? Load(string kind)
    {
        try
        {
            var path = PathFor(kind);
            if (!File.Exists(path)) return null;
            var model = JsonSerializer.Deserialize<AiModel>(File.ReadAllText(path), Json.Options);
            return model is { Weights.Length: > 0 } ? model : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(string kind)
    {
        try
        {
            var path = PathFor(kind);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }
}
