using System;
using System.IO;
using Ryujinx.Appliance.Client;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;

namespace Ryujinx.Appliance.Services
{
    public class SettingsService
    {
        private readonly object _lock = new();

        public SettingsDto GetSettings()
        {
            lock (_lock)
            {
                return new SettingsDto
                {
                    CpuBackend = ConfigurationState.Instance.System.UseHypervisor.Value ? "LinuxKvm" : "Jit",
                    GraphicsBackend = ConfigurationState.Instance.Graphics.GraphicsBackend.Value.ToString(),
                    VSync = ConfigurationState.Instance.Graphics.VSyncMode.Value != 0,
                    ResolutionScale = ConfigurationState.Instance.Graphics.ResScale.Value,
                    AudioVolume = ConfigurationState.Instance.System.AudioVolume.Value,
                    SystemLanguage = ConfigurationState.Instance.System.Language.Value.ToString(),
                    SystemRegion = ConfigurationState.Instance.System.Region.Value.ToString(),
                    SystemTimeZone = ConfigurationState.Instance.System.TimeZone.Value,
                    DockedMode = ConfigurationState.Instance.System.EnableDockedMode.Value
                };
            }
        }

        public SettingsDto UpdateSettings(SettingsDto newSettings)
        {
            if (newSettings == null)
                return GetSettings();

            lock (_lock)
            {
                if (newSettings.AudioVolume >= 0.0f && newSettings.AudioVolume <= 2.0f)
                {
                    ConfigurationState.Instance.System.AudioVolume.Value = newSettings.AudioVolume;
                }

                ConfigurationState.Instance.System.EnableDockedMode.Value = newSettings.DockedMode;

                if (!string.IsNullOrEmpty(newSettings.CpuBackend))
                {
                    ConfigurationState.Instance.System.UseHypervisor.Value = 
                        newSettings.CpuBackend.Equals("LinuxKvm", StringComparison.OrdinalIgnoreCase) || 
                        newSettings.CpuBackend.Equals("Auto", StringComparison.OrdinalIgnoreCase);
                }

                // Save configuration to disk
                string configPath = Path.Combine(AppDataManager.BaseDirPath, "Config.json");
                try
                {
                    ConfigurationState.Instance.ToFileFormat().SaveConfig(configPath);
                    Logger.Notice.Print(LogClass.Application, $"Configuration saved to {configPath}");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Failed to save config: {ex.Message}");
                }

                return GetSettings();
            }
        }
    }
}
