using MahApps.Metro.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace InfoPanel.LianLiLedRing.Picker
{
    public partial class MainWindow : MetroWindow
    {
        private static readonly string[] RainbowDefault =
        {
            "#FF0000", "#FF9900", "#FFFF00", "#00FF66", "#33FFFF", "#4257F8",
        };

        private readonly string _filePath;
        private readonly ObservableCollection<ColorRow> _rows = new();
        private bool _loading;

        public MainWindow()
        {
            InitializeComponent();

            var args = Environment.GetCommandLineArgs();
            _filePath = args.Length > 1
                ? args[1]
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "InfoPanel", "plugins", "lianli-led-ring.colors.txt");

            for (var i = 0; i < 6; i++)
            {
                var row = new ColorRow(i);
                row.PropertyChanged += Row_PropertyChanged;
                _rows.Add(row);
            }

            ColorList.ItemsSource = _rows;
            Load();

            // Make sure the window comes to the front when launched from the
            // plugin host (which is not the foreground process).
            Loaded += (_, _) =>
            {
                Topmost = true;
                Activate();
                Topmost = false;
                Focus();
            };
        }

        private int Count
        {
            get => (int)(CountBox.Value ?? 6);
            set => CountBox.Value = Math.Clamp(value, 1, 6);
        }

        private void Load()
        {
            _loading = true;
            try
            {
                var colors = (string[])RainbowDefault.Clone();
                var count = 6;

                if (File.Exists(_filePath))
                {
                    var lines = File.ReadAllLines(_filePath);
                    if (lines.Length >= 1 && int.TryParse(lines[0], out var c))
                    {
                        count = Math.Clamp(c, 1, 6);
                    }
                    for (var i = 0; i < 6 && i + 1 < lines.Length; i++)
                    {
                        if (TryParseColor(lines[i + 1], out _))
                        {
                            colors[i] = lines[i + 1].Trim();
                        }
                    }
                }

                for (var i = 0; i < 6; i++)
                {
                    _rows[i].Color = ToColor(colors[i]);
                }
                Count = count;
                UpdateOpacities();
            }
            catch
            {
                // start from defaults on any read error
            }
            finally
            {
                _loading = false;
            }
        }

        private void Save()
        {
            if (_loading || _rows.Count < 6) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var lines = new string[7];
                lines[0] = Count.ToString(CultureInfo.InvariantCulture);
                for (var i = 0; i < 6; i++)
                {
                    var c = _rows[i].Color;
                    lines[i + 1] = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                }
                File.WriteAllLines(_filePath, lines);
            }
            catch
            {
                // best effort; the plugin will pick up the next successful write
            }
        }

        private void UpdateOpacities()
        {
            // NumericUpDown raises ValueChanged while the XAML is still being
            // parsed (before the constructor fills _rows), so guard the access.
            if (_rows.Count < 6) return;

            var count = Count;
            for (var i = 0; i < 6; i++)
            {
                _rows[i].Opacity = i < count ? 1.0 : 0.35;
            }
        }

        private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ColorRow.Color))
            {
                Save();
            }
        }

        private void CountBox_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double?> e)
        {
            if (_rows.Count < 6) return; // ignore the change raised during XAML init
            UpdateOpacities();
            Save();
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _loading = true;
            for (var i = 0; i < 6; i++)
            {
                _rows[i].Color = ToColor(RainbowDefault[i]);
            }
            Count = 6;
            _loading = false;
            UpdateOpacities();
            Save();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private static bool TryParseColor(string? text, out Color color)
        {
            color = Colors.White;
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                color = (Color)ColorConverter.ConvertFromString(text.Trim());
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Color ToColor(string hex) => TryParseColor(hex, out var c) ? c : Colors.White;

        private sealed class ColorRow : INotifyPropertyChanged
        {
            private Color _color = Colors.White;
            private double _opacity = 1.0;

            public ColorRow(int index)
            {
                Label = $"Color {index + 1}";
            }

            public string Label { get; }

            public Color Color
            {
                get => _color;
                set
                {
                    if (_color != value)
                    {
                        _color = value;
                        OnChanged(nameof(Color));
                    }
                }
            }

            public double Opacity
            {
                get => _opacity;
                set
                {
                    if (_opacity != value)
                    {
                        _opacity = value;
                        OnChanged(nameof(Opacity));
                    }
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
