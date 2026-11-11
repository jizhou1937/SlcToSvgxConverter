using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SlcToSvgxConverter
{
    /// <summary>
    /// 参数记忆：一行一个 <c>key=value</c> 存到
    /// <c>%APPDATA%\SlcToSvgxConverter\settings.ini</c>。
    /// 值里的换行/反斜杠会转义，所以多行 G 代码也能原样存回来。
    /// </summary>
    internal static class SettingsStore
    {
        private const string FolderName = "SlcToSvgxConverter";

        /// <summary>旧版（中文名）目录，仅在首次运行时用来迁移已有设置。</summary>
        private const string LegacyFolderName = "lcd切片转换工具";

        private static string AppDataFolder(string name)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                name);
        }

        private static string SettingsPath()
        {
            return Path.Combine(AppDataFolder(FolderName), "settings.ini");
        }

        /// <summary>把旧目录里的 settings.ini 复制到新目录（只做一次，之后忽略旧文件）。</summary>
        private static void MigrateLegacySettings()
        {
            try
            {
                string current = SettingsPath();
                if (File.Exists(current))
                {
                    return;
                }

                string legacy = Path.Combine(AppDataFolder(LegacyFolderName), "settings.ini");
                if (!File.Exists(legacy))
                {
                    return;
                }

                string folder = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                File.Copy(legacy, current, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public static Dictionary<string, string> Load()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            MigrateLegacySettings();

            try
            {
                string path = SettingsPath();
                if (!File.Exists(path))
                {
                    return map;
                }

                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    {
                        continue;
                    }

                    int index = line.IndexOf('=');
                    if (index <= 0)
                    {
                        continue;
                    }

                    map[line.Substring(0, index)] = Unescape(line.Substring(index + 1));
                }
            }
            catch (IOException)
            {
                // 读不到就当没有记忆，用界面默认值。
            }
            catch (UnauthorizedAccessException)
            {
            }

            return map;
        }

        public static void Save(IDictionary<string, string> map)
        {
            MigrateLegacySettings();

            try
            {
                string path = SettingsPath();
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                List<string> lines = new List<string>();
                lines.Add("# SLC to SVGX Converter — remembered settings (auto-generated). Delete this file to reset.");

                foreach (KeyValuePair<string, string> pair in map)
                {
                    lines.Add(pair.Key + "=" + Escape(pair.Value ?? string.Empty));
                }

                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(true));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string Escape(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static string Unescape(string value)
        {
            StringBuilder sb = new StringBuilder(value.Length);

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '\\' || i + 1 >= value.Length)
                {
                    sb.Append(c);
                    continue;
                }

                char next = value[++i];
                switch (next)
                {
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 'n':
                        sb.Append('\n');
                        break;
                    case '\\':
                        sb.Append('\\');
                        break;
                    default:
                        sb.Append('\\').Append(next);
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
