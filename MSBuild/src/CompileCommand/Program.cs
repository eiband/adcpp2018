using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace cl
{
  public struct CompileCommand
  {
    public readonly string Directory;
    public readonly string Command;
    public readonly string File;

    public CompileCommand(string directory, string command, string file)
    {
      this.Directory = directory;
      this.Command = command;
      this.File = file;
    }

    public List<string> ToJson()
    {
      var lines = new List<string>();

      lines.Add("{");
      lines.Add("\"directory\" : \"" + Directory + "\",");
      lines.Add("\"command\" : \"" + Command + "\",");
      lines.Add("\"file\" : \"" + File + "\"");
      lines.Add("}");

      return lines;
    }
  };

  public static class JsonExtensions
  {
    public static List<string> ToJson(this List<CompileCommand> commands)
    {
      var lines = new List<string>();

      bool firstItem = true;

      lines.Add("[");

      foreach (CompileCommand command in commands)
      {
        if (firstItem)
        {
          firstItem = false;
        }
        else
        {
          lines.Add(",");
        }

        lines.AddRange(command.ToJson());
      }

      lines.Add("]");

      return lines;
    }
  }

  class Program
  {
    private static string ObjectCommandLineSwitch
    {
      get
      {
        return "/Fo";
      }
    }

    private static string PdbCommandLineSwitch
    {
      get
      {
        return "/Fd";
      }
    }

    private bool UseFullPath
    {
      get
      {
        return false;
      }
    }

    static int Main(string[] args)
    {
      var p = new Program();

      p.Init(args);
      p.Run();

      return 0;
    }

    private bool _verbose = false;
    private List<string> _arguments = null;

    private Program()
    {
    }

    private void Init(string[] args)
    {
      _arguments = GetClangArgs(args);
    }

    private void Run()
    {
      if (_verbose)
      {
        Console.WriteLine("Command line arguments: " + String.Join(", ", GetClangArgs()));
      }

      var commands = ParseCommands().OrderBy(command => command.File).ToList();

      // For the msbuild tracker we need to read the input files. Otherwise the targets will be rebuild on every build.
      ReadInputFiles(commands);
      WriteOutputFiles(commands);
    }

    private static List<string> SplitArgs(string line)
    {
      var result = new List<string>();

      bool outsideQuote = true;
      StringBuilder builder = new StringBuilder();

      foreach (char c in line)
      {
        if ((c == ' ') && outsideQuote)
        {
          if (builder.Length > 0)
          {
            result.Add(builder.ToString());
            builder.Clear();
          }
        }
        else
        {
          if (c == '"')
            outsideQuote = !outsideQuote;

          builder.Append(c);
        }
      }

      if (builder.Length > 0)
        result.Add(builder.ToString());

      return result;
    }

    private static List<string> ParseArgs(string[] switches)
    {
      var result = new List<string>();

      foreach (string sw in switches)
      {
        if (!String.IsNullOrEmpty(sw))
        {
          switch (sw.ToCharArray()[0])
          {
            case '#':
              // Ignore comments
              break;

            case '@':
              var args = File.ReadAllLines(sw.Substring(1), Encoding.Unicode).SelectMany(line => SplitArgs(line));
              result.AddRange(ParseArgs(args.ToArray()));
              break;

            default:
              result.Add(sw);
              break;
          }
        }
      }

      return result;
    }

    private static List<string> FilterArgs(List<string> args)
    {
      var result = new List<string>();

      foreach (string arg in args)
      {
        if (arg.StartsWith("/I"))
        {
          if (arg.Contains("\\Windows Kits\\") || arg.Contains("\\VC\\"))
          {
            // This is a system include, so use clang option "/imsvc" to suppress all warnings in system headers
            result.Add("/imsvc" + arg.Substring(2));
          }
          else
          {
            result.Add(arg);
          }
        }
        else
        {
          result.Add(arg);
        }
      }

      return result;
    }

    private static List<string> GetAdditionalClangArgs(string msversion, string[] systemPrefixes)
    {
      var args = new List<string> { "--driver-mode=cl", "-fms-compatibility-version=" + msversion,
                                    "-fdiagnostics-absolute-paths", "-Qunused-arguments", "-m64",
                                    "-Wno-nonportable-include-path" };

      if (systemPrefixes != null)
      {
        args.AddRange(systemPrefixes.SelectMany(prefix => new string[] { "-Xclang", "--system-header-prefix=\"" + prefix + "\"" }));
      }

      return args;
    }

    private static List<string> GetClangArgs(string[] args)
    {
      List<string> parsed = FilterArgs(ParseArgs(args));

      string msversion = "0";
      string[] systemPrefixes = null;
      string outputPath = Path.GetDirectoryName(GetClangArg(parsed, ObjectCommandLineSwitch));

      if (!String.IsNullOrEmpty(outputPath))
      {
        string versionPath = Path.Combine(outputPath, "_version.txt");
        string systemPath = Path.Combine(outputPath, "_system.txt");

        if (File.Exists(versionPath))
        {
          var lines = File.ReadAllLines(versionPath, Encoding.Unicode);

          if (lines.Length > 0)
          {
            int mscver = Convert.ToInt32(lines[0]);
            msversion = (mscver / 100) + "." + String.Format("{0:00}", mscver % 100);
          }
        }

        if (File.Exists(systemPath))
        {
          systemPrefixes = File.ReadAllLines(systemPath, Encoding.Unicode);
        }
      }

      var arguments = GetAdditionalClangArgs(msversion, systemPrefixes);

      arguments.AddRange(parsed);

      return arguments;
    }

    private List<string> GetClangArgs()
    {
      return _arguments;
    }

    private string GetClangArg(string prefix)
    {
      return GetClangArg(GetClangArgs(), prefix);
    }

    private static string GetClangArg(List<string> args, string prefix)
    {
      var matched = args.Where(argument => argument.StartsWith(prefix))
        .Select(argument => argument.Remove(0, prefix.Length).Trim('"', ' ', '\t')).ToArray();

      if (matched.Length == 0)
      {
        return null;
      }
      else if (matched.Length == 1)
      {
        return matched[0].Replace('/', '\\');
      }
      else
      {
        throw new ExternalException("Cannot handle multiple " + prefix + " switches.");
      }
    }

    private List<CompileCommand> ParseCommands()
    {
      var arguments = new List<string>(GetClangArgs());
      var files = new List<string>();

      while ((arguments.Count > 0) && File.Exists(arguments.Last().Trim('"', ' ', '\t')))
      {
        var file = arguments.Last().Trim('"', ' ', '\t');
        if (UseFullPath)
        {
          file = Path.GetFullPath(file);
        }

        files.Add(file.Replace("\\", "/"));
        arguments.RemoveAt(arguments.Count - 1);
      }

      var command = "\\\"" + GetClangExe().Replace("\\", "/") + "\\\" " + String.Join(" ", arguments.ToArray())
        .Replace("\\\\", "/").Replace("\\", "/").Replace("\"", "\\\"");

      string directory = Directory.GetCurrentDirectory().Replace("\\", "/");

      return files.Select(file => new CompileCommand(directory, command + " \\\"" + file + "\\\"", file)).ToList();
    }

    private void ReadInputFiles(List<CompileCommand> commands)
    {
      foreach (CompileCommand command in commands)
      {
        ReadInputFile(command.File);
      }
    }

    private void ReadInputFile(string file)
    {
      if (!String.IsNullOrEmpty(file))
      {
        if (_verbose)
        {
          Console.WriteLine("Reading file: " + file);
        }

        var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Close();
        stream.Dispose();
      }
    }

    private string CreateOutputDirectory(string prefix)
    {
      var result = GetClangArg(prefix);

      if (!String.IsNullOrEmpty(result))
      {
        var path = Path.GetDirectoryName(result);

        if (!String.IsNullOrEmpty(path))
        {
          if (_verbose)
          {
            Console.WriteLine("Creating directory: " + path);
          }

          Directory.CreateDirectory(path);
        }
      }

      return result;
    }

    private void WriteOutputFiles(List<CompileCommand> commands)
    {
      // Create output files (dummy object files and build command json files)
      string objExtension = ".obj";
      string jsonExtension = ".json";

      var path = CreateOutputDirectory(ObjectCommandLineSwitch);
      bool isPath = String.IsNullOrEmpty(path) || (path.Last() == '\\');

      if (commands.Count > 1)
      {
        if (!isPath)
        {
          throw new ExternalException("Single output file given for multiple input files.");
        }

        var pdbFile = CreateOutputDirectory(PdbCommandLineSwitch);

        foreach (CompileCommand command in commands)
        {
          var jsonFile = GetOutputFile(path, command, jsonExtension);
          var objectFile = GetOutputFile(path, command, objExtension);

          WriteOutputFile(jsonFile, objectFile, pdbFile, command);
        }
      }
      else if (commands.Count == 1)
      {
        var command = commands.First();
        var jsonFile = isPath ? GetOutputFile(path, command, jsonExtension) : Path.ChangeExtension(path, jsonExtension);
        var objectFile = isPath ? GetOutputFile(path, command, objExtension) : path;
        var pdbFile = CreateOutputDirectory(PdbCommandLineSwitch);

        WriteOutputFile(jsonFile, objectFile, pdbFile, command);
      }
    }

    private void WriteOutputFile(string jsonFile, string objectFile, string pdbFile, CompileCommand command)
    {
      // We need to read the file for tracking
      ReadInputFile(command.File);
      WriteJsonFile(jsonFile, command);

      // Create dummy object and PDB files
      TouchOutputFile(objectFile);
      TouchOutputFile(pdbFile);
    }

    private void WriteJsonFile(string file, CompileCommand command)
    {
      if (!String.IsNullOrEmpty(file))
      {
        if (_verbose)
        {
          Console.WriteLine("Writing file: " + file);
        }

        File.WriteAllLines(file, command.ToJson());
      }
    }

    private void TouchOutputFile(string file)
    {
      if (!String.IsNullOrEmpty(file))
      {
        if (_verbose)
        {
          Console.WriteLine("Touching file: " + file);
        }

        var stream = File.Open(file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Write);
        stream.Close();
        stream.Dispose();

        File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
      }
    }

    private string GetOutputFile(string path, CompileCommand command, string extension)
    {
      var file = Path.ChangeExtension(Path.GetFileName(command.File), extension);
      return String.IsNullOrEmpty(path) ? file : Path.Combine(path, file);
    }

    private static string GetClangExecutable(string executable, bool warn)
    {
      const string key = "HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node\\LLVM\\LLVM";
      var llvmPath = Registry.GetValue(key, null, null);

      if (llvmPath != null)
      {
        return Path.Combine(llvmPath.ToString(), "bin\\" + executable);
      }

      if (warn)
      {
        Console.WriteLine(key + " not found: Please install clang.");
      }

      return executable;
    }

    private static string GetClangExe()
    {
      return GetClangExecutable("clang++.exe", true);
    }
  }
}
