﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ARM_Builder_V6
{
    class CmdGenerator
    {
        public struct CmdInfo
        {
            public string exePath;
            public string commandLine;
            public string sourcePath;
            public string outPath;
        }

        class TypeErrorException : Exception
        {
            public TypeErrorException(string msg) : base(msg)
            {
            }
        }

        public delegate void CmdVisitor(string key, string cmdLine);

        class CmdFormat
        {
            public string prefix = "";
            public string body = null;
            public string suffix = "";
            public string sep = " ";
            public bool noQuotes = false;
        };

        class InvokeFormat
        {
            public bool useFile = false;
            public string body = null;
        };

        //-----------------------------------------------------------

        public static readonly string optionKey = "options";
        public static readonly string[] formatKeyList = {
            "$includes", "$defines"
        };

        private readonly Dictionary<string, Encoding> encodings = new Dictionary<string, Encoding>();

        private readonly Dictionary<string, string[]> cmdLists = new Dictionary<string, string[]>();
        private readonly Dictionary<string, JObject> paramObj = new Dictionary<string, JObject>();
        private readonly Dictionary<string, JObject> models = new Dictionary<string, JObject>();

        private readonly Dictionary<string, Dictionary<string, CmdFormat>> formats =
            new Dictionary<string, Dictionary<string, CmdFormat>>();
        private readonly Dictionary<string, InvokeFormat> invokeFormats = new Dictionary<string, InvokeFormat>();

        private readonly string toolPrefix;
        private readonly string cCompilerName;
        private readonly string asmCompilerName;
        private readonly string linkerName;
        private readonly bool useUnixPath;

        private readonly string outDir;
        private readonly string binDir;
        private readonly JObject model;
        private readonly JObject parameters;

        private readonly Dictionary<string, int> objNameMap = new Dictionary<string, int>();

        public CmdGenerator(JObject cModel, JObject cParams, string bindir, string outpath)
        {
            model = cModel;
            parameters = cParams;
            outDir = outpath;
            binDir = bindir;

            useUnixPath = cModel.ContainsKey("useUnixPath") ? cModel["useUnixPath"].Value<bool>() : false;

            JObject compileOptions = (JObject)cParams[optionKey];

            paramObj.Add("global", compileOptions.ContainsKey("global") ? (JObject)compileOptions["global"] : new JObject());
            paramObj.Add("c", compileOptions.ContainsKey("c/cpp-compiler") ? (JObject)compileOptions["c/cpp-compiler"] : new JObject());
            paramObj.Add("cpp", compileOptions.ContainsKey("c/cpp-compiler") ? (JObject)compileOptions["c/cpp-compiler"] : new JObject());
            paramObj.Add("asm", compileOptions.ContainsKey("asm-compiler") ? (JObject)compileOptions["asm-compiler"] : new JObject());
            paramObj.Add("linker", compileOptions.ContainsKey("linker") ? (JObject)compileOptions["linker"] : new JObject());

            cCompilerName = paramObj["c"].ContainsKey("$use") ? paramObj["c"]["$use"].Value<string>() : "c/cpp";
            asmCompilerName = paramObj["asm"].ContainsKey("$use") ? paramObj["asm"]["$use"].Value<string>() : "asm";
            linkerName = paramObj["linker"].ContainsKey("$use") ? paramObj["linker"]["$use"].Value<string>() : "linker";

            if (!((JObject)cModel["groups"]).ContainsKey(cCompilerName))
                throw new Exception("Invalid '$use' option!，please check compile option 'c/cpp-compiler.$use'");

            if (!((JObject)cModel["groups"]).ContainsKey(asmCompilerName))
                throw new Exception("Invalid '$use' option!，please check compile option 'asm-compiler.$use'");

            models.Add("c", (JObject)cModel["groups"][cCompilerName]);
            models.Add("cpp", (JObject)cModel["groups"][cCompilerName]);
            models.Add("asm", (JObject)cModel["groups"][asmCompilerName]);
            models.Add("linker", (JObject)cModel["groups"]["linker"]);

            // init command line from model
            JObject globalParams = paramObj["global"];

            // set tool path prefix
            if (globalParams.ContainsKey("toolPrefix"))
            {
                toolPrefix = globalParams["toolPrefix"].Value<string>();
            }
            else if (cModel.ContainsKey("toolPrefix"))
            {
                toolPrefix = cModel["toolPrefix"].Value<string>();
            }
            else
            {
                toolPrefix = "";
            }

            // set encodings
            foreach (string modelName in models.Keys)
            {
                if (models[modelName].ContainsKey("$encoding"))
                {
                    string codeName = models[modelName]["$encoding"].Value<string>();
                    switch (codeName)
                    {
                        case "UTF8":
                            encodings.Add(modelName, new UTF8Encoding(false));
                            break;
                        default:
                            encodings.Add(modelName, getEncoding(codeName));
                            break;
                    }
                }
                else
                {
                    encodings.Add(modelName, Encoding.Default);
                }
            }

            // set include, define commands format
            foreach (string modelName in models.Keys)
            {
                JObject modelParams = models[modelName];

                Dictionary<string, CmdFormat> properties = new Dictionary<string, CmdFormat>();
                foreach (string key in formatKeyList)
                {
                    if (modelParams.ContainsKey(key))
                    {
                        properties.Add(key, modelParams[key].ToObject<CmdFormat>());
                    }
                }
                formats.Add(modelName, properties);

                invokeFormats.Add(modelName, modelParams["$invoke"].ToObject<InvokeFormat>());
            }

            // set outName to unique
            getUniqueName(getOutName());

            // set stable command line
            foreach (var model in models)
            {
                string name = model.Key;
                JObject cmpModel = model.Value;
                List<string> commandList = new List<string>();

                JObject[] cmpParams = {
                    globalParams,
                    paramObj[name]
                };

                if (cmpModel.ContainsKey("$default"))
                {
                    foreach (var ele in ((JArray)cmpModel["$default"]).Values<string>())
                        commandList.Add(ele);
                }

                foreach (var ele in cmpModel)
                {
                    try
                    {
                        if (ele.Key[0] != '$')
                        {
                            object paramsValue = null;

                            foreach (var param in cmpParams)
                            {
                                if (param.ContainsKey(ele.Key))
                                {
                                    switch (param[ele.Key].Type)
                                    {
                                        case JTokenType.String:
                                            paramsValue = param[ele.Key].Value<string>();
                                            break;
                                        case JTokenType.Boolean:
                                            paramsValue = param[ele.Key].Value<bool>() ? "true" : "false";
                                            break;
                                        case JTokenType.Integer:
                                        case JTokenType.Float:
                                            paramsValue = param[ele.Key].Value<object>().ToString();
                                            break;
                                        case JTokenType.Array:
                                            paramsValue = param[ele.Key].Values<string>();
                                            break;
                                        default:
                                            break;
                                    }
                                }
                            }

                            try
                            {
                                string cmd = getCommandValue((JObject)ele.Value, paramsValue).Trim();
                                if (!string.IsNullOrEmpty(cmd))
                                {
                                    commandList.Add(cmd);
                                }
                            }
                            catch (TypeErrorException err)
                            {
                                throw new TypeErrorException("The type of key '" + ele.Key + "' is '" + err.Message
                                    + "' but you gived '" + paramsValue.GetType().Name + "'");
                            }
                        }
                    }
                    catch (TypeErrorException err)
                    {
                        throw err;
                    }
                    catch (Exception err)
                    {
                        throw new Exception("Init command failed: '" + name + "', Key: '" + ele.Key + "' !, " + err.Message);
                    }
                }

                // set include path and defines
                if (name != "linker")
                {
                    string[] additionList = new string[] {
                        getIncludesCmdLine(name, ((JArray)cParams["incDirs"]).Values<string>()),
                        getdefinesCmdLine(name, ((JArray)cParams["defines"]).Values<string>())
                    };

                    foreach (string command in additionList)
                    {
                        if (!string.IsNullOrEmpty(command))
                        {
                            commandList.Add(command);
                        }
                    }
                }

                if (cmpModel.ContainsKey("$default-tail"))
                {
                    foreach (var ele in ((JArray)cmpModel["$default-tail"]).Values<string>())
                        commandList.Add(ele);
                }

                cmdLists.Add(name, commandList.ToArray());
            }
        }

        public CmdInfo fromCFile(string fpath)
        {
            return fromModel("c", "language-c", fpath);
        }

        public CmdInfo fromCppFile(string fpath)
        {
            return fromModel("cpp", "language-cpp", fpath);
        }

        public CmdInfo fromAsmFile(string fpath)
        {
            return fromModel("asm", null, fpath);
        }

        public List<string> getMapMatcher()
        {
            JObject linkerModel = models["linker"];
            return linkerModel.ContainsKey("$matcher")
                ? new List<string>(linkerModel["$matcher"].Values<string>()) : new List<string>();
        }

        public Regex getRamSizeMatcher()
        {
            JObject linkerModel = models["linker"];
            return linkerModel.ContainsKey("$ramMatcher")
                ? new Regex(linkerModel["$ramMatcher"].Value<string>(), RegexOptions.IgnoreCase) : null;
        }

        public Regex getRomSizeMatcher()
        {
            JObject linkerModel = models["linker"];
            return linkerModel.ContainsKey("$romMatcher")
                ? new Regex(linkerModel["$romMatcher"].Value<string>(), RegexOptions.IgnoreCase) : null;
        }

        public CmdInfo genLinkCommand(List<string> objList)
        {
            JObject linkerModel = models["linker"];
            JObject linkerParams = paramObj["linker"];
            InvokeFormat iFormat = invokeFormats["linker"];
            string sep = iFormat.useFile ? "\r\n" : " ";

            string outSuffix = linkerModel.ContainsKey("$outputSuffix")
                ? linkerModel["$outputSuffix"].Value<string>() : ".axf";

            string mapSuffix = linkerModel.ContainsKey("$mapSuffix")
                ? linkerModel["$mapSuffix"].Value<string>() : ".map";

            string cmdLocation = linkerModel.ContainsKey("$commandLocation")
                ? linkerModel["$commandLocation"].Value<string>() : "start";

            string objSep = linkerModel.ContainsKey("$objPathSep")
                ? linkerModel["$objPathSep"].Value<string>() : "\r\n";

            bool mainFirst = linkerModel.ContainsKey("$mainFirst")
                ? linkerModel["$mainFirst"].Value<bool>() : false;

            string outName = getOutName();
            string outPath = outDir + Path.DirectorySeparatorChar + outName + outSuffix;
            string stableCommand = string.Join(" ", cmdLists["linker"]);
            string cmdLine = "";

            switch (cmdLocation)
            {
                case "start":
                    cmdLine = stableCommand;
                    break;
                default:
                    break;
            }

            if (mainFirst)
            {
                string mainName = linkerParams.ContainsKey("$mainFileName")
                    ? linkerParams["$mainFileName"].Value<string>() : "main";

                int index = objList.FindIndex((string fName) =>
                {
                    return Path.GetFileNameWithoutExtension(fName).Equals(mainName);
                });

                if (index != -1)
                {
                    string name = objList[index];
                    objList.RemoveAt(index);
                    objList.Insert(0, name);
                }
                else
                {
                    throw new Exception("Not found '"
                        + mainName + ".rel' file in output list, the '"
                        + mainName + ".rel' file must be the first linker file !");
                }
            }

            for (int i = 0; i < objList.Count; i++)
            {
                objList[i] = toUnixQuotingPath(objList[i]);
            }

            cmdLine += sep + linkerModel["$output"].Value<string>()
                .Replace("${out}", toUnixQuotingPath(outPath))
                .Replace("${in}", string.Join(objSep, objList.ToArray()));

            string mapPath = outDir + Path.DirectorySeparatorChar + outName + mapSuffix;
            if (linkerModel.ContainsKey("$linkMap"))
            {
                cmdLine += sep + getCommandValue((JObject)linkerModel["$linkMap"], "")
                    .Replace("${mapPath}", toUnixQuotingPath(mapPath));
            }

            switch (cmdLocation)
            {
                case "end":
                    cmdLine += " " + stableCommand;
                    break;
                default:
                    break;
            }

            string commandLine = null;
            if (iFormat.useFile)
            {
                FileInfo paramFile = new FileInfo(outDir + Path.DirectorySeparatorChar + outName + ".lnp");
                File.WriteAllText(paramFile.FullName, cmdLine, encodings["linker"]);
                commandLine = iFormat.body.Replace("${value}", "\"" + paramFile.FullName + "\"");
            }
            else
            {
                commandLine = cmdLine;
            }

            return new CmdInfo
            {
                exePath = getToolPath("linker"),
                commandLine = commandLine,
                sourcePath = mapPath,
                outPath = outPath
            };
        }

        public CmdInfo genOutputCommand(string linkerOutputFile)
        {
            JObject outputModel = (JObject)model["groups"]["output"];
            string hexpath = outDir + Path.DirectorySeparatorChar + getOutName();

            if (outputModel.ContainsKey("$outputSuffix"))
            {
                hexpath += outputModel["$outputSuffix"].Value<string>();
            }
            else
            {
                hexpath += ".hex";
            }

            string command = outputModel["command"].Value<string>()
                .Replace("${linkerOutput}", toUnixQuotingPath(linkerOutputFile))
                .Replace("${output}", toUnixQuotingPath(hexpath));

            return new CmdInfo
            {
                exePath = getToolPathByRePath(outputModel["$path"].Value<string>()),
                commandLine = command,
                sourcePath = linkerOutputFile,
                outPath = hexpath
            };
        }

        public string getOutName()
        {
            return parameters.ContainsKey("name") ? parameters["name"].Value<string>() : "main";
        }

        public string getToolPathByRePath(string rePath)
        {
            return binDir + Path.DirectorySeparatorChar + rePath.Replace("${toolPrefix}", toolPrefix);
        }

        public string getToolPath(string name)
        {
            return binDir + Path.DirectorySeparatorChar
                + models[name]["$path"].Value<string>().Replace("${toolPrefix}", toolPrefix);
        }

        public void traverseCommands(CmdVisitor visitor)
        {
            foreach (var item in cmdLists)
                visitor(item.Key, string.Join(" ", item.Value));
        }

        public string getModelName()
        {
            return model.ContainsKey("name") ? model["name"].Value<string>() : "null";
        }

        public string getToolPrefix()
        {
            return toolPrefix;
        }

        //------------

        private CmdInfo fromModel(string modelName, string langName, string fpath)
        {
            JObject cModel = models[modelName];
            JObject cParams = paramObj[modelName];
            InvokeFormat iFormat = invokeFormats[modelName];

            string outputSuffix = cModel.ContainsKey("$outputSuffix")
                ? cModel["$outputSuffix"].Value<string>() : ".o";

            bool isQuote = cModel.ContainsKey("$quotePath")
                ? cModel["$quotePath"].Value<bool>() : true;

            string paramsSuffix = modelName == "asm" ? "._ia" : ".__i";
            string fName = getUniqueName(Path.GetFileNameWithoutExtension(fpath));
            string outPath = outDir + Path.DirectorySeparatorChar + fName + outputSuffix;
            string listPath = outDir + Path.DirectorySeparatorChar + fName + ".lst";
            string langOption = null;

            List<string> commands = new List<string>();

            if (langName != null && cParams.ContainsKey(langName) && cModel.ContainsKey("$" + langName))
            {
                langOption = cParams[langName].Value<string>();
                commands.Add(getCommandValue((JObject)cModel["$" + langName], langOption));
            }

            if (cModel.ContainsKey("$listPath"))
            {
                commands.Add(getCommandValue((JObject)cModel["$listPath"], "")
                    .Replace("${listPath}", toUnixQuotingPath(listPath, isQuote)));
            }

            string outputFormat = cModel["$output"].Value<string>();
            if (outputFormat.Contains("${in}"))
            {
                commands.AddRange(cmdLists[modelName]);
                commands.Add(outputFormat.Replace("${out}", toUnixQuotingPath(outPath, isQuote))
                    .Replace("${in}", toUnixQuotingPath(fpath, isQuote)));
            }
            else
            {
                commands.Insert(0, toUnixQuotingPath(fpath));
                commands.AddRange(cmdLists[modelName]);
                commands.Add(outputFormat.Replace("${out}", toUnixQuotingPath(outPath, isQuote)));
            }

            string commandLine = null;
            if (iFormat.useFile)
            {
                FileInfo paramFile = new FileInfo(outDir + Path.DirectorySeparatorChar + fName + paramsSuffix);
                File.WriteAllText(paramFile.FullName, string.Join(" ", commands.ToArray()), encodings[modelName]);
                commandLine = iFormat.body.Replace("${value}", "\"" + paramFile.FullName + "\"");
            }
            else
            {
                commandLine = string.Join(" ", commands.ToArray());
            }

            return new CmdInfo
            {
                exePath = getToolPath(modelName),
                commandLine = commandLine,
                sourcePath = fpath,
                outPath = outPath
            };
        }

        private string toUnixQuotingPath(string path, bool quote = true)
        {
            if (useUnixPath)
            {
                return (quote && path.Contains(" ")) ? ("\"" + path.Replace("\\", "/") + "\"") : path.Replace("\\", "/");
            }

            return (quote && path.Contains(" ")) ? ("\"" + path + "\"") : path;
        }

        private string getUniqueName(string expectedName)
        {
            string lowerName = expectedName.ToLower();
            if (objNameMap.ContainsKey(lowerName))
            {
                objNameMap[lowerName] += 1;
                return expectedName + '_' + objNameMap[lowerName].ToString();
            }
            else
            {
                objNameMap.Add(lowerName, 0);
                return expectedName;
            }
        }

        private Encoding getEncoding(string name)
        {
            switch (name.ToLower())
            {
                case "utf8":
                    return Encoding.UTF8;
                case "utf16":
                    return Encoding.Unicode;
                default:
                    return Encoding.Default;
            }
        }

        private string getCommandValue(JObject option, object value)
        {
            if (value == null)
            {
                return "";
            }

            string type = option["type"].Value<string>();
            switch (type)
            {
                case "list":
                    if (!(value is IEnumerable<string>))
                    {
                        throw new TypeErrorException("array");
                    }
                    break;
                default:
                    if (!(value is string))
                    {
                        throw new TypeErrorException("string");
                    }
                    break;
            }

            string prefix = null;
            string suffix = null;

            if (option.ContainsKey("prefix"))
            {
                prefix = option["prefix"].Value<string>();
            }
            else
            {
                prefix = "";
            }

            if (option.ContainsKey("suffix"))
            {
                suffix = option["suffix"].Value<string>();
            }
            else
            {
                suffix = "";
            }

            string command = "";
            switch (type)
            {
                case "selectable":
                    if (value != null && ((JObject)option["command"]).ContainsKey((string)value))
                    {
                        command = option["command"][value].Value<string>();
                    }
                    else
                    {
                        command = option["command"]["false"].Value<string>();
                    }
                    break;
                case "keyValue":
                    if (value != null && ((JObject)option["enum"]).ContainsKey((string)value))
                    {
                        command = option["command"].Value<string>() + option["enum"][value].Value<string>();
                    }
                    else
                    {
                        command = option["command"].Value<string>() + option["enum"]["default"].Value<string>();
                    }
                    break;
                case "value":
                    command = option["command"].Value<string>() + toUnixQuotingPath((string)value ?? "", false);
                    break;
                case "list":
                    List<string> cmds = new List<string>() { option["command"].Value<string>() };

                    foreach (var item in (IEnumerable<string>)value)
                    {
                        cmds.Add(item);
                    }

                    command = string.Join(" ", cmds.ToArray());
                    break;
                default:
                    break;
            }

            return prefix + command + suffix;
        }

        private string getIncludesCmdLine(string modelName, IEnumerable<string> incList)
        {
            if (!formats[modelName].ContainsKey("$includes"))
            {
                return "";
            }

            List<string> cmds = new List<string>();
            JObject cmpModel = models[modelName];
            CmdFormat incFormat = formats[modelName]["$includes"];

            if (incFormat.noQuotes)
            {
                foreach (var inculdePath in incList)
                {
                    cmds.Add(incFormat.body.Replace("${value}", inculdePath));
                }
            }
            else
            {
                foreach (var inculdePath in incList)
                {
                    cmds.Add(incFormat.body.Replace("${value}", toUnixQuotingPath(inculdePath)));
                }
            }

            return incFormat.prefix + string.Join(incFormat.sep, cmds.ToArray()) + incFormat.suffix;
        }

        private string getdefinesCmdLine(string modelName, IEnumerable<string> defList)
        {
            if (!formats[modelName].ContainsKey("$defines"))
            {
                return "";
            }

            List<string> cmds = new List<string>();
            JObject cmpModel = models[modelName];
            CmdFormat defFormat = formats[modelName]["$defines"];

            foreach (var define in defList)
            {
                string macro = null;
                string value = null;

                int index = define.IndexOf('=');
                if (index >= 0)
                {
                    macro = define.Substring(0, index).Trim();
                    value = define.Substring(index + 1).Trim();
                    cmds.Add(defFormat.body.Replace("${key}", macro).Replace("${value}", value));
                }
                else
                {
                    macro = define.Trim();

                    if (modelName == "asm")
                    {
                        value = "1";
                        cmds.Add(defFormat.body.Replace("${key}", macro).Replace("${value}", value));
                    }
                    else
                    {
                        cmds.Add(Regex.Replace(defFormat.body, @"(?<define>^[^\$]+\$\{key\}).*$", "${define}")
                            .Replace("${key}", macro));
                    }
                }
            }

            return defFormat.prefix + string.Join(defFormat.sep, cmds.ToArray()) + defFormat.suffix;
        }
    }

    class Program
    {
        static readonly int CODE_ERR = 1;
        static readonly int CODE_DONE = 0;

        static readonly int compileThreshold = 16;
        static readonly string incSearchName = "IncludeSearcher.exe";

        // file filters
        static readonly Regex cFileFilter = new Regex(@"\.c$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex asmFileFilter = new Regex(@"\.(?:s|asm|a51)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex libFileFilter = new Regex(@"\.lib$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex cppFileFilter = new Regex(@"\.(?:cpp|cxx|cc|c\+\+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex enterReg = new Regex(@"\r\n|\n", RegexOptions.Compiled);

        static readonly List<string> cList = new List<string>();
        static readonly List<string> cppList = new List<string>();
        static readonly List<string> asmList = new List<string>();
        static readonly List<string> libList = new List<string>();

        static readonly string prevWorkDir = Environment.CurrentDirectory;

        static int ramMaxSize = -1;
        static int romMaxSize = -1;

        static string dumpPath;
        static string binDir;
        static int reqThreadsNum;
        static JObject compilerModel;
        static JObject paramsObj;
        static string outDir;
        static HashSet<BuilderMode> modeList = new HashSet<BuilderMode>();

        [DllImport("msvcrt.dll", SetLastError = false, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private extern static int system(string command);

        enum BuilderMode
        {
            NORMAL = 0,
            FAST,
            DEBUG,
            MULTHREAD
        }

        /**
         * command format: 
         *      -b <binDir>
         *      -d <dumpDir>
         *      -M <modelFile>
         *      -p <paramsFile>
         *      -o <outDir>
         *      -m <mode>
         */
        static int Main(string[] args)
        {
            if (args.Length < 10)
            {
                errorWithLable("Too few parameters !\r\n");
                return CODE_ERR;
            }

            Dictionary<string, string[]> paramsTable = new Dictionary<string, string[]>();

            // init params
            for (int i = 0; i < args.Length; i += 2)
            {
                try
                {
                    paramsTable.Add(args[i], args[i + 1].Split(';'));
                }
                catch (ArgumentException err)
                {
                    errorWithLable(err.Message);
                    return CODE_ERR;
                }
            }

            // prepare params
            try
            {
                binDir = paramsTable["-b"][0];
                dumpPath = paramsTable["-d"][0];
                compilerModel = (JObject)JToken.ReadFrom(new JsonTextReader(File.OpenText(paramsTable["-M"][0])));
                paramsObj = (JObject)JToken.ReadFrom(new JsonTextReader(File.OpenText(paramsTable["-p"][0])));
                addToSourceList(((JArray)paramsObj["sourceDirs"]).Values<string>());
                outDir = paramsTable["-o"][0];
                modeList.Add(BuilderMode.NORMAL);
                reqThreadsNum = paramsObj.ContainsKey("threadNum") ? paramsObj["threadNum"].Value<int>() : 0;
                ramMaxSize = paramsObj.ContainsKey("ram") ? paramsObj["ram"].Value<int>() : -1;
                romMaxSize = paramsObj.ContainsKey("rom") ? paramsObj["rom"].Value<int>() : -1;
                prepareModel();
                prepareParams(paramsObj);
                if (paramsTable.ContainsKey("-m"))
                {
                    foreach (var modeStr in paramsTable["-m"][0].Split('-'))
                    {
                        try
                        {
                            modeList.Add((BuilderMode)Enum.Parse(typeof(BuilderMode), modeStr.ToUpper()));
                        }
                        catch (Exception)
                        {
                            warn("\r\nInvalid mode option '" + modeStr + "', ignore it !");
                        }
                    }
                }
            }
            catch (Exception err)
            {
                errorWithLable("Init params failed !, " + err.Message + "\r\n");
                return CODE_ERR;
            }

            Dictionary<string, CmdGenerator.CmdInfo> commands = new Dictionary<string, CmdGenerator.CmdInfo>();
            List<string> doneList = new List<string>();
            Dictionary<string, string> toolPaths = new Dictionary<string, string>();
            Dictionary<Regex, string> tasksEnv = new Dictionary<Regex, string>();

            try
            {
                Directory.CreateDirectory(outDir);
                CmdGenerator cmdGen = new CmdGenerator(compilerModel, paramsObj, ".", outDir);

                // add env path for tasks
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                tasksEnv.Add(new Regex(@"\$\{TargetName\}", RegexOptions.IgnoreCase), cmdGen.getOutName());
                tasksEnv.Add(new Regex(@"\$\{ExeDir\}", RegexOptions.IgnoreCase), Path.GetDirectoryName(exePath));
                tasksEnv.Add(new Regex(@"\$\{BinDir\}", RegexOptions.IgnoreCase), binDir);
                tasksEnv.Add(new Regex(@"\$\{OutDir\}", RegexOptions.IgnoreCase), outDir);
                tasksEnv.Add(new Regex(@"\$\{CompileToolDir\}", RegexOptions.IgnoreCase),
                    Path.GetDirectoryName(binDir + Path.DirectorySeparatorChar + cmdGen.getToolPath("linker")));
                tasksEnv.Add(new Regex(@"\$\{toolPrefix\}", RegexOptions.IgnoreCase), cmdGen.getToolPrefix());

                if (checkMode(BuilderMode.DEBUG))
                {
                    string appName = Assembly.GetExecutingAssembly().GetName().Name;

                    log("\r\n" + appName + " v" + Assembly.GetExecutingAssembly().GetName().Version);

                    cmdGen.traverseCommands(delegate (string key, string cmdLine) {
                        warn("\r\n " + key + " tool's command: \r\n");
                        log(cmdLine);
                    });
                    return CODE_DONE;
                }

                List<string> linkerFiles = new List<string>(32);
                int cCount = 0, asmCount = 0, cppCount = 0;

                toolPaths.Add("C/C++ Compiler", cmdGen.getToolPath("c"));
                toolPaths.Add("ASM Compiler", cmdGen.getToolPath("asm"));
                toolPaths.Add("ARM Linker", cmdGen.getToolPath("linker"));

                // =============== Check Compiler Tools ================

                changeWorkDir(binDir);

                foreach (var tool in toolPaths)
                {
                    if (!File.Exists(tool.Value))
                        throw new Exception("Not found " + tool.Key + " !, [path] : \"" + tool.Value + "\"");
                }

                //========================================================

                log("");

                // run tasks before build
                if (runTasks("Run Tasks Before Build", "beforeBuildTasks", tasksEnv, true) != CODE_DONE)
                {
                    throw new Exception("Run Tasks Failed !, Stop Build !");
                }

                resetWorkDir();

                foreach (var cFile in cList)
                {
                    CmdGenerator.CmdInfo info = cmdGen.fromCFile(cFile);
                    linkerFiles.Add(info.outPath);
                    commands.Add(info.sourcePath, info);
                    cCount++;
                }

                foreach (var asmFile in asmList)
                {
                    CmdGenerator.CmdInfo info = cmdGen.fromAsmFile(asmFile);
                    linkerFiles.Add(info.outPath);
                    commands.Add(info.sourcePath, info);
                    asmCount++;
                }

                foreach (var cppFile in cppList)
                {
                    CmdGenerator.CmdInfo info = cmdGen.fromCppFile(cppFile);
                    linkerFiles.Add(info.outPath);
                    commands.Add(info.sourcePath, info);
                    cppCount++;
                }

                foreach (var libFile in libList)
                {
                    linkerFiles.Add(libFile);
                }

                CmdGenerator.CmdInfo linkInfo = cmdGen.genLinkCommand(linkerFiles);
                CmdGenerator.CmdInfo outputInfo = cmdGen.genOutputCommand(linkInfo.outPath);

                // start build
                DateTime time = DateTime.Now;
                infoWithLable(cmdGen.getModelName() + "\r\n", true, "MODEL");
                infoWithLable("-------------------- Start build at " + time.ToString("yyyy-MM-dd HH:mm:ss") + " --------------------\r\n");

                if (checkMode(BuilderMode.FAST))
                {
                    doneWithLable("\r\n", true, "Use Fast Build");
                    info(" > Comparing differences ...");
                    CheckDiffRes res = checkDiff(commands);
                    cCount = res.cCount;
                    asmCount = res.asmCount;
                    cppCount = res.cppCount;
                    commands = res.totalCmds;
                    log("");
                }

                infoWithLable("-------------------- File statistics --------------------\r\n");
                log(" > C Files: " + cCount.ToString());
                log(" > C++ Files: " + cppCount.ToString());
                log(" > ASM Files: " + asmCount.ToString());
                log(" > LIB Files: " + libList.Count.ToString());
                log("\r\n > Source File Totals: " + (cCount + cppCount + asmCount).ToString());

                // switch work directory
                changeWorkDir(binDir);

                log("");
                infoWithLable("-------------------- Start compilation... --------------------\r\n");

                if (!checkMode(BuilderMode.MULTHREAD) || commands.Values.Count < compileThreshold)
                {
                    foreach (var cmdInfo in commands.Values)
                    {
                        log(" > Compile... " + Path.GetFileName(cmdInfo.sourcePath));
                        if (system(cmdInfo.exePath + " " + cmdInfo.commandLine) != CODE_DONE)
                        {
                            throw new Exception("Compilation failed at : \"" + cmdInfo.sourcePath + "\"");
                        }
                        doneList.Add(cmdInfo.sourcePath);
                    }
                }
                else
                {
                    int threads = calcuThreads(reqThreadsNum, commands.Count);
                    doneWithLable(threads.ToString() + " threads\r\n", true, "Use Multi-Thread Mode");
                    CmdGenerator.CmdInfo[] cmds = new CmdGenerator.CmdInfo[commands.Count];
                    commands.Values.CopyTo(cmds, 0);
                    compileByMulThread(threads, cmds, doneList);
                }

                log("");
                infoWithLable("-------------------- Start link... --------------------");

                if (!File.Exists(linkInfo.exePath))
                    throw new Exception("Not found linker !, [path] : \"" + linkInfo.exePath + "\"");

                if (libList.Count > 0)
                {
                    log("");

                    foreach (var lib in libList)
                    {
                        log(" > Link Lib... " + Path.GetFileName(lib));
                    }
                }

                int linkerExitCode = runExe(linkInfo.exePath, linkInfo.commandLine, out string linkerOut);

                if (!string.IsNullOrEmpty(linkerOut.Trim()))
                {
                    log("\r\n" + linkerOut, false);
                }

                if (linkerExitCode != CODE_DONE)
                    throw new Exception("Link failed !");
                
                // print more information
                if (linkInfo.sourcePath != null && File.Exists(linkInfo.sourcePath))
                {
                    try
                    {
                        int ramSize = -1;
                        int romSize = -1;

                        Regex ramReg = cmdGen.getRamSizeMatcher();
                        Regex romReg = cmdGen.getRomSizeMatcher();
                        Regex[] regList = cmdGen.getMapMatcher().ConvertAll((string reg) =>
                        {
                            return new Regex(reg, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                        }).ToArray();

                        foreach (string line in File.ReadAllLines(linkInfo.sourcePath))
                        {
                            if (Array.FindIndex(regList, (Regex reg) => { return reg.IsMatch(line); }) != -1)
                            {
                                log("\r\n" + line.Trim());

                                if (ramSize == -1 && ramReg != null)
                                {
                                    Match matcher = ramReg.Match(line);
                                    if (matcher.Success && matcher.Groups.Count > 1)
                                    {
                                        ramSize = int.Parse(matcher.Groups[1].Value);
                                    }
                                }

                                if (romSize == -1 && romReg != null)
                                {
                                    Match matcher = romReg.Match(line);
                                    if (matcher.Success && matcher.Groups.Count > 1)
                                    {
                                        romSize = int.Parse(matcher.Groups[1].Value);
                                    }
                                }
                            }
                        }

                        float sizeKb = 0.0f;
                        float maxKb = 1.0f;

                        if (ramMaxSize != -1 && ramSize > 0)
                        {
                            sizeKb = ramSize / 1024.0f;
                            maxKb = ramMaxSize / 1024.0f;

                            string suffix = "\t" + sizeKb.ToString("f1") + "KB/" + maxKb.ToString("f1") + "KB";
                            printProgress("\r\nRAM Usage: ", (float)ramSize / ramMaxSize, suffix);
                        }

                        if (romMaxSize != -1 && romSize > 0)
                        {
                            sizeKb = romSize / 1024.0f;
                            maxKb = romMaxSize / 1024.0f;

                            string suffix = "\t" + sizeKb.ToString("f1") + "KB/" + maxKb.ToString("f1") + "KB";
                            printProgress("\r\nROM Usage: ", (float)romSize / romMaxSize, suffix);
                        }
                    }
                    catch (Exception err)
                    {
                        warn("\r\nCan't read information from '.map' file !, " + err.Message);
                    }
                }

                log("");
                infoWithLable("-------------------- Start output hex... --------------------");

                try
                {
                    if (!File.Exists(outputInfo.exePath))
                        throw new Exception("Not found " + Path.GetFileName(outputInfo.exePath)
                            + " !, [path] : \"" + outputInfo.exePath + "\"");

                    int outExit = runExe("cmd", "/C " + outputInfo.exePath + " " + outputInfo.commandLine, out string _hexOut);

                    if (!string.IsNullOrEmpty(_hexOut.Trim()))
                    {
                        log("\r\n" + _hexOut, false);
                    }

                    if (outExit != CODE_DONE)
                        throw new Exception("exec command failed !");
                    
                    info("\r\nHex file path : \"" + outputInfo.outPath + "\"");
                }
                catch (Exception err)
                {
                    warn("\r\nOutput Hex file failed !, msg: " + err.Message);
                }

                resetWorkDir();

                // flush to database
                updateDatabase(commands.Keys);

                TimeSpan tSpan = DateTime.Now.Subtract(time);
                log("");
                doneWithLable("-------------------- Build successfully ! Elapsed time "
                    + string.Format("{0}:{1}:{2}", tSpan.Hours, tSpan.Minutes, tSpan.Seconds)
                    + " --------------------\r\n");
            }
            catch (Exception err)
            {
                resetWorkDir();

                log("");
                errorWithLable(err.Message + "\r\n");
                errorWithLable("Build failed !");
                log("");

                // dump error log
                appendLogs(err);

                // flush to database
                updateDatabase(doneList);

                return CODE_ERR;
            }

            try
            {
                // run tasks after build success
                changeWorkDir(binDir);
                runTasks("Run Tasks After Build", "afterBuildTasks", tasksEnv);
                resetWorkDir();
            }
            catch (Exception err)
            {
                errorWithLable(err.Message + "\r\n");
            }

            return CODE_DONE;
        }

        //============================================

        static void printProgress(string label, float progress, string suffix = "")
        {
            int num = (int)(progress * 10.0f + 0.45f);
            num = num > 10 ? 10 : num;
            char[] sBuf = new char[10];

            for (int i = 0; i < 10; i++)
            {
                sBuf[i] = ' ';
            }

            for (int i = 0; i < num; i++)
            {
                sBuf[i] = '=';
            }

            string res = label + "[" + new string(sBuf) + "] " + (progress * 100).ToString("f1") + "% " + suffix;

            if (progress >= 1.0f)
            {
                error(res);
            }
            else if (progress >= 0.95f)
            {
                warn(res);
            }
            else
            {
                info(res);
            }
        }

        static int calcuThreads(int threads, int cmdCount)
        {
            if (threads < 2)
            {
                return 4;
            }

            int maxThread = threads;
            int expactThread = threads / 2;

            if (cmdCount / maxThread >= 2)
            {
                return maxThread;
            }

            if (cmdCount / expactThread >= 2)
            {
                return expactThread;
            }

            return 8;
        }

        static void changeWorkDir(string path)
        {
            Environment.CurrentDirectory = path;
        }

        static void resetWorkDir()
        {
            Environment.CurrentDirectory = prevWorkDir;
        }

        static int runExe(string filename, string args, out string _output)
        {
            Process process = new Process();
            process.StartInfo.FileName = filename;
            process.StartInfo.Arguments = args;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            StringBuilder output = new StringBuilder();

            process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e) {
                output.Append(e.Data == null ? "" : (e.Data + "\r\n"));
            };

            process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e) {
                output.Append(e.Data == null ? "" : (e.Data + "\r\n"));
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();
            int exitCode = process.ExitCode;
            process.Close();

            _output = output.ToString();

            return exitCode;
        }

        struct TaskData
        {
            public ManualResetEvent _event;
            public int index;
            public int end;
        }

        static void compileByMulThread(int thrNum, CmdGenerator.CmdInfo[] cmds, List<string> doneList)
        {
            Thread[] tasks = new Thread[thrNum];
            ManualResetEvent[] tEvents = new ManualResetEvent[thrNum];

            int part = cmds.Length / thrNum;
            Exception err = null;

            for (int i = 0; i < thrNum; i++)
            {
                tEvents[i] = new ManualResetEvent(false);
                tasks[i] = new Thread(delegate (object _dat) {

                    TaskData dat = (TaskData)_dat;

                    for (int index = dat.index; index < dat.end; index++)
                    {
                        if (err != null)
                        {
                            break;
                        }

                        log(" > Compile... " + Path.GetFileName(cmds[index].sourcePath));

                        int exitCode = runExe(cmds[index].exePath, cmds[index].commandLine, out string output);

                        lock (Console.Out)
                        {
                            Console.Write(output);
                        }

                        if (exitCode != CODE_DONE)
                        {
                            err = new Exception("Compilation failed at : \"" + cmds[index].sourcePath + "\"");
                            break;
                        }

                        lock (doneList)
                        {
                            doneList.Add(cmds[index].sourcePath);
                        }
                    }

                    dat._event.Set();
                });

                TaskData param = new TaskData
                {
                    _event = tEvents[i],
                    index = i * part
                };
                param.end = i == thrNum - 1 ? cmds.Length : param.index + part;

                tasks[i].Start(param);
            }

            WaitHandle.WaitAll(tEvents);

            if (err != null)
            {
                throw err;
            }
        }

        static int runTasks(string label, string fieldName, Dictionary<Regex, string> envList, bool nLine = false)
        {
            JObject options = (JObject)paramsObj[CmdGenerator.optionKey];
            if (options.ContainsKey(fieldName))
            {
                try
                {
                    JArray taskList = (JArray)options[fieldName];

                    if (taskList.Count > 0)
                    {
                        infoWithLable("", true, label);
                        int x, y, cX, cY;

                        foreach (JObject cmd in taskList)
                        {
                            if (!cmd.ContainsKey("name"))
                            {
                                throw new Exception("Task name can't be null !");
                            }

                            info("\r\n[Run]>> ", false);
                            log(cmd["name"].Value<string>() + "\t", false);
                            x = Console.CursorLeft;
                            y = Console.CursorTop;
                            log("\r\n");

                            if (!cmd.ContainsKey("command"))
                            {
                                throw new Exception("Task command line can't be null !");
                            }

                            string command = cmd["command"].Value<string>();

                            // replace env path
                            foreach (var item in envList)
                                command = item.Key.Replace(command, item.Value);

                            if (system('"' + command + '"') == CODE_DONE)
                            {
                                cX = Console.CursorLeft;
                                cY = Console.CursorTop;
                                Console.CursorLeft = x;
                                Console.CursorTop = y;
                                doneWithLable("", false);
                                Console.CursorLeft = cX;
                                Console.CursorTop = cY;
                            }
                            else
                            {
                                cX = Console.CursorLeft;
                                cY = Console.CursorTop;
                                Console.CursorLeft = x;
                                Console.CursorTop = y;
                                errorWithLable("", false, "Failed");
                                Console.CursorLeft = cX;
                                Console.CursorTop = cY;

                                if (cmd.ContainsKey("stopBuildAfterFailed")
                                    && cmd["stopBuildAfterFailed"].Type == JTokenType.Boolean
                                    && cmd["stopBuildAfterFailed"].Value<bool>())
                                    return CODE_ERR;

                                if (cmd.ContainsKey("abortAfterFailed")
                                    && cmd["abortAfterFailed"].Type == JTokenType.Boolean
                                    && cmd["abortAfterFailed"].Value<bool>())
                                    break;
                            }
                        }

                        log("\r\n", nLine);
                    }
                }
                catch (Exception e)
                {
                    warn("---------- Run task failed ! " + e.Message + " ----------");
                    warnWithLable("Can not parse task information, aborted !\r\n");
                }
            }

            return CODE_DONE;
        }

        static void updateDatabase(IEnumerable<string> sourceList)
        {
            try
            {
                if (!checkMode(BuilderMode.FAST))
                {
                    // update source to db if use Normal mode
                    if (updateSource(dumpPath, sourceList) != CODE_DONE)
                    {
                        warn("\r\nupdate source to database failed !");
                    }
                }
                else
                {
                    // flush db after build success if use Fast mode
                    if (flushDB(dumpPath) != CODE_DONE)
                    {
                        warn("\r\nflush to database failed !");
                    }
                }
            }
            catch (Exception err)
            {
                error("\r\n" + err.Message);
                warn("\r\nupdate to database invoke failed !");
            }
        }

        static bool checkMode(BuilderMode mode)
        {
            return modeList.Contains(mode);
        }

        class CheckDiffRes
        {
            public int cCount;
            public int cppCount;
            public int asmCount;
            public Dictionary<string, CmdGenerator.CmdInfo> totalCmds;

            public CheckDiffRes()
            {
                cCount = cppCount = asmCount = 0;
                totalCmds = new Dictionary<string, CmdGenerator.CmdInfo>();
            }
        }

        static CheckDiffRes checkDiff(Dictionary<string, CmdGenerator.CmdInfo> commands)
        {
            CheckDiffRes res = new CheckDiffRes();
            try
            {
                List<string> datas = new List<string>();

                // prepare params file
                string paramsPath = outDir + Path.DirectorySeparatorChar + "ia.params";
                datas.Add("[includes]");
                foreach (var inculdePath in ((JArray)paramsObj["incDirs"]).Values<string>())
                    datas.Add(inculdePath);

                datas.Add("[sourceList]");
                foreach (var cmd in commands.Values)
                {
                    if (!File.Exists(cmd.outPath))
                    {
                        if (cFileFilter.IsMatch(cmd.sourcePath))
                        {
                            res.cCount++;
                        }
                        else if (cppFileFilter.IsMatch(cmd.sourcePath))
                        {
                            res.cppCount++;
                        }
                        else if (asmFileFilter.IsMatch(cmd.sourcePath))
                        {
                            res.asmCount++;
                        }

                        res.totalCmds.Add(cmd.sourcePath, cmd);
                    }
                    else
                    {
                        datas.Add(cmd.sourcePath);
                    }
                }

                File.WriteAllText(paramsPath, string.Join("\r\n", datas.ToArray()));

                Process proc = new Process();
                proc.StartInfo.FileName = incSearchName;
                proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.Arguments = "-p \"" + paramsPath + "\" -d \"" + dumpPath + "\"";
                proc.StartInfo.CreateNoWindow = false;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.Start();

                string[] lines = enterReg.Split(proc.StandardOutput.ReadToEnd());
                string errorLine = proc.StandardError.ReadToEnd();

                proc.WaitForExit();
                int exitCode = proc.ExitCode;
                proc.Close();

                if (!string.IsNullOrEmpty(errorLine))
                {
                    appendLogs(new Exception("[IncSearcher exit " + exitCode.ToString() + "] : " + errorLine));
                }

                foreach (var line in lines)
                {
                    if (line.StartsWith("[End-New]") || line.StartsWith("[End-Changed]"))
                    {
                        int index = line.IndexOf(']');
                        if (index > 0)
                        {
                            string path = line.Substring(index + 1);

                            if (cFileFilter.IsMatch(path))
                            {
                                res.cCount++;
                            }
                            else if (cppFileFilter.IsMatch(path))
                            {
                                res.cppCount++;
                            }
                            else if (asmFileFilter.IsMatch(path))
                            {
                                res.asmCount++;
                            }

                            if (commands.ContainsKey(path))
                            {
                                res.totalCmds.Add(path, commands[path]);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                log("");
                warn(e.Message);
                log("");
                warnWithLable("Check difference failed !, Use normal build !");
                log("");
            }

            return res;
        }

        static int flushDB(string dumpDir)
        {
            Process proc = new Process();
            proc.StartInfo.FileName = incSearchName;
            proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.Arguments = " -d \"" + dumpDir + "\" -m flush";
            proc.StartInfo.CreateNoWindow = false;
            proc.Start();
            proc.WaitForExit();
            int exitCode = proc.ExitCode;
            proc.Close();
            return exitCode;
        }

        static int updateSource(string dumpDir, IEnumerable<string> sourceList)
        {
            // prepare params file
            List<string> datas = new List<string>();
            string paramsPath = outDir + Path.DirectorySeparatorChar + "ia.params";

            datas.Add("[includes]");
            foreach (var inculdePath in ((JArray)paramsObj["incDirs"]).Values<string>())
                datas.Add(inculdePath);

            datas.Add("[sourceList]");
            foreach (var src in sourceList)
                datas.Add(src);

            File.WriteAllText(paramsPath, string.Join("\r\n", datas.ToArray()));

            Process proc = new Process();
            proc.StartInfo.FileName = incSearchName;
            proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.Arguments = " -p \"" + paramsPath + "\" -d \"" + dumpDir + "\" -m update";
            proc.StartInfo.CreateNoWindow = false;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.Start();

            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            int exitCode = proc.ExitCode;
            proc.Close();
            return exitCode;
        }

        static void prepareModel()
        {
            var globals = (JObject)compilerModel["global"];
            var groups = (JObject)compilerModel["groups"];

            foreach (var ele in globals)
            {
                if (!((JObject)ele.Value).ContainsKey("group"))
                    throw new Exception("Not found 'group' in global option '" + ele.Key + "'");

                foreach (var category in (JArray)ele.Value["group"])
                {
                    if (groups.ContainsKey(category.Value<string>()))
                    {
                        ((JObject)groups[category.Value<string>()]).AddFirst(new JProperty(ele.Key, ele.Value));
                    }
                }
            }
        }

        static void prepareParams(JObject _params)
        {
            if (compilerModel.ContainsKey("defines"))
            {
                foreach (var define in ((JArray)compilerModel["defines"]).Values<string>())
                {
                    ((JArray)paramsObj["defines"]).Add(define);
                }
            }
        }

        static void addToSourceList(IEnumerable<string> srcDirs)
        {
            foreach (string srcDirPath in srcDirs)
            {
                DirectoryInfo dir = new DirectoryInfo(srcDirPath);
                if (dir.Exists)
                {
                    foreach (FileInfo file in dir.GetFiles())
                    {
                        if (cFileFilter.IsMatch(file.Name))
                        {
                            cList.Add(file.FullName);
                        }
                        else if (cppFileFilter.IsMatch(file.Name))
                        {
                            cppList.Add(file.FullName);
                        }
                        else if (asmFileFilter.IsMatch(file.Name))
                        {
                            asmList.Add(file.FullName);
                        }
                        else if (libFileFilter.IsMatch(file.Name))
                        {
                            libList.Add(file.FullName);
                        }
                    }
                }
                else
                {
                    warn("Ignore invalid source dir, [path] : \"" + srcDirPath + "\"");
                }
            }
        }

        //============================================

        static void appendLogs(Exception err)
        {
            try
            {
                string logFile = dumpPath + Path.DirectorySeparatorChar + "arm_builder.log";
                string txt = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "]\t";
                txt += err.Message + "\r\n" + err.StackTrace + "\r\n\r\n";
                File.AppendAllText(logFile, txt);
            }
            catch (Exception _err)
            {
                error("log dump failed !, " + _err.Message);
            }
        }

        static void log(string line, bool newLine = true)
        {
            if (newLine)
                Console.WriteLine(line);
            else
                Console.Write(line);
        }

        static void info(string txt, bool newLine = true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            if (newLine)
                Console.WriteLine(txt);
            else
                Console.Write(txt);
            Console.ResetColor();
        }

        static void warn(string txt, bool newLine = true)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            if (newLine)
                Console.WriteLine(txt);
            else
                Console.Write(txt);
            Console.ResetColor();
        }

        static void error(string txt, bool newLine = true)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            if (newLine)
                Console.WriteLine(txt);
            else
                Console.Write(txt);
            Console.ResetColor();
        }

        static void infoWithLable(string txt, bool newLine = true, string label = "INFO")
        {
            Console.Write("[");
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.BackgroundColor = ConsoleColor.Black;
            Console.Write(" " + label + " ");
            Console.ResetColor();
            Console.Write("]");
            Console.Write(" " + (newLine ? (txt + "\r\n") : txt));
        }

        static void warnWithLable(string txt, bool newLine = true, string label = "WARNING")
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.Yellow;
            Console.Write(" " + label + " ");
            Console.ResetColor();
            Console.Write(" " + (newLine ? (txt + "\r\n") : txt));
        }

        static void errorWithLable(string txt, bool newLine = true, string label = "ERROR")
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.Red;
            Console.Write(" " + label + " ");
            Console.ResetColor();
            Console.Write(" " + (newLine ? (txt + "\r\n") : txt));
        }

        static void doneWithLable(string txt, bool newLine = true, string label = "DONE")
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.Green;
            Console.Write(" " + label + " ");
            Console.ResetColor();
            Console.Write(" " + (newLine ? (txt + "\r\n") : txt));
        }
    }
}
