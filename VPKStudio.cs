using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InterpraticVPK
{
    /// <summary>
    /// InterpraticVPK - Valve VPK Packing, Extraction and Management Library (.NET 10.0)
    /// </summary>
    public static class VpkStudio
    {
        /// <summary>
        /// Reads the VPK directory and returns the list of entries.
        /// </summary>
        public static List<VpkEntry> ReadVpkDirectory(string vpkFilePath, HashSet<string>? filterExtensions = null, string searchPattern = "")
        {
            List<VpkEntry> entries = new List<VpkEntry>();

            using FileStream fs = new FileStream(vpkFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader br = new BinaryReader(fs, Encoding.ASCII);

            if (br.ReadUInt32() != 0x55AA1234)
                throw new InvalidDataException("Invalid VPK Header Magic!");

            uint version = br.ReadUInt32();
            uint treeSize = br.ReadUInt32();

            if (version == 2) { br.ReadBytes(16); }

            Regex? searchRegex = string.IsNullOrEmpty(searchPattern) ? null : new Regex(searchPattern, RegexOptions.IgnoreCase);

            while (true)
            {
                string extension = ReadNullTerminatedString(br);
                if (string.IsNullOrEmpty(extension)) break;

                while (true)
                {
                    string path = ReadNullTerminatedString(br);
                    if (string.IsNullOrEmpty(path)) break;

                    while (true)
                    {
                        string filename = ReadNullTerminatedString(br);
                        if (string.IsNullOrEmpty(filename)) break;

                        VpkEntry entry = new VpkEntry
                        {
                            Extension = extension,
                            Path = path,
                            FileName = filename,
                            CRC = br.ReadUInt32(),
                            PreloadBytes = br.ReadUInt16(),
                            ArchiveIndex = br.ReadUInt16(),
                            EntryOffset = br.ReadUInt32(),
                            EntryLength = br.ReadUInt32()
                        };

                        br.ReadUInt16();

                        entry.RelativePath = string.IsNullOrEmpty(path) || path == " "
                            ? $"{filename}.{extension}"
                            : $"{path}/{filename}.{extension}";

                        if (filterExtensions != null && !filterExtensions.Contains(extension) && !filterExtensions.Contains(extension + "_c"))
                            continue;

                        if (searchRegex != null && !searchRegex.IsMatch(entry.RelativePath))
                            continue;

                        entries.Add(entry);
                    }
                }
            }

            return entries;
        }

        /// <summary>
        /// Extracts VPK entries concurrently to the specified output directory.
        /// </summary>
        public static void ExtractParallel(string vpkPath, List<VpkEntry> entries, string outputDirectory, Action<int, int>? progressCallback = null)
        {
            int completedFiles = 0;
            int totalFiles = entries.Count;

            Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, entry =>
            {
                byte[] fileData = ExtractFileData(vpkPath, entry);
                string destinationPath = Path.Combine(outputDirectory, entry.RelativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.WriteAllBytes(destinationPath, fileData);

                int currentCount = Interlocked.Increment(ref completedFiles);
                progressCallback?.Invoke(currentCount, totalFiles);
            });
        }

        /// <summary>
        /// Packs a folder into a Valve VPK archive format.
        /// </summary>
        public static void PackFolder(string inputFolderPath, string outputVpkPath)
        {
            string[] files = Directory.GetFiles(inputFolderPath, "*.*", SearchOption.AllDirectories);

            using FileStream fs = new FileStream(outputVpkPath, FileMode.Create);
            using BinaryWriter bw = new BinaryWriter(fs, Encoding.ASCII);

            bw.Write(0x55AA1234); // Magic
            bw.Write(1);          // Version 1
            bw.Write(0);          // TreeSize

            foreach (var file in files)
            {
                string relPath = Path.GetRelativePath(inputFolderPath, file).Replace("\\", "/");
                string dir = Path.GetDirectoryName(relPath)?.Replace("\\", "/") ?? " ";
                string fileName = Path.GetFileNameWithoutExtension(file);
                string ext = Path.GetExtension(file).TrimStart('.');

                byte[] fileData = File.ReadAllBytes(file);

                bw.Write(Encoding.ASCII.GetBytes(ext + "\0"));
                bw.Write(Encoding.ASCII.GetBytes((dir == "" ? " " : dir) + "\0"));
                bw.Write(Encoding.ASCII.GetBytes(fileName + "\0"));

                bw.Write(0u);
                bw.Write((ushort)0);
                bw.Write((ushort)0x7FFF);
                bw.Write((uint)fs.Position);
                bw.Write((uint)fileData.Length);
                bw.Write((ushort)0xFFFF);
            }

            bw.Write((byte)0);
        }

        /// <summary>
        /// Extracts raw file data from the VPK entry.
        /// </summary>
        public static byte[] ExtractFileData(string mainVpkPath, VpkEntry entry)
        {
            byte[] fileData = new byte[entry.PreloadBytes + entry.EntryLength];

            using FileStream fs = new FileStream(mainVpkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader br = new BinaryReader(fs, Encoding.ASCII);

            fs.Seek(12, SeekOrigin.Begin);
            uint version = br.ReadUInt32();
            if (version == 2) fs.Seek(16, SeekOrigin.Current);

            if (entry.EntryLength > 0)
            {
                if (entry.ArchiveIndex == 0x7FFF)
                {
                    fs.Seek(entry.EntryOffset, SeekOrigin.Begin);
                    fs.Read(fileData, entry.PreloadBytes, (int)entry.EntryLength);
                }
                else
                {
                    string archivePath = GetArchivePath(mainVpkPath, entry.ArchiveIndex);
                    if (File.Exists(archivePath))
                    {
                        using FileStream archiveFs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        archiveFs.Seek(entry.EntryOffset, SeekOrigin.Begin);
                        archiveFs.Read(fileData, entry.PreloadBytes, (int)entry.EntryLength);
                    }
                }
            }

            return fileData;
        }

        private static string ReadNullTerminatedString(BinaryReader reader)
        {
            StringBuilder sb = new StringBuilder();
            byte b;
            while ((b = reader.ReadByte()) != 0) sb.Append((char)b);
            return sb.ToString();
        }

        private static string GetArchivePath(string dirVpkPath, ushort index)
        {
            string folder = Path.GetDirectoryName(dirVpkPath) ?? "";
            string name = Path.GetFileNameWithoutExtension(dirVpkPath);
            if (name.EndsWith("_dir")) name = name[..^4];
            return Path.Combine(folder, $"{name}_{index:D3}.vpk");
        }
    }
}