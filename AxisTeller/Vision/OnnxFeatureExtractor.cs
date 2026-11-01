using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace AxisTeller.Vision;

/// <summary>用 ONNX Runtime 跑轻量预训练模型（MobileNetV2）取 1280 维特征，再交给本地训练的小分类头。</summary>
public sealed class OnnxFeatureExtractor : IDisposable
{
    public const int InputSize = 224;
    public const int Dimension = 1280;

    private static readonly float[] ChannelMean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] ChannelStd = { 0.229f, 0.224f, 0.225f };

    private readonly InferenceSession? _session;
    private readonly string _inputName = "input";
    private readonly string _featureName = "472";

    public bool Available => _session is not null;
    public string? Error { get; private set; }

    public static string DefaultModelPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "models", "mobilenetv2-features.onnx");

    public OnnxFeatureExtractor(string? modelPath = null)
    {
        var path = modelPath ?? DefaultModelPath;
        try
        {
            if (!File.Exists(path))
            {
                Error = "ONNX 模型文件不存在：" + path;
                return;
            }

            var options = new SessionOptions
            {
                IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount)),
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            _session = new InferenceSession(path, options);

            // 自动挑选「2 维、最后一维 1280」的输出当特征
            var featureOutput = _session.OutputMetadata
                .FirstOrDefault(kv => kv.Value.Dimensions.Length == 2 && kv.Value.Dimensions[1] == Dimension);
            if (featureOutput.Key is not null) _featureName = featureOutput.Key;
        }
        catch (Exception ex)
        {
            Error = "加载 ONNX 模型失败：" + ex.Message;
        }
    }

    /// <summary>输入一块 BGRA 像素，返回 1280 维特征；失败返回 null。</summary>
    public float[]? Extract(byte[] bgra, int width, int height)
    {
        if (_session is null || width <= 0 || height <= 0) return null;

        try
        {
            using var src = new Mat(height, width, MatType.CV_8UC4);
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, src.Data, Math.Min(bgra.Length, width * height * 4));

            using var rgb = new Mat();
            Cv2.CvtColor(src, rgb, ColorConversionCodes.BGRA2RGB);
            using var resized = new Mat();
            Cv2.Resize(rgb, resized, new Size(InputSize, InputSize), interpolation: InterpolationFlags.Area);

            var plane = InputSize * InputSize;
            var bytes = new byte[plane * 3];
            System.Runtime.InteropServices.Marshal.Copy(resized.Data, bytes, 0, bytes.Length);

            var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
            var span = tensor.Buffer.Span;
            for (var i = 0; i < plane; i++)
            {
                var o = i * 3;
                for (var c = 0; c < 3; c++)
                    span[c * plane + i] = (bytes[o + c] / 255f - ChannelMean[c]) / ChannelStd[c];
            }

            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
            var value = results.FirstOrDefault(r => r.Name == _featureName);
            if (value is null) return null;
            return value.AsTensor<float>().ToArray();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _session?.Dispose();
}
