namespace InterpraticVPK
{
    public class VpkEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public uint CRC { get; set; }
        public ushort PreloadBytes { get; set; }
        public ushort ArchiveIndex { get; set; }
        public uint EntryOffset { get; set; }
        public uint EntryLength { get; set; }
    }
}