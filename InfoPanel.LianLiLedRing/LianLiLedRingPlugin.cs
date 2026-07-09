using InfoPanel.Plugins;
using Serilog;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace InfoPanel.LianLiLedRing
{
    /// <summary>
    /// InfoPanel plugin that drives the Lian Li Universal Screen 8.8" LED ring
    /// (VID 0x0416, PID 0x8050). Runs its own streaming loop in the plugin host
    /// process, independent of the LCD pipeline in the main application.
    /// </summary>
    public class LianLiLedRingPlugin : BasePlugin, IPluginConfigurable
    {
        private static readonly ILogger Logger = Log.ForContext<LianLiLedRingPlugin>();

        // Status entries shown in InfoPanel
        private readonly PluginText _statusText = new("status", "Ring Status", "Starting...");
        private readonly PluginText _effectText = new("effect", "Active Effect", "-");
        private readonly PluginSensor _fpsSensor = new("fps", "LED Frame Rate", 0, "fps");

        // Config state (host persists these automatically)
        private volatile bool _enabled = true;
        private volatile string _effectName = "Rainbow Morph";
        private volatile int _speed = 5;
        private volatile int _brightness = 100;
        private volatile bool _reverse;
        private volatile int _colorCount = 6;
        private readonly string[] _colors =
        {
            "#FF0000",
            "#FF9900",
            "#FFFF00",
            "#00FF66",
            "#33FFFF",
            "#4257F8",
        };

        // Streaming loop state
        private CancellationTokenSource? _cts;
        private Task? _streamTask;
        private FileSystemWatcher? _colorsWatcher;
        private volatile string _connectionStatus = "Starting...";
        private volatile float _fps;

        private static readonly string[] EffectNames =
        {
            "Off",
            "Rainbow",
            "Wave",
            "Static Color",
            "Breathing",
            "Rainbow Morph",
            "Paint",
            "Runway",
            "Tide",
            "Blow Up",
            "Meteor",
            "Snooker",
            "Mixing",
            "Ping-Pong",
            "Bullet Stack",
            "Twinkle",
            "River",
            "Hourglass",
            "Electric Current",
            "Rainbow Wave",
        };

        private static readonly Dictionary<string, LianLiLedEffect> EffectByName = new()
        {
            ["Off"] = LianLiLedEffect.Off,
            ["Rainbow"] = LianLiLedEffect.Rainbow,
            ["Wave"] = LianLiLedEffect.Wave,
            ["Static Color"] = LianLiLedEffect.StaticColor,
            ["Breathing"] = LianLiLedEffect.Breathing,
            ["Rainbow Morph"] = LianLiLedEffect.RainbowMorph,
            ["Paint"] = LianLiLedEffect.Paint,
            ["Runway"] = LianLiLedEffect.Runway,
            ["Tide"] = LianLiLedEffect.Tide,
            ["Blow Up"] = LianLiLedEffect.BlowUp,
            ["Meteor"] = LianLiLedEffect.Meteor,
            ["Snooker"] = LianLiLedEffect.Snooker,
            ["Mixing"] = LianLiLedEffect.Mixing,
            ["Ping-Pong"] = LianLiLedEffect.PingPong,
            ["Bullet Stack"] = LianLiLedEffect.BulletStack,
            ["Twinkle"] = LianLiLedEffect.Twinkle,
            ["River"] = LianLiLedEffect.River,
            ["Hourglass"] = LianLiLedEffect.Hourglass,
            ["Electric Current"] = LianLiLedEffect.ElectricCurrent,
            ["Rainbow Wave"] = LianLiLedEffect.RainbowWave,
        };

        public LianLiLedRingPlugin()
            : base("lianli-led-ring", "Lian Li LED Ring", "Lighting control for the Lian Li Universal Screen 8.8\" LED ring")
        {
        }

        [Obsolete("Config persistence is handled by the host")]
        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        public IReadOnlyList<PluginConfigProperty> ConfigProperties =>
        [
            new PluginConfigProperty
            {
                Key = "enabled",
                DisplayName = "LED Ring Enabled",
                Description = "Master switch for the LED ring",
                Type = PluginConfigType.Boolean,
                Value = _enabled,
            },
            new PluginConfigProperty
            {
                Key = "effect",
                DisplayName = "Effect",
                Description = "Lighting effect (matches L-Connect)",
                Type = PluginConfigType.Choice,
                Value = _effectName,
                Options = EffectNames,
            },
            new PluginConfigProperty
            {
                Key = "speed",
                DisplayName = "Speed",
                Description = "1 = slow, 10 = fast",
                Type = PluginConfigType.Integer,
                Value = _speed,
                MinValue = 1,
                MaxValue = 10,
                Step = 1,
            },
            new PluginConfigProperty
            {
                Key = "brightness",
                DisplayName = "Brightness",
                Description = "0-100%",
                Type = PluginConfigType.Integer,
                Value = _brightness,
                MinValue = 0,
                MaxValue = 100,
                Step = 5,
            },
            new PluginConfigProperty
            {
                Key = "reverse",
                DisplayName = "Reverse Direction",
                Type = PluginConfigType.Boolean,
                Value = _reverse,
            },
        ];

        public void ApplyConfig(string key, object? value)
        {
            switch (key)
            {
                case "enabled":
                    _enabled = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    break;
                case "effect":
                    _effectName = value as string ?? _effectName;
                    SaveEffect(); // let an open picker grey out unused color slots
                    break;
                case "speed":
                    _speed = Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 1, 10);
                    break;
                case "brightness":
                    _brightness = Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 0, 100);
                    break;
                case "reverse":
                    _reverse = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    break;
            }
        }

        public override void Initialize()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _streamTask = Task.Run(() => StreamLoopAsync(token), token);
        }

        public override void Load(List<IPluginContainer> containers)
        {
            LoadColors();
            SaveEffect();
            StartColorsWatcher();

            var container = new PluginContainer("led-ring", "LED Ring");
            container.Entries.Add(_statusText);
            container.Entries.Add(_effectText);
            container.Entries.Add(_fpsSensor);
            containers.Add(container);
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            _statusText.Value = _connectionStatus;
            _effectText.Value = _enabled ? _effectName : "Disabled";
            _fpsSensor.Value = _fps;
            return Task.CompletedTask;
        }

        public override void Update() => throw new NotImplementedException();

        public override void Close()
        {
            try
            {
                _colorsWatcher?.Dispose();
                _colorsWatcher = null;
                _cts?.Cancel();
                _streamTask?.Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // best effort
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        [PluginAction("Configure Colors...")]
        public void ConfigureColors()
        {
            // Launch on a throwaway background thread so nothing - not even an
            // unexpected exception type - can propagate onto the plugin host's
            // action-invocation thread and take the host (or app) down.
            var worker = new Thread(LaunchPicker) { IsBackground = true, Name = "LianLi-LaunchPicker" };
            worker.Start();
        }

        private void LaunchPicker()
        {
            try
            {
                SaveColors(); // make sure the picker sees current values
                SaveEffect(); // and the current effect, so it can grey unused slots

                var exePath = ResolvePickerPath();
                if (exePath == null)
                {
                    Logger.Warning(
                        "LedRingColorPicker.exe not found next to the plugin. Searched: {Dirs}",
                        string.Join("; ", CandidatePickerDirs()));
                    _connectionStatus = "Picker exe missing - reinstall plugin";
                    return;
                }

                Logger.Information("Launching color picker: {Path}", exePath);
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = $"\"{ColorsFilePath}\"",
                        WorkingDirectory = Path.GetDirectoryName(exePath)!,
                        UseShellExecute = false, // direct CreateProcess - no COM/ShellExecute
                    },
                };
                proc.Start();
                Logger.Information("Color picker started, pid {Pid}", proc.Id);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to launch color picker");
                _connectionStatus = "Picker launch failed: " + ex.Message;
            }
        }

        private static IEnumerable<string> CandidatePickerDirs()
        {
            // Fast path: next to this assembly. Works on a normal (first) load.
            var asmDir = Path.GetDirectoryName(typeof(LianLiLedRingPlugin).Assembly.Location);
            if (!string.IsNullOrEmpty(asmDir)) yield return asmDir;

            // After InfoPanel's "Reload", the plugin is re-loaded into a
            // collectible AssemblyLoadContext whose Assembly.Location comes back
            // EMPTY - so the line above yields nothing and we'd otherwise only
            // look in InfoPanel's install dir. Scan the plugins folder directly
            // instead; this is reload-proof.
            var pluginsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "InfoPanel", "plugins");
            if (Directory.Exists(pluginsRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(pluginsRoot))
                {
                    yield return dir;
                }
            }

            yield return AppContext.BaseDirectory;
        }

        private static string? ResolvePickerPath()
        {
            foreach (var dir in CandidatePickerDirs())
            {
                var candidate = Path.Combine(dir, "LedRingColorPicker.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string ColorsFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InfoPanel",
            "plugins",
            "lianli-led-ring.colors.txt");

        private static string EffectFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InfoPanel",
            "plugins",
            "lianli-led-ring.effect.txt");

        private void SaveEffect()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(EffectFilePath)!);
                File.WriteAllText(EffectFilePath, _effectName);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to write LED effect file");
            }
        }

        /// <summary>
        /// Colors picked via the native dialog can't be pushed back through the
        /// host's config pipeline, so the plugin keeps its own small color file.
        /// It is written on every change and wins over host-restored values.
        /// </summary>
        private void StartColorsWatcher()
        {
            try
            {
                var dir = Path.GetDirectoryName(ColorsFilePath)!;
                Directory.CreateDirectory(dir);
                _colorsWatcher = new FileSystemWatcher(dir, Path.GetFileName(ColorsFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _colorsWatcher.Changed += (_, _) => ReloadColorsDebounced();
                _colorsWatcher.Created += (_, _) => ReloadColorsDebounced();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to start colors file watcher");
            }
        }

        private DateTime _lastReload = DateTime.MinValue;
        private void ReloadColorsDebounced()
        {
            // File writes fire multiple events; ignore our own recent saves and coalesce.
            var now = DateTime.UtcNow;
            if ((now - _lastReload).TotalMilliseconds < 150) return;
            _lastReload = now;

            try
            {
                Thread.Sleep(60); // let the writer finish
                LoadColors();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to reload colors after file change");
            }
        }

        private void SaveColors()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ColorsFilePath)!);
                File.WriteAllLines(ColorsFilePath, _colors.Prepend(_colorCount.ToString(CultureInfo.InvariantCulture)));
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to save LED colors file");
            }
        }

        private void LoadColors()
        {
            try
            {
                if (!File.Exists(ColorsFilePath)) return;
                var lines = File.ReadAllLines(ColorsFilePath);
                if (lines.Length >= 1 && int.TryParse(lines[0], out var count))
                {
                    _colorCount = Math.Clamp(count, 1, 6);
                }
                for (var i = 0; i < _colors.Length && i + 1 < lines.Length; i++)
                {
                    if (lines[i + 1].Length > 0)
                    {
                        _colors[i] = lines[i + 1];
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to load LED colors file");
            }
        }

        private string EffectiveColorText()
        {
            return string.Join(",", _colors.Take(Math.Clamp(_colorCount, 1, _colors.Length)));
        }

        private async Task StreamLoopAsync(CancellationToken token)
        {
            LianLiLedRingDevice? ring = null;
            LianLiLedAnimation? animation = null;
            string? animationKey = null;
            var frameIndex = 0;
            var wasActive = true;
            var fpsWindowStart = Environment.TickCount64;
            var fpsFrames = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (ring == null)
                    {
                        ring = LianLiLedRingDevice.TryOpen();
                        if (ring == null)
                        {
                            _connectionStatus = "Not found";
                            _fps = 0;
                            await Task.Delay(5000, token);
                            continue;
                        }

                        Logger.Information("LianLi LED ring connected");
                        _connectionStatus = "Connected";
                        wasActive = true;
                    }

                    if (!EffectByName.TryGetValue(_effectName, out var effect))
                    {
                        effect = LianLiLedEffect.RainbowMorph;
                    }

                    var active = _enabled && effect != LianLiLedEffect.Off;
                    if (!active)
                    {
                        if (wasActive)
                        {
                            ring.TurnOff();
                            wasActive = false;
                        }
                        _fps = 0;
                        await Task.Delay(500, token);
                        continue;
                    }

                    wasActive = true;

                    var colorText = EffectiveColorText();
                    var key = $"{effect}|{colorText}|{_brightness}|{_reverse}";
                    if (animation == null || key != animationKey)
                    {
                        animation = LianLiLedEffectRenderer.Build(effect, ParseColors(colorText), _brightness, _reverse);
                        animationKey = key;
                        frameIndex = 0;
                    }

                    if (!ring.SendFrame(animation.Frames[frameIndex]))
                    {
                        Logger.Warning("LianLi LED ring frame send failed; reconnecting");
                        _connectionStatus = "Reconnecting...";
                        ring.Dispose();
                        ring = null;
                        await Task.Delay(1000, token);
                        continue;
                    }

                    frameIndex = (frameIndex + 1) % animation.Frames.Count;

                    fpsFrames++;
                    var now = Environment.TickCount64;
                    if (now - fpsWindowStart >= 1000)
                    {
                        _fps = fpsFrames * 1000f / (now - fpsWindowStart);
                        fpsFrames = 0;
                        fpsWindowStart = now;
                    }

                    // Vendor pacing: frame interval = speed-step x interval_base,
                    // with a 0.625 duty factor. Speed 1 (slow) .. 10 (fast).
                    var speedStep = 11 - Math.Clamp(_speed, 1, 10);
                    var delayMs = (int)Math.Clamp(animation.IntervalBaseMs * speedStep * 0.625, 15, 500);
                    await Task.Delay(delayMs, token);
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "LianLi LED ring loop failed");
                _connectionStatus = "Error: " + ex.Message;
            }
            finally
            {
                try
                {
                    ring?.TurnOff();
                }
                catch
                {
                    // best effort
                }
                ring?.Dispose();
            }
        }

        /// <summary>Parse a comma-separated list of #RRGGBB colors.</summary>
        internal static List<(byte R, byte G, byte B)> ParseColors(string? text)
        {
            var colors = new List<(byte R, byte G, byte B)>();

            if (!string.IsNullOrWhiteSpace(text))
            {
                foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var value = part.TrimStart('#');
                    if (value.Length == 6
                        && byte.TryParse(value.AsSpan(0, 2), NumberStyles.HexNumber, null, out var r)
                        && byte.TryParse(value.AsSpan(2, 2), NumberStyles.HexNumber, null, out var g)
                        && byte.TryParse(value.AsSpan(4, 2), NumberStyles.HexNumber, null, out var b))
                    {
                        colors.Add((r, g, b));
                    }
                }
            }

            if (colors.Count == 0)
            {
                colors.Add((255, 102, 0)); // default orange
            }

            return colors;
        }
    }
}
