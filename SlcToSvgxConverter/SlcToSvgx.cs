using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

namespace SlcToSvgxConverter
{
    /// <summary>一层切片数据：Z 高度 + 若干条闭合轮廓。</summary>
    internal sealed class SlcLayer
    {
        public float Z;
        public List<float[]> Contours = new List<float[]>();

        /// <summary>这一层的净面积（mm²，外轮廓为正、内孔为负）。</summary>
        public double NetArea()
        {
            double area = 0;
            foreach (float[] contour in Contours)
            {
                area += SlcFile.SignedArea(contour);
            }

            return area;
        }
    }

    /// <summary>解析后的 SLC 文件。</summary>
    internal sealed class SlcFile
    {
        public string HeaderText = string.Empty;
        public bool IsInch;
        public float LayerThicknessMm;
        public float MaxZ;
        public float MinX, MaxX, MinY, MaxY;
        public List<SlcLayer> Layers = new List<SlcLayer>();

        public int LayerCount
        {
            get { return Layers.Count; }
        }

        /// <summary>读取 SLC：文件头(-SLCVER 2.0 …) + 256 字节保留区 + 采样表 + 层/轮廓数据。</summary>
        public static SlcFile Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);

            int headerEnd = FindHeaderEnd(data);
            if (headerEnd < 0)
            {
                throw new InvalidDataException("Not a valid SLC file: header terminator (CR LF 0x1A) not found.");
            }

            string header = Encoding.ASCII.GetString(data, 0, headerEnd);
            if (header.IndexOf("-SLCVER", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidDataException("Not a valid SLC file: missing -SLCVER marker.");
            }

            SlcFile slc = new SlcFile();
            slc.HeaderText = header;
            slc.IsInch = header.IndexOf("-UNIT INCH", StringComparison.OrdinalIgnoreCase) >= 0;

            int pos = headerEnd + 3;          // 跳过 CR LF 0x1A
            pos += 256;                       // 3D 保留区

            if (pos + 1 > data.Length)
            {
                throw new InvalidDataException("Truncated SLC file: no sample table after the reserved block.");
            }

            int tableCount = data[pos];
            pos++;

            if (tableCount < 1)
            {
                throw new InvalidDataException("SLC sample table is empty; layer height is unknown.");
            }

            float thickness = 0;
            for (int i = 0; i < tableCount; i++)
            {
                if (pos + 16 > data.Length)
                {
                    throw new InvalidDataException("Truncated SLC file: incomplete sample table.");
                }

                if (i == 0)
                {
                    thickness = BitConverter.ToSingle(data, pos + 4);
                }

                pos += 16;
            }

            float scale = slc.IsInch ? 25.4f : 1f;
            slc.LayerThicknessMm = thickness * scale;

            float maxZ = 0;
            bool terminated = false;

            while (pos + 8 <= data.Length)
            {
                float z = BitConverter.ToSingle(data, pos);
                pos += 4;

                uint boundaryCount = BitConverter.ToUInt32(data, pos);
                pos += 4;

                if (boundaryCount == 0xFFFFFFFFu)
                {
                    maxZ = z * scale;
                    terminated = true;
                    break;
                }

                if (boundaryCount > 1000000)
                {
                    throw new InvalidDataException("Invalid SLC layer data: contour count too large.");
                }

                SlcLayer layer = new SlcLayer();
                layer.Z = z * scale;

                for (uint b = 0; b < boundaryCount; b++)
                {
                    if (pos + 8 > data.Length)
                    {
                        throw new InvalidDataException("Truncated SLC file: incomplete contour header.");
                    }

                    uint vertexCount = BitConverter.ToUInt32(data, pos);
                    pos += 4;

                    pos += 4; // 间隙数量，本转换不使用

                    if (vertexCount < 3 || vertexCount > 10000000)
                    {
                        throw new InvalidDataException("Invalid SLC contour vertex count: " + vertexCount);
                    }

                    int bytes = (int)vertexCount * 8;
                    if (pos + bytes > data.Length)
                    {
                        throw new InvalidDataException("Truncated SLC file: incomplete contour vertex data.");
                    }

                    float[] pts = new float[vertexCount * 2];
                    Buffer.BlockCopy(data, pos, pts, 0, bytes);
                    pos += bytes;

                    if (scale != 1f)
                    {
                        for (int i = 0; i < pts.Length; i++)
                        {
                            pts[i] *= scale;
                        }
                    }

                    layer.Contours.Add(pts);
                }

                slc.Layers.Add(layer);
            }

            if (!terminated)
            {
                throw new InvalidDataException("SLC file has no end marker (0xFFFFFFFF); it may be corrupted.");
            }

            if (slc.Layers.Count == 0)
            {
                throw new InvalidDataException("The SLC file contains no layers.");
            }

            slc.MaxZ = maxZ;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (SlcLayer layer in slc.Layers)
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

            slc.MinX = minX;
            slc.MaxX = maxX;
            slc.MinY = minY;
            slc.MaxY = maxY;

            return slc;
        }

        private static int FindHeaderEnd(byte[] data)
        {
            for (int i = 0; i + 2 < data.Length && i < 8192; i++)
            {
                if (data[i] == 0x0D && data[i + 1] == 0x0A && data[i + 2] == 0x1A)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>鞋带公式的有符号面积：逆时针为正（外轮廓），顺时针为负（内孔）。</summary>
        internal static double SignedArea(float[] pts)
        {
            int n = pts.Length / 2;
            if (n < 3)
            {
                return 0;
            }

            double sum = 0;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                sum += (double)pts[j * 2] * pts[i * 2 + 1] - (double)pts[i * 2] * pts[j * 2 + 1];
            }

            return sum / 2.0;
        }
    }


    /// <summary>写入 svgx 的打印参数。</summary>
    internal sealed class SvgxParams
    {
        public string MaterialName = "FE1100 (Standard)";

        /// <summary>输出方言。</summary>
        public int AttachLayers = 8;
        public int BuildInLayers = 8;
        public double AttachTime = 50;
        public double BaseTime = 3.3;
        public int ResolutionX = 5760;
        public int ResolutionY = 3600;
        public double DisplayWidth = 197;
        public double DisplayHeight = 122;
        public double MachineZ = 200;

        /// <summary>
        /// true = 把 SLC 的平台角点坐标平移成 svgx 的屏中心坐标（减去 大小X/2、大小Y/2）；
        /// false = 原样输出 SLC 坐标。
        /// </summary>

        /// <summary>把包围盒中心挪到原点（闪铸原厂就是这样做，输出坐标 −85…85 这种）。</summary>
        public bool CenterBoundingBox;

        /// <summary>曝光时的光源 PWM（写进 G 代码 M106 S…）。</summary>
        public int LightPwm = 254;
    }

    /// <summary>SLC → SVGX（DLP-II 1.1）转换。</summary>
    internal static class SvgxConverter
    {
        private const int ThumbnailWidth = 44;
        private const int ThumbnailHeight = 44;
        private const int PreviewWidth = 200;
        private const int PreviewHeight = 240;
        private const byte PreviewR = 7;
        private const byte PreviewG = 82;
        private const byte PreviewB = 134;

        /// <summary>生成输出文件名：原名_层厚_曝光时间_年月日_时分秒.svgx。</summary>
        public static string BuildOutputPath(string slcPath, double layerHeight, double baseTime)
        {
            string dir = Path.GetDirectoryName(slcPath);
            if (string.IsNullOrEmpty(dir))
            {
                dir = Directory.GetCurrentDirectory();
            }

            string name = Path.GetFileNameWithoutExtension(slcPath);
            string stamp = DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture);
            string fileName = string.Format(
                CultureInfo.InvariantCulture,
                "{0}_{1}_{2}_{3}.svgx",
                name,
                Num(layerHeight),
                Num(baseTime),
                stamp);

            string full = Path.Combine(dir, fileName);
            int index = 2;
            while (File.Exists(full))
            {
                full = Path.Combine(dir, string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}_{1}_{2}_{3}_{4}.svgx",
                    name,
                    Num(layerHeight),
                    Num(baseTime),
                    stamp,
                    index));
                index++;
            }

            return full;
        }

        public static void Convert(SlcFile slc, string outputPath, SvgxParams parameters)
        {
            string svg = BuildSvg(slc, parameters);
            byte[] svgBytes = new UTF8Encoding(false).GetBytes(svg);

            // FlashForge native layout: 128x128 thumbnail and 410x410 preview
            const int thumbW = 128;
            const int thumbH = 128;
            const int prevW = 410;
            const int prevH = 410;

            byte[] thumbnail = ToBmpBytes(RenderProjection(slc, thumbW, thumbH));
            byte[] preview = ToBmpBytes(RenderProjection(slc, prevW, prevH));

            const int bmp1Offset = 28;                       // "DLP-II 1.1\n" + 5 字节 + 3 个 uint32
            int bmp2Offset = bmp1Offset + thumbnail.Length;
            int svgOffset = bmp2Offset + preview.Length;

            using (FileStream stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("DLP-II 1.1\n"));
                writer.Write(new byte[5]);
                writer.Write(bmp1Offset);
                writer.Write(bmp2Offset);
                writer.Write(svgOffset);
                writer.Write(thumbnail);
                writer.Write(preview);
                writer.Write(svgBytes);
            }
        }

        private static string BuildSvg(SlcFile slc, SvgxParams parameters)
        {
            double thickness = slc.LayerThicknessMm > 0 ? slc.LayerThicknessMm : 0.05;
            double volume = 0;
            foreach (SlcLayer layer in slc.Layers)
            {
                volume += Math.Abs(layer.NetArea()) * thickness;
            }

            volume /= 1000.0;   // mm³ → cm³

            // 坐标平移：
            //   CenterBoundingBox  -> 包围盒中心移到原点（同闪铸原厂）
            //   都不勾              -> 原样输出 SLC 坐标
            double minAllX = 0, maxAllX = 0, minAllY = 0, maxAllY = 0;
            bool needBounds = true;   // printrange always needs the bounding box

            if (needBounds)
            {
                minAllX = double.MaxValue;
                maxAllX = double.MinValue;
                minAllY = double.MaxValue;
                maxAllY = double.MinValue;

                foreach (SlcLayer layer in slc.Layers)
                {
                    foreach (float[] contour in layer.Contours)
                    {
                        for (int i = 0; i < contour.Length; i += 2)
                        {
                            if (contour[i] < minAllX) minAllX = contour[i];
                            if (contour[i] > maxAllX) maxAllX = contour[i];
                            if (contour[i + 1] < minAllY) minAllY = contour[i + 1];
                            if (contour[i + 1] > maxAllY) maxAllY = contour[i + 1];
                        }
                    }
                }

                if (minAllX > maxAllX)
                {
                    minAllX = maxAllX = minAllY = maxAllY = 0;
                }
            }

            double shiftX = 0;
            double shiftY = 0;

            if (parameters.CenterBoundingBox)
            {
                shiftX = -(minAllX + maxAllX) / 2.0;
                shiftY = -(minAllY + maxAllY) / 2.0;
            }

            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minY = float.MaxValue;
            float maxY = float.MinValue;

            foreach (SlcLayer layer in slc.Layers)
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

            StringBuilder sb = new StringBuilder(1 << 20);
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"no\"?>\n");
            sb.Append("<svg xmlns:svg=\"http://www.w3.org/2000/svg\" xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\">\n");

            // FlashForge native printparams: 5 fields only, material name "elegoo"
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "<printparams materialname=\"elegoo\" layerheight=\"{0}\" volume=\"{1}\" layercount=\"{2}\" lightintensity=\"{3}\">\n",
                Num(thickness),
                volume.ToString("0.#####", CultureInfo.InvariantCulture),
                slc.LayerCount,
                "1");

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "<projectiontime attachlayer=\"{0}\" buildinlayer=\"{1}\" attachtime=\"{2}\" basetime=\"{3}\"/>\n",
                parameters.AttachLayers,
                parameters.BuildInLayers,
                Num(parameters.AttachTime),
                Num(parameters.BaseTime));
            sb.Append("<projectionadjust x=\"100\" y=\"100\" />\n");

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "<printrange minx=\"{0}\" miny=\"{1}\" minz=\"0\"  maxx=\"{2}\" maxy=\"{3}\" maxz=\"{4}\" />\n",
                Num(minX + shiftX),
                Num(minY + shiftY),
                Num(maxX + shiftX),
                Num(maxY + shiftY),
                Num(slc.LayerCount * thickness));

            sb.Append("</printparams>\n");
            sb.Append("<g id=\"background\">\n");
            sb.Append("</g>\n");

            for (int i = 0; i < slc.Layers.Count; i++)
            {
                SlcLayer layer = slc.Layers[i];
                double area = Math.Abs(layer.NetArea());

                // area in cm², plus perimeter in cm (FlashForge native layout)
                double perimeter = 0;
                foreach (float[] contour in layer.Contours)
                {
                    for (int k = 0; k < contour.Length; k += 2)
                    {
                        int next = (k + 2) % contour.Length;
                        double dx = contour[next] - contour[k];
                        double dy = contour[next + 1] - contour[k + 1];
                        perimeter += Math.Sqrt(dx * dx + dy * dy);
                    }
                }

                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<g id=\"layer-{0}\" area=\"{1}\" perimeter=\"{2}\">\n",
                    i,
                    (area / 100.0).ToString("0.#####", CultureInfo.InvariantCulture),
                    (perimeter / 10.0).ToString("0.#####", CultureInfo.InvariantCulture));

                foreach (List<float[]> island in GroupContours(layer.Contours))
                {
                    sb.Append("<path d=\"");
                    for (int c = 0; c < island.Count; c++)
                    {
                        AppendContour(sb, island[c], shiftX, shiftY);
                        sb.Append("Z ");
                    }

                    sb.Append("\" style=\"fill:white\" fill-rule=\"evenodd\"/>\n");
                }

                sb.Append("</g>\n");
            }

            sb.Append("</svg>\n");
            return sb.ToString();
        }

        /// <summary>把一条轮廓写入 path：M x y L x y x y … ，每条子路径后面由调用方补 Z。</summary>
        private static void AppendContour(StringBuilder sb, float[] contour, double shiftX, double shiftY)
        {
            int n = contour.Length / 2;
            if (n < 3)
            {
                return;
            }

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "M {0} {1} L ",
                Num(contour[0] + shiftX),
                Num(contour[1] + shiftY));

            for (int i = 1; i < n; i++)
            {
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0} {1} ",
                    Num(contour[i * 2] + shiftX),
                    Num(contour[i * 2 + 1] + shiftY));
            }
        }

        /// <summary>
        /// 把一层里的轮廓按“岛”分组：外轮廓一条 path，嵌在它里面的内孔作为同一个 path 的子路径，
        /// 这样即使渲染器不认多子路径，孔也不会被错误填充。
        /// </summary>
        private static List<List<float[]>> GroupContours(List<float[]> contours)
        {
            List<List<float[]>> islands = new List<List<float[]>>();
            List<float[]> holes = new List<float[]>();

            foreach (float[] contour in contours)
            {
                if (SlcFile.SignedArea(contour) < 0)
                {
                    holes.Add(contour);
                }
                else
                {
                    islands.Add(new List<float[]> { contour });
                }
            }

            foreach (float[] hole in holes)
            {
                int owner = -1;
                for (int i = 0; i < islands.Count; i++)
                {
                    if (PointInPolygon(hole[0], hole[1], islands[i][0]))
                    {
                        owner = i;
                        break;
                    }
                }

                if (owner >= 0)
                {
                    islands[owner].Add(hole);
                }
                else
                {
                    islands.Add(new List<float[]> { hole });
                }
            }

            return islands;
        }

        private static bool PointInPolygon(float px, float py, float[] polygon)
        {
            bool inside = false;
            int n = polygon.Length / 2;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = polygon[i * 2];
                float yi = polygon[i * 2 + 1];
                float xj = polygon[j * 2];
                float yj = polygon[j * 2 + 1];

                if (((yi > py) != (yj > py)) && (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>把所有层的轮廓投影成一张俯视剪影图，作为 svgx 里的预览/缩略图。</summary>
        private static Bitmap RenderProjection(SlcFile slc, int width, int height)
        {
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            using (Graphics g = Graphics.FromImage(bitmap))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(PreviewR, PreviewG, PreviewB)))
            {
                g.Clear(Color.Black);
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                double spanX = Math.Max(1e-6, slc.MaxX - slc.MinX);
                double spanY = Math.Max(1e-6, slc.MaxY - slc.MinY);
                double margin = 2;
                double scale = Math.Min((width - 2 * margin) / spanX, (height - 2 * margin) / spanY);
                if (scale <= 0)
                {
                    scale = 1e-6;
                }

                double offsetX = (width - spanX * scale) / 2.0;
                double offsetY = (height - spanY * scale) / 2.0;

                foreach (SlcLayer layer in slc.Layers)
                {
                    using (GraphicsPath path = new GraphicsPath(FillMode.Alternate))
                    {
                        foreach (float[] contour in layer.Contours)
                        {
                            int n = contour.Length / 2;
                            if (n < 3)
                            {
                                continue;
                            }

                            PointF[] points = new PointF[n];
                            for (int i = 0; i < n; i++)
                            {
                                double x = (contour[i * 2] - slc.MinX) * scale + offsetX;
                                double y = height - ((contour[i * 2 + 1] - slc.MinY) * scale + offsetY);
                                points[i] = new PointF((float)x, (float)y);
                            }

                            path.AddPolygon(points);
                        }

                        if (path.PointCount > 0)
                        {
                            g.FillPath(brush, path);
                        }
                    }
                }
            }

            return bitmap;
        }

        /// <summary>按 24 位 BMP（54 字节头、自下而上、4 字节行对齐）写出字节。</summary>
        private static byte[] ToBmpBytes(Bitmap bitmap)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            int stride = ((width * 3) + 3) / 4 * 4;
            int imageSize = stride * height;

            using (MemoryStream stream = new MemoryStream(54 + imageSize))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((byte)'B');
                writer.Write((byte)'M');
                writer.Write(54 + imageSize);
                writer.Write(0);
                writer.Write(54);
                writer.Write(40);
                writer.Write(width);
                writer.Write(height);
                writer.Write((short)1);
                writer.Write((short)24);
                writer.Write(0);
                writer.Write(imageSize);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);

                for (int y = height - 1; y >= 0; y--)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Color color = bitmap.GetPixel(x, y);
                        writer.Write(color.B);
                        writer.Write(color.G);
                        writer.Write(color.R);
                    }

                    for (int pad = width * 3; pad < stride; pad++)
                    {
                        writer.Write((byte)0);
                    }
                }

                writer.Flush();
                byte[] result = stream.ToArray();
                bitmap.Dispose();
                return result;
            }
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        internal static string Num(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
