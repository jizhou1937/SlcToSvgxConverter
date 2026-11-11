using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace SlcToSvgxConverter
{
    public partial class Form1 : Form
    {
        private SlcFile _slc;
        private SliceModel _model;
        private bool _merged;
        private string[] _mergedPaths;
        private string _slcPath = string.Empty;
        private readonly ToolTip _tips = new ToolTip();
        private int _layerIndex;
        private double _zoom = 1.0;
        private double _panX;
        private double _panY;
        private bool _panning;
        private Point _panStart;
        private double _panStartX;
        private double _panStartY;

        private readonly string[] _startupFiles;

        public Form1(string[] startupFiles)
        {
            _startupFiles = startupFiles;
            InitializeComponent();
            SetupTips();
            RestoreSettings();
            FormClosing += Form1_FormClosing;
            pictureBox1.Resize += Preview_Resize;
            MouseWheel += Form1_MouseWheel;
            pictureBox1.MouseDown += Preview_MouseDown;
            pictureBox1.MouseMove += Preview_MouseMove;
            pictureBox1.MouseUp += Preview_MouseUp;
            UpdateLayerControls();
            RenderPreview();
        }

        /// <summary>给“主 + 副”两值的输入框加提示，说明哪个是主值、哪个是副值。</summary>
        private void SetupTips()
        {
            SetPairTips("Base lift", textBox15, textBox27);
            SetPairTips("Lift", textBox14, textBox28);
            SetPairTips("Base return", textBox13, textBox29);
            SetPairTips("Return", textBox12, textBox30);
            SetPairTips("Base lift F", textBox11, textBox31);
            SetPairTips("Lift F", textBox18, textBox32);
            SetPairTips("Base return F", textBox17, textBox33);
            SetPairTips("Return F", textBox16, textBox34);

            _tips.SetToolTip(checkBox1, "Center the model bounding box on the origin (as FlashForge's slicer does). Unchecked: keep raw SLC coordinates.");
            _tips.SetToolTip(button1, "Convert the loaded SLC (or merged result) to SVGX");
        }

        private void SetPairTips(string name, TextBox main, TextBox secondary)
        {
            _tips.SetToolTip(main, name + " · primary value (a in a + b)");
            _tips.SetToolTip(secondary, name + " · secondary value (b in a + b)");
        }

        // ------------------------------------------------------------------ //
        // 参数记忆：关窗时把界面值写进 settings.ini，下次启动再读回来。
        // ------------------------------------------------------------------ //

        /// <summary>命令行/拖放带进来的文件：单文件直接打开，多文件按合并处理。</summary>
        private void ApplyStartupFiles()
        {
            if (_startupFiles == null || _startupFiles.Length == 0)
            {
                return;
            }

            List<string> slc = new List<string>();
            foreach (string path in _startupFiles)
            {
                if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".slc", StringComparison.OrdinalIgnoreCase))
                {
                    slc.Add(path);
                }
            }

            try
            {
                if (slc.Count > 1)
                {
                    LoadMerged(slc.ToArray());
                }
                else if (slc.Count == 1)
                {
                    LoadSlc(slc[0], false);
                }
                else if (_startupFiles.Length > 0 && File.Exists(_startupFiles[0]))
                {
                    if (string.Equals(Path.GetExtension(_startupFiles[0]), ".svgx", StringComparison.OrdinalIgnoreCase))
                    {
                        LoadSvgx(_startupFiles[0], false);
                    }
                }
            }
            catch (Exception)
            {
                // 启动阶段失败就退回“无文件”的状态，界面已经自己提示过了
            }
        }
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

            SaveText(values, "resolution_x", textBox1);
            SaveText(values, "resolution_y", textBox2);
            SaveText(values, "size_x", textBox4);
            SaveText(values, "size_y", textBox3);
            SaveText(values, "size_z", textBox5);
            SaveText(values, "bottom_layers", textBox9);
            SaveText(values, "exposure", textBox8);
            SaveText(values, "bottom_exposure", textBox7);
            SaveText(values, "transition_layers", textBox6);
            values["transition_type"] = domainUpDown1.Text;
            SaveText(values, "light_pwm", textBox21);
            SaveText(values, "bottom_light_pwm", textBox22);
            SaveText(values, "image_gray", textBox20);
            SaveText(values, "antialias", textBox19);
            values["antialias_level"] = numericUpDown1.Value.ToString(CultureInfo.InvariantCulture);

            SaveText(values, "lift_bottom_main", textBox15);
            SaveText(values, "lift_bottom_second", textBox27);
            SaveText(values, "lift_main", textBox14);
            SaveText(values, "lift_second", textBox28);
            SaveText(values, "retract_bottom_main", textBox13);
            SaveText(values, "retract_bottom_second", textBox29);
            SaveText(values, "retract_main", textBox12);
            SaveText(values, "retract_second", textBox30);
            SaveText(values, "liftspeed_bottom_main", textBox11);
            SaveText(values, "liftspeed_bottom_second", textBox31);
            SaveText(values, "liftspeed_main", textBox18);
            SaveText(values, "liftspeed_second", textBox32);
            SaveText(values, "retractspeed_bottom_main", textBox17);
            SaveText(values, "retractspeed_bottom_second", textBox33);
            SaveText(values, "retractspeed_main", textBox16);
            SaveText(values, "retractspeed_second", textBox34);

            SaveText(values, "gcode_start", textBox23);
            SaveText(values, "gcode_middle", textBox24);
            SaveText(values, "gcode_end", textBox25);

            values["center_bbox"] = checkBox1.Checked ? "1" : "0";
            SaveText(values, "last_path", textBox26);

            SettingsStore.Save(values);
        }

        private void RestoreSettings()
        {
            Dictionary<string, string> values = SettingsStore.Load();
            if (values.Count == 0)
            {
                return;
            }

            RestoreText(values, "resolution_x", textBox1);
            RestoreText(values, "resolution_y", textBox2);
            RestoreText(values, "size_x", textBox4);
            RestoreText(values, "size_y", textBox3);
            RestoreText(values, "size_z", textBox5);
            RestoreText(values, "bottom_layers", textBox9);
            RestoreText(values, "exposure", textBox8);
            RestoreText(values, "bottom_exposure", textBox7);
            RestoreText(values, "transition_layers", textBox6);
            RestoreText(values, "light_pwm", textBox21);
            RestoreText(values, "bottom_light_pwm", textBox22);
            RestoreText(values, "image_gray", textBox20);
            RestoreText(values, "antialias", textBox19);
            if (HasCjk(textBox19.Text))
            {
                textBox19.Text = "Off";
            }

            string text;
            if (values.TryGetValue("transition_type", out text))
            {
                domainUpDown1.Text = HasCjk(text) ? "Linear" : text;
            }

            if (values.TryGetValue("antialias_level", out text))
            {
                decimal level;
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out level))
                {
                    if (level < numericUpDown1.Minimum)
                    {
                        level = numericUpDown1.Minimum;
                    }

                    if (level > numericUpDown1.Maximum)
                    {
                        level = numericUpDown1.Maximum;
                    }

                    numericUpDown1.Value = level;
                }
            }

            RestoreText(values, "lift_bottom_main", textBox15);
            RestoreText(values, "lift_bottom_second", textBox27);
            RestoreText(values, "lift_main", textBox14);
            RestoreText(values, "lift_second", textBox28);
            RestoreText(values, "retract_bottom_main", textBox13);
            RestoreText(values, "retract_bottom_second", textBox29);
            RestoreText(values, "retract_main", textBox12);
            RestoreText(values, "retract_second", textBox30);
            RestoreText(values, "liftspeed_bottom_main", textBox11);
            RestoreText(values, "liftspeed_bottom_second", textBox31);
            RestoreText(values, "liftspeed_main", textBox18);
            RestoreText(values, "liftspeed_second", textBox32);
            RestoreText(values, "retractspeed_bottom_main", textBox17);
            RestoreText(values, "retractspeed_bottom_second", textBox33);
            RestoreText(values, "retractspeed_main", textBox16);
            RestoreText(values, "retractspeed_second", textBox34);

            RestoreText(values, "gcode_start", textBox23);
            RestoreText(values, "gcode_middle", textBox24);
            RestoreText(values, "gcode_end", textBox25);
            // 旧的 G 代码模板里可能带字面量 “\n”，还原成真正的换行再显示
            NormalizeMultiline(textBox23);
            NormalizeMultiline(textBox24);
            NormalizeMultiline(textBox25);

            if (values.TryGetValue("center_bbox", out text))
            {
                checkBox1.Checked = text == "1";
            }


            RestoreText(values, "last_path", textBox26);
            RestoreLastFile();
            ApplyStartupFiles();
        }

        /// <summary>上次的文件还在的话，顺手把层厚和标题也恢复出来。</summary>
        private void RestoreLastFile()
        {
            string path = textBox26.Text.Trim();
            if (path.Length == 0)
            {
                return;
            }

            if (!File.Exists(path))
            {
                textBox26.Text = string.Empty;
                return;
            }

            try
            {
                if (string.Equals(Path.GetExtension(path), ".svgx", StringComparison.OrdinalIgnoreCase))
                {
                    LoadSvgx(path, false);
                }
                else
                {
                    LoadSlc(path, false);
                }
            }
            catch (Exception)
            {
                _slc = null;
                _model = null;
                _slcPath = string.Empty;
                textBox26.Text = string.Empty;
                textBox10.Text = string.Empty;
                UpdateLayerControls();
                RenderPreview();
            }
        }

        /// <summary>把文本框里的字面量 \n、\r 还原成真正的换行。</summary>
        private static void NormalizeMultiline(TextBox box)
        {
            string text = box.Text;
            if (string.IsNullOrEmpty(text) || (text.IndexOf("\\n", StringComparison.Ordinal) < 0 &&
                                               text.IndexOf("\\r", StringComparison.Ordinal) < 0))
            {
                return;
            }

            box.Text = text
                .Replace("\\r\\n", "\n")
                .Replace("\\n", "\n")
                .Replace("\\r", "\n");
        }
        /// <summary>判断文本里是否含中文（用于把旧版中文设置迁移成英文）。</summary>
        private static bool HasCjk(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (char c in text)
            {
                if (c >= '\u3000' && c <= '\u9fff')
                {
                    return true;
                }
            }

            return false;
        }
        private static void SaveText(IDictionary<string, string> values, string key, TextBox box)
        {
            values[key] = box.Text;
        }

        private static void RestoreText(IDictionary<string, string> values, string key, TextBox box)
        {
            string text;
            if (values.TryGetValue(key, out text))
            {
                box.Text = text;
            }
        }

        // ------------------------------------------------------------------ //
        // 预览：panel1 里逐层显示 SLC 轮廓（黑底白实心）
        //       滚轮缩放、双击复位、滑块 + 上下层按钮翻层
        // ------------------------------------------------------------------ //

        private void Preview_Resize(object sender, EventArgs e)
        {
            RenderPreview();
        }

        private void button3_Click(object sender, EventArgs e)
        {
            ChangeLayer(-1);
        }

        private void button4_Click(object sender, EventArgs e)
        {
            ChangeLayer(1);
        }

        private void trackBar1_Scroll(object sender, EventArgs e)
        {
            if (_model == null || _model.Layers.Count == 0)
            {
                return;
            }

            _layerIndex = Math.Max(0, Math.Min(trackBar1.Value, _model.Layers.Count - 1));
            RenderPreview();
        }

        private void pictureBox1_MouseWheel(object sender, MouseEventArgs e)
        {
            TryZoom(e.Location, e.Delta);
        }

        /// <summary>滚轮缩放：鼠标在预览区里滚才生效（PictureBox 拿不到焦点，所以由窗体转发）。</summary>
        private void Form1_MouseWheel(object sender, MouseEventArgs e)
        {
            Point client = pictureBox1.PointToClient(Cursor.Position);
            if (!pictureBox1.ClientRectangle.Contains(client))
            {
                return;
            }

            TryZoom(client, e.Delta);
        }

        private void TryZoom(Point location, int delta)
        {
            if (_model == null || _model.Layers.Count == 0)
            {
                return;
            }

            int width = pictureBox1.Width;
            int height = pictureBox1.Height;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            double before = _zoom;
            double factor = delta > 0 ? 1.2 : 1 / 1.2;
            double zoom = Math.Max(0.2, Math.Min(40.0, before * factor));
            if (Math.Abs(zoom - before) < 1e-9)
            {
                return;
            }

            double fit = FitScale(width, height);
            double midX = (_model.MinX + _model.MaxX) / 2.0;
            double midY = (_model.MinY + _model.MaxY) / 2.0;

            // 缩放前后，光标下面那个模型点保持不动
            double modelX = midX + (location.X - width / 2.0 - _panX) / (fit * before);
            double modelY = midY - (location.Y - height / 2.0 - _panY) / (fit * before);

            _zoom = zoom;
            _panX = location.X - width / 2.0 - (modelX - midX) * (fit * zoom);
            _panY = location.Y - height / 2.0 + (modelY - midY) * (fit * zoom);

            RenderPreview();
        }

        private void pictureBox1_DoubleClick(object sender, EventArgs e)
        {
            ResetView();
        }

        private void button5_Click(object sender, EventArgs e)
        {
            ResetView();
        }

        /// <summary>还原默认视图：缩放回 100%、平移归零（层号不变）。</summary>
        private void ResetView()
        {
            _zoom = 1.0;
            _panX = 0;
            _panY = 0;
            RenderPreview();
        }

        private void Preview_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _model == null || _model.Layers.Count == 0)
            {
                return;
            }

            _panning = true;
            _panStart = e.Location;
            _panStartX = _panX;
            _panStartY = _panY;
            pictureBox1.Cursor = Cursors.SizeAll;
            pictureBox1.Capture = true;
        }

        private void Preview_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_panning)
            {
                return;
            }

            _panX = _panStartX + (e.X - _panStart.X);
            _panY = _panStartY + (e.Y - _panStart.Y);

            // 别让图形被拖到完全看不见
            _panX = Math.Max(-pictureBox1.Width * 2.0, Math.Min(pictureBox1.Width * 2.0, _panX));
            _panY = Math.Max(-pictureBox1.Height * 2.0, Math.Min(pictureBox1.Height * 2.0, _panY));

            RenderPreview();
        }

        private void Preview_MouseUp(object sender, MouseEventArgs e)
        {
            if (!_panning)
            {
                return;
            }

            _panning = false;
            pictureBox1.Capture = false;
            pictureBox1.Cursor = Cursors.Default;
        }

        private void ChangeLayer(int delta)
        {
            if (_model == null || _model.Layers.Count == 0)
            {
                return;
            }

            _layerIndex = Math.Max(0, Math.Min(_layerIndex + delta, _model.Layers.Count - 1));

            if (trackBar1.Value != _layerIndex)
            {
                trackBar1.Value = _layerIndex;
            }

            RenderPreview();
        }

        private void ResetPreview()
        {
            _layerIndex = 0;
            _zoom = 1.0;
            _panX = 0;
            _panY = 0;

            UpdateLayerControls();
            RenderPreview();
        }

        private void UpdateLayerControls()
        {
            bool hasData = _model != null && _model.Layers.Count > 0;

            trackBar1.Enabled = hasData;
            button3.Enabled = hasData;
            button4.Enabled = hasData;

            if (!hasData)
            {
                trackBar1.Maximum = 0;
                trackBar1.Value = 0;
                _layerIndex = 0;
                return;
            }

            int maximum = _model.Layers.Count - 1;
            if (trackBar1.Maximum != maximum)
            {
                trackBar1.Maximum = maximum;
            }

            _layerIndex = Math.Max(0, Math.Min(_layerIndex, maximum));
            if (trackBar1.Value != _layerIndex)
            {
                trackBar1.Value = _layerIndex;
            }
        }

        /// <summary>把模型装进画布（zoom=1 时）所需的 mm→px 比例。</summary>
        private double FitScale(int width, int height)
        {
            double spanX = Math.Max(1e-6, _model.MaxX - _model.MinX);
            double spanY = Math.Max(1e-6, _model.MaxY - _model.MinY);

            const double padding = 28;
            double byX = Math.Max(1.0, width - padding) / spanX;
            double byY = Math.Max(1.0, height - padding) / spanY;

            return Math.Min(byX, byY);
        }

        private void RenderPreview()
        {
            int width = pictureBox1.Width;
            int height = pictureBox1.Height;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            Bitmap bitmap = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Black);

                if (_model == null || _model.Layers.Count == 0)
                {
                    using (Font font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9F))
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(110, 110, 110)))
                    {
                        const string hint = "No file loaded.";
                        SizeF size = g.MeasureString(hint, font);
                        g.DrawString(hint, font, brush, (width - size.Width) / 2F, (height - size.Height) / 2F);
                    }
                }
                else
                {
                    _layerIndex = Math.Max(0, Math.Min(_layerIndex, _model.Layers.Count - 1));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    DrawCurrentLayer(g, width, height);
                    DrawPreviewText(g, width, height);
                }
            }

            Image previous = pictureBox1.Image;
            pictureBox1.Image = bitmap;

            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private void DrawCurrentLayer(Graphics g, int width, int height)
        {
            SliceLayerInfo layer = _model.Layers[_layerIndex];
            double scale = FitScale(width, height) * _zoom;
            double midX = (_model.MinX + _model.MaxX) / 2.0;
            double midY = (_model.MinY + _model.MaxY) / 2.0;
            double cx = width / 2.0 + _panX;
            double cy = height / 2.0 + _panY;

            using (GraphicsPath path = new GraphicsPath(FillMode.Alternate))
            {
                foreach (float[] contour in layer.Contours)
                {
                    int count = contour.Length / 2;
                    if (count < 3)
                    {
                        continue;
                    }

                    PointF[] points = new PointF[count];
                    for (int i = 0; i < count; i++)
                    {
                        points[i] = new PointF(
                            (float)(cx + (contour[i * 2] - midX) * scale),
                            (float)(cy - (contour[i * 2 + 1] - midY) * scale));
                    }

                    path.AddPolygon(points);
                }

                if (path.PointCount > 0)
                {
                    g.FillPath(Brushes.White, path);
                }
            }
        }

        private void DrawPreviewText(Graphics g, int width, int height)
        {
            SliceLayerInfo layer = _model.Layers[_layerIndex];

            // 第一行：层号 / 总层数 / Z
            string line1 = string.Format(
                CultureInfo.CurrentCulture,
                "Layer {0} / {1}     Z = {2:0.00} mm",
                _layerIndex + 1,
                _model.Layers.Count,
                layer.Z);

            // 第二行：面积 + 轮廓数（外/孔）+ 来源
            string line2 = string.Format(
                CultureInfo.CurrentCulture,
                "Area {0:0.0} mm²     Contours {1} (outer {2} / holes {3})     {4}",
                layer.Area,
                layer.Contours.Count,
                layer.OuterCount,
                layer.HoleCount,
                _model.Format);

            string zoomText = Math.Abs(_zoom - 1.0) > 1e-6
                ? string.Format(CultureInfo.CurrentCulture, "Zoom {0:0}%", _zoom * 100)
                : string.Empty;

            using (Font font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9F))
            {
                SizeF size1 = g.MeasureString(line1, font);
                SizeF size2 = g.MeasureString(line2, font);
                float lineHeight = size1.Height;

                using (SolidBrush back = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                {
                    g.FillRectangle(back, 0, 0, width, lineHeight * 2 + 12);
                }

                g.DrawString(line1, font, Brushes.White, 8, 3);
                g.DrawString(line2, font, Brushes.White, 8, lineHeight + 7);

                if (zoomText.Length > 0)
                {
                    SizeF zoomSize = g.MeasureString(zoomText, font);
                    g.DrawString(zoomText, font, Brushes.White, width - zoomSize.Width - 8, 3);
                }
            }
        }

        /// <summary>“...” 选择 SLC 文件。</summary>
        private void button2_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Select a slice file (SLC or SVGX)";
                dialog.Filter = "Slice files (*.slc;*.svgx)|*.slc;*.svgx|SLC files (*.slc)|*.slc|SVGX files (*.svgx)|*.svgx|All files (*.*)|*.*";
                dialog.CheckFileExists = true;
                dialog.Multiselect = true;

                string current = textBox26.Text.Trim();
                if (current.Length > 0)
                {
                    try
                    {
                        string folder = Path.GetDirectoryName(current);
                        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                        {
                            dialog.InitialDirectory = folder;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // 路径里有非法字符，忽略即可，用默认目录。
                    }
                }

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                if (dialog.FileNames.Length > 1)
                {
                    LoadMerged(dialog.FileNames);
                }
                else if (string.Equals(Path.GetExtension(dialog.FileName), ".svgx", StringComparison.OrdinalIgnoreCase))
                {
                    LoadSvgx(dialog.FileName, true);
                }
                else
                {
                    LoadSlc(dialog.FileName, true);
                }
            }
        }

        /// <summary>读取 SLC：显示路径、把层厚（只读）填成 SLC 自带的值。</summary>
        private void LoadSlc(string path, bool reportError)
        {
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;

            try
            {
                SlcFile slc = SlcFile.Load(path);
                _slc = slc;
                _slcPath = path;
                _model = SliceModel.FromSlc(slc);
                _model.FilePath = path;
                _merged = false;
                _mergedPaths = null;

                ShowModelInfo(_model, path);
                ResetPreview();
            }
            catch (Exception ex)
            {
                _slc = null;
                _model = null;
                _slcPath = string.Empty;
                textBox10.Text = string.Empty;
                button1.Enabled = true;

                if (reportError)
                {
                    MessageBox.Show(
                        this,
                        "Failed to read the SLC file:" + Environment.NewLine + Environment.NewLine + ex.Message,
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                Cursor = previous;
            }
        }

        /// <summary>把路径、层厚（只读）和窗口标题同步到界面；SVGX 只能预览，Start 会被禁用。</summary>
        private void ShowModelInfo(SliceModel model, string path)
        {
            textBox26.Text = path;
            textBox10.Text = SvgxConverter.Num(model.LayerThickness);

            string format = string.Equals(model.Format, "SVGX", StringComparison.OrdinalIgnoreCase) ? " — SVGX preview" : string.Empty;
            Text = string.Format(
                CultureInfo.CurrentCulture,
                "SLC to SVGX Converter — {0} ({1} layers, {2} mm{3})",
                Path.GetFileName(path),
                model.Layers.Count,
                SvgxConverter.Num(model.LayerThickness),
                format);

            button1.Enabled = !string.Equals(model.Format, "SVGX", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>读取 svgx（其他软件导出的切片）用于预览。</summary>
        private void LoadSvgx(string path, bool reportError)
        {
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;

            try
            {
                SliceModel model = SvgxReader.Load(path);
                _slc = null;
                _model = model;
                _slcPath = path;

                ShowModelInfo(model, path);
                ResetPreview();
            }
            catch (Exception ex)
            {
                _slc = null;
                _model = null;
                _slcPath = string.Empty;
                textBox10.Text = string.Empty;
                button1.Enabled = true;
                UpdateLayerControls();
                RenderPreview();

                if (reportError)
                {
                    MessageBox.Show(
                        this,
                        "Failed to read the SVGX file:" + Environment.NewLine + Environment.NewLine + ex.Message,
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                Cursor = previous;
            }
        }



        /// <summary>多选 SLC：合并成一份再转换（层厚取最细、按绝对 Z 对齐、轮廓全保留）。</summary>
        private void LoadMerged(string[] paths)
        {
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;

            try
            {
                List<SlcFile> files = new List<SlcFile>();
                foreach (string path in paths)
                {
                    if (!string.Equals(Path.GetExtension(path), ".slc", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Merging supports .slc files only: " + Path.GetFileName(path));
                    }

                    files.Add(SlcFile.Load(path));
                }

                SlcFile merged = SlcMerger.Merge(files);

                _slc = merged;
                _model = SliceModel.FromSlc(merged);
                _model.FilePath = paths[0];
                _merged = true;
                _mergedPaths = paths;
                _slcPath = paths[0];

                textBox26.Text = paths[0];
                textBox10.Text = SvgxConverter.Num(merged.LayerThicknessMm);
                Text = string.Format(
                    CultureInfo.CurrentCulture,
                    "SLC to SVGX Converter — merged {0} SLC files ({1} layers, {2} mm)",
                    paths.Length,
                    merged.Layers.Count,
                    SvgxConverter.Num(merged.LayerThicknessMm));
                button1.Enabled = true;

                ResetPreview();
            }
            catch (Exception ex)
            {
                _slc = null;
                _model = null;
                _merged = false;
                _mergedPaths = null;
                textBox10.Text = string.Empty;
                UpdateLayerControls();
                RenderPreview();

                MessageBox.Show(
                    this,
                    "Merge failed:" + Environment.NewLine + Environment.NewLine + ex.Message,
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = previous;
            }
        }
        /// <summary>“Start” 把当前的 SLC 转换成 svgx。</summary>
        private void button1_Click(object sender, EventArgs e)
        {
            if (_model != null && string.Equals(_model.Format, "SVGX", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    this,
                    "This is an SVGX file: preview only. Use Start to convert an SLC file.",
                    "Notice",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            string path = textBox26.Text.Trim();

            if (path.Length == 0)
            {
                MessageBox.Show(
                    this,
                    "Select an SLC file with “...” first.",
                    "Notice",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (!File.Exists(path))
            {
                MessageBox.Show(
                    this,
                    "File not found: " + Environment.NewLine + path,
                    "Notice",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            SvgxParams parameters = ReadParameters();
            if (parameters == null)
            {
                return;
            }

            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;

            try
            {
                SlcFile slc;
                if (_slc != null && string.Equals(_slcPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    slc = _slc;
                }
                else
                {
                    slc = SlcFile.Load(path);
                    _slc = slc;
                    _slcPath = path;
                    _model = SliceModel.FromSlc(slc);
                    ShowModelInfo(_model, path);
                    ResetPreview();
                }

                Stopwatch watch = Stopwatch.StartNew();

                string basePath = _merged && _mergedPaths != null && _mergedPaths.Length > 0
                    ? Path.Combine(Path.GetDirectoryName(_mergedPaths[0]), "merged_" + _mergedPaths.Length + "_files.slc")
                    : path;

                string outputPath = SvgxConverter.BuildOutputPath(basePath, slc.LayerThicknessMm, parameters.BaseTime);
                SvgxConverter.Convert(slc, outputPath, parameters);

                watch.Stop();

                MessageBox.Show(
                    this,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Done.{0}{0}Layers: {1}{0}Layer height: {2} mm{0}Elapsed: {3} ms{0}{0}Output:{0}{4}",
                        Environment.NewLine,
                        slc.LayerCount,
                        SvgxConverter.Num(slc.LayerThicknessMm),
                        watch.ElapsedMilliseconds,
                        outputPath),
                    "Done",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "Conversion failed:" + Environment.NewLine + Environment.NewLine + ex.Message,
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = previous;
            }
        }

        /// <summary>从界面控件读出 svgx 打印参数；数值非法时提示并返回 null。</summary>
        private SvgxParams ReadParameters()
        {
            SvgxParams parameters = new SvgxParams();

            parameters.AttachLayers = ReadInt(textBox9, parameters.AttachLayers);

            // 两个参考样本里 attachlayer 与 buildinlayer 恒等于同一个值（底层数）；
            // “过渡层数”在 svgx 里没有对应字段，只在界面上保留。
            parameters.BuildInLayers = ReadInt(textBox6, parameters.BuildInLayers);   // buildinlayer = transition layers

            parameters.AttachTime = ReadDouble(textBox7, parameters.AttachTime);
            parameters.BaseTime = ReadDouble(textBox8, parameters.BaseTime);
            parameters.ResolutionX = ReadInt(textBox1, parameters.ResolutionX);
            parameters.ResolutionY = ReadInt(textBox2, parameters.ResolutionY);
            parameters.DisplayWidth = ReadDouble(textBox4, parameters.DisplayWidth);
            parameters.DisplayHeight = ReadDouble(textBox3, parameters.DisplayHeight);
            parameters.MachineZ = ReadDouble(textBox5, parameters.MachineZ);
            parameters.CenterBoundingBox = checkBox1.Checked;   // 勾选 = 包围盒居中（同闪铸原厂）
            parameters.LightPwm = ReadInt(textBox21, parameters.LightPwm);   // 光强，写进分区曝光 G 代码的 M106 S…

            if (parameters.ResolutionX <= 0 || parameters.ResolutionY <= 0)
            {
                MessageBox.Show(this, "Please enter a valid resolution (X / Y).", "Notice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            if (parameters.DisplayWidth <= 0 || parameters.DisplayHeight <= 0)
            {
                MessageBox.Show(this, "Please enter a valid screen size (X / Y).", "Notice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            if (parameters.MachineZ <= 0)
            {
                MessageBox.Show(this, "Please enter a valid Z travel (Size Z).", "Notice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            return parameters;
        }

        private static double ReadDouble(TextBox box, double fallback)
        {
            string text = box.Text.Trim();
            double value;

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                return value;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return fallback;
        }

        private static int ReadInt(TextBox box, int fallback)
        {
            double value = ReadDouble(box, fallback);
            if (double.IsNaN(value) || double.IsInfinity(value) || value < int.MinValue || value > int.MaxValue)
            {
                return fallback;
            }

            return (int)Math.Round(value);
        }

        private void textBox23_TextChanged(object sender, EventArgs e)
        {

        }

        private void label28_Click(object sender, EventArgs e)
        {

        }
    }
}
