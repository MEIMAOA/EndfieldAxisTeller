using System.IO;
using System.Windows;
using OpenCvSharp;

namespace AxisTeller.Vision;

/// <summary>训练样本：把当前画面里 ROI 区域裁下来存成 PNG，正样本=动作出现，负样本=没出现。</summary>
public static class AiSampleStore
{
    public static string DirectoryPath => Path.Combine(Store.Root, "ai");

    public static string KindDirectory(string kind) => Path.Combine(DirectoryPath, kind);

    public static string SampleDirectory(string kind, bool positive) =>
        Path.Combine(KindDirectory(kind), positive ? "pos" : "neg");

    public static int Count(string kind, bool positive)
    {
        try
        {
            var dir = SampleDirectory(kind, positive);
            return System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, "*.png").Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static string? SavePatch(string kind, bool positive, byte[] frameBgra, int frameWidth, int frameHeight, Int32Rect roi)
    {
        try
        {
            var dir = SampleDirectory(kind, positive);
            System.IO.Directory.CreateDirectory(dir);
            var png = TemplateMatcher.CropToPng(frameBgra, frameWidth, frameHeight, roi.X, roi.Y, roi.Width, roi.Height);
            var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}_{(positive ? "p" : "n")}.png");
            File.WriteAllBytes(file, png);
            return file;
        }
        catch
        {
            return null;
        }
    }

    public static void Clear(string kind)
    {
        try
        {
            var dir = KindDirectory(kind);
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        }
        catch
        {
            // 忽略
        }
    }

    public static List<byte[]> LoadPatches(string kind, bool positive)
    {
        var list = new List<byte[]>();
        try
        {
            var dir = SampleDirectory(kind, positive);
            if (!System.IO.Directory.Exists(dir)) return list;
            foreach (var file in System.IO.Directory.GetFiles(dir, "*.png"))
            {
                try
                {
                    list.Add(File.ReadAllBytes(file));
                }
                catch
                {
                    // 跳过损坏样本
                }
            }
        }
        catch
        {
            // 忽略
        }

        return list;
    }
}

/// <summary>用 ONNX 特征 + 逻辑回归训练出一个小分类头。</summary>
public static class AiTrainer
{
    public const int MinSamplesPerClass = 6;

    public static AiModel? Train(string kind, Int32Rect roi, OnnxFeatureExtractor extractor, out string log)
    {
        log = "";
        if (!extractor.Available)
        {
            log = "ONNX 特征提取器不可用：" + (extractor.Error ?? "未知原因");
            return null;
        }

        var positives = AiSampleStore.LoadPatches(kind, true);
        var negatives = AiSampleStore.LoadPatches(kind, false);
        if (positives.Count < MinSamplesPerClass || negatives.Count < MinSamplesPerClass)
        {
            log = $"样本太少：正 {positives.Count} / 负 {negatives.Count}，每类至少 {MinSamplesPerClass} 张。";
            return null;
        }

        var features = new List<float[]>();
        var labels = new List<int>();
        foreach (var png in positives)
        {
            var f = ExtractFromPng(png, extractor);
            if (f is null) continue;
            features.Add(f);
            labels.Add(1);
        }

        foreach (var png in negatives)
        {
            var f = ExtractFromPng(png, extractor);
            if (f is null) continue;
            features.Add(f);
            labels.Add(0);
        }

        if (features.Count < MinSamplesPerClass * 2)
        {
            log = $"有效样本不足（{features.Count} 张）。";
            return null;
        }

        var dim = OnnxFeatureExtractor.Dimension;
        var mean = new float[dim];
        var std = new float[dim];
        for (var i = 0; i < dim; i++)
        {
            double sum = 0, sum2 = 0;
            foreach (var f in features)
            {
                sum += f[i];
                sum2 += f[i] * (double)f[i];
            }

            var m = sum / features.Count;
            var v = Math.Max(1e-6, sum2 / features.Count - m * m);
            mean[i] = (float)m;
            std[i] = (float)Math.Sqrt(v);
        }

        // 标准化
        var x = new double[features.Count][];
        for (var i = 0; i < features.Count; i++)
        {
            x[i] = new double[dim];
            for (var d = 0; d < dim; d++) x[i][d] = (features[i][d] - mean[d]) / std[d];
        }

        var y = labels.Select(l => (double)l).ToArray();

        // 固定随机种子做 80/20 拆分
        var rng = new Random(20240501);
        var order = Enumerable.Range(0, x.Length).OrderBy(_ => rng.Next()).ToArray();
        var valCount = Math.Max(4, x.Length / 5);
        var trainIdx = order.Take(x.Length - valCount).ToArray();
        var valIdx = order.Skip(x.Length - valCount).ToArray();

        var w = new double[dim];
        double b = 0;
        const int epochs = 400;
        const double lr = 0.15;
        const double l2 = 1e-4;

        for (var epoch = 0; epoch < epochs; epoch++)
        {
            var grad = new double[dim];
            double gradB = 0;
            foreach (var i in trainIdx)
            {
                var z = b;
                for (var d = 0; d < dim; d++) z += w[d] * x[i][d];
                var pred = 1.0 / (1.0 + Math.Exp(-Math.Clamp(z, -60, 60)));
                var err = pred - y[i];
                for (var d = 0; d < dim; d++) grad[d] += err * x[i][d];
                gradB += err;
            }

            var n = Math.Max(1, trainIdx.Length);
            for (var d = 0; d < dim; d++) w[d] -= lr * (grad[d] / n + l2 * w[d]);
            b -= lr * gradB / n;
        }

        double Accuracy(IEnumerable<int> idx)
        {
            var total = 0;
            var right = 0;
            foreach (var i in idx)
            {
                var z = b;
                for (var d = 0; d < dim; d++) z += w[d] * x[i][d];
                var pred = z >= 0 ? 1 : 0;
                total++;
                if (pred == (int)y[i]) right++;
            }

            return total == 0 ? 0 : (double)right / total;
        }

        var model = new AiModel
        {
            Kind = kind,
            RoiX = roi.X,
            RoiY = roi.Y,
            RoiWidth = roi.Width,
            RoiHeight = roi.Height,
            Weights = w.Select(v => (float)v).ToArray(),
            Bias = (float)b,
            FeatureMean = mean,
            FeatureStd = std,
            PositiveCount = positives.Count,
            NegativeCount = negatives.Count,
            TrainAccuracy = Accuracy(trainIdx),
            ValidationAccuracy = Accuracy(valIdx),
            TrainedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        model.Save();

        log = $"训练完成：正样本 {positives.Count} / 负样本 {negatives.Count}，训练集准确率 {model.TrainAccuracy:P0}，" +
              $"验证集准确率 {model.ValidationAccuracy:P0}。";
        return model;
    }

    private static float[]? ExtractFromPng(byte[] png, OnnxFeatureExtractor extractor)
    {
        try
        {
            using var bgr = Cv2.ImDecode(png, ImreadModes.Color);
            if (bgr.Empty()) return null;
            using var bgra = new Mat();
            Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
            var bytes = new byte[bgra.Rows * bgra.Cols * 4];
            System.Runtime.InteropServices.Marshal.Copy(bgra.Data, bytes, 0, bytes.Length);
            return extractor.Extract(bytes, bgra.Cols, bgra.Rows);
        }
        catch
        {
            return null;
        }
    }
}
