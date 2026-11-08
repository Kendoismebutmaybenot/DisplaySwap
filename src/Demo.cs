// Screenshot mode for the README: set DISPLAYSWAP_DEMO=1 and DisplaySwap shows made-up screens, sound
// devices and colour profiles instead of the real ones, and switching does nothing. Used so screenshots
// don't show anyone's actual hardware. Run it from a separate folder with a demo profiles.json.
using System;
using System.Collections.Generic;

namespace DisplaySwap
{
    public static class Demo
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("DISPLAYSWAP_DEMO") == "1";
        /// <summary>DISPLAYSWAP_DEMO_SELECT=&lt;profile name&gt;: start with that profile selected (for screenshots).</summary>
        public static readonly string Select = On ? Environment.GetEnvironmentVariable("DISPLAYSWAP_DEMO_SELECT") : null;

        public const string DeskPath = @"\\?\DISPLAY#DEM0001#1&demo&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        public const string SidePath = @"\\?\DISPLAY#DEM0002#1&demo&0&UID2#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        public const string TvPath   = @"\\?\DISPLAY#DEM0003#1&demo&0&UID3#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        /// <summary>Pretend the desk setup (desk monitor + side monitor) is on and the TV is connected but off.</summary>
        public static List<LiveDisplay> Displays()
        {
            return new List<LiveDisplay> {
                new LiveDisplay { Name = "Desk Monitor", DevicePath = DeskPath, Active = true, Available = true, Width = 2560, Height = 1440, X = 0, Y = 0, Hz = 240, Rate = 240,
                                  HdrSupported = true, HdrOn = true, HdrProfile = "Desk Monitor HDR.icc", TargetId = 1 },
                new LiveDisplay { Name = "Side Monitor", DevicePath = SidePath, Active = true, Available = true, Width = 1920, Height = 1080, X = 2560, Y = 180, Hz = 144, Rate = 144,
                                  HdrSupported = false, TargetId = 2 },
                new LiveDisplay { Name = "Living Room TV", DevicePath = TvPath, Active = false, Available = true, HdrSupported = true,
                                  HdrProfile = "Living Room TV HDR.icc", TargetId = 3 },
            };
        }

        public static List<AudioDevice> Audio()
        {
            return new List<AudioDevice> {
                new AudioDevice { Id = "{demo}.{1}", Name = "Headphones (USB Audio)", Active = true, Present = true, IsDefault = true },
                new AudioDevice { Id = "{demo}.{2}", Name = "Speakers (Realtek(R) Audio)", Active = true, Present = true },
                new AudioDevice { Id = "{demo}.{3}", Name = "Desk Monitor (NVIDIA High Definition Audio)", Active = true, Present = true },
                new AudioDevice { Id = "{demo}.{4}", Name = "Living Room TV (NVIDIA High Definition Audio)", Active = false, Present = false },
            };
        }

        public static List<int> Rates(int width)
        {
            return width >= 3840 ? new List<int> { 24, 50, 60, 100, 120 }
                 : width >= 2560 ? new List<int> { 60, 120, 144, 165, 240 }
                 : new List<int> { 60, 75, 100, 120, 144 };
        }

        public static List<string> ColourProfiles()
        {
            return new List<string> { "Desk Monitor HDR.icc", "Living Room TV HDR.icc", "sRGB Color Space Profile.icm" };
        }
    }
}
