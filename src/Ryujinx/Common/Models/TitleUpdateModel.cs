namespace Ryujinx.Ava.Common.Models
{
    public record TitleUpdateModel(ulong TitleId, ulong Version, string DisplayVersion, string Path)
        : Ryujinx.HLE.FileSystem.TitleUpdateModel(TitleId, Version, DisplayVersion, Path);
}
