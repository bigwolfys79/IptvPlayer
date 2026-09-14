using System;
using System.Collections.Generic;

namespace IptvPlayer.Services
{


    public static class VideoUpscaler
    {
        public const string Off = "Off";
        public const string Sharp = "Sharp";
        public const string Denoise = "Denoise";
        public const string SdUpscale = "SdUpscale";


        public static readonly IReadOnlyList<string> AllModes = new[]
        {
            Off, Sharp, Denoise, SdUpscale
        };


        public static string? GetFilters(string? mode) => mode switch
        {

            Sharp => "unsharp=5:5:1.5:5:5:0.3",

            Denoise => "hqdn3d=4:3:6:4.5,unsharp=5:5:1.0:5:5:0.3",


            SdUpscale => "hqdn3d=3:2:6:4,unsharp=5:5:1.8:5:5:0.4",
            _ => null
        };


        public static string Normalize(string? mode)
        {
            foreach (var m in AllModes)
            {
                if (string.Equals(m, mode, StringComparison.OrdinalIgnoreCase))
                {
                    return m;
                }
            }
            return Off;
        }
    }
}
