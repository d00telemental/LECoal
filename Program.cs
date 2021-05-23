using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace LECoal
{
    public static class ConsoleExtensions
    {
        public static void WriteLine(this ConsoleColor color, params string[] text)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(string.Join(" ", text));
            Console.ResetColor();
        }
    }

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

    namespace Exceptions
    {
        public class CBundleException : Exception
        {
            public CBundleException(string message) : base(message) { }
        }

        public class CToolException : Exception
        {
            public CToolException(string message) : base(message) { }
        }
    }

    internal class CoalescedManifestInfo
    {
        public string DestinationFilename { get; } = null;
        public List<(string, string)> RelativePaths { get; } = new();

        public CoalescedManifestInfo(string manifestPath)
        {
            if (!File.Exists(manifestPath))
            {
                throw new Exceptions.CBundleException($"Failed to find a manifest at {manifestPath}");
            }

            using var manifestReader = new StreamReader(manifestPath);
            DestinationFilename = manifestReader.ReadLine();

            var countLine = manifestReader.ReadLine().Trim("\r\n ".ToCharArray());
            for (int i = 0; i < int.Parse(countLine); i++)
            {
                var lineChunks = manifestReader.ReadLine()?.Split(";;", 2, StringSplitOptions.RemoveEmptyEntries)
                    ?? throw new Exceptions.CBundleException("Expected to read a full line, got null");
                if (lineChunks.Length != 2)
                {
                    throw new Exceptions.CBundleException("Expected a manifest line to have 2 chunks");
                }
                RelativePaths.Add((lineChunks[0], lineChunks[1]));
            }
        }
    }

    [DebuggerDisplay("CoalescedSection \"{Name}\"")]
    public class CoalescedSection
    {
        public string Name { get; private set; }
        public List<(string, string)> Pairs { get; private set; } = new();

        public CoalescedSection(string name)
        {
            Name = name;
        }

        public CoalescedSection(BinaryReader reader)
        {
            Name = reader.ReadCoalescedString();

            var pairCount = reader.ReadInt32();
            //Debug.WriteLine($"Section {Name}, {pairCount} pairs");
            for (int i = 0; i < pairCount; i++)
            {
                var key = reader.ReadCoalescedString();
                var val = reader.ReadCoalescedString();

                Pairs.Add((key, val));
            }
        }
    }

    [DebuggerDisplay("CoalescedFile \"{Name}\" with {Sections.Count} sections")]
    public class CoalescedFile
    {
        public string Name { get; private set; }
        public List<CoalescedSection> Sections { get; private set; } = new ();

        public CoalescedFile(string name)
        {
            Name = name;
        }

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
            var manifestPath = Path.Combine(path, "mele.extractedbin");
            if (!File.Exists(manifestPath))
            {
                throw new Exception("Didn't find a manifest in path");
            }

            CoalescedManifestInfo manifest = new (manifestPath);
            CoalescedBundle bundle = new (manifest.DestinationFilename);
            CoalescedFile currentFile = null;

            foreach (var relativePath in manifest.RelativePaths)
            {
                var filePath = Path.Combine(path, relativePath.Item1);
                if (!File.Exists(filePath)) { throw new Exceptions.CBundleException("Failed to find a file according to manifest, either the file was removed or the manifest was changed"); }
                StreamReader reader = new (filePath);

                currentFile = new CoalescedFile(relativePath.Item2);

                CoalescedSection currentSection = null;
                string line = null;
                while ((line = reader.ReadLine()) is not null)
                {
                    // Empty line
                    if (string.IsNullOrWhiteSpace(line)) { continue; }

                    // Comment
                    if (line.StartsWith(";") || line.StartsWith("#")) { continue; }

                    // Section header
                    if (line.StartsWith('[') && line.EndsWith(']'))
                    {
                        var header = line.Substring(1, line.Length - 2);
                        if (header.Length < 1 || string.IsNullOrWhiteSpace(header)) { throw new Exceptions.CBundleException("Expected to have a header with text"); }

                        if (currentSection is not null)
                        {
                            currentFile.Sections.Add(currentSection);
                        }
                        currentSection = new CoalescedSection(header);

                        continue;
                    }

                    // Pair
                    var chunks = line.Split('=', 2);
                    if (chunks.Length != 2) { throw new Exceptions.CBundleException("Expected to have exactly two chunks after splitting the line by ="); }

                    if (chunks[0].EndsWith("||"))  // It's a multiline value UGH
                    {
                        var strippedKey = chunks[0].Substring(0, chunks[0].Length - 2);
                        
                        if (currentSection.Pairs.Count > 0 && currentSection.Pairs.Last().Item1 == strippedKey)  // It's a second or further line in multiline value
                        {
                            var last = currentSection.Pairs[currentSection.Pairs.Count() - 1];
                            currentSection.Pairs[currentSection.Pairs.Count() - 1]
                                = (last.Item1, last.Item2 + "\r\n" + chunks[1]);
                        }
                        else
                        {
                            currentSection.Pairs.Add((strippedKey, chunks[1]));
                        }
                    }
                    else
                    {
                        currentSection.Pairs.Add((chunks[0], chunks[1]));
                    }

                }

                if (currentSection is not null)
                {
                    currentFile.Sections.Add(currentSection);
                }
                bundle.Files.Add(currentFile);
            }

            return bundle;
        }

        public void WriteToDirectory(string destinationPath)
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
                        var lines = splitValue(pair.Item2);
                        if (lines is null || lines.Count() == 1)
                        {
                            writerStream.WriteLine($"{pair.Item1}={pair.Item2}");
                            continue;
                        }

                        foreach (var line in lines)
                        {
                            writerStream.WriteLine($"{pair.Item1}||={line}");
                        }
                    }
                }
            }

            // Write out a manifest to rebuild from.
            var manifestPath = Path.Combine(destinationPath, "mele.extractedbin");
            using var manifestWriter = new StreamWriter(manifestPath);
            manifestWriter.WriteLine($"{Name}");
            manifestWriter.WriteLine($"{Files.Count}");
            foreach (var file in Files)
            {
                manifestWriter.WriteLine($"{CoalescedFile.EscapeName(file.Name)};;{file.Name}");
            }
        }

        public void WriteToFile(string destinationPath)
        {
            BinaryWriter writer = new(new MemoryStream());

            writer.Write((Int32)Files.Count);
            foreach (var file in Files)
            {
                writer.WriteCoalescedString(file.Name);
                writer.Write((Int32)file.Sections.Count);

                foreach (var section in file.Sections)
                {
                    if (section is null)
                    {
                        continue;
                    }

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

        internal List<string> splitValue(string val)
        {
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

            return splitVal;
        }
    }

    public class CoalescedTool
    {
        public CoalescedTool(string[] args)
        {
            if (args.Count() != 3)
            {
                throw new Exceptions.CToolException($"Expected exactly 3 arguments, given {args.Count()}.");
            }

            switch (args[0].ToUpper())
            {
                case "UNPACK":
                    {
                        var fromPath = args[1];
                        var toPath = args[2];

                        if (string.IsNullOrEmpty(fromPath)) { throw new Exceptions.CToolException($"Expected the 'from' argument for 'unpack' not to be empty"); }
                        if (string.IsNullOrEmpty(toPath)) { throw new Exceptions.CToolException($"Expected the 'to' argument for 'unpack' not to be empty"); }

                        // Expand '.' into working dir in TO argument.
                        if (toPath.Trim() == ".")
                        {
                            toPath = Directory.GetCurrentDirectory();
                        }

                        // Make both pathes absolute.
                        if (!Path.IsPathRooted(fromPath)) { fromPath = Path.Combine(Directory.GetCurrentDirectory(), fromPath); }
                        if (!Path.IsPathRooted(toPath)) { toPath = Path.Combine(Directory.GetCurrentDirectory(), toPath); }

                        if (!File.Exists(fromPath) || File.GetAttributes(fromPath).HasFlag(FileAttributes.Directory))
                        {
                            throw new Exceptions.CToolException($"Expected the 'from' argument for 'unpack' to be an existing file");
                        }

                        Console.WriteLine($"Unpacking {fromPath}\n  into {toPath}...");
                        Unpack(fromPath, toPath);
                    }
                    break;
                case "PACK":
                    {
                        var fromPath = args[1];
                        var toPath = args[2];

                        if (string.IsNullOrEmpty(fromPath)) { throw new Exceptions.CToolException($"Expected the 'from' argument for 'pack' not to be empty"); }
                        if (string.IsNullOrEmpty(toPath)) { throw new Exceptions.CToolException($"Expected the 'to' argument for 'pack' not to be empty"); }

                        // Expand '.' into working dir in FROM argument.
                        if (fromPath.Trim() == ".")
                        {
                            fromPath = Directory.GetCurrentDirectory();
                        }

                        // Make both pathes absolute.
                        if (!Path.IsPathRooted(fromPath)) { fromPath = Path.Combine(Directory.GetCurrentDirectory(), fromPath); }
                        if (!Path.IsPathRooted(toPath)) { toPath = Path.Combine(Directory.GetCurrentDirectory(), toPath); }

                        if (!Directory.Exists(fromPath))
                        {
                            throw new Exceptions.CToolException($"Expected the 'from' argument for 'pack' to be an existing directory");
                        }

                        Console.WriteLine($"Packing {fromPath}\n  into {toPath}...");
                        Pack(fromPath, toPath);
                    }
                    break;
                default:
                    throw new Exceptions.CToolException($"Expected 'pack' or 'unpack' as the first argument, given {args[1]}");
            }
        }

        static void Unpack(string fromFile, string toDir)
        {
            var bundle = CoalescedBundle.ReadFromFile(Path.GetFileName(fromFile), fromFile);
            bundle.WriteToDirectory(toDir);
        }
        static void Pack(string fromDir, string toFile)
        {
            var manifest = new CoalescedManifestInfo(Path.Combine(fromDir, "mele.extractedbin"));
            var bundle = CoalescedBundle.ReadFromDirectory(manifest.DestinationFilename, fromDir);
            bundle.WriteToFile(toFile);
        }
    }

    class Program
    {
        public static bool IsDebug = false;

        static void Main(string[] args)
        {
#if DEBUG
            IsDebug = true;
#else
            IsDebug = false;
#endif

            try
            {
                string[] helpTokens = new[] { "?", "/?", "\\?", "HELP", "-HELP", "--HELP", "--RTFM" };
                if (args.Count() > 0 && helpTokens.Contains(args[0].ToUpper()))
                {
                    Console.WriteLine("This tool is capable of packing and unpacking Coalesced_*.bin files for LE1/2.");
                    Console.WriteLine();
                    Console.WriteLine("Usage patterns:");
                    Console.WriteLine("   .\\LECoal.exe unpack <coalesced file> <target directory>");
                    Console.WriteLine("         - Will unpack the file at <coalesced file> into a directory at <target directory>.");
                    Console.WriteLine("         - <coalesced file> must exist and be a file, <target directory> will be created if missing.");
                    Console.WriteLine("   .\\LECoal.exe pack <unpacked directory> <target file>");
                    Console.WriteLine("         - Will pack the directory at <unpacked directory> into a coalesced file at <target file>.");
                    Console.WriteLine("         - <unpacked directory> must exist, <target file> will be created or overwritten.");
                    Console.WriteLine();
                    Console.WriteLine("Examples:");
                    Console.WriteLine("   .\\LECoal.exe unpack Coalesced_INT.bin D:\\MELE\\ME1_Coalesced");
                    Console.WriteLine("         - Will unpack the 'Coalesced_INT.bin' file in the directory you are running the tool from");
                    Console.WriteLine("           into a bunch of files at D:\\MELE\\ME1_Coalesced.");
                    Console.WriteLine("         - Those files can then be edited using Notepad++ / Sublime Text / VS Code / etc.");
                    Console.WriteLine("         - Do not modify 'mele.extractedbin', as it will be needed for packing the files back.");
                    Console.WriteLine("   .\\LECoal.exe pack D:\\MELE\\ME1_Coalesced Coalesced_INT.bin");
                    Console.WriteLine("         - Will pack a bunch of files from D:\\MELE\\ME1_Coalesced into a Coalesced_INT.bin file");
                    Console.WriteLine("           which will be located in the directory you are running the tool from.");
                    Console.WriteLine("         - If the tool fails to find the auto-genereated 'mele.extractedbin' in unpacked directory,");
                    Console.WriteLine("           the packing will fail.");
                    Console.WriteLine();
                    return;
                }

                CoalescedTool tool = new(args);
            }
            catch (Exceptions.CBundleException ex) when (!IsDebug)
            {
                ConsoleColor.Red.WriteLine($"Error in bundle reading/writing logic:");
                ConsoleColor.Red.WriteLine($"{ex.Message}\n");
                ConsoleColor.Red.WriteLine(string.Join("\n", ex.StackTrace.Split("\n").Select(s => " " + s)));
            }
            catch (Exceptions.CToolException ex) when (!IsDebug)
            {
                ConsoleColor.Red.WriteLine($"Error in command interpretation:");
                ConsoleColor.Red.WriteLine($"{ex.Message}\n");
                ConsoleColor.Red.WriteLine(string.Join("\n", ex.StackTrace.Split("\n").Select(s => " " + s)) + "\n");
                ConsoleColor.Cyan.WriteLine("Run the tool with '?' or '--help' argument for usage reference.\n");
            }
            catch (Exception ex) when (!IsDebug)
            {
                ConsoleColor.Red.WriteLine($"Generic exception {ex.GetType().FullName}:\n{ex.Message}\n{ex.StackTrace}\n");
            }
        }
    }
}
