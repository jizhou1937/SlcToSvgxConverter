using System;
using System.Collections.Generic;

namespace SlcToSvgxConverter
{
    /// <summary>
    /// 把多份 SLC 合成一份：按绝对 Z 对齐，层厚取最细的那份并重采样。
    /// 轮廓全部保留（含内轮廓），不做裁剪——靠 svgx 的逐 path 填充语义得到并集。
    /// </summary>
    internal static class SlcMerger
    {
        public static SlcFile Merge(IList<SlcFile> files)
        {
            if (files == null || files.Count == 0)
            {
                throw new ArgumentException("No files to merge.");
            }

            if (files.Count == 1)
            {
                return files[0];
            }

            // 参考层厚 = 所有输入里最细的
            float thickness = float.MaxValue;
            foreach (SlcFile file in files)
            {
                if (file.LayerThicknessMm > 0 && file.LayerThicknessMm < thickness)
                {
                    thickness = file.LayerThicknessMm;
                }
            }

            if (thickness == float.MaxValue || thickness <= 0)
            {
                thickness = 0.05f;
            }

            // Z 范围：按每份文件自己的层 Z 与层厚算真实覆盖区间
            float zMin = float.MaxValue;
            float zMax = float.MinValue;

            foreach (SlcFile file in files)
            {
                foreach (SlcLayer layer in file.Layers)
                {
                    float top = layer.Z + file.LayerThicknessMm;
                    if (layer.Z < zMin)
                    {
                        zMin = layer.Z;
                    }

                    if (top > zMax)
                    {
                        zMax = top;
                    }
                }
            }

            if (zMin == float.MaxValue)
            {
                throw new InvalidOperationException("The input files contain no layers.");
            }

            int count = Math.Max(1, (int)Math.Ceiling((zMax - zMin) / thickness - 1e-6f));

            SlcFile merged = new SlcFile();
            merged.LayerThicknessMm = thickness;
            merged.MaxZ = zMax;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            for (int k = 0; k < count; k++)
            {
                float z = zMin + k * thickness;
                SlcLayer layer = new SlcLayer();
                layer.Z = z;

                // 在目标层的中间高度取样，避免刚好落在层边界上
                float sampleZ = z + thickness * 0.5f;

                foreach (SlcFile file in files)
                {
                    SlcLayer source = FindLayer(file, sampleZ);
                    if (source == null)
                    {
                        continue;
                    }

                    foreach (float[] contour in source.Contours)
                    {
                        layer.Contours.Add(contour);

                        for (int i = 0; i < contour.Length; i += 2)
                        {
                            if (contour[i] < minX) minX = contour[i];
                            if (contour[i] > maxX) maxX = contour[i];
                            if (contour[i + 1] < minY) minY = contour[i + 1];
                            if (contour[i + 1] > maxY) maxY = contour[i + 1];
                        }
                    }
                }

                merged.Layers.Add(layer);
            }

            merged.MinX = minX > maxX ? 0 : minX;
            merged.MaxX = minX > maxX ? 0 : maxX;
            merged.MinY = minY > maxY ? 0 : minY;
            merged.MaxY = minY > maxY ? 0 : maxY;

            return merged;
        }

        /// <summary>找覆盖指定 Z 的那一层（层按 Z 升序）。</summary>
        private static SlcLayer FindLayer(SlcFile file, float z)
        {
            if (file.Layers.Count == 0)
            {
                return null;
            }

            int low = 0;
            int high = file.Layers.Count - 1;

            if (z < file.Layers[0].Z)
            {
                return file.Layers[0];
            }

            if (z >= file.Layers[high].Z + file.LayerThicknessMm)
            {
                return null;   // 超出这份文件的高度
            }

            while (low <= high)
            {
                int mid = (low + high) / 2;
                float layerZ = file.Layers[mid].Z;
                float nextZ = mid + 1 < file.Layers.Count ? file.Layers[mid + 1].Z : layerZ + file.LayerThicknessMm;

                if (z < layerZ)
                {
                    high = mid - 1;
                }
                else if (z >= nextZ)
                {
                    low = mid + 1;
                }
                else
                {
                    return file.Layers[mid];
                }
            }

            return file.Layers[Math.Max(0, Math.Min(low, file.Layers.Count - 1))];
        }
    }
}
