using MTT;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;
using System.Text.RegularExpressions;

namespace MSBuildTasks
{
    public class ConvertMain : MSBuildTask
    {
        /// <summary>
        /// The current working directory for the convert process
        /// </summary>
        public string WorkingDirectory { get; set; }

        /// <summary>
        /// The directory to save the ts models
        /// </summary>
        public string ConvertDirectory { get; set; }

        /// <summary>
        /// Comments at the top of each file that it was auto generated
        /// </summary>
        public bool AutoGeneratedTag { get; set; } = true; //default value if one is not provided;

        protected MessageImportance LoggingImportance { get; } = MessageImportance.High;  //If its not high then there are no logs

        private List<ModelFile> Models { get; set; }

        private string LocalWorkingDir { get; set; }

        private string LocalConvertDir { get; set; }

        public ConvertMain()
        {
            Models = new List<ModelFile>();
        }

        public override bool Execute()
        {
            Log.LogMessage(LoggingImportance, "Starting MTT");
            GetWorkingDirectory();
            GetConvertDirectory();
            LoadModels();

            try
            {
                BreakDown();
            }
            catch (Exception e)
            {
                throw e;
            }

            Convert();
            Log.LogMessage(LoggingImportance, "Finished MTT");
            return true;
        }

        private void GetWorkingDirectory()
        {
            var dir = Directory.GetCurrentDirectory();

            if (string.IsNullOrEmpty(WorkingDirectory))
            {
                Log.LogMessage(LoggingImportance, "Using Default Working Directory {0}", dir);
                LocalWorkingDir = dir;
                return;
            }

            var localdir = Path.Combine(dir, WorkingDirectory);

            if (!Directory.Exists(localdir))
            {
                Log.LogMessage("Working Directory does not exist {0}, creating..", localdir);
                Directory.CreateDirectory(localdir).Create();
                LocalConvertDir = localdir;
                return;
            }

            Log.LogMessage(LoggingImportance, "Working Directory {0}", localdir);
            LocalWorkingDir = localdir;
            return;
        }

        private void GetConvertDirectory()
        {
            var dir = Directory.GetCurrentDirectory();

            if (string.IsNullOrEmpty(ConvertDirectory))
            {
                Log.LogMessage(LoggingImportance, "Using Default Convert Directory {0} - this does not always update", dir);
                LocalConvertDir = dir;
                return;
            }

            var localdir = Path.Combine(dir, ConvertDirectory);

            if (!Directory.Exists(localdir))
            {
                Log.LogMessage("Convert Directory does not exist {0}, creating..", localdir);
            }
            else
            {
                Log.LogMessage(LoggingImportance, "Convert Directory {0}", localdir);
                Directory.Delete(localdir, true);
            }

            Directory.CreateDirectory(localdir).Create();
            LocalConvertDir = localdir;
            return;
        }

        private void LoadModels(string dirname = "")
        {
            if (String.IsNullOrEmpty(dirname))
            {
                dirname = LocalWorkingDir;
            }

            var files = Directory.GetFiles(dirname);
            var dirs = Directory.GetDirectories(dirname);

            foreach (var dir in dirs)
            {
                string d = dir.Replace(dirname, String.Empty);

                if (!String.IsNullOrEmpty(d))
                {
                    LoadModels(dir);
                }
            }

            foreach (var file in files)
            {
                AddModel(file, dirname.Replace(LocalWorkingDir, String.Empty));
            }
        }

        private void AddModel(string file, string structure = "")
        {
            structure = structure.Replace(@"\", "/");
            string[] explodedDir = file.Replace(@"\", "/").Split('/');

            string fileName = explodedDir[explodedDir.Length - 1];

            string[] fileInfo = File.ReadAllLines(file);

            var modelFile = new ModelFile()
            {
                Name = ToPascalCase(fileName.Replace(".cs", String.Empty)),
                Info = fileInfo,
                Structure = structure
            };

            Models.Add(modelFile);
        }

        private void BreakDown()
        {
            foreach (var file in Models)
            {
                foreach (var _line in file.Info)
                {
                    var line = StripComments(_line);

                    var modLine = new List<string>(ExplodeLine(line));

                    // Check for correct structure
                    if ((line.StrictContains("enum") && line.Contains("{")) || (line.StrictContains("class") && line.Contains("{")))
                    {                        
                        throw new ArgumentException(string.Format("For parsing, C# DTO's must use curly braces on the next line\nin {0}.cs\n\"{1}\"", file.Name, _line));
                    }

                    // Enum declaration
                    if (line.StrictContains("enum"))
                    {
                        if (modLine.Count > 2)
                        {
                            file.Inherits = modLine[modLine.Count - 1];
                        }

                        file.IsEnum = true;

                        int value = 0;

                        foreach (var enumLine in file.Info)
                        {
                            modLine = new List<string>(ExplodeLine(enumLine));

                            if (!enumLine.StrictContains("enum") && !IsContructor(enumLine) && !enumLine.Contains("{") && !enumLine.Contains("}") && !String.IsNullOrWhiteSpace(enumLine) && !enumLine.StrictContains("namespace"))
                            {
                                String name = modLine[0];
                                bool isImplicit = false;

                                if (modLine.Count > 1 && modLine[1] == "=")
                                {
                                    try
                                    {
                                        var tmpValue = modLine[2].Replace(",", "");
                                        if (tmpValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                                        {
                                            value = System.Convert.ToInt32(tmpValue, 16);
                                        }
                                        else
                                        {
                                            value = Int32.Parse(tmpValue);
                                        }
                                    }
                                    catch (System.Exception e)
                                    {
                                        throw e;
                                    }
                                }
                                else
                                {
                                    isImplicit = true;
                                }

                                EnumObject obj = new EnumObject()
                                {
                                    Name = name.Replace(",", ""),
                                    Value = value,
                                    IsImplicit = isImplicit
                                };

                                file.EnumObjects.Add(obj);
                            }
                        }

                        break;  //since enums are different we move onto the next file
                    }


                    // Class declaration
                    if (line.StrictContains("class") && line.Contains(":"))
                    {
                        string inheritance = modLine[modLine.Count - 1];
                        file.Inherits = inheritance;
                        file.InheritenceStructure = Find(inheritance, file);


                        /** If the class only contains inheritence we need a place holder obj */
                        LineObject obj = new LineObject() { };
                        file.Objects.Add(obj);
                    }

                    // Class property
                    if (line.StrictContains("public") && !line.StrictContains("class") && !IsContructor(line))
                    {
                        string type = modLine[0];
                        /** If the property is marked virtual, skip the virtual keyword. */
                        if (type.Equals("virtual"))
                        {
                            modLine.RemoveAt(0);
                            type = modLine[0];
                        }

                        bool IsArray = CheckIsArray(type);

                        bool isOptional = CheckOptional(type);

                        type = CleanType(type);

                        var userDefinedImport = Find(type, file);
                        var isUserDefined = !String.IsNullOrEmpty(userDefinedImport);

                        string varName = modLine[1];

                        if (varName.EndsWith(";")) {
                            varName = varName.Substring(0, varName.Length - 1);
                        }

                        LineObject obj = new LineObject()
                        {
                            VariableName = varName,
                            Type = isUserDefined ? type : TypeOf(type),
                            IsArray = IsArray,
                            IsOptional = isOptional,
                            UserDefined = isUserDefined,
                            UserDefinedImport = userDefinedImport
                        };

                        file.Objects.Add(obj);
                    }
                }
            }
        }

        private string TypeOf(string type)
        {
            switch (type)
            {
                case "byte":
                case "sbyte":
                case "decimal":
                case "double":
                case "float":
                case "int":
                case "uint":
                case "long":
                case "ulong":
                case "short":
                case "ushort":
                case "Byte":
                case "Decimal":
                case "Double":
                case "Int16":
                case "Int32":
                case "Int64":
                case "SByte":
                case "UInt16":
                case "UInt32":
                case "UInt64":
                    return "number";

                case "bool":
                case "Boolean":
                    return "boolean";

                case "string":
                case "char":
                case "String":
                case "Char":
                    return "string";

                case "DateTime":
                    return "Date";

                default: return "any";
            }
        }

        private void Convert()
        {
            Log.LogMessage(LoggingImportance, "Converting..");

            foreach (var file in Models)
            {
                DirectoryInfo di = Directory.CreateDirectory(Path.Combine(LocalConvertDir, file.Structure));
                di.Create();

                string fileName = ToCamelCase(file.Name + ".ts");
                Log.LogMessage(LoggingImportance, "Creating file {0}", fileName);
                string saveDir = Path.Combine(di.FullName, fileName);

                using (var stream = new FileStream(saveDir, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan))
                using (StreamWriter f =
                    new StreamWriter(stream, System.Text.Encoding.UTF8, 1024, false))
                {
                    var importing = false;  //only used for formatting
                    var imports = new List<string>();  //used for duplication

                    if (AutoGeneratedTag) 
                    {
                        f.WriteLine("/* Auto Generated */");
                        f.WriteLine();
                    }

                    if (file.IsEnum)
                    {

                        f.WriteLine(
                            "export enum "
                            + file.Name
                            // + (String.IsNullOrEmpty(file.Inherits) ? "" : (" : " + file.Inherits)) //typescript doesn't extend enums like c#
                            + " {"
                            );

                        foreach (var obj in file.EnumObjects)
                        {
                            if (!String.IsNullOrEmpty(obj.Name))
                            {  //not an empty obj
                                var str =
                                    ToCamelCase(obj.Name)
                                    + (obj.IsImplicit ? "" : (" = " + obj.Value))
                                    + ",";

                                f.WriteLine("    " + str);
                            }
                        }

                        f.WriteLine("}");

                    }
                    else
                    {

                        foreach (var obj in file.Objects)
                        {
                            if (!String.IsNullOrEmpty(file.Inherits))
                            {
                                importing = true;
                                var import = "import { " + file.Inherits + " } from \"" + file.InheritenceStructure + "\"";

                                if (!imports.Contains(import))
                                {
                                    f.WriteLine(import);
                                    imports.Add(import);
                                }
                            }

                            if (obj.UserDefined)
                            {
                                importing = true;
                                var import = "import { " + obj.Type + " } from \"" + obj.UserDefinedImport + "\"";

                                if (!imports.Contains(import))
                                {
                                    f.WriteLine(import);
                                    imports.Add(import);
                                }
                            }
                        }

                        if (importing)
                        {
                            f.WriteLine("");
                        }

                        f.WriteLine(
                            "export interface "
                            + file.Name
                            + (String.IsNullOrEmpty(file.Inherits) ? "" : (" extends " + file.Inherits)) //if class has inheritance
                            + " {"
                            );

                        foreach (var obj in file.Objects)
                        {
                            if (!String.IsNullOrEmpty(obj.VariableName))
                            {  //not an empty obj
                                var str =
                                    ToCamelCase(obj.VariableName)
                                    + (obj.IsOptional ? "?" : String.Empty)
                                    + ": "
                                    + obj.Type
                                    + (obj.IsArray ? "[]" : String.Empty)
                                    + ";";

                                f.WriteLine("    " + str);
                            }
                        }
                        f.WriteLine("}");
                    }
                }
            }
        }

        private string ToCamelCase(string str)
        {
            if (String.IsNullOrEmpty(str) || Char.IsLower(str, 0))
                return str;

            bool isCaps = true;

            foreach (var c in str)
            {
                if (Char.IsLetter(c) && Char.IsLower(c))
                    isCaps = false;
            }

            if (isCaps) return str.ToLower();

            return Char.ToLowerInvariant(str[0]) + str.Substring(1);
        }

        private string ToPascalCase(string str)
        {
            if (String.IsNullOrEmpty(str) || Char.IsUpper(str, 0))
                return str;

            return Char.ToUpperInvariant(str[0]) + str.Substring(1);
        }

        private bool CheckIsArray(string type)
        {
            return type.Contains("[]") ||
                type.Contains("ICollection") ||
                type.Contains("IEnumerable") ||
                type.Contains("Array") ||
                type.Contains("Enumerable") ||
                type.Contains("Collection");
        }

        private bool CheckOptional(string type)
        {
            return type.Contains("?");
        }

        private string CleanType(string type)
        {
            return type.Replace("?", String.Empty)
                .Replace("[]", String.Empty)
                .Replace("ICollection", String.Empty)
                .Replace("IEnumerable", String.Empty)
                .Replace("<", String.Empty)
                .Replace(">", String.Empty);
        }

        private bool IsContructor(string line)
        {
            return line.Contains("()") || ((line.Contains("(") && line.Contains(")")));
        }

        private string[] ExplodeLine(string line)
        {
            var regex = new Regex("\\s*,\\s*");
            
            var l = regex.Replace(line, ",");

            return l
                .Replace("public", String.Empty)
                .Replace("static", String.Empty)
                .Replace("const", String.Empty)
                .Replace("readonly", String.Empty)
                .Trim()
                .Split(' ');
        }

        private string StripComments(string line)
        {
            if (line.Contains("//"))
            {
                line = line.Substring(0, line.IndexOf("//"));
            }

            return line;
        }

        private string GetRelativePath(string from, string to)
        {
            Uri path1 = new Uri(from.Replace("\\", "/"));
            Uri path2 = new Uri(to.Replace("\\", "/"));

            var rel = path1.MakeRelativeUri(path2);

            return Uri.UnescapeDataString(rel.OriginalString);
        }

        private string GetRelativePathFromLocalPath(string from, string to)
        {
            var path1 = Path.Combine(LocalWorkingDir, from);
            var path2 = Path.Combine(LocalWorkingDir, to);
            path1 = path1.Replace("/", "\\");
            path2 = path2.Replace("/", "\\");

            if (!String.Equals(path1.Substring(path1.Length - 1), "\\"))
            {
                path1 = path1 + "\\";
            }

            if (!String.Equals(path2.Substring(path2.Length - 1), "\\"))
            {
                path2 = path2 + "\\";
            }

            var rel = GetRelativePath(path1, path2).Replace("\\", "/");

            if (!String.Equals(rel.Substring(0), "."))
            {
                rel = "./" + rel;
            }

            return rel;
        }

        private string Find(string query, ModelFile file)
        {
            string userDefinedImport = null;

            foreach (var f in Models)
            {
                if (f.Name.Equals(query))
                {
                    var rel = GetRelativePathFromLocalPath(file.Structure, f.Structure);

                    return rel + ToCamelCase(f.Name);
                }
            }

            return userDefinedImport;
        }
    }

    public static class StringExtension
    {
        public static bool StrictContains(this string str, string match)
        {
            string reg = "(^|\\s)" + match + "(\\s|$)";
            return Regex.IsMatch(str, reg);
        }
    }
}
