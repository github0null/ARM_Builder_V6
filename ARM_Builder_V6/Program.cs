using ConsoleTableExt;
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

namespace unify_builder
{
    class Utility
    {
        public delegate TargetType MapCallBk<Type, TargetType>(Type element);

        public static TargetType[] map<Type, TargetType>(IEnumerable<Type> iterator, MapCallBk<Type, TargetType> callBk)
        {
            List<TargetType> res = new List<TargetType>(16);

            foreach (var item in iterator)
            {
                res.Add(callBk(item));
            }

            return res.ToArray();
        }

        public static string toUnixPath(string path)
        {
            return path.Replace('\\', '/');
        }

        public static string toLocalPath(string path)
        {
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        // convert JObject to 'string' or 'IEnumerable<string>'
        public static object getJObjectVal(JToken jobj)
        {
            object paramsValue;

            switch (jobj.Type)
            {
                case JTokenType.String:
                    paramsValue = jobj.Value<string>();
                    break;
                case JTokenType.Boolean:
                    paramsValue = jobj.Value<bool>() ? "true" : "false";
                    break;
                case JTokenType.Integer:
                case JTokenType.Float:
                    paramsValue = jobj.Value<object>().ToString();
                    break;
                case JTokenType.Array:
                    paramsValue = jobj.Values<string>();
                    break;
                default:
                    paramsValue = null;
                    break;
            }

            return paramsValue;
        }
    }

    class CmdGenerator
    {
        public class GeneratorOption
        {
            public string bindir;
            public string outpath;
            public string cwd;
            public bool testMode;
        };

        public class CmdInfo
        {
            public string compilerType;
            public string title;
            public string exePath;
            public string commandLine;
            public string sourcePath;
            public string outPath;
        };

        class TypeErrorException : Exception
        {
            public TypeErrorException(string msg) : base(msg)
            {
            }
        };

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


        public static readonly string optionKey = "options";
        public static readonly string[] formatKeyList = {
            "$includes", "$defines", "$libs"
        };

        private readonly Dictionary<string, Encoding> encodings = new Dictionary<string, Encoding>();

        private readonly Dictionary<string, string[]> cmdLists = new Dictionary<string, string[]>();
        private readonly Dictionary<string, JObject> paramObj = new Dictionary<string, JObject>();
        private readonly Dictionary<string, JObject> models = new Dictionary<string, JObject>();

        private readonly Dictionary<string, Dictionary<string, CmdFormat>> formats =
            new Dictionary<string, Dictionary<string, CmdFormat>>();
        private readonly Dictionary<string, InvokeFormat> invokeFormats = new Dictionary<string, InvokeFormat>();

        private readonly string toolPrefix;
        private readonly bool useUnixPath;

        private readonly string outDir;
        private readonly string binDir;
        private readonly string cwd;

        private readonly JObject model;
        private readonly JObject parameters;

        private readonly Dictionary<string, int> objNameMap = new Dictionary<string, int>();

        public CmdGenerator(JObject cModel, JObject cParams, GeneratorOption option)
        {
            model = cModel;
            parameters = cParams;
            outDir = option.outpath;
            binDir = option.bindir != null ? (option.bindir + Path.DirectorySeparatorChar) : "";
            cwd = option.cwd;

            useUnixPath = cModel.ContainsKey("useUnixPath") ? cModel["useUnixPath"].Value<bool>() : false;

            JObject compileOptions = (JObject)cParams[optionKey];

            paramObj.Add("global", compileOptions.ContainsKey("global") ? (JObject)compileOptions["global"] : new JObject());
            paramObj.Add("c", compileOptions.ContainsKey("c/cpp-compiler") ? (JObject)compileOptions["c/cpp-compiler"] : new JObject());
            paramObj.Add("cpp", compileOptions.ContainsKey("c/cpp-compiler") ? (JObject)compileOptions["c/cpp-compiler"] : new JObject());
            paramObj.Add("asm", compileOptions.ContainsKey("asm-compiler") ? (JObject)compileOptions["asm-compiler"] : new JObject());
            paramObj.Add("linker", compileOptions.ContainsKey("linker") ? (JObject)compileOptions["linker"] : new JObject());

            string cCompilerName = ((JObject)cModel["groups"]).ContainsKey("c/cpp") ? "c/cpp" : "c";
            string cppCompilerName = ((JObject)cModel["groups"]).ContainsKey("c/cpp") ? "c/cpp" : "cpp";
            string asmCompilerName = paramObj["asm"].ContainsKey("$use") ? paramObj["asm"]["$use"].Value<string>() : "asm";
            string linkerName = paramObj["linker"].ContainsKey("$use") ? paramObj["linker"]["$use"].Value<string>() : "linker";

            if (!((JObject)cModel["groups"]).ContainsKey(cCompilerName))
                throw new Exception("Not found c compiler model");

            if (!((JObject)cModel["groups"]).ContainsKey(cppCompilerName))
                throw new Exception("Not found cpp compiler model");

            if (!((JObject)cModel["groups"]).ContainsKey(asmCompilerName))
                throw new Exception("Invalid '$use' option!，please check compile option 'asm-compiler.$use'");

            if (!((JObject)cModel["groups"]).ContainsKey(linkerName))
                throw new Exception("Invalid '$use' option!，please check compile option 'linker.$use'");

            models.Add("c", (JObject)cModel["groups"][cCompilerName]);
            models.Add("cpp", (JObject)cModel["groups"][cppCompilerName]);
            models.Add("asm", (JObject)cModel["groups"][asmCompilerName]);
            models.Add("linker", (JObject)cModel["groups"][linkerName]);

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

                // invoke mode
                InvokeFormat invokeFormat = modelParams["$invoke"].ToObject<InvokeFormat>();
                if (option.testMode)
                {
                    invokeFormat.useFile = false;
                }
                invokeFormats.Add(modelName, invokeFormat);
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
                        if (ele.Key[0] != '$') // ignore optional commands
                        {
                            object paramsValue = getValueFromParamList(cmpParams, ele.Key);

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

                // set lib search folders
                if (name == "linker")
                {
                    string command = getLibSearchFolders(name, ((JArray)cParams["libDirs"]).Values<string>());
                    if (!string.IsNullOrEmpty(command))
                    {
                        commandList.Add(command);
                    }
                }
                else // set include path and defines
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

                // replace ${var} to value
                Regex matcher = new Regex(@"\$\{([^\}]+)\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                for (int i = 0; i < commandList.Count; i++)
                {
                    Match mList = matcher.Match(commandList[i]);
                    if (mList.Success && mList.Groups.Count > 1)
                    {
                        for (int mIndex = 1; mIndex < mList.Groups.Count; mIndex++)
                        {
                            string key = mList.Groups[mIndex].Value;

                            if (cmpModel.ContainsKey(key))
                            {
                                try
                                {
                                    string value = getCommandValue((JObject)cmpModel[key], getValueFromParamList(cmpParams, key));
                                    commandList[i] = commandList[i].Replace("${" + key + "}", value);
                                }
                                catch (Exception)
                                {
                                    // ignore log
                                }
                            }
                        }
                    }
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

            string lib_flags = linkerModel.ContainsKey("$LIB_FLAGS") && linkerParams.ContainsKey("LIB_FLAGS")
                ? getCommandValue((JObject)linkerModel["$LIB_FLAGS"], Utility.getJObjectVal(linkerParams["LIB_FLAGS"])) : "";

            string outName = getOutName();
            string outPath = outDir + Path.DirectorySeparatorChar + outName + outSuffix;
            string stableCommand = string.Join(" ", cmdLists["linker"]);
            string cmdLine = "";
            string mapPath = outDir + Path.DirectorySeparatorChar + outName + mapSuffix;

            switch (cmdLocation)
            {
                case "start":
                    cmdLine = stableCommand;
                    if (linkerModel.ContainsKey("$linkMap"))
                    {
                        cmdLine += sep + getCommandValue((JObject)linkerModel["$linkMap"], "")
                            .Replace("${mapPath}", toUnixQuotingPath(mapPath));
                    }
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
                        + mainName + ".rel' file must be the first object file !");
                }
            }

            for (int i = 0; i < objList.Count; i++)
            {
                objList[i] = toUnixQuotingPath(objList[i]);
            }

            cmdLine += sep + linkerModel["$output"].Value<string>()
                .Replace("${out}", toUnixQuotingPath(outPath))
                .Replace("${in}", string.Join(objSep, objList.ToArray()))
                .Replace("${lib_flags}", lib_flags);

            switch (cmdLocation)
            {
                case "end":
                    if (linkerModel.ContainsKey("$linkMap"))
                    {
                        cmdLine += sep + getCommandValue((JObject)linkerModel["$linkMap"], "")
                            .Replace("${mapPath}", toUnixQuotingPath(mapPath));
                    }
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
                compilerType = "linker",
                exePath = getToolPath("linker"),
                commandLine = commandLine,
                sourcePath = mapPath,
                outPath = outPath
            };
        }

        public CmdInfo[] genOutputCommand(string linkerOutputFile)
        {
            JObject linkerModel = models["linker"];
            List<CmdInfo> commandsList = new List<CmdInfo>();

            // not need output hex/bin
            if (!linkerModel.ContainsKey("$outputBin"))
                return commandsList.ToArray();

            string outFileName = outDir + Path.DirectorySeparatorChar + getOutName();

            foreach (JObject outputModel in (JArray)linkerModel["$outputBin"])
            {
                string outFilePath = outFileName;

                if (outputModel.ContainsKey("outputSuffix"))
                {
                    outFilePath += outputModel["outputSuffix"].Value<string>();
                }

                string command = outputModel["command"].Value<string>()
                    .Replace("${linkerOutput}", toUnixQuotingPath(linkerOutputFile))
                    .Replace("${output}", toUnixQuotingPath(outFilePath));

                commandsList.Add(new CmdInfo
                {
                    title = outputModel["name"].Value<string>(),
                    exePath = getToolPathByRePath(outputModel["toolPath"].Value<string>()),
                    commandLine = command,
                    sourcePath = linkerOutputFile,
                    outPath = outFilePath
                });
            }

            return commandsList.ToArray();
        }

        public CmdInfo[] genLinkerExtraCommand(string linkerOutputFile)
        {
            JObject linkerModel = models["linker"];
            List<CmdInfo> commandList = new List<CmdInfo>();

            // not have Extra Command
            if (!linkerModel.ContainsKey("$extraCommand"))
                return commandList.ToArray();

            foreach (JObject model in (JArray)linkerModel["$extraCommand"])
            {
                string exePath = getToolPathByRePath(model["toolPath"].Value<string>());

                string command = model["command"].Value<string>()
                    .Replace("${linkerOutput}", toUnixQuotingPath(linkerOutputFile));

                commandList.Add(new CmdInfo
                {
                    title = model.ContainsKey("name") ? model["name"].Value<string>() : exePath,
                    exePath = exePath,
                    commandLine = command,
                    sourcePath = linkerOutputFile,
                    outPath = null
                });
            }

            return commandList.ToArray();
        }

        public string getOutName()
        {
            return parameters.ContainsKey("name") ? parameters["name"].Value<string>() : "main";
        }

        private string getToolPathByRePath(string rePath)
        {
            return binDir + rePath.Replace("${toolPrefix}", toolPrefix);
        }

        public string getToolPath(string name)
        {
            return binDir + getOriginalToolPath(name);
        }

        public string getOriginalToolPath(string name)
        {
            return models[name]["$path"].Value<string>().Replace("${toolPrefix}", toolPrefix);
        }

        public string getModelName()
        {
            return model.ContainsKey("name") ? model["name"].Value<string>() : "null";
        }

        public string getModelID()
        {
            return model.ContainsKey("id") ? model["id"].Value<string>() : getModelName();
        }

        public string getToolPrefix()
        {
            return toolPrefix;
        }

        //------------

        private object getValueFromParamList(JObject[] pList, string key)
        {
            object paramsValue = null;

            foreach (var param in pList)
            {
                if (param.ContainsKey(key))
                {
                    paramsValue = Utility.getJObjectVal(param[key]);
                    // note: can't break this loop, we need overwrite old value
                }
            }

            return paramsValue;
        }

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
            string refPath = outDir + Path.DirectorySeparatorChar + fName + ".d"; // --depend ${refPath} 
            string listPath = outDir + Path.DirectorySeparatorChar + fName + ".lst";
            string langOption = null;

            List<string> commands = new List<string>();
            IEnumerable<string> excludeList = null;

            if (langName != null && cModel.ContainsKey("$" + langName))
            {
                langOption = cParams.ContainsKey(langName) ? cParams[langName].Value<string>() : "default";
                commands.Add(getCommandValue((JObject)cModel["$" + langName], langOption));
                excludeList = ((JObject)cModel["$" + langName]).ContainsKey("exclude") ? cModel["$" + langName]["exclude"].Values<string>() : null;
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
                commands.Add(outputFormat
                    .Replace("${out}", toUnixQuotingPath(outPath, isQuote))
                    .Replace("${in}", toUnixQuotingPath(fpath, isQuote))
                    .Replace("${refPath}", toUnixQuotingPath(refPath, isQuote))
                );
            }
            else
            {
                commands.Insert(0, toUnixQuotingPath(fpath));
                commands.AddRange(cmdLists[modelName]);
                commands.Add(outputFormat
                    .Replace("${out}", toUnixQuotingPath(outPath, isQuote))
                    .Replace("${refPath}", toUnixQuotingPath(refPath, isQuote))
                );
            }

            // delete exclude commands
            if (excludeList != null)
            {
                foreach (string item in excludeList)
                {
                    Regex reg = new Regex("(?<!\\w|-)" + item + "(?!\\w|-)", RegexOptions.Compiled);

                    for (int i = 0; i < commands.Count; i++)
                    {
                        commands[i] = reg.Replace(commands[i], "");
                    }
                }
            }

            // delete whitespace
            commands.RemoveAll(delegate (string _command) { return string.IsNullOrEmpty(_command); });

            string commandLines = string.Join(" ", commands.ToArray());

            if (iFormat.useFile)
            {
                FileInfo paramFile = new FileInfo(outDir + Path.DirectorySeparatorChar + fName + paramsSuffix);
                File.WriteAllText(paramFile.FullName, commandLines, encodings[modelName]);
                commandLines = iFormat.body.Replace("${value}", "\"" + paramFile.FullName + "\"");
            }

            return new CmdInfo
            {
                compilerType = modelName,
                exePath = getToolPath(modelName),
                commandLine = commandLines,
                sourcePath = fpath,
                outPath = outPath
            };
        }

        private string toRelative(string root, string absPath)
        {
            if (root.Length >= absPath.Length)
            {
                return null;
            }

            string[] rList = Utility.toUnixPath(root).Split('/');
            string[] absList = Utility.toUnixPath(absPath).Split('/');

            int cIndex;
            for (cIndex = 0; cIndex < rList.Length; cIndex++)
            {
                if (rList[cIndex] != absList[cIndex])
                {
                    break;
                }
            }

            if (cIndex == rList.Length)
            {
                return Utility.toLocalPath("." + absPath.Substring(root.Length));
            }

            return null;
        }

        private string toUnixQuotingPath(string path, bool quote = true)
        {
            if (cwd != null)
            {
                string rePath = toRelative(cwd, path);
                if (rePath != null)
                {
                    path = rePath;
                }
            }

            if (useUnixPath)
            {
                path = Utility.toUnixPath(path);
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
            string type = option["type"].Value<string>();

            // check list type
            if (type == "list")
            {
                if (value is string)
                {
                    type = "value"; /* compatible type: 'list' and 'value' */
                }
                else if (value != null && !(value is IEnumerable<string>))
                {
                    throw new TypeErrorException("array");
                }
            }

            // check other type (other type must be a string)
            else if (value != null && !(value is string))
            {
                throw new TypeErrorException("string");
            }

            string prefix = option.ContainsKey("prefix") ? option["prefix"].Value<string>() : "";
            string suffix = option.ContainsKey("suffix") ? option["suffix"].Value<string>() : "";
            string command = null;

            switch (type)
            {
                case "selectable":

                    if (!option.ContainsKey("command"))
                        throw new Exception("type \'selectable\' must have \'command\' key !");

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

                    if (!option.ContainsKey("enum"))
                        throw new Exception("type \'keyValue\' must have \'enum\' key !");

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
                    if (value != null)
                    {
                        // convert '\\' -> '/'
                        string nValue = useUnixPath ? Utility.toUnixPath((string)value) : (string)value;
                        command = option["command"].Value<string>() + nValue;
                    }
                    break;
                case "list":
                    {
                        List<string> cmdList = new List<string>();

                        string cmd = option["command"].Value<string>();

                        if (value != null)
                        {
                            foreach (var item in (IEnumerable<string>)value)
                            {
                                // convert '\\' -> '/'
                                string nValue = useUnixPath ? Utility.toUnixPath(item) : item;
                                cmdList.Add(cmd + nValue);
                            }

                            command = string.Join(" ", cmdList.ToArray());
                        }
                    }
                    break;
                default:
                    throw new Exception("Invalid type \"" + type + "\"");
            }

            if (command == null)
                return "";

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

            foreach (var inculdePath in incList)
            {
                cmds.Add(incFormat.body.Replace("${value}", toUnixQuotingPath(inculdePath, !incFormat.noQuotes)));
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
                if (index >= 0) // macro have '='
                {
                    macro = define.Substring(0, index).Trim();
                    value = define.Substring(index + 1).Trim();
                    // remove '"' from value
                    value = (value[0] == '"' && value[value.Length - 1] == '"') ? value.Substring(1, value.Length - 2) : value;
                    cmds.Add(defFormat.body.Replace("${key}", macro).Replace("${value}", value));
                }
                else // macro have no '='
                {
                    macro = define.Trim();

                    string macroStr = null;

                    if (modelName == "asm")
                    {
                        value = "1";
                        macroStr = defFormat.body
                            .Replace("${key}", macro)
                            .Replace("${value}", value);
                    }
                    else // delete macro fmt str suffix
                    {
                        macroStr = Regex
                            .Replace(defFormat.body, @"(?<macro_key>^[^\$]*\$\{key\}).*$", "${macro_key}")
                            .Replace("${key}", macro);
                    }

                    cmds.Add(macroStr);
                }
            }

            return defFormat.prefix + string.Join(defFormat.sep, cmds.ToArray()) + defFormat.suffix;
        }

        private string getLibSearchFolders(string modelName, IEnumerable<string> libList)
        {
            if (!formats[modelName].ContainsKey("$libs"))
            {
                return "";
            }

            List<string> cmds = new List<string>();
            JObject cmpModel = models[modelName];
            CmdFormat incFormat = formats[modelName]["$libs"];

            foreach (var libDirPath in libList)
            {
                cmds.Add(incFormat.body.Replace("${value}", toUnixQuotingPath(libDirPath, !incFormat.noQuotes)));
            }

            return incFormat.prefix + string.Join(incFormat.sep, cmds.ToArray()) + incFormat.suffix;
        }
    }

    class Program
    {
        static readonly int CODE_ERR = 1;
        static readonly int CODE_DONE = 0;
        static int ERR_LEVEL = CODE_DONE;

        static readonly int compileThreshold = 12;

        static readonly Encoding UTF8 = new UTF8Encoding(false);

        // file filters
        static readonly Regex cFileFilter = new Regex(@"\.c$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex asmFileFilter = new Regex(@"\.(?:s|asm|a51)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex libFileFilter = new Regex(@"\.(?:lib|a)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex cppFileFilter = new Regex(@"\.(?:cpp|cxx|cc|c\+\+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex enterReg = new Regex(@"\r\n|\n", RegexOptions.Compiled);

        static readonly HashSet<string> cList = new HashSet<string>();
        static readonly HashSet<string> cppList = new HashSet<string>();
        static readonly HashSet<string> asmList = new HashSet<string>();
        static readonly HashSet<string> libList = new HashSet<string>();

        static readonly string defWorkDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        static readonly Dictionary<string, string> envMapper = new Dictionary<string, string>();

        static int ramMaxSize = -1;
        static int romMaxSize = -1;

        static string dumpPath;
        static string binDir;
        static int reqThreadsNum;
        static JObject compilerModel;
        static JObject paramsObj;
        static string outDir;
        static string projectRoot;

        static bool enableNormalOut = true;

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
         *      -M <modelFile>
         *      -p <paramsFile>
         *      -m <mode>
         */
        static int Main(string[] args)
        {
            // set current dir
            switchWorkDir(defWorkDir);

            // init
            try
            {
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
                        errorWithLable("params format failed !, " + err.Message);
                        return CODE_ERR;
                    }
                }

                try
                {
                    binDir = paramsTable["-b"][0];

                    string modelJson = File.ReadAllText(paramsTable["-M"][0], UTF8);
                    compilerModel = (JObject)JToken.Parse(modelJson);

                    string paramsJson = File.ReadAllText(paramsTable["-p"][0], UTF8);
                    paramsObj = (JObject)JToken.Parse(paramsJson);
                }
                catch (KeyNotFoundException)
                {
                    throw new Exception("some command line arguments missing !");
                }

                // init path
                projectRoot = paramsObj["rootDir"].Value<string>();
                dumpPath = paramsObj["dumpPath"].Value<string>();
                outDir = paramsObj["outDir"].Value<string>();

                // get real path
                dumpPath = isAbsolutePath(dumpPath) ? dumpPath : (projectRoot + Path.DirectorySeparatorChar + dumpPath);
                outDir = isAbsolutePath(outDir) ? outDir : (projectRoot + Path.DirectorySeparatorChar + outDir);

                // prepare source
                addToSourceList(projectRoot, paramsObj["sourceList"].Values<string>());

                // to absolute paths
                toAbsolutePaths(projectRoot, (JArray)paramsObj["incDirs"]);
                toAbsolutePaths(projectRoot, (JArray)paramsObj["libDirs"]);

                modeList.Add(BuilderMode.NORMAL);
                reqThreadsNum = paramsObj.ContainsKey("threadNum") ? paramsObj["threadNum"].Value<int>() : 0;
                ramMaxSize = paramsObj.ContainsKey("ram") ? paramsObj["ram"].Value<int>() : -1;
                romMaxSize = paramsObj.ContainsKey("rom") ? paramsObj["rom"].Value<int>() : -1;

                // init other params
                ERR_LEVEL = compilerModel.ContainsKey("ERR_LEVEL") ? compilerModel["ERR_LEVEL"].Value<int>() : ERR_LEVEL;
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
                errorWithLable("init build failed !, " + err.Message + "\r\n" + err.ToString());
                return CODE_ERR;
            }

            Dictionary<string, CmdGenerator.CmdInfo> commands = new Dictionary<string, CmdGenerator.CmdInfo>();
            List<string> doneList = new List<string>();
            Dictionary<string, string> toolPaths = new Dictionary<string, string>();
            Dictionary<Regex, string> tasksEnv = new Dictionary<Regex, string>();

            // record build start time
            DateTime time = DateTime.Now;

            try
            {
                Directory.CreateDirectory(outDir);
                CmdGenerator cmdGen = new CmdGenerator(compilerModel, paramsObj, new CmdGenerator.GeneratorOption
                {
                    bindir = "%TOOL_DIR%",
                    outpath = outDir,
                    cwd = projectRoot,
                    testMode = checkMode(BuilderMode.DEBUG)
                });

                // ingnore keil c51 normal output
                enableNormalOut = cmdGen.getModelID() != "Keil_C51";

                // add env path for tasks
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                string ccPath = binDir + Path.DirectorySeparatorChar + cmdGen.getOriginalToolPath("c");
                tasksEnv.Add(new Regex(@"\$\{TargetName\}", RegexOptions.IgnoreCase), cmdGen.getOutName());
                tasksEnv.Add(new Regex(@"\$\{ExeDir\}", RegexOptions.IgnoreCase), Path.GetDirectoryName(exePath));
                tasksEnv.Add(new Regex(@"\$\{ToolDir\}", RegexOptions.IgnoreCase), binDir);
                tasksEnv.Add(new Regex(@"\$\{OutDir\}", RegexOptions.IgnoreCase), outDir);
                tasksEnv.Add(new Regex(@"\$\{toolPrefix\}", RegexOptions.IgnoreCase), cmdGen.getToolPrefix());
                tasksEnv.Add(new Regex(@"\$\{CompileToolDir\}", RegexOptions.IgnoreCase), Path.GetDirectoryName(ccPath));

                if (checkMode(BuilderMode.DEBUG))
                {
                    string appName = Assembly.GetExecutingAssembly().GetName().Name;

                    log("\r\n" + appName + " v" + Assembly.GetExecutingAssembly().GetName().Version);

                    CmdGenerator.CmdInfo cmdInf;

                    warn("\r\nC command line: \r\n");
                    cmdInf = cmdGen.fromCFile("c_file.c");
                    log(cmdInf.exePath + " " + cmdInf.commandLine);

                    warn("\r\nCPP command line: \r\n");
                    cmdInf = cmdGen.fromCppFile("cpp_file.cpp");
                    log(cmdInf.exePath + " " + cmdInf.commandLine);

                    warn("\r\nASM command line: \r\n");
                    cmdInf = cmdGen.fromAsmFile("asm_file.s");
                    log(cmdInf.exePath + " " + cmdInf.commandLine);

                    warn("\r\nLinker command line: \r\n");
                    cmdInf = cmdGen.genLinkCommand(new List<string> { "main.o", "obj1.o", "obj2.o" });
                    log(cmdInf.exePath + " " + cmdInf.commandLine);

                    warn("\r\nOuput file command line: \r\n");
                    CmdGenerator.CmdInfo[] cmdInfoList = cmdGen.genOutputCommand(cmdInf.outPath);
                    foreach (CmdGenerator.CmdInfo info in cmdInfoList)
                    {
                        log("\t" + info.title + ": ");
                        log("\t\t" + info.exePath + " " + info.commandLine);
                        log("");
                    }

                    return CODE_DONE;
                }

                List<string> linkerFiles = new List<string>(32);
                int cCount = 0, asmCount = 0, cppCount = 0;

                toolPaths.Add("C/C++ Compiler", cmdGen.getToolPath("c"));
                toolPaths.Add("ASM Compiler", cmdGen.getToolPath("asm"));
                toolPaths.Add("ARM Linker", cmdGen.getToolPath("linker"));

                // Check Compiler Tools
                if (!Directory.Exists(binDir))
                {
                    throw new Exception("Not found toolchain directory !, [path] : \"" + binDir + "\"");
                }

                try
                {
                    setEnvValue("TOOL_DIR", binDir);
                }
                catch (Exception e)
                {
                    throw new Exception("Set Environment Failed !, [path] : \"" + binDir + "\"", e);
                }

                // check compiler path
                foreach (var tool in toolPaths)
                {
                    string absPath = replaceEnvVariable(tool.Value);
                    if (!File.Exists(absPath))
                        throw new Exception("not found " + tool.Key + " !, [path] : \"" + absPath + "\"");
                }

                log("");

                // switch to project root directory
                switchWorkDir(projectRoot);

                // run tasks before build
                if (runTasks("Run Tasks Before Build", "beforeBuildTasks", tasksEnv, true) != CODE_DONE)
                {
                    throw new Exception("Run Tasks Failed !, Stop Build !");
                }

                // reset work directory
                resetWorkDir();

                // prepare build
                infoWithLable(cmdGen.getModelName() + "\r\n", true, "TOOL");
                infoWithLable("-------------------- start building at " + time.ToString("yyyy-MM-dd HH:mm:ss") + " --------------------\r\n");

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

                if (linkerFiles.Count == 0)
                {
                    throw new Exception("Not found any source files !, please add some source files !");
                }

                // use fast mode
                if (checkMode(BuilderMode.FAST))
                {
                    info(">> comparing differences ...");
                    CheckDiffRes res = checkDiff(cmdGen.getModelID(), commands);
                    cCount = res.cCount;
                    asmCount = res.asmCount;
                    cppCount = res.cppCount;
                    commands = res.totalCmds;
                    log("");
                }

                log(">> file statistics:");

                int totalFilesCount = (cCount + cppCount + asmCount);

                string tString = ConsoleTableBuilder
                    .From(new List<List<object>> { new List<object> { cCount, cppCount, asmCount, libList.Count, totalFilesCount } })
                    .WithFormat(ConsoleTableBuilderFormat.Alternative)
                    .WithColumn(new List<string> { "C Files", "Cpp Files", "Asm Files", "Lib Files", "Totals" })
                    .Export()
                    .Insert(0, "   ").Replace("\n", "\n   ")
                    .ToString();

                Console.Write(tString);

                // build start
                switchWorkDir(projectRoot);

                log("");
                infoWithLable("-------------------- start compilation ... --------------------");

                if (commands.Count > 0)
                {
                    log("");
                }

                if (!checkMode(BuilderMode.MULTHREAD) || commands.Values.Count < compileThreshold)
                {
                    foreach (var cmdInfo in commands.Values)
                    {
                        string logTag = cmdInfo.compilerType == "asm" ? "assembling" : "compiling";
                        log(">> " + logTag + " '" + Path.GetFileName(cmdInfo.sourcePath) + "'");

                        int exitCode = runExe(cmdInfo.exePath, cmdInfo.commandLine, out string ccOut);

                        // ignore normal output
                        if (enableNormalOut || exitCode != CODE_DONE)
                        {
                            printCompileOutput(ccOut.Trim());
                        }

                        if (exitCode > ERR_LEVEL)
                        {
                            throw new Exception("compilation failed at : \"" + cmdInfo.sourcePath + "\", exit code: " + exitCode.ToString());
                        }

                        doneList.Add(cmdInfo.sourcePath);
                    }
                }
                else
                {
                    int threads = calcuThreads(reqThreadsNum, commands.Count);
                    info("Use Multi-Thread Mode: " + threads.ToString() + " threads\r\n", true);
                    CmdGenerator.CmdInfo[] cmds = new CmdGenerator.CmdInfo[commands.Count];
                    commands.Values.CopyTo(cmds, 0);
                    compileByMulThread(threads, cmds, doneList);
                }

                log("");
                infoWithLable("-------------------- start linking ... --------------------");

                CmdGenerator.CmdInfo linkInfo = cmdGen.genLinkCommand(linkerFiles);

                if (libList.Count > 0)
                {
                    log("");

                    foreach (var lib in libList)
                    {
                        log(">> linking '" + Path.GetFileName(lib) + "'");
                    }
                }

                int linkerExitCode = runExe(linkInfo.exePath, linkInfo.commandLine, out string linkerOut);

                if (!string.IsNullOrEmpty(linkerOut.Trim()))
                {
                    log("\r\n" + linkerOut, false);
                }

                if (linkerExitCode > ERR_LEVEL)
                    throw new Exception("link failed !, exit code: " + linkerExitCode.ToString());

                // execute extra command
                foreach (CmdGenerator.CmdInfo extraLinkerCmd in cmdGen.genLinkerExtraCommand(linkInfo.outPath))
                {
                    int exitCode = runExe(extraLinkerCmd.exePath, extraLinkerCmd.commandLine, out string cmdOutput);
                    if (exitCode == CODE_DONE)
                    {
                        log("\r\n>> " + extraLinkerCmd.title + ":", false);
                        log("\r\n" + cmdOutput, false);
                    }
                }

                // print ram/rom usage
                if (linkInfo.sourcePath != null && File.Exists(linkInfo.sourcePath))
                {
                    try
                    {
                        int ramSize = -1;
                        int romSize = -1;
                        bool hasPrintNewline = false;

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
                                if (hasPrintNewline == false)
                                {
                                    log("");
                                    hasPrintNewline = true;
                                }

                                log(line.Trim());

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
                        float maxKb = 0.0f;

                        if (ramSize >= 0 || romSize >= 0)
                        {
                            log("");

                            if (ramSize >= 0) // print RAM info
                            {
                                sizeKb = ramSize / 1024.0f;

                                if (ramMaxSize != -1)
                                {
                                    maxKb = ramMaxSize / 1024.0f;
                                    string suffix = "\t" + sizeKb.ToString("f1") + "KB/" + maxKb.ToString("f1") + "KB";
                                    printProgress("RAM  : ", (float)ramSize / ramMaxSize, suffix);
                                }
                            }

                            if (romSize >= 0) // print ROM info
                            {
                                sizeKb = romSize / 1024.0f;

                                if (romMaxSize != -1)
                                {
                                    maxKb = romMaxSize / 1024.0f;
                                    string suffix = "\t" + sizeKb.ToString("f1") + "KB/" + maxKb.ToString("f1") + "KB";
                                    printProgress("FLASH: ", (float)romSize / romMaxSize, suffix);
                                }
                            }
                        }
                    }
                    catch (Exception err)
                    {
                        warn("\r\ncan't read information from '.map' file !, " + err.Message);
                    }
                }

                // execute output command
                CmdGenerator.CmdInfo[] commandList = cmdGen.genOutputCommand(linkInfo.outPath);

                if (commandList.Length > 0)
                {
                    log("");
                    infoWithLable("-------------------- start outputting file ... --------------------");

                    foreach (CmdGenerator.CmdInfo outputCmdInfo in commandList)
                    {
                        log("\r\n>> " + outputCmdInfo.title, false);

                        string exeLog = "";

                        try
                        {
                            string exeAbsPath = replaceEnvVariable(outputCmdInfo.exePath);

                            if (!File.Exists(exeAbsPath))
                            {
                                throw new Exception("not found " + Path.GetFileName(exeAbsPath)
                                    + " !, [path] : \"" + exeAbsPath + "\"");
                            }

                            // must use 'cmd', because SDCC has '>' command
                            int eCode = runExe("cmd", "/C \"\"" + outputCmdInfo.exePath + "\" " + outputCmdInfo.commandLine + "\"", out string _exe_log);
                            exeLog = _exe_log;

                            if (eCode > ERR_LEVEL)
                                throw new Exception("execute command failed !, exit code: " + eCode.ToString());

                            // done !, output txt

                            success("\t\t[done]"); // show status after title

                            if (!string.IsNullOrEmpty(exeLog.Trim()))
                            {
                                log("\r\n" + exeLog, false);
                            }

                            log("\r\nfile path: \"" + outputCmdInfo.outPath + "\"");
                        }
                        catch (Exception err)
                        {
                            error("\t\t[failed]"); // show status after title

                            if (!string.IsNullOrEmpty(exeLog.Trim()))
                            {
                                log("\r\n" + exeLog, false);
                            }

                            warn("\r\nfailed !, msg: " + err.Message);
                        }
                    }
                }

                // reset work directory
                resetWorkDir();

                TimeSpan tSpan = DateTime.Now.Subtract(time);
                log("");
                doneWithLable("====================", false);
                success(" build successfully !, elapsed time " + string.Format("{0}:{1}:{2}", tSpan.Hours, tSpan.Minutes, tSpan.Seconds) + " ", false);
                log("====================", true);
                log("");

                // dump log
                appendLogs("[done]", "\tbuild successfully !");
            }
            catch (Exception err)
            {
                TimeSpan tSpan = DateTime.Now.Subtract(time);
                log("");
                errorWithLable(err.Message + "\r\n");
                errorWithLable("build failed !, elapsed time " + string.Format("{0}:{1}:{2}", tSpan.Hours, tSpan.Minutes, tSpan.Seconds));
                log("");

                // reset work dir when failed
                resetWorkDir();

                // dump error log
                appendErrLogs(err);

                return CODE_ERR;
            }

            try
            {
                switchWorkDir(projectRoot);
                runTasks("Run Tasks After Build", "afterBuildTasks", tasksEnv);
                resetWorkDir();
            }
            catch (Exception err)
            {
                errorWithLable(err.Message + "\r\n");
            }

            return CODE_DONE;
        }


        enum OutputMatcherType
        {
            WarningMatcher = 0,
            ErrorMatcher,
            NoteMatcher,
            LineHintMatcher,
            LableMatcher
        };

        static Regex[] outputMatcher = new Regex[] {
            new Regex(@"\s(warning[:]?)\s", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s(error[:]?)\s", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s(note[:]?)\s", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"^\s+\|(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"^\s+(\^[~]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        static void printCompileOutput(string output)
        {
            if (!string.IsNullOrEmpty(output))
            {
                string[] lines = Regex.Split(output, @"\r\n|\n");

                foreach (string line in lines)
                {
                    int index;

                    for (index = 0; index < outputMatcher.Length; index++)
                    {
                        Match matcher = outputMatcher[index].Match(line);

                        if (matcher.Success)
                        {
                            Group group = matcher.Groups[1];

                            if (group.Index > 0)
                            {
                                Console.Write(line.Substring(0, group.Index));
                            }

                            switch ((OutputMatcherType)index)
                            {
                                case OutputMatcherType.ErrorMatcher:
                                    error(group.Value, false);
                                    break;
                                case OutputMatcherType.NoteMatcher:
                                    info(group.Value, false);
                                    break;
                                case OutputMatcherType.WarningMatcher:
                                    warn(group.Value, false);
                                    break;
                                case OutputMatcherType.LableMatcher:
                                case OutputMatcherType.LineHintMatcher:
                                    printColor(group.Value, ConsoleColor.Magenta, false);
                                    break;
                                default:
                                    break;
                            }

                            if (line.Length > group.Index + group.Length)
                            {
                                Console.WriteLine(line.Substring(group.Index + group.Length));
                            }
                            else
                            {
                                Console.WriteLine("");
                            }

                            break;
                        }
                    }

                    if (index == outputMatcher.Length) // not found any tag
                    {
                        Console.WriteLine(line);
                    }
                }
            }
        }

        static bool isAbsolutePath(string path)
        {
            return Regex.IsMatch(path, @"^(?:[a-z]:|/)", RegexOptions.IgnoreCase);
        }

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
            int expactThread = threads >= 4 ? (threads / 2) : 4;
            int minThread = threads >= 8 ? (threads / 4) : 2;

            if (cmdCount / maxThread >= 2)
            {
                return maxThread;
            }

            if (cmdCount / expactThread >= 2)
            {
                return expactThread;
            }

            if (cmdCount / minThread >= 2)
            {
                return minThread;
            }

            return 8;
        }

        static void switchWorkDir(string path)
        {
            try
            {
                Environment.CurrentDirectory = path;
            }
            catch (DirectoryNotFoundException e)
            {
                throw new Exception("Switch workspace failed ! Not found directory: " + path, e);
            }
        }

        static void resetWorkDir()
        {
            Environment.CurrentDirectory = defWorkDir;
        }

        //---

        static void setEnvValue(string key, string value)
        {
            Environment.SetEnvironmentVariable(key, value);
            if (envMapper.ContainsKey(key))
            {
                envMapper.Remove(key);
            }
            envMapper.Add(key, value);
        }

        static string replaceEnvVariable(string path)
        {
            foreach (var keyValue in envMapper)
            {
                path = path.Replace("%" + keyValue.Key + "%", keyValue.Value);
            }

            return path;
        }

        //---

        static int runExe(string filename, string args, out string _output)
        {
            Process process = new Process();
            process.StartInfo.FileName = replaceEnvVariable(filename);
            process.StartInfo.Arguments = args;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            StringBuilder output = new StringBuilder();

            process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
            {
                output.Append(e.Data == null ? "" : (e.Data + "\r\n"));
            };

            process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
            {
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
                tasks[i] = new Thread(delegate (object _dat)
                {

                    TaskData dat = (TaskData)_dat;

                    for (int index = dat.index; index < dat.end; index++)
                    {
                        if (err != null)
                        {
                            break;
                        }

                        string logTag = cmds[index].compilerType == "asm" ? "assembling" : "compiling";
                        log(">> " + logTag + " '" + Path.GetFileName(cmds[index].sourcePath) + "'");

                        int exitCode = runExe(cmds[index].exePath, cmds[index].commandLine, out string output);

                        lock (Console.Out)
                        {
                            // ignore normal output
                            if (enableNormalOut || exitCode != CODE_DONE)
                            {
                                printCompileOutput(output.Trim());
                            }
                        }

                        if (exitCode > ERR_LEVEL)
                        {
                            err = new Exception("compilation failed at : \"" + cmds[index].sourcePath + "\", exit code: " + exitCode.ToString());
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

        static string getBlanks(int num)
        {
            char[] buf = new char[num];

            for (int i = 0; i < num; i++)
            {
                buf[i] = ' ';
            }

            return new string(buf);
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
                        int maxLen = -1;

                        infoWithLable("", true, label);

                        // get max length
                        foreach (JObject cmd in taskList)
                        {
                            if (cmd.ContainsKey("name"))
                            {
                                string name = cmd["name"].Value<string>();
                                maxLen = name.Length > maxLen ? name.Length : maxLen;
                            }
                        }

                        foreach (JObject cmd in taskList)
                        {
                            if (cmd.ContainsKey("disable")
                                && cmd["disable"].Type == JTokenType.Boolean
                                && cmd["disable"].Value<bool>())
                            {
                                // task is disabled, ignore it !
                                continue;
                            }

                            if (!cmd.ContainsKey("name"))
                            {
                                throw new Exception("task name can't be null !");
                            }

                            // print task name
                            string tName = cmd["name"].Value<string>();
                            log("\r\n>> " + tName + getBlanks(maxLen - tName.Length) + "\t\t", false);

                            if (!cmd.ContainsKey("command"))
                            {
                                throw new Exception("task command line can't be null !");
                            }

                            string command = cmd["command"].Value<string>();

                            // replace env path
                            foreach (var item in envList)
                                command = item.Key.Replace(command, item.Value);

                            // run command
                            if (runExe("cmd", "/C \"" + command + "\"", out string cmdStdout) == CODE_DONE)
                            {
                                success("[done]\r\n");
                                if (!string.IsNullOrEmpty(cmdStdout.Trim()))
                                    log(cmdStdout, false);
                            }
                            else
                            {
                                error("[failed]\r\n");
                                log(command + "\r\n");
                                if (!string.IsNullOrEmpty(cmdStdout.Trim()))
                                    error(cmdStdout, false);

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
                    warn("---------- run task failed ! " + e.Message + " ----------");
                    warnWithLable("can not parse task information, aborted !\r\n");
                }
            }

            return CODE_DONE;
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

        static Dictionary<string, bool> diffCache = new Dictionary<string, bool>();

        static CheckDiffRes checkDiff(string modelID, Dictionary<string, CmdGenerator.CmdInfo> commands)
        {
            CheckDiffRes res = new CheckDiffRes();

            Func<CmdGenerator.CmdInfo, bool> AddToChangeList = (cmd) =>
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

                return true;
            };

            try
            {
                foreach (var cmd in commands.Values)
                {
                    if (File.Exists(cmd.outPath))
                    {
                        DateTime objLastWriteTime = File.GetLastWriteTime(cmd.outPath);

                        if (DateTime.Compare(File.GetLastWriteTime(cmd.sourcePath), objLastWriteTime) > 0) // src file is newer than obj file
                        {
                            AddToChangeList(cmd);
                        }
                        else // check referance is changed
                        {
                            string refFilePath = Path.GetDirectoryName(cmd.outPath)
                                + Path.DirectorySeparatorChar + Path.GetFileNameWithoutExtension(cmd.outPath) + ".d";

                            if (File.Exists(refFilePath))
                            {
                                string[] refList = parseRefFile(refFilePath, modelID);

                                if (refList != null)
                                {
                                    foreach (var refPath in refList)
                                    {
                                        if (diffCache.ContainsKey(refPath))
                                        {
                                            if (diffCache[refPath])
                                            {
                                                AddToChangeList(cmd);
                                                break; // file need recompile, exit
                                            }
                                        }
                                        else // not in cache
                                        {
                                            if (File.Exists(refPath))
                                            {
                                                DateTime lastWrTime = File.GetLastWriteTime(refPath);
                                                bool isOutOfDate = DateTime.Compare(lastWrTime, objLastWriteTime) > 0;
                                                diffCache.Add(refPath, isOutOfDate); // add to cache

                                                if (isOutOfDate)
                                                {
                                                    AddToChangeList(cmd);
                                                    break; // out of date, need recompile, exit
                                                }
                                            }
                                            else // not found ref, ref file need update
                                            {
                                                AddToChangeList(cmd);
                                                break; // need recompile, exit
                                            }
                                        }
                                    }
                                }
                                else // not found parser or parse error
                                {
                                    AddToChangeList(cmd);
                                }
                            }
                            else // not found ref file
                            {
                                AddToChangeList(cmd);
                            }
                        }
                    }
                    else
                    {
                        AddToChangeList(cmd);
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

        static string toAbsolutePath(string _repath)
        {
            string repath = Utility.toLocalPath(_repath);

            if (repath.Length > 1 && char.IsLetter(repath[0]) && repath[1] == ':')
            {
                return repath;
            }

            return projectRoot + Path.DirectorySeparatorChar + repath;
        }

        static Regex whitespaceMatcher = new Regex(@"(?<![\\:]) ", RegexOptions.Compiled);

        static string[] gnu_parseRefLines(string[] lines)
        {
            HashSet<string> resultList = new HashSet<string>();

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string _line = lines[lineIndex];

                string line = _line[_line.Length - 1] == '\\' ? _line.Substring(0, _line.Length - 1) : _line; // remove char '\'
                string[] subLines = whitespaceMatcher.Split(line.Trim());

                if (lineIndex == 0) // first line
                {
                    for (int i = 1; i < subLines.Length; i++) // skip first sub line
                    {
                        resultList.Add(toAbsolutePath(subLines[i].Trim().Replace("\\ ", " ")));
                    }
                }
                else  // other lines, first char is whitespace
                {
                    foreach (var item in subLines)
                    {
                        resultList.Add(toAbsolutePath(item.Trim().Replace("\\ ", " ")));
                    }
                }
            }

            string[] resList = new string[resultList.Count];
            resultList.CopyTo(resList);

            return resList;
        }

        static string[] ac5_parseRefLines(string[] lines, int startIndex = 1)
        {
            HashSet<string> resultList = new HashSet<string>();

            for (int i = startIndex; i < lines.Length; i++)
            {
                int sepIndex = lines[i].IndexOf(": ");
                if (sepIndex > 0)
                {
                    string line = lines[i].Substring(sepIndex + 1).Trim();
                    resultList.Add(toAbsolutePath(line));
                }
            }

            string[] resList = new string[resultList.Count];
            resultList.CopyTo(resList);

            return resList;
        }

        static string[] parseRefFile(string fpath, string modeID)
        {
            string[] lines = File.ReadAllLines(fpath, Encoding.Default);

            switch (modeID)
            {
                case "AC5":
                    return ac5_parseRefLines(lines);
                case "IAR_STM8":
                    return ac5_parseRefLines(lines, 2);
                case "SDCC":
                case "AC6":
                case "GCC":
                    return gnu_parseRefLines(lines);
                default:
                    return null;
            }
        }

        static void prepareModel()
        {
            var globals = (JObject)compilerModel["global"];
            var groups = (JObject)compilerModel["groups"];

            foreach (var ele in globals)
            {
                if (!((JObject)ele.Value).ContainsKey("group"))
                    throw new Exception("not found 'group' in global option '" + ele.Key + "'");

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

        static void addToSourceList(string rootDir, IEnumerable<string> sourceList)
        {
            foreach (string repath in sourceList)
            {
                string sourcePath = isAbsolutePath(repath)
                    ? repath : (rootDir + Path.DirectorySeparatorChar + repath);
                FileInfo file = new FileInfo(sourcePath);

                if (file.Exists)
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
        }

        static void toAbsolutePaths(string rootDir, JArray jArr)
        {
            string[] incList = jArr.ToObject<string[]>();

            jArr.RemoveAll();

            foreach (string _path in incList)
            {
                if (isAbsolutePath(_path))
                {
                    jArr.Add(_path);
                }
                else
                {
                    jArr.Add(rootDir + Path.DirectorySeparatorChar + _path);
                }
            }
        }

        // log func

        static void appendLogs(string lable, string msg)
        {
            try
            {
                string logFile = dumpPath + Path.DirectorySeparatorChar + "arm_builder.log";
                string txt = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "]\t";
                txt += lable + "\r\n" + msg + "\r\n\r\n";
                File.AppendAllText(logFile, txt);
            }
            catch (Exception _err)
            {
                error("log dump failed !, " + _err.Message);
            }
        }

        static void appendErrLogs(Exception err)
        {
            appendLogs(err.Message, err.StackTrace);
        }

        static void log(string line, bool newLine = true)
        {
            if (newLine)
                Console.WriteLine(line);
            else
                Console.Write(line);
        }

        static void success(string txt, bool newLine = true)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            if (newLine)
                Console.WriteLine(txt);
            else
                Console.Write(txt);
            Console.ResetColor();
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

        static void printColor(string txt, ConsoleColor color, bool newLine = true)
        {
            Console.ForegroundColor = color;
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
            Console.Write("[");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.BackgroundColor = ConsoleColor.Black;
            Console.Write(" " + label + " ");
            Console.ResetColor();
            Console.Write("]");
            Console.Write(" " + (newLine ? (txt + "\r\n") : txt));
        }
    }
}
