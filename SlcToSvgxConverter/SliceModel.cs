using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace SlcToSvgxConverter
{
    /// <summary>预览用的一层：轮廓 + 统计值。SLC 和 SVGX 都归一成这个结构。</summary>
    internal sealed class SliceLayerInfo
    {
        public float Z;
        public double Area;
        public int OuterCount;
        public int HoleCount;
        public List<float[]> Contours = new List<float[]>();
    }

    /// <summary>预览用的切片模型（一份 SLC 或一份 SVGX）。</summary>
    internal sealed class SliceModel
    {
        public string FilePath = string.Empty;
        public string Format = "SLC";          // "SLC" / "SVGX"
        public float LayerThickness;
        public List<SliceLayerInfo> Layers = new List<SliceLayerInfo>();
        public float MinX, MaxX, MinY, MaxY;

        /// <summary>SVGX 带过来的打印参数；SLC 为 null。</summary>
        public SvgxParams Parameters;

        public void UpdateBounds()
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            foreach (SliceLayerInfo layer in Layers)
            {
                foreach (float[] contour in layer.Contours)
                {
                    for (int i = 0; i < contour.Length; i += 2)
                    {
                        if (contour[i] < minX) minX = contour[i];
                        if (contour[i] > maxX) maxX = contour[i];
                        if (contour[i + 1] < minY) minY = contour[i + 1];
                        if (contour[i + 1] > maxY) maxY = contour[i + 1];
                    }
                }
            }

            if (minX > maxX)
            {
                minX = maxX = minY = maxY = 0;
            }

            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }

        /// <summary>SLC → 预览模型。</summary>
        public static SliceModel FromSlc(SlcFile slc)
        {
            SliceModel model = new SliceModel();
            model.Format = "SLC";
            model.LayerThickness = slc.LayerThicknessMm;

            foreach (SlcLayer layer in slc.Layers)
            {
                SliceLayerInfo info = new SliceLayerInfo();
                info.Z = layer.Z;
                info.Area = Math.Abs(layer.NetArea());

                foreach (float[] contour in layer.Contours)
                {
                    info.Contours.Add(contour);
                    if (SlcFile.SignedArea(contour) >= 0)
                    {
                        info.OuterCount++;
                    }
                    else
                    {
                        info.HoleCount++;
                    }
                }

                model.Layers.Add(info);
            }

            model.UpdateBounds();
            return model;
        }

        internal static void Accumulate(SliceLayerInfo info, float[] contour)
        {
            info.Contours.Add(contour);

            double signed = SlcFile.SignedArea(contour);
            if (signed >= 0)
            {
                info.OuterCount++;
            }
            else
            {
                info.HoleCount++;
            }
        }
    }

    /// <summary>读 .svgx（DLP-II 1.1）用于预览：容器头 + SVG 里的 printparams 和逐层 path。</summary>
    internal static class SvgxReader
    {
        public static SliceModel Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 32)
            {
                throw new InvalidDataException("File is too small to be an SVGX file.");
            }

            string svg = ExtractSvg(data);

            XDocument document;
            try
            {
                document = XDocument.Parse(svg);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Failed to parse the SVG section: " + ex.Message);
            }

            SliceModel model = new SliceModel();
            model.FilePath = path;
            model.Format = "SVGX";

            SvgxParams parameters = new SvgxParams();
            float thickness = 0;
            string material = null;

            foreach (XElement element in document.Descendants())
            {
                string name = element.Name.LocalName;

                if (name == "printparams")
                {
                    material = (string)element.Attribute("materialname");
                    thickness = ReadFloat(element, "layerheight", 0);
                    parameters.ResolutionX = (int)ReadFloat(element, "resolutionx", parameters.ResolutionX);
                    parameters.ResolutionY = (int)ReadFloat(element, "resolutiony", parameters.ResolutionY);
                    parameters.DisplayWidth = ReadFloat(element, "displaywidth", (float)parameters.DisplayWidth);
                    parameters.DisplayHeight = ReadFloat(element, "displayheight", (float)parameters.DisplayHeight);
                    parameters.MachineZ = ReadFloat(element, "machinez", (float)parameters.MachineZ);
                }
                else if (name == "projectiontime")
                {
                    parameters.AttachLayers = (int)ReadFloat(element, "attachlayer", parameters.AttachLayers);
                    parameters.BuildInLayers = (int)ReadFloat(element, "buildinlayer", parameters.BuildInLayers);
                    parameters.AttachTime = ReadFloat(element, "attachtime", (float)parameters.AttachTime);
                    parameters.BaseTime = ReadFloat(element, "basetime", (float)parameters.BaseTime);
                }
            }

            if (!string.IsNullOrEmpty(material))
            {
                parameters.MaterialName = material;
            }

            if (thickness <= 0)
            {
                thickness = 0.05f;
            }

            model.LayerThickness = thickness;
            model.Parameters = parameters;

            List<XElement> groups = new List<XElement>();
            foreach (XElement element in document.Descendants())
            {
                if (element.Name.LocalName == "g")
                {
                    string id = (string)element.Attribute("id") ?? string.Empty;
                    if (id.StartsWith("layer-", StringComparison.Ordinal))
                    {
                        groups.Add(element);
                    }
                }
            }

            if (groups.Count == 0)
            {
                throw new InvalidDataException("No layer group found in the SVG (expected <g id=\"layer-N\">).");
            }

            foreach (XElement group in groups)
            {
                SliceLayerInfo info = new SliceLayerInfo();
                double declaredArea = 0;
                string areaText = (string)group.Attribute("area");
                if (!string.IsNullOrEmpty(areaText))
                {
                    double.TryParse(areaText, NumberStyles.Float, CultureInfo.InvariantCulture, out declaredArea);
                }

                foreach (XElement pathElement in group.Descendants())
                {
                    if (pathElement.Name.LocalName != "path")
                    {
                        continue;
                    }

                    string d = (string)pathElement.Attribute("d");
                    if (string.IsNullOrEmpty(d))
                    {
                        continue;
                    }

                    foreach (float[] contour in ParsePathData(d))
                    {
                        SliceModel.Accumulate(info, contour);
                    }
                }

                double geometric = 0;
                foreach (float[] contour in info.Contours)
                {
                    geometric += SlcFile.SignedArea(contour);
                }

                geometric = Math.Abs(geometric);

                // 有的厂商把 area 写成 cm²（如 Elegoo 方言），按与几何面积的比例自动归一成 mm²
                if (declaredArea > 0)
                {
                    double asMillimetre = Math.Abs(declaredArea - geometric);
                    double asCentimetre = Math.Abs(declaredArea * 100 - geometric);
                    info.Area = asCentimetre < asMillimetre ? declaredArea * 100 : declaredArea;
                }
                else
                {
                    info.Area = geometric;
                }

                model.Layers.Add(info);
            }

            for (int i = 0; i < model.Layers.Count; i++)
            {
                model.Layers[i].Z = (float)(i * thickness);
            }

            model.UpdateBounds();
            return model;
        }

        private static string ExtractSvg(byte[] data)
        {
            // 优先用容器头里的 SVG 偏移
            if (data.Length > 28)
            {
                int offset = BitConverter.ToInt32(data, 24);
                if (offset > 0 && offset < data.Length - 8)
                {
                    string candidate = Encoding.UTF8.GetString(data, offset, data.Length - offset).TrimStart('\uFEFF', '\r', '\n', ' ');
                    if (candidate.StartsWith("<", StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
            }

            // 其他软件的容器结构不同：直接找 XML 起点
            string text = Encoding.UTF8.GetString(data);
            int index = text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                throw new InvalidDataException("No SVG content found; this may not be an SVGX file.");
            }

            int xmlIndex = text.LastIndexOf("<?xml", index, StringComparison.OrdinalIgnoreCase);
            return text.Substring(xmlIndex >= 0 ? xmlIndex : index);
        }

        private static float ReadFloat(XElement element, string name, float fallback)
        {
            string text = (string)element.Attribute(name);
            if (string.IsNullOrEmpty(text))
            {
                return fallback;
            }

            float value;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        /// <summary>解析 path 的 d：支持 M/L/H/V/Z（含相对写法）；遇到曲线命令会明确报错。</summary>
        internal static List<float[]> ParsePathData(string d)
        {
            List<float[]> result = new List<float[]>();
            List<float> current = new List<float>();
            List<double> pending = new List<double>();

            double cx = 0, cy = 0, startX = 0, startY = 0;
            char command = '\0';
            int i = 0;

            while (i < d.Length)
            {
                char ch = d[i];

                if (char.IsWhiteSpace(ch) || ch == ',')
                {
                    i++;
                    continue;
                }

                if (char.IsLetter(ch))
                {
                    if (ch == 'C' || ch == 'c' || ch == 'Q' || ch == 'q' || ch == 'A' || ch == 'a' || ch == 'S' || ch == 's' || ch == 'T' || ch == 't')
                    {
                        throw new InvalidDataException("Path contains a curve command (" + ch + "), which is not supported (only M/L/H/V/Z).");
                    }

                    command = ch;
                    i++;

                    if (ch == 'Z' || ch == 'z')
                    {
                        FlushContour(result, current);
                        cx = startX;
                        cy = startY;
                    }

                    continue;
                }

                int start = i;
                if (ch == '+' || ch == '-')
                {
                    i++;
                }

                while (i < d.Length && (char.IsDigit(d[i]) || d[i] == '.'))
                {
                    i++;
                }

                if (i < d.Length && (d[i] == 'e' || d[i] == 'E'))
                {
                    i++;
                    if (i < d.Length && (d[i] == '+' || d[i] == '-'))
                    {
                        i++;
                    }

                    while (i < d.Length && char.IsDigit(d[i]))
                    {
                        i++;
                    }
                }

                if (i == start)
                {
                    i++;   // 无法识别的字符，跳过，别死循环
                    continue;
                }

                double value = double.Parse(d.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
                pending.Add(value);

                while (true)
                {
                    if (command == 'M' || command == 'm')
                    {
                        if (pending.Count < 2)
                        {
                            break;
                        }

                        double x = pending[0], y = pending[1];
                        pending.RemoveRange(0, 2);
                        if (command == 'm')
                        {
                            x += cx;
                            y += cy;
                        }

                        FlushContour(result, current);
                        cx = startX = x;
                        cy = startY = y;
                        current.Add((float)cx);
                        current.Add((float)cy);
                        command = command == 'M' ? 'L' : 'l';
                    }
                    else if (command == 'L' || command == 'l')
                    {
                        if (pending.Count < 2)
                        {
                            break;
                        }

                        double x = pending[0], y = pending[1];
                        pending.RemoveRange(0, 2);
                        if (command == 'l')
                        {
                            x += cx;
                            y += cy;
                        }

                        cx = x;
                        cy = y;
                        current.Add((float)cx);
                        current.Add((float)cy);
                    }
                    else if (command == 'H' || command == 'h')
                    {
                        if (pending.Count < 1)
                        {
                            break;
                        }

                        double x = pending[0];
                        pending.RemoveAt(0);
                        if (command == 'h')
                        {
                            x += cx;
                        }

                        cx = x;
                        current.Add((float)cx);
                        current.Add((float)cy);
                    }
                    else if (command == 'V' || command == 'v')
                    {
                        if (pending.Count < 1)
                        {
                            break;
                        }

                        double y = pending[0];
                        pending.RemoveAt(0);
                        if (command == 'v')
                        {
                            y += cy;
                        }

                        cy = y;
                        current.Add((float)cx);
                        current.Add((float)cy);
                    }
                    else
                    {
                        pending.Clear();
                        break;
                    }
                }
            }

            FlushContour(result, current);
            return result;
        }

        private static void FlushContour(List<float[]> result, List<float> current)
        {
            if (current.Count >= 6)
            {
                result.Add(current.ToArray());
            }

            current.Clear();
        }
    }
}
