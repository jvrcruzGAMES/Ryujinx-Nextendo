using System;
using System.Collections.Generic;
using Ryujinx.Appliance.Client;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using Ryujinx.Input.SDL3;

namespace Ryujinx.Appliance.Services
{
    public class InputService : IDisposable
    {
        private InputManager _inputManager;
        private readonly object _lock = new();

        public InputService()
        {
            try
            {
                _inputManager = new InputManager(new SDL3KeyboardDriver(), new SDL3GamepadDriver());
            }
            catch { }
        }

        public InputDeviceDto[] ListDevices()
        {
            List<InputDeviceDto> devices = [];

            lock (_lock)
            {
                if (_inputManager == null)
                {
                    try
                    {
                        _inputManager = new InputManager(new SDL3KeyboardDriver(), new SDL3GamepadDriver());
                    }
                    catch { }
                }

                if (_inputManager != null)
                {
                    try
                    {
                        foreach (string id in _inputManager.GamepadDriver.GamepadsIds)
                        {
                            using IGamepad gp = _inputManager.GamepadDriver.GetGamepad(id);
                            if (gp != null)
                            {
                                devices.Add(new InputDeviceDto
                                {
                                    Id = id,
                                    Name = gp.Name,
                                    Type = "Gamepad",
                                    Connected = gp.IsConnected
                                });
                            }
                        }
                    }
                    catch { }
                }
            }

            return devices.ToArray();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _inputManager?.Dispose();
                _inputManager = null;
            }
        }
    }
}
