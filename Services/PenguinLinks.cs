using System;
using System.Diagnostics;

namespace PenguinExtention.Services
{
    /// <summary>External links shown in the UI (Tools → Penguin → Support Penguin, Shortcuts window, Self-Test).</summary>
    internal static class PenguinLinks
    {
        /// <summary>Where "Support Penguin (Donate)" goes. Change this one line to switch donation platforms.</summary>
        public const string DonateUrl = "https://github.com/sponsors/jahidjay";

        public const string DonateMessage = "Penguin is free. If it saves you time, please consider a donation: " + DonateUrl;

        public static void OpenDonate()
        {
            try { Process.Start(new ProcessStartInfo(DonateUrl) { UseShellExecute = true }); }
            catch (Exception ex) { PenguinLog.Error("Donate", "Could not open " + DonateUrl + ": " + ex.Message); }
        }
    }
}
