namespace Ryujinx.Appliance.Client
{
    public static class ErrorCodes
    {
        public const string KeysMissing = "KEYS_MISSING";
        public const string KeysInvalid = "KEYS_INVALID";
        public const string FirmwareMissing = "FIRMWARE_MISSING";
        public const string FirmwareInvalid = "FIRMWARE_INVALID";
        public const string UnsupportedContent = "UNSUPPORTED_CONTENT";
        public const string ContentInvalid = "CONTENT_INVALID";
        public const string TitleAlreadyInstalled = "TITLE_ALREADY_INSTALLED";
        public const string UpdateOlderThanInstalled = "UPDATE_OLDER_THAN_INSTALLED";
        public const string GameRunning = "GAME_RUNNING";
        public const string GameNotRunning = "GAME_NOT_RUNNING";
        public const string GameAlreadyRunning = "GAME_ALREADY_RUNNING";
        public const string InsufficientSpace = "INSUFFICIENT_SPACE";
        public const string InstallFailed = "INSTALL_FAILED";
        public const string BackendNotReady = "BACKEND_NOT_READY";
        public const string InvalidPath = "INVALID_PATH";
        public const string PermissionDenied = "PERMISSION_DENIED";
        public const string TitleNotFound = "TITLE_NOT_FOUND";
        public const string InvalidMethod = "INVALID_METHOD";
        public const string InvalidParams = "INVALID_PARAMS";
        public const string KeysGateFailed = "KEYS_GATE_FAILED";
        public const string BaseTitleMissing = "BASE_TITLE_MISSING";
        public const string ContentTypeMismatch = "CONTENT_TYPE_MISMATCH";
        public const string WriteProtected = "WRITE_PROTECTED";
        public const string UnsupportedFs = "UNSUPPORTED_FS";
        public const string DeviceBusy = "DEVICE_BUSY";
        public const string UsbRemoved = "USB_REMOVED";
        public const string InternalError = "INTERNAL_ERROR";
    }
}
