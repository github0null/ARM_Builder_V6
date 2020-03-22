using Newtonsoft.Json;
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

        enum ToolChain
        {
            AC5,
            AC6,
            None
        }

        //-----------------------------------------------------------

        public static readonly string optionKey = "options";

        private readonly Encoding encoding;
        private readonly Encoding asmEncoding;

        private readonly Dictionary<string, string> cmdLines = new Dictionary<string, string>();
        private readonly Dictionary<string, JObject> paramObj = new Dictionary<string, JObject>();
        private readonly Dictionary<string, JObject> models = new Dictionary<string, JObject>();

        private readonly string cCompilerName;
        private readonly string asmCompilerName;
        private readonly string linkerName;

        private readonly string outDir;
        private readonly string binDir;
        private readonly JObject model;
        private readonly JObject parameters;

        private readonly ToolChain toolchain;
        private readonly Dictionary<string, int> objNameMap = new Dictionary<string, int>();

        public CmdGenerator(JObject cModel, JObject cParams, string bindir, string outpath)
        {
            toolchain = cModel["toolchain"].Value<string>() == "AC5" ? ToolChain.AC5 : ToolChain.AC6;

            if (toolchain == ToolChain.AC5)
            {
                encoding = Encoding.Default;
            }
            else
            {
                encoding = new UTF8Encoding(false);
            }

            model = cModel;
            parameters = cParams;
            outDir = outpath;
            binDir = bindir;

            cmdLines.Add("c", "");
            cmdLines.Add("cpp", "");
            cmdLines.Add("asm", "");
            cmdLines.Add("linker", "");

            var compileOptions = (JObject)cParams[optionKey];

            paramObj.Add("global", compileOptions.ContainsKey("global") ? (JObject)compileOptions["global"] : new JObject());
            paramObj.Add("c", compileOptions.ContainsKey("c/cpp-compiler") ? (JObject)compileOptions["c/cpp-compiler"] : new JObject());
            paramObj.Add("cpp", compileOptions.ContainsKey("c/cpp-compiler") ? (JObject)compileOptions["c/cpp-compiler"] : new JObject());
            paramObj.Add("asm", compileOptions.ContainsKey("asm-compiler") ? (JObject)compileOptions["asm-compiler"] : new JObject());
            paramObj.Add("linker", compileOptions.ContainsKey("linker") ? (JObject)compileOptions["linker"] : new JObject());

            cCompilerName = paramObj["c"].ContainsKey("$use") ? paramObj["c"]["$use"].Value<string>() : "c/cpp";
            asmCompilerName = paramObj["asm"].ContainsKey("$use") ? paramObj["asm"]["$use"].Value<string>() : "asm";
            linkerName = paramObj["linker"].ContainsKey("$use") ? paramObj["linker"]["$use"].Value<string>() : "linker";

            if (asmCompilerName == "asm")
            {
                asmEncoding = Encoding.Default;
            }
            else
            {
                asmEncoding = new UTF8Encoding(false);
            }

            if (!((JObject)cModel["groups"]).ContainsKey(cCompilerName))
                throw new Exception("无效的 '$use' 选项!，检查编译配置 'c/cpp-compiler.$use' 是否正确");

            if (!((JObject)cModel["groups"]).ContainsKey(asmCompilerName))
                throw new Exception("无效的 '$use' 选项!，检查编译配置 'asm-compiler.$use' 是否正确");

            models.Add("c", (JObject)cModel["groups"][cCompilerName]);
            models.Add("cpp", (JObject)cModel["groups"][cCompilerName]);
            models.Add("asm", (JObject)cModel["groups"][asmCompilerName]);
            models.Add("linker", (JObject)cModel["groups"]["linker"]);

            // init command line from model
            JObject globalParams = paramObj["global"];

            // set outName to unique
            getUniqueName(getOutName());

            foreach (var model in models)
            {
                string name = model.Key;
                JObject cmpModel = model.Value;
                string cmdLine = cmdLines[name];

                JObject[] cmpParams = {
                    globalParams,
                    paramObj[name]
                };

                foreach (var ele in ((JArray)cmpModel["$default"]).Values<string>())
                    cmdLine += " " + ele;

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
                                cmdLine += getCommandValue((JObject)ele.Value, paramsValue);
                            }
                            catch (TypeErrorException err)
                            {
                                throw new TypeErrorException("The type of key '" + ele.Key + "' is '" + err.Message
                                    + "' but you gived '" + paramsValue.GetType().Name + "'");
                            }

                            cmdLine = cmdLine.TrimEnd();
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
                switch (name)
                {
                    case "linker":
                        break;
                    default:
                        cmdLine += "\r\n" + getIncludesCmdLine(cmpModel, ((JArray)cParams["incDirs"]).Values<string>());
                        cmdLine += "\r\n" + getdefinesCmdLine(cmpModel, name, ((JArray)cParams["defines"]).Values<string>());
                        break;
                }

                if (cmpModel.ContainsKey("$default-tail"))
                {
                    foreach (var ele in ((JArray)cmpModel["$default-tail"]).Values<string>())
                        cmdLine += " " + ele;
                }

                cmdLines[name] = cmdLine;
            }
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

        public CmdInfo fromCFile(string fpath)
        {
            JObject ccModel = models["c"];
            JObject cParams = paramObj["c"];

            string fName = getUniqueName(Path.GetFileNameWithoutExtension(fpath));
            string outPath = outDir + Path.DirectorySeparatorChar + fName + ".o";

            string langOption = cParams.ContainsKey("language-c") ? cParams["language-c"].Value<string>() : null;
            string fileCmd = ccModel.ContainsKey("$fileCmd") ? ccModel["$fileCmd"].Value<string>() : null;
            string cmdLine = getCommandValue((JObject)ccModel["$language-c"], langOption).TrimStart()
                + cmdLines["c"] + " " + ccModel["$output"].Value<string>() + "\"" + outPath.Replace('\\', '/') + "\""
                + " " + (fileCmd ?? "") + "\"" + fpath.Replace('\\', '/') + "\"";

            if (toolchain == ToolChain.AC5)
            {
                string depPath = outDir + "/" + fName + ".v5.d";
                cmdLine += " --depend \"" + depPath.Replace('\\', '/') + "\"";
            }

            FileInfo paramFile = new FileInfo(outDir + Path.DirectorySeparatorChar + fName + ".__i");
            File.WriteAllText(paramFile.FullName, cmdLine, encoding);

            return new CmdInfo
            {
                commandLine = ccModel["$invokeCmd"].Value<string>() + "\"" + paramFile.FullName + "\"",
                exePath = binDir + Path.DirectorySeparatorChar + ccModel["$path"].Value<string>(),
                sourcePath = fpath,
                outPath = outPath
            };
        }

        public CmdInfo fromCppFile(string fpath)
        {
            JObject cppModel = models["cpp"];
            JObject cppParams = paramObj["cpp"];

            string fName = getUniqueName(Path.GetFileNameWithoutExtension(fpath));
            string outPath = outDir + Path.DirectorySeparatorChar + fName + ".o";

            string langOption = cppParams.ContainsKey("language-cpp") ? cppParams["language-cpp"].Value<string>() : null;
            string fileCmd = cppModel.ContainsKey("$fileCmd") ? cppModel["$fileCmd"].Value<string>() : null;
            string cmdLine = getCommandValue((JObject)cppModel["$language-cpp"], langOption).TrimStart()
                + cmdLines["cpp"] + " " + cppModel["$output"].Value<string>() + "\"" + outPath.Replace('\\', '/') + "\""
                + " " + (fileCmd ?? "") + "\"" + fpath.Replace('\\', '/') + "\"";

            if (toolchain == ToolChain.AC5)
            {
                string depPath = outDir + "/" + fName + ".v5.d";
                cmdLine += " --depend \"" + depPath.Replace('\\', '/') + "\"";
            }

            FileInfo paramFile = new FileInfo(outDir + Path.DirectorySeparatorChar + fName + ".__i");
            File.WriteAllText(paramFile.FullName, cmdLine, encoding);

            return new CmdInfo
            {
                commandLine = cppModel["$invokeCmd"].Value<string>() + "\"" + paramFile.FullName + "\"",
                exePath = binDir + Path.DirectorySeparatorChar + cppModel["$path"].Value<string>(),
                sourcePath = fpath,
                outPath = outPath
            };
        }

        public CmdInfo fromAsmFile(string fpath)
        {
            JObject asmModel = models["asm"];

            string fName = getUniqueName(Path.GetFileNameWithoutExtension(fpath));
            string outPath = outDir + Path.DirectorySeparatorChar + fName + ".o";

            string fileCmd = asmModel.ContainsKey("$fileCmd") ? asmModel["$fileCmd"].Value<string>() : null;
            string cmdLine = cmdLines["asm"].TrimStart() + " " + asmModel["$output"].Value<string>() + "\"" + outPath.Replace('\\', '/') + "\""
                + " " + (fileCmd ?? "") + "\"" + fpath.Replace('\\', '/') + "\"";

            if (toolchain == ToolChain.AC5)
            {
                string depPath = outDir + "/" + fName + ".v5.d";
                cmdLine += " --depend \"" + depPath.Replace('\\', '/') + "\"";
            }

            FileInfo paramFile = new FileInfo(outDir + Path.DirectorySeparatorChar + fName + "._ia");
            File.WriteAllText(paramFile.FullName, cmdLine, asmEncoding);

            return new CmdInfo
            {
                commandLine = asmModel["$invokeCmd"].Value<string>() + "\"" + paramFile.FullName + "\"",
                exePath = binDir + Path.DirectorySeparatorChar + asmModel["$path"].Value<string>(),
                sourcePath = fpath,
                outPath = outPath
            };
        }

        public CmdInfo genLinkCommand(string[] objList)
        {
            JObject linkerModel = models["linker"];

            string outName = getOutName();
            string outPath = outDir + Path.DirectorySeparatorChar + outName + ".axf";
            string mapPath = outDir + Path.DirectorySeparatorChar + outName + ".map";

            string cmdLine = cmdLines["linker"].TrimStart() + "\r\n";
            foreach (var objPath in objList)
                cmdLine += "\"" + objPath.Replace('\\', '/') + "\"\r\n";
            cmdLine += linkerModel["$link-map"]["command"].Value<string>() + "\"" + mapPath.Replace('\\', '/') + "\"\r\n";
            cmdLine += linkerModel["$output"].Value<string>() + "\"" + outPath.Replace('\\', '/') + "\"";

            FileInfo paramFile = new FileInfo(outDir + Path.DirectorySeparatorChar + outName + ".lnp");
            File.WriteAllText(paramFile.FullName, cmdLine, Encoding.Default);

            return new CmdInfo
            {
                exePath = binDir + Path.DirectorySeparatorChar + linkerModel["$path"].Value<string>(),
                commandLine = linkerModel["$invokeCmd"].Value<string>() + "\"" + paramFile.FullName + "\"",
                sourcePath = mapPath,
                outPath = outPath
            };
        }

        public string getOutName()
        {
            return parameters.ContainsKey("name") ? parameters["name"].Value<string>() : "main";
        }

        public string getToolPath(string name)
        {
            return binDir + Path.DirectorySeparatorChar + models[name]["$path"].Value<string>();
        }

        public void traverseCommands(CmdVisitor visitor)
        {
            foreach (var item in cmdLines)
                visitor(item.Key, item.Value);
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

            switch (type)
            {
                case "selectable":
                    if (value != null && ((JObject)option["command"]).ContainsKey((string)value))
                    {
                        return " " + option["command"][value].Value<string>();
                    }
                    else
                    {
                        return " " + option["command"]["false"].Value<string>();
                    }
                case "keyValue":
                    if (value != null && ((JObject)option["enum"]).ContainsKey((string)value))
                    {
                        return " " + option["command"].Value<string>() + option["enum"][value].Value<string>();
                    }
                    else
                    {
                        return " " + option["command"].Value<string>() + option["enum"]["default"].Value<string>();
                    }
                case "value":
                    return " " + option["command"].Value<string>() + ((string)value ?? "").Replace('\\', '/');
                case "list":
                    string res = option["command"].Value<string>();
                    foreach (var item in (IEnumerable<string>)value)
                    {
                        res += " " + item.Replace('\\', '/');
                    }
                    return " " + res.TrimStart();
                default:
                    return "";
            }
        }

        private string getIncludesCmdLine(JObject cmpModel, IEnumerable<string> incList)
        {
            string cmdLine = "";
            foreach (var inculdePath in incList)
            {
                cmdLine += " " + cmpModel["$includeHead"].Value<string>();
                cmdLine += "\"" + inculdePath.Replace('\\', '/') + "\"";
            }
            return cmdLine.TrimStart();
        }

        private string getdefinesCmdLine(JObject cmpModel, string modelName, IEnumerable<string> defList)
        {
            string cmdLine = "";
            foreach (var define in defList)
            {
                cmdLine += " " + cmpModel["$defineHead"].Value<string>();
                if (modelName == "asm" && asmCompilerName == "asm")
                {
                    if (Regex.IsMatch(define, @".+=.+"))
                    {
                        cmdLine += "\""
                            + Regex.Replace(Regex.Replace(define, "(?<==)\\s*\"\\s*(?<val>.[^\"]*)\\s*\"\\s*$", "${val}"), @"=", " SETA ")
                            + "\"";
                    }
                    else
                    {
                        cmdLine += "\"" + define + " SETA 1\"";
                    }
                }
                else
                {
                    cmdLine += Regex.Replace(define, @"(?<=.[^=]*=)(?<val>.*)$", "\"${val}\"");
                }
            }
            return cmdLine.TrimStart();
        }
    }

    class Program
    {
        static readonly int CODE_ERR = 1;
        static readonly int CODE_DONE = 0;
        static readonly int compileThreshold = 16;
        static readonly string incSearchName = "IncludeSearcher.exe";

        // file filters
        static readonly Regex cFileFilter = new Regex(@"\.c$", RegexOptions.IgnoreCase);
        static readonly Regex asmFileFilter = new Regex(@"\.(?:s|asm)$", RegexOptions.IgnoreCase);
        static readonly Regex libFileFilter = new Regex(@"\.lib$", RegexOptions.IgnoreCase);
        static readonly Regex cppFileFilter = new Regex(@"\.(?:cpp|cxx|cc|c\+\+)$", RegexOptions.IgnoreCase);

        static readonly List<string> cList = new List<string>();
        static readonly List<string> cppList = new List<string>();
        static readonly List<string> asmList = new List<string>();
        static readonly List<string> libList = new List<string>();

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
                reqThreadsNum = reqThreadsNum >= 8 ? 8 : 4;
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
            Dictionary<string, string> toolPaths = new Dictionary<string, string>();
            Dictionary<Regex, string> tasksEnv = new Dictionary<Regex, string>();

            try
            {
                Directory.CreateDirectory(outDir);
                CmdGenerator cmdGen = new CmdGenerator(compilerModel, paramsObj, binDir, outDir);

                // add env path for tasks
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                tasksEnv.Add(new Regex(@"\$\{TargetName\}", RegexOptions.IgnoreCase), cmdGen.getOutName());
                tasksEnv.Add(new Regex(@"\$\{ExeDir\}", RegexOptions.IgnoreCase), Path.GetDirectoryName(exePath));
                tasksEnv.Add(new Regex(@"\$\{BinDir\}", RegexOptions.IgnoreCase), binDir);
                tasksEnv.Add(new Regex(@"\$\{OutDir\}", RegexOptions.IgnoreCase), outDir);
                tasksEnv.Add(new Regex(@"\$\{CompileToolDir\}", RegexOptions.IgnoreCase),
                    Path.GetDirectoryName(cmdGen.getToolPath("linker")));

                if (checkMode(BuilderMode.DEBUG))
                {
                    log("\r\nARM_Builder v" + Assembly.GetExecutingAssembly().GetName().Version + "\r\n");

                    cmdGen.traverseCommands(delegate (string key, string cmdLine) {
                        warn("\r\n " + key + " tool's command: \r\n");
                        log(cmdLine);
                    });
                    return CODE_DONE;
                }

                Console.WriteLine();

                // run tasks before build
                if (runTasks("Run Tasks Before Build", "beforeBuildTasks", tasksEnv, true) != CODE_DONE)
                {
                    throw new Exception("Run Tasks Failed !, Stop Build !");
                }

                List<string> linkerFiles = new List<string>(32);
                int cCount = 0, asmCount = 0, cppCount = 0;

                toolPaths.Add("C/C++ Compiler", cmdGen.getToolPath("c"));
                toolPaths.Add("ASM Compiler", cmdGen.getToolPath("asm"));
                toolPaths.Add("ARM Linker", cmdGen.getToolPath("linker"));

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

                CmdGenerator.CmdInfo linkInfo = cmdGen.genLinkCommand(linkerFiles.ToArray());
                DateTime time = DateTime.Now;

                // start build
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

                // Check Compiler Tools
                foreach (var tool in toolPaths)
                {
                    if (!File.Exists(tool.Value))
                        throw new Exception("Not found " + tool.Key + " !, [path] : \"" + tool.Value + "\"");
                }

                log("\r\n");
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
                    }
                }
                else
                {
                    int part = commands.Count / reqThreadsNum;
                    reqThreadsNum = part < 4 ? 4 : reqThreadsNum;
                    doneWithLable(reqThreadsNum.ToString() + " threads\r\n", true, "Use Multi-Thread Mode");
                    CmdGenerator.CmdInfo[] cmds = new CmdGenerator.CmdInfo[commands.Count];
                    commands.Values.CopyTo(cmds, 0);
                    compileByMulThread(reqThreadsNum, cmds);
                }

                log("\r\n");
                infoWithLable("-------------------- Start link... --------------------\r\n");

                if (libList.Count > 0)
                {
                    foreach (var lib in libList)
                    {
                        log(" > Link Lib... " + Path.GetFileName(lib));
                    }
                    log("");
                }

                if (system(linkInfo.exePath + " " + linkInfo.commandLine) != CODE_DONE)
                {
                    throw new Exception("Link failed !");
                }

                // print more information
                if (File.Exists(linkInfo.sourcePath))
                {
                    try
                    {
                        Regex reg = new Regex(@"^\s*total.*size", RegexOptions.IgnoreCase);
                        foreach (var line in File.ReadAllLines(linkInfo.sourcePath))
                        {
                            if (reg.IsMatch(line))
                            {
                                log("\r\n" + line.Trim());
                            }
                        }
                    }
                    catch (Exception err)
                    {
                        warn("\r\nCan't read information from '.map' file !, " + err.Message);
                    }
                }

                log("\r\n");
                infoWithLable("-------------------- Start output hex... --------------------\r\n");

                string fromelfPath = Path.GetDirectoryName(linkInfo.exePath) + Path.DirectorySeparatorChar + "fromelf.exe";
                string hexPath = Path.GetDirectoryName(linkInfo.outPath) + Path.DirectorySeparatorChar
                    + Path.GetFileNameWithoutExtension(linkInfo.outPath) + ".hex";

                try
                {
                    if (!File.Exists(fromelfPath))
                        throw new Exception("Not found fromelf.exe !, [path] : \"" + fromelfPath + "\"");

                    string outputCmd = fromelfPath
                        + " --i32combined \"" + linkInfo.outPath.Replace('\\', '/') + "\""
                        + " --output \"" + hexPath.Replace('\\', '/') + "\"";

                    if (system(outputCmd) != CODE_DONE)
                        throw new Exception("Output hex failed !");

                    infoWithLable("Hex file path : \"" + hexPath + "\"");
                }
                catch (Exception err)
                {
                    warn("Output Hex file failed !, msg: " + err.Message);
                }

                // flush to database
                updateDatabase(commands.Keys);

                TimeSpan tSpan = DateTime.Now.Subtract(time);
                log("\r\n");
                doneWithLable("-------------------- Build successfully ! Elapsed time "
                    + string.Format("{0}:{1}:{2}", tSpan.Hours, tSpan.Minutes, tSpan.Seconds)
                    + " --------------------\r\n\r\n");
            }
            catch (Exception err)
            {
                log("");
                errorWithLable(err.Message + "\r\n");
                errorWithLable("Build failed !");
                log("");

                // dump error log
                string logFile = dumpPath + Path.DirectorySeparatorChar + "arm_builder.log";
                string txt = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "]\t";
                txt += err.Message + "\r\n" + err.StackTrace + "\r\n\r\n";
                File.AppendAllText(logFile, txt);

                // flush to database
                updateDatabase(commands.Keys);

                return CODE_ERR;
            }

            try
            {
                // run tasks after build success
                runTasks("Run Tasks After Build", "afterBuildTasks", tasksEnv);
            }
            catch (Exception err)
            {
                errorWithLable(err.Message + "\r\n");
            }

            return CODE_DONE;
        }

        //============================================

        struct TaskData
        {
            public ManualResetEvent _event;
            public int index;
            public int end;
        }

        static void compileByMulThread(int thrNum, CmdGenerator.CmdInfo[] cmds)
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
                        if (system(cmds[index].exePath + " " + cmds[index].commandLine) != CODE_DONE)
                        {
                            err = new Exception("Compilation failed at : \"" + cmds[index].sourcePath + "\"");
                            break;
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
                warn("\r\nupdate to database failed !");
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
                proc.StartInfo.Arguments = " -p \"" + paramsPath + "\" -d \"" + dumpPath + "\"";
                proc.StartInfo.CreateNoWindow = false;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.Start();

                string[] lines = Regex.Split(proc.StandardOutput.ReadToEnd(), @"\r\n|\n");
                proc.WaitForExit();
                proc.Close();

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
            bool isExist = false;
            //__UVISION_VERSION = "526"
            Regex reg = new Regex(@"^\s*__UVISION_VERSION");
            foreach (var item in ((JArray)paramsObj["defines"]).Values<string>())
            {
                if (reg.IsMatch(item))
                {
                    isExist = true;
                    break;
                }
            }
            if (!isExist)
            {
                ((JArray)paramsObj["defines"]).Add("__UVISION_VERSION=526");
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
