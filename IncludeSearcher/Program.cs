using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IncludeAnalyzer
{
    class Program
    {
        static readonly int CODE_ERR = 1;
        static readonly int CODE_DONE = 0;

        static readonly string dbName = "inc.db3";
        static readonly string tableName = "incList";
        static readonly string cacheName = "incCache";
        static readonly string dateFormat = "yyyy-MM-dd HH:mm:ss";

        static FileInfo[] sourceList;
        static string[] headerDirsList;
        static Dictionary<string, string> headersMap = new Dictionary<string, string>();
        static Dictionary<string, IEnumerable<string>> searchCache = new Dictionary<string, IEnumerable<string>>();

        enum Mode
        {
            Normal = 0,
            Flush,
            Update
        }

        enum FileStateFlag
        {
            New = 0,
            Changed,
            Stable
        }

        class FileState
        {
            public readonly FileStateFlag state;
            public readonly string path;

            public FileState(string path, FileStateFlag state)
            {
                this.path = path;
                this.state = state;
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

            // start
            SQLiteCommand command = new SQLiteCommand(
               "select lastWriteTime from " + tableName + " where path=@path;", conn);
            command.Parameters.Add(new SQLiteParameter("@path", DbType.String));

            Dictionary<int, FileState> changesCache = new Dictionary<int, FileState>();

            foreach (var srcFile in sourceList)
            {
                try
                {
                    IEnumerable<string> reqHeaders = searchHeader(headerDirsList, srcFile.FullName);

                    if (conn == null)
                    {
                        log("[File] [New] " + srcFile.FullName);

                        foreach (var reqF in reqHeaders)
                        {
                            logWithLabel("New", reqF);
                        }

                        log("[End] [New] " + srcFile.FullName);
                    }
                    else
                    {
                        FileStateFlag totalState = FileStateFlag.Stable;

                        // check source file
                        int hashCode = srcFile.FullName.GetHashCode();
                        if (!changesCache.ContainsKey(hashCode))
                        {
                            if (mode != Mode.Update)
                            {
                                checkDiff(command, srcFile.FullName, delegate (FileStateFlag state) {
                                    log("[File-" + Enum.GetName(typeof(FileStateFlag), state) + "]" + srcFile.FullName);
                                    totalState = state;
                                    changesCache.Add(hashCode, new FileState(srcFile.FullName, state));
                                });
                            }
                            else
                            {
                                totalState = FileStateFlag.Stable;
                                changesCache.Add(hashCode, new FileState(srcFile.FullName, FileStateFlag.New));
                            }
                        }
                        else
                        {
                            totalState = changesCache[hashCode].state;
                        }

                        // check header file if source not changed
                        if (totalState == FileStateFlag.Stable)
                        {
                            foreach (var reqF in reqHeaders)
                            {
                                hashCode = reqF.GetHashCode();
                                if (!changesCache.ContainsKey(hashCode))
                                {
                                    if (mode != Mode.Update)
                                    {
                                        checkDiff(command, reqF, delegate (FileStateFlag state) {
                                            if (state != FileStateFlag.Stable)
                                            {
                                                totalState = FileStateFlag.Changed;
                                                //logWithLabel(Enum.GetName(typeof(FileStateFlag), state), reqF);
                                            }
                                            changesCache.Add(hashCode, new FileState(reqF, state));
                                        });
                                    }
                                    else
                                    {
                                        totalState = FileStateFlag.New;
                                        changesCache.Add(hashCode, new FileState(reqF, FileStateFlag.New));
                                    }
                                }
                                else
                                {
                                    FileStateFlag state = changesCache[hashCode].state;
                                    if (state != FileStateFlag.Stable)
                                    {
                                        //logWithLabel(Enum.GetName(typeof(FileStateFlag), state), reqF);
                                        totalState = state;
                                    }
                                }
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
            SQLiteCommand insertCmd = new SQLiteCommand(
                "insert or replace into " + cacheName + " values(@hash,@path,@time);", conn);
            insertCmd.Parameters.Add(new SQLiteParameter("@hash", DbType.String));
            insertCmd.Parameters.Add(new SQLiteParameter("@path", DbType.String));
            insertCmd.Parameters.Add(new SQLiteParameter("@time", DbType.String));

            var trans = conn.BeginTransaction();

            foreach (var fInfo in changesCache.Values)
            {
                if (fInfo.state != FileStateFlag.Stable)
                {
                    insertCmd.Parameters[0].Value = getMD5(fInfo.path);
                    insertCmd.Parameters[1].Value = fInfo.path;
                    insertCmd.Parameters[2].Value = File.GetLastWriteTime(fInfo.path).ToString(dateFormat);
                    insertCmd.ExecuteNonQuery();
                }
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

            if (mode == Mode.Update)
            {
                return flushDB(conn);
            }

            if (conn != null)
            {
                conn.Close();
            }

            return CODE_DONE;
        }

        //===================================

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
                                        string path = hFile.ToLower();
                                        string name = Path.GetFileName(path);
                                        if (!headersMap.ContainsKey(name))
                                        {
                                            headersMap.Add(name, path);
                                        }
                                    }
                                }
                                dirList.Add(_line.ToLower());
                            }
                            break;
                        case "[sourceList]":
                            if (File.Exists(_line))
                            {
                                sList.Add(new FileInfo(_line.ToLower()));
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
            int start = -1, end = -1, index = 0;

            foreach (var c in str)
            {
                if (start == -1 && (c == '"' || c == '<'))
                {
                    start = index + 1;
                }
                else if (end == -1 && (c == '"' || c == '>'))
                {
                    end = index - 1;
                    break;
                }
                index++;
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

            // search source file
            foreach (var item in searchFileIncludes(headerDirs, path))
            {
                fStack.Push(item);
                resList.Add(item);
            }

            while (fStack.Count > 0)
            {
                string headerPath = fStack.Pop();

                if (searchCache.ContainsKey(headerPath))
                {
                    foreach (var item in searchCache[headerPath])
                    {
                        if (resList.Add(item))
                        {
                            fStack.Push(item);
                        }
                    }
                }
                else
                {
                    var resultSet = searchFileIncludes(headerDirs, headerPath);
                    searchCache.Add(headerPath, resultSet);

                    foreach (var item in resultSet)
                    {
                        if (resList.Add(item))
                        {
                            fStack.Push(item);
                        }
                    }
                }
            }

            return resList;
        }

        static string[] searchFileIncludes(string[] headerDirs, string path)
        {
            string[] lines = File.ReadAllLines(path);
            HashSet<string> reqHeaders = new HashSet<string>();

            foreach (var _line in lines)
            {
                string line = _line.TrimStart();

                if (line.StartsWith("#include"))
                {
                    var matcher = matchInc(line);
                    if (matcher.Success)
                    {
                        string fName = matcher.Value.Replace('/', '\\').ToLower();
                        string res = null;

                        if (fName.StartsWith("."))
                        {
                            res = Path.GetDirectoryName(path) + Path.DirectorySeparatorChar + fName;
                            if (!File.Exists(res))
                                res = null;
                        }
                        else if (fName.IndexOf("\\") >= 0)
                        {
                            foreach (var dir in headerDirs)
                            {
                                res = dir + Path.DirectorySeparatorChar + fName;
                                if (File.Exists(res))
                                {
                                    break;
                                }
                                res = null;
                            }
                        }
                        else
                        {
                            if (headersMap.ContainsKey(fName))
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

        static void checkDiff(SQLiteCommand command, string fPath, CheckDiffCallBk CallBk)
        {
            command.Parameters[0].Value = fPath;
            var reader = command.ExecuteReader();
            try
            {
                if (reader.Read())
                {
                    var preTime = DateTime.Parse(reader.GetString(0));
                    var cTime = File.GetLastWriteTime(fPath);
                    cTime = DateTime.Parse(cTime.ToString(dateFormat));

                    if (DateTime.Compare(cTime, preTime) > 0)
                    {
                        CallBk(FileStateFlag.Changed);
                    }
                    else
                    {
                        CallBk(FileStateFlag.Stable);
                    }
                }
                else
                {
                    CallBk(FileStateFlag.New);
                }

                reader.Close();
            }
            catch (Exception e)
            {
                CallBk(FileStateFlag.New);
                error(e.Message);
                reader.Close();
            }
        }

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

                // create incList
                SQLiteCommand command = new SQLiteCommand(conn)
                {
                    CommandText = "CREATE TABLE IF NOT EXISTS " + tableName + "(" +
                    "uid VARCHAR(256) PRIMARY KEY NOT NULL" +
                    ",path TEXT NOT NULL" +
                    ",lastWriteTime VARCHAR(64) NOT NULL" +
                    ");"
                };
                command.ExecuteNonQuery();

                // create cache
                command.CommandText = "CREATE TABLE IF NOT EXISTS " + cacheName + "(" +
                    "uid VARCHAR(256) PRIMARY KEY NOT NULL" +
                    ",path TEXT NOT NULL" +
                    ",lastWriteTime VARCHAR(64) NOT NULL" +
                    ");";
                command.ExecuteNonQuery();

                command.Dispose();

                return conn;
            }
            catch (Exception e)
            {
                error(e.Message);
            }

            return null;
        }

        static int flushDB(SQLiteConnection conn)
        {
            try
            {
                // insert or replace into <tableName> (path,lastWriteTime) values()
                SQLiteCommand command = new SQLiteCommand(
                    "insert or replace into " + tableName + " select * from " + cacheName + ";", conn);
                command.ExecuteNonQuery();

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

        static string getMD5(string str)
        {
            MD5 md5 = MD5.Create();
            return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(str)))
                .Replace("-", "").ToLower();
        }

        //====================================

        static void log(string txt)
        {
            Console.WriteLine(txt);
        }

        static void logWithLabel(string label, string txt)
        {
            Console.WriteLine("[" + label + "] " + txt);
        }

        static void error(string txt)
        {
            Console.Error.WriteLine("[ERROR]" + txt);
        }
    }
}
