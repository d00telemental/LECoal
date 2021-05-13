using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace LECoal
{
    public static class BinaryExtensions
    {
        public static string ReadCoalescedString(this BinaryReader reader)
        {
            var length = reader.ReadInt32();
            var bytes = reader.ReadBytes(length * -2);
            if (bytes.Length != 0)
            {
                var str = Encoding.Unicode.GetString(bytes, 0, bytes.Length - 2);
                return str;
            }
            
            return string.Empty;
        }

        public static void WriteCoalescedString(this BinaryWriter writer, string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                writer.Write((Int32)0);
                return;
            }

            var length = str.Length;
            var wideString = Encoding.Unicode.GetBytes(str + '\0');

            writer.Write((Int32)(length + 1) * -1);
            writer.Write(wideString);
        }
    }

    [DebuggerDisplay("CoalescedSection \"{Name}\"")]
    public class CoalescedSection
    {
        public string Name { get; private set; }
        public List<(string, string)> Pairs { get; private set; } = new();

        public CoalescedSection(BinaryReader reader)
        {
            Name = reader.ReadCoalescedString();

            var pairCount = reader.ReadInt32();
            //Debug.WriteLine($"Section {Name}, {pairCount} pairs");
            for (int i = 0; i < pairCount; i++)
            {
                var key = reader.ReadCoalescedString();
                var val = reader.ReadCoalescedString();

                List<string> splitVal = null;

                if (val.Contains("\r\n"))
                {
                    splitVal = val.Split("\r\n").ToList();
                }
                else if (val.Contains('\r') && !val.Contains('\n'))
                {
                    splitVal = val.Split('\r').ToList();
                }
                else if (!val.Contains('\r') && val.Contains('\n'))
                {
                    splitVal = val.Split('\n').ToList();
                }
                else if (val.Contains('\r') && val.Contains('\n'))
                {
                    throw new Exception("Value contains both CR and LF but not in a CRLF sequence!");
                }

                if (splitVal is null)
                {
                    Pairs.Add((key, val));
                }
                else
                {
                    foreach (var line in splitVal)
                    {
                        Pairs.Add(($"{key}||", line));
                    }
                }
            }
        }
    }

    [DebuggerDisplay("CoalescedFile \"{Name}\" with {Sections.Count} sections")]
    public class CoalescedFile
    {
        public string Name { get; private set; }
        public List<CoalescedSection> Sections { get; private set; } = new ();

        public CoalescedFile(BinaryReader reader)
        {
            Name = reader.ReadCoalescedString();

            var sectionCount = reader.ReadInt32();
            //Debug.WriteLine($"File {Name}, {sectionCount} sections");
            for (int i = 0; i < sectionCount; i++)
            {
                CoalescedSection section = new (reader);
                Sections.Add(section);
            }
        }

        public static string EscapeName(string name) => name.Replace("\\", "_").Replace("..", "-");
    }

    [DebuggerDisplay("CoalescedBundle \"{Name}\" with {Files.Count} files")]
    public class CoalescedBundle
    {
        public string Name { get; private set; }
        public List<CoalescedFile> Files { get; private set; } = new();

        public CoalescedBundle(string name)
        {
            Name = name;
        }

        public static CoalescedBundle ReadFromFile(string name, string path)
        {
            BinaryReader reader = new (new MemoryStream(File.ReadAllBytes(path)));
            CoalescedBundle bundle = new (name);

            var fileCount = reader.ReadInt32();
            //Debug.WriteLine($"Bundle {bundle.Name}, {fileCount} files");

            for (int i = 0; i < fileCount; i++)
            {
                CoalescedFile file = new (reader);
                bundle.Files.Add(file);
            }

            return bundle;
        }

        public static CoalescedBundle ReadFromDirectory(string name, string path)
        {
            var manifestPath = Path.Combine(path, Path.ChangeExtension(name, "extracted"));
            if (!File.Exists(manifestPath))
            {
                throw new Exception("Didn't find a manifest in path");
            }

            // Read the manifest
            string destinationName;
            List <(string,string)> relativePaths = new ();
            {
                using var manifestReader = new StreamReader(manifestPath);
                destinationName = manifestReader.ReadLine();

                var countLine = manifestReader.ReadLine().Trim("\r\n ".ToCharArray());
                for (int i = 0; i < int.Parse(countLine); i++)
                {
                    var lineChunks = manifestReader.ReadLine().Split(";;", 2, StringSplitOptions.None);
                    if (lineChunks.Length != 2)
                    {
                        throw new Exception("Expected a manifest line to have 2 chunks");
                    }
                    relativePaths.Add((lineChunks[0], lineChunks[1]));
                }
            }

            // Construct a bundle
            CoalescedBundle bundle = new(destinationName);

            foreach (var relativePath in relativePaths)
            {
                
            }

            return bundle;
        }

        public void UnpackToDirectory(string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);

            foreach (var file in Files)
            {
                var outPath = Path.Combine(destinationPath, CoalescedFile.EscapeName(file.Name));
                using var writerStream = new StreamWriter(outPath);
                foreach (var section in file.Sections)
                {
                    writerStream.WriteLine($"[{section.Name}]");
                    foreach (var pair in section.Pairs)
                    {
                        writerStream.WriteLine($"{pair.Item1}={pair.Item2}");
                    }
                }
            }

            // Write out a manifest to rebuild from.
            var manifestPath = Path.Combine(destinationPath, Path.ChangeExtension(Name, "extracted"));
            using var manifestWriter = new StreamWriter(manifestPath);
            manifestWriter.WriteLine($"{Name}");
            manifestWriter.WriteLine($"{Files.Count}");
            foreach (var file in Files)
            {
                manifestWriter.WriteLine($"{CoalescedFile.EscapeName(file.Name)};;{file.Name}");
            }
        }

        public void PackToFile(string destinationPath)
        {
            BinaryWriter writer = new(new MemoryStream());

            writer.Write((Int32)Files.Count);
            foreach (var file in Files)
            {
                writer.WriteCoalescedString(file.Name);
                writer.Write((Int32)file.Sections.Count);

                foreach (var section in file.Sections)
                {
                    writer.WriteCoalescedString(section.Name);
                    writer.Write((Int32)section.Pairs.Count);

                    foreach (var pair in section.Pairs)
                    {
                        writer.WriteCoalescedString(pair.Item1);
                        writer.WriteCoalescedString(pair.Item2);
                    }
                }
            }

            File.WriteAllBytes(destinationPath, (writer.BaseStream as MemoryStream).ToArray());
        }
    }

    class Program
    {
        private static string _pathPrefix = @"D:\Temp\_0MELE\Coal\";
        private static string _inputBundleName = "ME2_Coalesced_INT.bin";

        static void Main(string[] args)
        {
            var inputPath = Path.Combine(_pathPrefix, _inputBundleName);
            var extractedDir = Path.ChangeExtension(Path.Combine(_pathPrefix, _inputBundleName), "").TrimEnd('.');
            var rebuiltPath = inputPath + ".rebuilt";

            var bundle = CoalescedBundle.ReadFromFile(_inputBundleName, inputPath);
            bundle.UnpackToDirectory(extractedDir);

            var rebuiltBundle = CoalescedBundle.ReadFromDirectory(_inputBundleName, extractedDir);
            //bundle.PackToFile(rebuiltPath);
        }
    }
}
