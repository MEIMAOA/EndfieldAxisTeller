using System.IO;

namespace AxisTeller.Vision;

public sealed record TemplateInfo(ActionKind Kind, string FilePath, string Name);

/// <summary>模板库：%AppData%\EndfieldAxisTeller\templates\&lt;类型&gt;_&lt;时间戳&gt;.png</summary>
public sealed class TemplateLibrary
{
    public string Directory { get; }

    public TemplateLibrary() => Directory = Path.Combine(Store.Root, "templates");

    public IReadOnlyList<TemplateInfo> Templates { get; private set; } = Array.Empty<TemplateInfo>();

    public void Reload()
    {
        var list = new List<TemplateInfo>();
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.png"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var kind = ActionKind.Unknown;
                foreach (var k in ActionKinds.Selectable)
                {
                    if (name.StartsWith(k + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        kind = k;
                        break;
                    }
                }

                list.Add(new TemplateInfo(kind, file, name));
            }
        }
        catch
        {
            // 忽略读取失败
        }

        Templates = list;
    }

    public IReadOnlyList<TemplateInfo> For(ActionKind kind) =>
        Templates.Where(t => t.Kind == kind).ToList();

    public string Save(ActionKind kind, byte[] frameBgra, int frameWidth, int frameHeight, int x, int y, int w, int h)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var png = TemplateMatcher.CropToPng(frameBgra, frameWidth, frameHeight, x, y, w, h);
        var file = Path.Combine(Directory, $"{kind}_{DateTime.Now:yyyyMMddHHmmssfff}.png");
        File.WriteAllBytes(file, png);
        Reload();
        return file;
    }

    public void Delete(string filePath)
    {
        try
        {
            if (File.Exists(filePath)) File.Delete(filePath);
            Reload();
        }
        catch
        {
            // 忽略
        }
    }
}
