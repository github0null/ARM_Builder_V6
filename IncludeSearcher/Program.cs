using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Text.RegularExpressions;

namespace IncludeSearcher
{
    class Program
    {
        static readonly int CODE_ERR = 1;
        static readonly int CODE_DONE = 0;

        static readonly string dbName = ConfigurationManager.AppSettings["dbName"] ?? "eide.dat";
        static readonly string dateFormat = "yyyy-MM-dd HH:mm:ss";

        static readonly Dictionary<string, string> tables = new Dictionary<string, string>() {
            { "main", "files" },
            { "cache", "files_cache" }
        };

        static FileInfo[] sourceList;
        static string[] headerDirsList;
        static FileMap headersMap = new FileMap(true);
        static Dictionary<string, DBData> searchCache = new Dictionary<string, DBData>();

        enum Mode
        {
            Normal = 0,
            Flush,
            Update
        }

        enum FileStateFlag
        {
            Stable = 0,
            Changed,
            New
        }

        class DBData
        {
            public string path;
            public string lastTime;
            public string[] depList;
            public FileStateFlag state;

            public DBData()
            {
                state = FileStateFlag.Stable;
            }
        }

        class FileMap
        {
            private Dictionary<string, string> map = new Dictionary<string, string>();
            private bool ignoreCase;

            public FileMap(bool ignoreCase)
            {
                this.ignoreCase = ignoreCase;
            }

            public string this[string name]
            {
                get
                {
                    return ignoreCase ? map[name.ToLower()] : map[name];
                }
                set
                {
                    map[ignoreCase ? name.ToLower() : name] = value;
                }
            }

            public bool include(string fileName)
            {
                return map.ContainsKey(ignoreCase ? fileName.ToLower() : fileName);
            }

            public void add(string filePath)
            {
                string path = ignoreCase ? filePath.ToLower() : filePath;
                string name = Path.GetFileName(path);
                if (!map.ContainsKey(name))
                    map.Add(name, path);
            }
        }

        /**
         * paramter format
         *      -p <paramsFile.txt>
         *      -d <dumpDir>
         *      -m <mode>
         */
        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                error("Params too less");
                return CODE_ERR;
            }

            Dictionary<string, string> pMap = new Dictionary<string, string>();

            for (int i = 0; i < args.Length; i += 2)
            {
                if (!pMap.ContainsKey(args[i]))
                    pMap.Add(args[i], args[i + 1]);
            }

            FileInfo paramFile = null;
            SQLiteConnection conn = null;
            Mode mode = Mode.Normal;

            if (pMap.ContainsKey("-m"))
            {
                try
                {
                    mode = (Mode)Enum.Parse(typeof(Mode), pMap["-m"], true);
                }
                catch (Exception)
                {
                    error("Invalid option '" + pMap["-m"] + "'");
                }
            }

            try
            {
                if (pMap.ContainsKey("-p"))
                {
                    paramFile = new FileInfo(pMap["-p"]);
                    if (!paramFile.Exists)
                    {
                        paramFile = null;
                        throw new Exception("File not found !, [path] : \"" + paramFile.FullName + "\"");
                    }
                }

                if (pMap.ContainsKey("-d"))
                {
                    Directory.CreateDirectory(pMap["-d"]);
                    DirectoryInfo dir = new DirectoryInfo(pMap["-d"]);
                    if (dir.Exists)
                    {
                        conn = initDatabase(dir);
                    }
                }
            }
            catch (Exception err)
            {
                error(err.Message);
            }

            switch (mode)
            {
                case Mode.Normal:
                    if (paramFile == null)
                    {
                        error("Invalid params file !");
                        return CODE_ERR;
                    }
                    break;
                case Mode.Flush:
                    if (conn == null)
                    {
                        error("Connect failed !");
                        return CODE_ERR;
                    }
                    return flushDB(conn);
                case Mode.Update:
                    if (paramFile == null || conn == null)
                    {
                        error("Invalid params file !, Connect failed !");
                        return CODE_ERR;
                    }
                    break;
                default:
                    break;
            }

            // parse params
            prepareParams(paramFile.FullName);

            // get all from database
            SQLiteCommand selectCmd = new SQLiteCommand("select * from " + tables["main"] + ";", conn);
            SQLiteDataReader reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                string deps = reader.GetString(2).Trim();

                DBData dat = new DBData()
                {
                    path = reader.GetString(0),
                    lastTime = reader.GetString(1),
                    depList = string.IsNullOrEmpty(deps) ? new string[0] : deps.Split(';')
                };

                if (File.Exists(dat.path))
                {
                    dat.state = getFileState(dat.path, DateTime.Parse(dat.lastTime), out DateTime newTime);

                    // update after header changed
                    if (dat.state != FileStateFlag.Stable)
                    {
                        dat.lastTime = newTime.ToString(dateFormat);
                        dat.depList = searchFileIncludes(headerDirsList, dat.path) ?? new string[0];
                    }

                    if (searchCache.ContainsKey(dat.path))
                    {
                        searchCache[dat.path] = dat;
                    }
                    else
                    {
                        searchCache.Add(dat.path, dat);
                    }
                }
            }
            reader.Close();

            //-------------------------------------------------------------------

            foreach (var srcFile in sourceList)
            {
                try
                {
                    IEnumerable<string> reqHeaders = searchHeader(headerDirsList, srcFile.FullName);

                    if (conn == null)
                    {
                        log("[File] [New] " + srcFile.FullName);

                        foreach (string reqF in reqHeaders)
                        {
                            logWithLabel("New", reqF);
                        }

                        log("[End] [New] " + srcFile.FullName);
                    }
                    else
                    {
                        FileStateFlag totalState = FileStateFlag.Stable;

                        // check source file
                        if (searchCache.ContainsKey(srcFile.FullName))
                        {
                            FileStateFlag state = searchCache[srcFile.FullName].state;
                            log("[File-" + Enum.GetName(typeof(FileStateFlag), state) + "]" + srcFile.FullName);
                            totalState = state;
                        }
                        else
                        {
                            error("not found source in cache !");
                            log("[File-" + Enum.GetName(typeof(FileStateFlag), FileStateFlag.New) + "]" + srcFile.FullName);
                            totalState = FileStateFlag.New;
                        }

                        // check header file if source not changed
                        if (totalState == FileStateFlag.Stable)
                        {
                            foreach (var reqF in reqHeaders)
                            {
                                if (searchCache.ContainsKey(reqF))
                                {
                                    FileStateFlag state = searchCache[reqF].state;
                                    if (state != FileStateFlag.Stable)
                                    {
                                        totalState = FileStateFlag.Changed;
                                        break;
                                    }
                                }
                                /*else
                                {
                                    error("not found header in cache !");
                                   totalState = FileStateFlag.New;
                                    break;
                                }*/
                            }
                        }

                        log("[End-" + Enum.GetName(typeof(FileStateFlag), totalState) + "]" + srcFile.FullName);
                    }
                }
                catch (Exception err)
                {
                    error(err.Message);
                }

                Console.WriteLine("");
            }

            // update to cache
            // command: insert or replace into <tableName> (path,lastWriteTime) values()

            // if mode == update, update to 'files' table, else update to 'files_cache' table
            string targetTableName = mode == Mode.Update ? tables["main"] : tables["cache"];

            SQLiteCommand insertCmd = new SQLiteCommand(
                "insert or replace into " + targetTableName + " values(@path,@time,@deps);", conn);
            insertCmd.Parameters.Add(new SQLiteParameter("@path", DbType.String));
            insertCmd.Parameters.Add(new SQLiteParameter("@time", DbType.String));
            insertCmd.Parameters.Add(new SQLiteParameter("@deps", DbType.String));

            var trans = conn.BeginTransaction();

            foreach (var fInfo in searchCache.Values)
            {
                insertCmd.Parameters[0].Value = fInfo.path;
                insertCmd.Parameters[1].Value = fInfo.lastTime;
                insertCmd.Parameters[2].Value = string.Join(";", fInfo.depList);
                insertCmd.ExecuteNonQuery();
            }

            try
            {
                trans.Commit();
                trans.Dispose();
            }
            catch (Exception e)
            {
                error(e.Message);
                trans.Rollback();
            }

            if (conn != null)
            {
                conn.Close();
            }

            return CODE_DONE;
        }

        //=====================================

        static SQLiteConnection initDatabase(DirectoryInfo dir)
        {
            try
            {
                string dbPath = dir.FullName + Path.DirectorySeparatorChar + dbName;

                if (!File.Exists(dbPath))
                {
                    SQLiteConnection.CreateFile(dbPath);
                }

                SQLiteConnectionStringBuilder conStr = new SQLiteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    FailIfMissing = true
                };

                SQLiteConnection conn = new SQLiteConnection(conStr.ConnectionString);
                conn.Open();

                try
                {
                    // check SQLite db is valid
                    (new SQLiteCommand("select sqlite_version() AS 'Version';", conn)).ExecuteNonQuery();
                }
                catch (SQLiteException e)
                {
                    if (e.ResultCode == SQLiteErrorCode.NotADb)
                    {
                        conn.Close();
                        conn.Dispose();
                        error(e.Message);
                        File.Delete(dbPath);
                        SQLiteConnection.CreateFile(dbPath);
                        conn = new SQLiteConnection(conStr.ConnectionString);
                        conn.Open();
                    }
                }

                SQLiteCommand command = new SQLiteCommand(conn);

                // create all tables
                foreach (var tableName in tables.Values)
                {
                    command.CommandText = "CREATE TABLE IF NOT EXISTS " + tableName + "(" +
                        "path TEXT PRIMARY KEY NOT NULL" +
                        ",lastWriteTime VARCHAR(64) NOT NULL" +
                        ",depends TEXT NOT NULL" +
                        ");";
                    command.ExecuteNonQuery();
                }

                command.Dispose();
                return conn;
            }
            catch (Exception e)
            {
                error(e.Message);
            }

            return null;
        }

        static void prepareParams(string paramsPath)
        {
            HashSet<string> dirList = new HashSet<string>();
            List<FileInfo> sList = new List<FileInfo>(8);

            string tag = "";
            Regex headerFilter = new Regex(@"\.(?:h|hpp|hxx)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            foreach (var line in File.ReadAllLines(paramsPath))
            {
                string _line = line.Trim();
                if (_line.StartsWith("["))
                {
                    tag = _line;
                }
                else
                {
                    switch (tag)
                    {
                        case "[includes]":
                            if (Directory.Exists(_line))
                            {
                                foreach (var hFile in Directory.GetFiles(_line))
                                {
                                    if (headerFilter.IsMatch(hFile))
                                    {
                                        headersMap.add(hFile);
                                    }
                                }
                                dirList.Add(_line);
                            }
                            break;
                        case "[sourceList]":
                            if (File.Exists(_line))
                            {
                                sList.Add(new FileInfo(_line));
                            }
                            break;
                        default:
                            break;
                    }
                }
            }

            headerDirsList = new string[dirList.Count];
            dirList.CopyTo(headerDirsList);

            sourceList = sList.ToArray();
        }

        class IncludeMatcher
        {
            public readonly bool Success;
            public string Value;

            public IncludeMatcher(bool s)
            {
                Success = s;
                Value = null;
            }
        }

        static IncludeMatcher matchInc(string str)
        {
            int start = -1, end = -1;

            for (int index = 0; index < str.Length; index++)
            {
                char c = str[index];

                if (start == -1 && (c == '"' || c == '<'))
                {
                    start = index + 1;
                }
                else if (end == -1 && (c == '"' || c == '>'))
                {
                    end = index - 1;
                    break;
                }
            }

            if (start != -1 && end > start)
            {
                return new IncludeMatcher(true)
                {
                    Value = str.Substring(start, end - start + 1)
                };
            }

            return new IncludeMatcher(false);
        }

        delegate void CheckDiffCallBk(FileStateFlag status);

        static IEnumerable<string> searchHeader(string[] headerDirs, string path)
        {
            HashSet<string> resList = new HashSet<string>();
            Stack<string> fStack = new Stack<string>(64);
            DBData srcData;

            // search source file
            if (searchCache.ContainsKey(path))
            {
                srcData = searchCache[path];
            }
            else
            {
                srcData = new DBData()
                {
                    path = path,
                    lastTime = File.GetLastWriteTime(path).ToString(dateFormat),
                    state = FileStateFlag.New,
                    depList = searchFileIncludes(headerDirs, path) ?? new string[0]
                };
                searchCache.Add(path, srcData);
            }

            foreach (var item in srcData.depList)
            {
                fStack.Push(item);
                resList.Add(item);
            }

            while (fStack.Count > 0)
            {
                string headerPath = fStack.Pop();

                if (searchCache.ContainsKey(headerPath))
                {
                    foreach (var item in searchCache[headerPath].depList)
                    {
                        if (resList.Add(item))
                        {
                            fStack.Push(item);
                        }
                    }
                }
                else
                {
                    string[] resultSet = searchFileIncludes(headerDirs, headerPath);

                    if (resultSet != null)
                    {
                        searchCache.Add(headerPath, new DBData
                        {
                            path = headerPath,
                            lastTime = File.GetLastWriteTime(headerPath).ToString(dateFormat),
                            depList = resultSet,
                            state = FileStateFlag.New
                        });

                        foreach (var item in resultSet)
                        {
                            if (resList.Add(item))
                            {
                                fStack.Push(item);
                            }
                        }
                    }
                }
            }

            return resList;
        }

        static string[] searchFileIncludes(string[] headerDirs, string path)
        {
            string[] lines;

            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception)
            {
                return null;
            }

            HashSet<string> reqHeaders = new HashSet<string>();
            foreach (var _line in lines)
            {
                string line = _line.TrimStart();

                if (line.StartsWith("#include"))
                {
                    var matcher = matchInc(line);
                    if (matcher.Success)
                    {
                        string fName = matcher.Value.Replace('/', Path.DirectorySeparatorChar);
                        string res = null;

                        if (fName.StartsWith("."))
                        {
                            res = Path.GetDirectoryName(path) + Path.DirectorySeparatorChar + fName;
                            if (!File.Exists(res))
                                res = null;
                        }
                        else if (fName.IndexOf(Path.DirectorySeparatorChar) != -1)
                        {
                            foreach (var dir in headerDirs) // search in include folders
                            {
                                if (File.Exists(dir + Path.DirectorySeparatorChar + fName))
                                {
                                    res = dir + Path.DirectorySeparatorChar + fName;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            if (headersMap.include(fName)) // search in header maps
                            {
                                res = headersMap[fName];
                            }
                        }

                        if (res != null)
                        {
                            reqHeaders.Add(res);
                        }
                    }
                }
            }

            string[] result = new string[reqHeaders.Count];
            reqHeaders.CopyTo(result);

            return result;
        }

        static FileStateFlag getFileState(string path, DateTime prevTime, out DateTime cTime)
        {
            cTime = File.GetLastWriteTime(path);
            cTime = DateTime.Parse(cTime.ToString(dateFormat));
            return DateTime.Compare(cTime, prevTime) > 0 ? FileStateFlag.Changed : FileStateFlag.Stable;
        }

        static int flushDB(SQLiteConnection conn)
        {
            try
            {
                // insert or replace into <tableName> (path,lastWriteTime) values()
                SQLiteCommand command = new SQLiteCommand(conn);

                string tableName = tables["main"];
                string cacheName = tables["cache"];

                // delete all datas
                command.CommandText = "delete from " + tableName + ";";
                command.ExecuteNonQuery();
                // update datas from cache
                command.CommandText = "insert or replace into " + tableName + " select * from " + cacheName + ";";
                command.ExecuteNonQuery();
                // delete all cache
                command.CommandText = "delete from " + cacheName + ";";
                command.ExecuteNonQuery();

                command.Dispose();
            }
            catch (Exception err)
            {
                error(err.Message);
                conn.Close();
                return CODE_ERR;
            }

            conn.Close();

            return CODE_DONE;
        }

        //======================================

        static void log(string txt)
        {
            Console.WriteLine(txt);
        }

        static void logWithLabel(string label, string txt)
        {
            Console.WriteLine("[" + label + "] " + txt);
        }

        static bool _isFirstLine = true;
        static void error(string txt)
        {
            if (_isFirstLine)
            {
                Console.Error.WriteLine();
                _isFirstLine = false;
            }

            Console.Error.WriteLine("[Warning] " + txt);
        }
    }
}
