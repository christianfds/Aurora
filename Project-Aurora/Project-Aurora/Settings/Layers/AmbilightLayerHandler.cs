﻿using Aurora.EffectsEngine;
using Aurora.Profiles;
using Aurora.Settings.Overrides;
using Newtonsoft.Json;
using SharpDX;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Aurora.Settings.Layers
{
    public enum AmbilightType
    {
        [Description("Default")]
        Default = 0,

        [Description("Average color")]
        AverageColor = 1
    }

    public enum AmbilightCaptureType
    {
        [Description("Coordinates")]
        Coordinates = 0,

        [Description("Entire Monitor")]
        EntireMonitor = 1,

        [Description("Foreground Application")]
        ForegroundApp = 2,

        [Description("Specific Process")]
        SpecificProcess = 3
    }

    public enum AmbilightFpsChoice
    {
        [Description("Lowest")]
        VeryLow = 0,

        [Description("Low")]
        Low,

        [Description("Medium")]
        Medium,

        [Description("High")]
        High,

        [Description("Highest")]
        Highest,
    }

    public enum AmbilightQuality
    {
        [Description("Lowest")]
        VeryLow = 0,

        [Description("Low")]
        Low,

        [Description("Medium")]
        Medium,

        [Description("High")]
        High,

        [Description("Highest")]
        Highest,
    }

    public class AmbilightLayerHandlerProperties : LayerHandlerProperties2Color<AmbilightLayerHandlerProperties>
    {
        public AmbilightType? _AmbilightType { get; set; }

        [JsonIgnore]
        public AmbilightType AmbilightType { get { return Logic._AmbilightType ?? _AmbilightType ?? AmbilightType.Default; } }

        public AmbilightCaptureType? _AmbilightCaptureType { get; set; }

        [JsonIgnore]
        public AmbilightCaptureType AmbilightCaptureType { get { return Logic._AmbilightCaptureType ?? _AmbilightCaptureType ?? AmbilightCaptureType.EntireMonitor; } }

        public int? _AmbilightOutputId { get; set; }

        [JsonIgnore]
        public int AmbilightOutputId { get { return Logic._AmbilightOutputId ?? _AmbilightOutputId ?? 0; } }

        public AmbilightFpsChoice? _AmbiLightUpdatesPerSecond { get; set; }

        [JsonIgnore]
        public AmbilightFpsChoice AmbiLightUpdatesPerSecond => Logic._AmbiLightUpdatesPerSecond ?? _AmbiLightUpdatesPerSecond ?? AmbilightFpsChoice.Medium;

        public String _SpecificProcess { get; set; }

        [JsonIgnore]
        public String SpecificProcess { get { return Logic._SpecificProcess ?? _SpecificProcess ?? String.Empty; } }

        public Rectangle? _Coordinates { get; set; }

        [JsonIgnore]
        public Rectangle Coordinates { get { return Logic._Coordinates ?? _Coordinates ?? Rectangle.Empty; } }

        public AmbilightQuality? _AmbilightQuality { get; set; }

        [JsonIgnore]
        public AmbilightQuality AmbilightQuality { get { return Logic._AmbilightQuality ?? _AmbilightQuality ?? AmbilightQuality.Medium; } }

        public bool? _BrightenImage { get; set; }

        [JsonIgnore]
        public bool BrightenImage { get { return Logic._BrightenImage ?? _BrightenImage ?? false; } }

        public float? _BrightnessChange { get; set; }
        [JsonIgnore]
        public float BrightnessChange => Logic._BrightnessChange ?? _BrightnessChange ?? 0.0f;

        public bool? _SaturateImage { get; set; }

        [JsonIgnore]
        public bool SaturateImage { get { return Logic._SaturateImage ?? _SaturateImage ?? false; } }

        public float? _SaturationChange { get; set; }
        [JsonIgnore]
        public float SaturationChange => Logic._SaturationChange ?? _SaturationChange ?? 0.0f;

        public AmbilightLayerHandlerProperties() : base() { }

        public AmbilightLayerHandlerProperties(bool assign_default = false) : base(assign_default) { }

        public override void Default()
        {
            base.Default();
            this._AmbilightOutputId = 0;
            this._AmbiLightUpdatesPerSecond = AmbilightFpsChoice.Medium;
            this._AmbilightType = AmbilightType.Default;
            this._AmbilightCaptureType = AmbilightCaptureType.EntireMonitor;
            this._SpecificProcess = "";
            this._Coordinates = new Rectangle(0, 0, 0, 0);
            this._AmbilightQuality = AmbilightQuality.Medium;
            this._BrightenImage = false;
            this._BrightnessChange = 0.0f;
            this._SaturateImage = false;
            this._SaturationChange = 1.0f;
        }
    }

    [LogicOverrideIgnoreProperty("_PrimaryColor")]
    [LogicOverrideIgnoreProperty("_SecondaryColor")]
    [LogicOverrideIgnoreProperty("_Sequence")]
    public class AmbilightLayerHandler : LayerHandler<AmbilightLayerHandlerProperties>, INotifyPropertyChanged
    {
        private static System.Timers.Timer captureTimer;
        private static Image screen;
        private static long last_use_time = 0;
        private static DesktopDuplicator desktopDuplicator;
        private static bool processing = false;  // Used to avoid updating before the previous update is processed
        private static System.Timers.Timer retryTimer;
        private static RawRectangle bounds;
        public event PropertyChangedEventHandler PropertyChanged;
        public int OutputId
        {
            get { return Properties.AmbilightOutputId; }
            set
            {
                if (Properties._AmbilightOutputId != value)
                {
                    Properties._AmbilightOutputId = value;
                    InvokePropertyChanged("OutputId");
                    this.Initialize();
                }
            }
        }

        // 10-30 updates / sec depending on setting
        private int Interval => 1000 / (10 + 5 * (int)Properties.AmbiLightUpdatesPerSecond);
        private int Scale => (int)Math.Pow(2, 4 - (int)Properties.AmbilightQuality);

        public AmbilightLayerHandler()
        {
            _ID = "Ambilight";

            if (captureTimer == null)
            {
                this.Initialize();
            }
            retryTimer = new System.Timers.Timer(500);
            retryTimer.Elapsed += RetryTimer_Elapsed;
        }

        public void Initialize()
        {
            if (desktopDuplicator != null)
            {
                desktopDuplicator.Dispose();
                desktopDuplicator = null;
            }
            if (captureTimer != null)
            {
                captureTimer.Stop();
                captureTimer.Interval = Interval;
            }
            var outputs = new Factory1().Adapters1
                .SelectMany(M => M.Outputs
                    .Select(N => new
                    {
                        Adapter = M,
                        Output = N.QueryInterface<Output1>()
                    }));
            var outputId = Properties.AmbilightOutputId;
            if (Properties.AmbilightOutputId > (outputs.Count() - 1))
            {
                outputId = 0;
            }
            var output = outputs.ElementAtOrDefault(outputId);
            bounds = output.Output.Description.DesktopBounds;
            var rect = new Rectangle(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            try
            {
                desktopDuplicator = new DesktopDuplicator(output.Adapter, output.Output, rect);
            }
            catch (SharpDXException e)
            {
                if (e.Descriptor == ResultCode.NotCurrentlyAvailable)
                {
                    throw new Exception("There is already the maximum number of applications using the Desktop Duplication API running, please close one of the applications and try again.", e);
                }
                if (e.Descriptor == ResultCode.Unsupported)
                {
                    throw new NotSupportedException("Desktop Duplication is not supported on this system.\nIf you have multiple graphic cards, try running on integrated graphics.", e);
                }
                Global.logger.Debug(e, String.Format("Caught exception when trying to setup desktop duplication. Retrying in {0} ms", AmbilightLayerHandler.retryTimer.Interval));
                captureTimer?.Stop();
                retryTimer.Start();
                return;
            }
            if (captureTimer == null)
            {
                captureTimer = new System.Timers.Timer(Interval);
                captureTimer.Elapsed += CaptureTimer_Elapsed;
            }
            captureTimer.Start();
        }

        protected override System.Windows.Controls.UserControl CreateControl()
        {
            return new Control_AmbilightLayer(this);
        }

        private void RetryTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            retryTimer.Stop();
            this.Initialize();
        }

        private void CaptureTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            // Reset the interval here, because it might have been changed in the config
            captureTimer.Interval = Interval;
            if (processing)
            {
                // Still busy processing the previous tick, do nothing
                return;
            }
            processing = true;
            if (desktopDuplicator == null)
            {
                processing = false;
                return;
            }
            Bitmap bigScreen;
            try
            {
                bigScreen = desktopDuplicator.Capture(5000);
            }
            catch (SharpDXException err)
            {
                Global.logger.Error("Failed to capture screen, reinitializing. Error was: " + err.Message);
                processing = false;
                this.Initialize();
                return;
            }
            if (bigScreen == null)
            {
                // Timeout, ignore
                processing = false;
                return;
            }

            Bitmap smallScreen = new Bitmap(bigScreen.Width / Scale, bigScreen.Height / Scale);

            using (var graphics = Graphics.FromImage(smallScreen))
                graphics.DrawImage(bigScreen, 0, 0, bigScreen.Width / Scale, bigScreen.Height / Scale);

            bigScreen?.Dispose();

            screen = smallScreen;

            if (Utils.Time.GetMillisecondsSinceEpoch() - last_use_time > 2000)
                // Stop if layer wasn't active for 2 seconds
                captureTimer.Stop();
            processing = false;
        }

        public override EffectLayer Render(IGameState gamestate)
        {
            last_use_time = Utils.Time.GetMillisecondsSinceEpoch();

            if (!captureTimer.Enabled) // Static timer isn't running, start it!
                captureTimer.Start();

            // Handle different capture types
            Image newImage = new Bitmap(Effects.canvas_width, Effects.canvas_height);
            User32.Rect app_rect = new User32.Rect();
            IntPtr foregroundapp;
            switch (Properties.AmbilightCaptureType)
            {
                case AmbilightCaptureType.EntireMonitor:
                    if (screen != null)
                    {
                        using (var graphics = Graphics.FromImage(newImage))
                            graphics.DrawImage(screen, 0, 0, Effects.canvas_width, Effects.canvas_height);
                    }
                    break;
                case AmbilightCaptureType.ForegroundApp:
                    if (screen != null)
                    {
                        foregroundapp = User32.GetForegroundWindow();
                        User32.GetWindowRect(foregroundapp, ref app_rect);
                        Screen display = Screen.FromHandle(foregroundapp);

                        if (SwitchDisplay(display.Bounds))
                            break;

                        Rectangle scr_region = Resize(new Rectangle(
                                app_rect.left - display.Bounds.Left,
                                app_rect.top - display.Bounds.Top,
                                app_rect.right - app_rect.left,
                                app_rect.bottom - app_rect.top));

                        using (var graphics = Graphics.FromImage(newImage))
                            graphics.DrawImage(screen, new Rectangle(0, 0, Effects.canvas_width, Effects.canvas_height), scr_region, GraphicsUnit.Pixel);
                    }
                    break;
                case AmbilightCaptureType.SpecificProcess:
                    if (!String.IsNullOrWhiteSpace(Properties.SpecificProcess))
                    {
                        var processes = Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(Properties.SpecificProcess));
                        foreach (Process p in processes)
                        {
                            if (p.MainWindowHandle != IntPtr.Zero)
                            {
                                if (screen != null)
                                {
                                    User32.GetWindowRect(p.MainWindowHandle, ref app_rect);
                                    Screen display = Screen.FromHandle(p.MainWindowHandle);
                                    if (SwitchDisplay(display.Bounds))
                                        break;

                                    Rectangle scr_region = Resize(new Rectangle(
                                            app_rect.left - display.Bounds.Left,
                                            app_rect.top - display.Bounds.Top,
                                            app_rect.right - app_rect.left,
                                            app_rect.bottom - app_rect.top));

                                    using (var graphics = Graphics.FromImage(newImage))
                                        graphics.DrawImage(screen, new Rectangle(0, 0, Effects.canvas_width, Effects.canvas_height), scr_region, GraphicsUnit.Pixel);
                                }

                                break;
                            }
                        }
                    }
                    break;
                case AmbilightCaptureType.Coordinates:
                    if (screen != null)
                    {
                        if (SwitchDisplay(Screen.FromRectangle(Properties.Coordinates).Bounds))
                            break;

                        Rectangle scr_region = Resize(new Rectangle(
                                Properties.Coordinates.X - bounds.Left,
                                Properties.Coordinates.Y - bounds.Top,
                                Properties.Coordinates.Width,
                                Properties.Coordinates.Height));

                        using (var graphics = Graphics.FromImage(newImage))
                            graphics.DrawImage(screen, new Rectangle(0, 0, Effects.canvas_width, Effects.canvas_height), scr_region, GraphicsUnit.Pixel);
                    }
                    break;
            }
            EffectLayer ambilight_layer = new EffectLayer();

            if (Properties.SaturateImage)
                newImage = Utils.BitmapUtils.AdjustImageSaturation(newImage, Properties.SaturationChange);
            if (Properties.BrightenImage)
                newImage = Utils.BitmapUtils.AdjustImageBrightness(newImage, Properties.BrightnessChange);

            if (Properties.AmbilightType == AmbilightType.Default)
            {
                using (Graphics g = ambilight_layer.GetGraphics())
                {
                    if (newImage != null)
                        g.DrawImageUnscaled(newImage, 0, 0);
                }
            }
            else if (Properties.AmbilightType == AmbilightType.AverageColor)
            {
                ambilight_layer.Fill(Utils.BitmapUtils.GetAverageColor(newImage));
            }

            newImage.Dispose();
            return ambilight_layer;
        }

        /// <summary>
        /// Changes the active display being captured if the desired region isn't contained in the current one, returning true if this happens.
        /// </summary>
        /// <param name="rectangle"></param>
        /// <returns></returns>
        private bool SwitchDisplay(Rectangle rectangle)
        {
            if (!RectangleEquals(rectangle, bounds))
            {
                OutputId = Array.FindIndex(Screen.AllScreens, d => RectangleEquals(d.Bounds, bounds));
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if a given Rectangle and RawRectangle have the same position and size
        /// </summary>
        /// <param name="rec"></param>
        /// <param name="raw"></param>
        /// <returns></returns>
        private static bool RectangleEquals(Rectangle rec, RawRectangle raw)
        {
            return ((rec.Left == raw.Left) && (rec.Top == raw.Top) && (rec.Right == raw.Right) && (rec.Bottom == raw.Bottom));
        }

        /// <summary>
        /// Resizes a given screen region for the position to coincide with the (also resized) screenshot
        /// </summary>
        /// <param name="r"></param>
        /// <returns></returns>
        private Rectangle Resize(Rectangle r)
        {
            return new Rectangle(r.X / Scale, r.Y / Scale, r.Width / Scale, r.Height / Scale);
        }

        protected void InvokePropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private class User32
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct Rect
            {
                public int left;
                public int top;
                public int right;
                public int bottom;
            }

            [DllImport("user32.dll")]
            public static extern IntPtr GetWindowRect(IntPtr hWnd, ref Rect rect);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();
        }
    }
}
