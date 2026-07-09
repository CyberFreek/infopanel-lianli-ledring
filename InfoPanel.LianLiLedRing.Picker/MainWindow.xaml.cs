using MahApps.Metro.Controls;
using System.Collections.ObjectModel;
using System.Collections.Generic;
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

        // How many of the six colors each effect actually uses.
        //   0  = the effect generates its own colors (your colors are ignored)
        //   6  = variable; the "Colors used" count applies
        //   1-4 = fixed number of colors
        // Mirrors the effect implementations in the plugin engine.
        private static readonly Dictionary<string, int> EffectColorCount = new()
        {
            ["Off"] = 0,
            ["Rainbow"] = 0,
            ["Wave"] = 6,
            ["Static Color"] = 1,
            ["Breathing"] = 1,
            ["Rainbow Morph"] = 0,
            ["Paint"] = 6,
            ["Runway"] = 2,
            ["Tide"] = 6,
            ["Blow Up"] = 6,
            ["Meteor"] = 6,
            ["Snooker"] = 6,
            ["Mixing"] = 2,
            ["Ping-Pong"] = 6,
            ["Bullet Stack"] = 0,
            ["Twinkle"] = 0,
            ["River"] = 2,
            ["Hourglass"] = 4,
            ["Electric Current"] = 4,
            ["Rainbow Wave"] = 0,
        };

        private readonly string _filePath;
        private readonly string _effectFilePath;
        private readonly ObservableCollection<ColorRow> _rows = new();
        private FileSystemWatcher? _effectWatcher;
        private string _effect = "";
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

            _effectFilePath = Path.Combine(
                Path.GetDirectoryName(_filePath) ?? ".", "lianli-led-ring.effect.txt");

            for (var i = 0; i < 6; i++)
            {
                var row = new ColorRow(i);
                row.PropertyChanged += Row_PropertyChanged;
                _rows.Add(row);
            }

            ColorList.ItemsSource = _rows;
            LoadEffect();
            Load();
            StartEffectWatcher();

            Loaded += (_, _) =>
            {
                Topmost = true;
                Activate();
                Topmost = false;
                Focus();
            };

            Closed += (_, _) => _effectWatcher?.Dispose();
        }

        private int Count
        {
            get => (int)(CountBox.Value ?? 6);
            set => CountBox.Value = Math.Clamp(value, 1, 6);
        }

        private int EffectMax => EffectColorCount.TryGetValue(_effect, out var m) ? m : 6;

        private int ActiveSlots()
        {
            var max = EffectMax;
            return max == 0 ? 0 : Math.Min(max, Count);
        }

        private void LoadEffect()
        {
            try
            {
                _effect = File.Exists(_effectFilePath)
                    ? File.ReadAllText(_effectFilePath).Trim()
                    : "";
            }
            catch
            {
                _effect = "";
            }
        }

        private void StartEffectWatcher()
        {
            try
            {
                var dir = Path.GetDirectoryName(_effectFilePath)!;
                Directory.CreateDirectory(dir);
                _effectWatcher = new FileSystemWatcher(dir, Path.GetFileName(_effectFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                FileSystemEventHandler onChange = (_, _) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        LoadEffect();
                        UpdateOpacities();
                    });
                };
                _effectWatcher.Changed += onChange;
                _effectWatcher.Created += onChange;
            }
            catch
            {
                // effect awareness is a nicety; ignore if the watcher can't start
            }
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
            if (_rows.Count < 6) return;

            var active = ActiveSlots();
            for (var i = 0; i < 6; i++)
            {
                _rows[i].IsActive = i < active;
                _rows[i].Opacity = i < active ? 1.0 : 0.35;
            }

            CountPanel.IsEnabled = EffectMax >= 2;
            EffectHeader.Text = DescribeEffect();
        }

        private string DescribeEffect()
        {
            if (string.IsNullOrEmpty(_effect))
            {
                return "Set the colors used by multi-color effects.";
            }

            return EffectMax switch
            {
                0 => $"“{_effect}” uses its own colors - the colors below don't apply.",
                1 => $"“{_effect}” uses 1 color.",
                6 => $"“{_effect}” uses up to 6 colors (set with “Colors used”).",
                _ => $"“{_effect}” uses {EffectMax} colors.",
            };
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
            if (_rows.Count < 6) return;
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
            private bool _isActive = true;

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

            public bool IsActive
            {
                get => _isActive;
                set
                {
                    if (_isActive != value)
                    {
                        _isActive = value;
                        OnChanged(nameof(IsActive));
                    }
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
