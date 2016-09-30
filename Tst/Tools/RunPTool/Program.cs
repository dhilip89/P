﻿using System.Data.SqlTypes;
using Microsoft.Build.Tasks;

namespace RunPTool
{
    using System;
    using System.IO;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using CheckP;
    using System.Diagnostics;

    class Program
    {
        private const int FailCode = 1;
        private const string ConfigFileName = "testconfig.txt";
        private const string ConfigFilePattern = "testconfig*.txt";
        //Generated for viewing failed subtests: 
        private const string FailedTestsFile = "failed-tests.txt";
        //Generated to use for resetting acceptors for failed tests,
        //as opposed to subtests as in "failed-tests.txt";
        //paths to subtests are not allowed in the test dir list
        //passed as a parameter to the regression tool
        private const string FailedTestsToResetFile = "failed-tests-for-reset.txt";
        private const string DisplayDiffsFile = "display-diffs.bat";
        private const string DiffTool = "kdiff3";

        bool reset;
        bool cooperative; // for testing cooperative multitasking.
        bool parallel; // whether to run multiple tests in parallel.
        int maxThreads; 

        //set according to the name of the parent directory for "testconfig.txt":
        string testFilePath;
        string execsToRun;
        string configuration = "Debug";
        string platform = "x86";

        static string testRoot; // the Tst source directory
        static string testOutput; // the Tst/TestResult directory (to separate the temporary files from our source directory).

        bool ParseCommandLine(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg[0] == '/' || arg[0] == '-')
                {
                    arg = (arg.Substring(1).ToLowerInvariant());
                    string option = null;
                    int sep = arg.IndexOfAny(new char[] { '=', ':'});
                    if (sep>0)
                    {
                        option = arg.Substring(sep + 1).Trim();
                        arg = arg.Substring(0, sep).Trim();
                    }
                    switch (arg)
                    {
                        case "run":
                            execsToRun = arg;
                            break;
                        case "platform":
                            if (option != "x86" && option != "x64")
                            {
                                Console.WriteLine("### Unrecognized platform '{0}', expecting /platform=x86 or /platform=x64", option);
                                return false;
                            }
                            platform = option;
                            break;
                        case "configuration":
                            if (option != "debug" && option != "release")
                            {
                                Console.WriteLine("### Unrecognized configuration '{0}', expecting /configuration=debug or /configuration=release", option);
                                return false;
                            }
                            configuration = option;
                            break;
                        case "reset":
                            reset = true;
                            break;
                        case "cooperative":
                            cooperative = true;
                            break;
                        case "parallel":
                            parallel = true;
                            if (!string.IsNullOrEmpty(option))
                            {
                                bool validValue = true;
                                int j;
                                if (int.TryParse(option, out j))
                                {
                                    validValue = (j > 0);
                                    maxThreads = j;
                                }
                                else
                                {
                                    validValue = false;
                                }
                                if (!validValue)
                                {
                                    Console.WriteLine("### /parallel specified invalid number of thread '{0}', for example /parallel:4 to set 4 threads as the maximum", option);
                                    return false;
                                }
                            }
                            break;
                        default:
                            Console.WriteLine("### Unrecognized option: " + arg);
                            return false;
                    }
                }

                else if (testFilePath == null)
                {
                    testFilePath = arg;
                }
                else
                {
                    Console.WriteLine("### Too many arguments");
                    return false;
                }
            }

            // we see diminishing returns if we use too many threads to the point of it becoming slower which is why we only use 
            // a maximum of Environment.ProcessorCount / 2.
            int upperLimit = Environment.ProcessorCount / 2;

            if (!Environment.Is64BitProcess || platform == "x86")
            {
                // a 32 bit process has 2 gig limit, and with each p compile consuming up to peak of 500mb we can only run about 4 threads safely.
                upperLimit = Math.Min(4, upperLimit);
            }
            
            this.maxThreads = Math.Min(this.maxThreads, upperLimit);
            if (this.maxThreads == 0) this.maxThreads = 1;

            return true;
        }

        static void PrintUsage()
        {
            Console.WriteLine("USAGE: RunPTool.exe  [options] [file with test dirs]");
            Console.WriteLine("Options:");
            Console.WriteLine("    /reset - remove old acceptor file, useful for generating new test baselines");
            Console.WriteLine("    /cooperative - enable testing of cooperative multitasking");
            Console.WriteLine("    /platform=[x64|x64] - specify the platform to test (default 'x86')");
            Console.WriteLine("    /configuration=[debug|release] - specify the configuration to test (default 'debug')");
            Console.WriteLine("    /runPc - do the compile step only");
            Console.WriteLine("    /runPrt - run the compiled state machine using PrtTester");
            Console.WriteLine("    /runZing - run zinger on the compiled output");
            Console.WriteLine("    /runAll (default)");
        }

        static void Main(string[] args)
        {
            Program p = new Program();
            if (!p.ParseCommandLine(args))
            {
                PrintUsage();
                return;
            }

            Stopwatch timer = new Stopwatch();
            timer.Start();

            p.Run();

            timer.Stop();
            Console.WriteLine("Test ran in {0} seconds, or {1}", timer.Elapsed.TotalSeconds, timer.Elapsed.ToString());
        }

        void Run()
        {
            try
            {
                // this should be the script directory.
                testRoot = FindTestRoot();

                if (execsToRun == null)
                {
                    execsToRun = "/runAll";
                }

                // we will copy test structure here so that all the temporary files we create are contained in one place rather
                // than poluting our source tree with all that.
                testOutput = Path.Combine(testRoot, "TestResult");

                //tstDir is where testP.bat is located
                List<DirectoryInfo> activeDirs;
                if (testFilePath == null)
                {
                    Console.WriteLine("Warning: no test directories file provided; running all tests under {0}", testRoot);
                    activeDirs = new List<DirectoryInfo>();
                    activeDirs.Add(new DirectoryInfo(testRoot));
                }
                else
                {
                    activeDirs = ExtractActiveDirsFromFile(testFilePath, new DirectoryInfo(testRoot));
                    if (activeDirs == null)
                    {
                        WriteError("Failed to run tests: directory name(s) in the test directories file are in a wrong format");
                        Environment.ExitCode = FailCode;
                        return;
                    }
                    if (activeDirs.Count == 0)
                    {
                        WriteError("Failed to run tests: no tests in the test directories file");
                        Environment.ExitCode = FailCode;
                        return;
                    }
                    //Type-check list of test dirs:
                    //Check that test dirs do not contain paths to Pc/Zing/Prt:
                    bool result = CheckTopPaths(activeDirs);
                    if (!result)
                    {
                        return;
                    }
                }

                bool isInTestResultDir = false;

                foreach (DirectoryInfo di in activeDirs)
                {
                    if (!di.Exists)
                    {
                        WriteError("Failed to run tests: directory '{0}' does not exist", di.FullName);
                        Environment.ExitCode = FailCode;
                        return;
                    }
                    if (di.FullName.StartsWith(testOutput, StringComparison.OrdinalIgnoreCase))
                    {
                        isInTestResultDir = true;
                    }
                }

                if (!isInTestResultDir)
                {
                    if (Directory.Exists(testOutput))
                    {
                        // wipe the whole subtree, so we start with a clean state. !!
                        Directory.Delete(testOutput, true);
                    }
                }

                // make sure test output directory exists.
                Directory.CreateDirectory(testOutput);
                Directory.SetCurrentDirectory(testOutput);

                // expand the list of test directories by walking the entire subtree looking for test folders
                // according to the expected pattern of 'Pc', 'Prt', and 'Zing' folders that contain testconfig.txt files.
                List<DirectoryInfo> allTestDirs = new List<DirectoryInfo>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                visited.Add(testOutput); // don't drill into this folder.
                EnumerateDirs(activeDirs, allTestDirs, visited);
                
                Console.WriteLine("Running tests");
                StreamWriter failedTestsWriter = null;
                StreamWriter failedTestsToResetWriter = null;
                StreamWriter tempWriter = null;
                StreamReader tempReader = null;
                StreamWriter displayDiffsWriter = null;
                //If reset = false, replace old "failed-tests.txt" and "display-diffs.bat" with newly created files:
                if (!reset)
                {
                    if (!OpenSummaryStreamWriter(FailedTestsFile, out failedTestsWriter))
                    {
                        throw new Exception("Cannot open failed-tests.txt for writing");
                    }
                    if (!OpenSummaryStreamWriter(FailedTestsToResetFile, out failedTestsToResetWriter))
                    {
                        throw new Exception("Cannot open failed-tests-to-reset.txt for writing");
                    }
                    if (!OpenSummaryStreamWriter("tempReset.txt", out tempWriter))
                    {
                        throw new Exception("Cannot open tempReset.txt for writing");
                    }
                    if (!OpenSummaryStreamWriter(DisplayDiffsFile, out displayDiffsWriter))
                    {
                        throw new Exception("Cannot open display-diffs.bat for writing");
                    }

                }

                string zingExe = string.Format(@"..\Bld\Drops\{0}\{1}\Binaries\zinger.exe", configuration, platform);
                string zingFilePath = Path.GetFullPath(Path.Combine(testRoot, zingExe));
                if (!File.Exists(zingFilePath))
                {
                    WriteError("ERROR in Test: zinger.exe not find in {0}", zingFilePath);
                    WriteError(@"Please run ~\Bld\build.bat");
                    return;
                }

                ThreadSafeOutput reporter = new RunPTool.Program.ThreadSafeOutput(failedTestsWriter, tempWriter, displayDiffsWriter);

                int threads = parallel ? maxThreads : 1;
                // now we can parallelize these test directories doing up to Environment.ProcessorCount - 1 jobs at once.
                Parallel.ForEach(allTestDirs, new ParallelOptions()
                {
                    MaxDegreeOfParallelism = threads
                },
                (dir) =>
                {
                    Test(dir, zingFilePath, reporter);
                });

                int testCount = reporter.TestCount;
                int failCount = reporter.FailCount;
                Console.WriteLine();
                Console.WriteLine("Total tests: {0}, Passed tests: {1}, Failed tests: {2}", testCount, testCount - failCount, failCount);

                if (failCount > 0)
                {
                    tempWriter.Close();

                    //open the reader (from the same file):
                    OpenSummaryStreamReader("tempReset.txt", out tempReader);

                    RemoveDupTests(failedTestsToResetWriter, tempReader);

                    failedTestsWriter.Close();
                    failedTestsToResetWriter.Close();

                    tempReader.Close();

                    SafeDelete(Path.Combine(testOutput, "tempReset.txt"));

                    displayDiffsWriter.Close();

                    Environment.ExitCode = FailCode;
                    Console.WriteLine("Test output written to: " + testOutput);
                    Console.WriteLine("List of all failed subtests: failed-tests.txt");
                    Console.WriteLine("List of all failed tests (to use for reset): failed-tests-for-reset.txt");
                    Console.WriteLine("To run kdiff3 on outputs for all failed tests: run display-diffs.bat");
                }

            }
            catch (Exception e)
            {
                WriteError("Failed to run tests - {0}", e.Message);
                Environment.ExitCode = FailCode;
            }
        }

        private void WriteError(string format, params object[] args)
        {
            var saved = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine(format, args);
            Console.ForegroundColor = saved;
        }

        private void SafeDelete(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        private void EnumerateDirs(List<DirectoryInfo> diArray, List<DirectoryInfo> allTestDirs, HashSet<string> visited)
        {
            List<string> result = new List<string>();
            try
            {
                foreach (DirectoryInfo di in diArray)
                {
                    if (visited.Contains(di.FullName))
                    {
                        continue;
                    }
                    visited.Add(di.FullName);

                    bool isTestDir = false;
                    foreach (var dir in di.EnumerateDirectories())
                    {
                        if ((dir.Name == "Pc") ||
                            (dir.Name == "Zing") ||
                            (dir.Name == "Prt"))
                        {
                            // look for ConfigFileName
                            foreach (var fi in dir.EnumerateFiles(ConfigFileName))
                            {
                                //if di.Name is Pc or Prt or Zing, leaf directory is reached;
                                //(we are assuming that test directories cannot have these names)
                                //Note:these directory names must be exactly Pc or Prt or Zing (they are case-sensitive)

                                // Found one of these dirs, so the parent is a real test directory then.
                                allTestDirs.Add(di);

                                // don't need to dig any deeper
                                isTestDir = true; 
                            }
                        }
                        if (isTestDir)
                        {
                            break;
                        }
                    }

                    // dig deeper...
                    List<DirectoryInfo> dpArray = new List<DirectoryInfo>(di.EnumerateDirectories());
                    EnumerateDirs(dpArray, allTestDirs, visited);
                }
            }
            catch (Exception e)
            {
                WriteError("### ERROR reading directories: {0}", e.Message);
            }
        }

        private static string FindTestRoot()
        {
            string runptoolPath = typeof(Program).Assembly.Location;
            DirectoryInfo tstDir = new DirectoryInfo(Path.GetDirectoryName(runptoolPath));
            Uri uri = new Uri(tstDir.FullName); //  D:\git\P\Bld\Drops\Debug\x64\Binaries

            if (!File.Exists(Path.Combine(tstDir.FullName, "testP.bat")))
            {
                // perhaps we are inside a specific test directory, like D:\git\p-org\P\Tst\RegressionTests\Feature2Stmts\DynamicError\receive4\Zing.
                // so walk up the parent chain looking for it.
                Uri parent = new Uri(uri, "..");
                while (true)
                {
                    if (File.Exists(new Uri(parent, "testP.bat").LocalPath))
                    {
                        // found it!
                        tstDir = new DirectoryInfo(parent.LocalPath);
                        break;
                    }
                    if (Directory.Exists(new Uri(parent, "Tst").LocalPath))
                    {
                        // found Tst directory.
                        tstDir = new DirectoryInfo(new Uri(parent, "Tst").LocalPath);
                        if (File.Exists(Path.Combine(tstDir.FullName, "testP.bat")))
                        {
                            break;
                        }
                    }
                    Uri next = new Uri(parent, "..");
                    if (next == parent)
                    {
                        break;
                    }
                    else
                    {
                        parent = next;
                    }
                }
            }

            if (!File.Exists(Path.Combine(tstDir.FullName, "testP.bat")))
            {
                throw new Exception("Cound not find 'Tst' directory, please set working directory to someplace inside this directory");
            }

            return tstDir.FullName;
        }

        //Check paths in the list that they do not contain paths to Pc/Zing/Prt:
        private static bool CheckTopPaths(List<DirectoryInfo> diArray)
        {
            bool result = true;
            foreach (DirectoryInfo di in diArray)
            {
                if (!di.Exists)
                {
                    Console.WriteLine("Directory {0} does not exist", di.FullName);
                    Console.WriteLine("");
                    Environment.ExitCode = FailCode;
                    result = false;
                }

                if ((di.Name == "Pc") || (di.Name == "Zing") || (di.Name == "Prt"))
                {
                    Console.WriteLine("Test directory list cannot contain path to Pc, Zing or Prt dir:");
                    Console.WriteLine("{0}", di.FullName);
                    Console.WriteLine("Replace with path to the parent dir");
                    Console.WriteLine("");
                    result = false;
                }
            }
            return result;
        }

        private void DebugWriteLine(string msg)
        {
            Debug.WriteLine("(" + System.Threading.Thread.CurrentThread.ManagedThreadId + ") " + msg);
        }

        //If reset = true, failedDirsWriter and displayDiffsWriter are "null"
        private void Test(DirectoryInfo testDir, string zingFilePath, ThreadSafeOutput reporter)
        {
            try
            {
                DebugWriteLine("Testing: " + testDir.FullName);

                string resultDir = PrepareTestDir(testDir.FullName);
                DebugWriteLine("Output: " + resultDir);

                //Since order of directory processing is significant (Pc should be processed before
                //Zing and Prt), order enumerated directories alphabetically:
                var dirs = (from dir in Directory.GetDirectories(resultDir)
                            orderby dir ascending
                            select dir);

                foreach (var di in dirs)
                {
                    string name = Path.GetFileName(di);
                    if ((name == "Pc") ||
                        (name == "Zing" && (execsToRun == "/runZing" || execsToRun == "/runAll")) ||
                        (name == "Prt" && (execsToRun == "/runPrt" || execsToRun == "/runAll")))
                    {
                        string configFile = di + Path.DirectorySeparatorChar + ConfigFileName;
                        if (File.Exists(configFile))
                        {
                            var checker = new Checker(di, testRoot, reset, cooperative, configuration, platform, execsToRun, zingFilePath);
                            if (!checker.Check(configFile))
                            {
                                reporter.RecordFailure(di);
                            }
                            else
                            {
                                reporter.RecordSuccess();
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                WriteError(
                    "ERROR in Test: {0}",
                    e.Message);
            }
        }

        private string PrepareTestDir(string fullName)
        {
            if (fullName.StartsWith(testOutput, StringComparison.OrdinalIgnoreCase))
            {
                // we are running straight out of the TestResult directory...
                return fullName;
            }

            // copy the test files over to the TestReults tree, mirroring the same directory structure.
            Uri uri = new Uri(fullName);
            Uri root = new Uri(testRoot + "/");
            Uri relative = root.MakeRelativeUri(uri);
            Uri test = new Uri(new Uri(testOutput + "/"), relative);
            string testDir = test.LocalPath;
            Checker.CloneSubtree(fullName, testDir + Path.DirectorySeparatorChar);

            return testDir;
        }

        class ThreadSafeOutput
        {
            int testCount;
            int failCount;
            object threadLock = new object();
            StreamWriter failedTestsWriter;
            StreamWriter tempWriter;
            StreamWriter displayDiffsWriter;

            public ThreadSafeOutput(
                StreamWriter failedTestsWriter,
                StreamWriter tempWriter,
                StreamWriter displayDiffsWriter)
            {
                this.failedTestsWriter = failedTestsWriter;
                this.tempWriter = tempWriter;
                this.displayDiffsWriter = displayDiffsWriter;
            }

            public int FailCount { get { return this.failCount; } }
            public int TestCount { get { return this.testCount; } }

            public void RecordSuccess()
            {
                lock (threadLock)
                {
                    this.testCount++;
                }
            }

            public void RecordFailure(string fullPath)
            {
                lock (threadLock)
                {
                    this.failCount++;
                    //add directory of the failed (sub)test to "failed_tests.txt": 
                    failedTestsWriter.WriteLine("{0}", fullPath);
                    //add directory of the failed test to "tempFailed.txt": 
                    //Console.WriteLine("+++++Writing to tempFailed: {0}", di.Parent.FullName);
                    tempWriter.WriteLine("{0}", System.IO.Path.GetDirectoryName(fullPath));
                    //add diffing command to "display_diff.bat":
                    displayDiffsWriter.WriteLine("{0} {1}\\acc_0.txt {1}\\check-output.log", DiffTool, fullPath);
                }
            }
        }

        //copy unique paths from src file into dest file
        //to keep it simple, this code only removes consecutive duplicate lines,
        //which should be good enough in most cases; the only dups that would be 
        //left are from duplicate tests in the original list of test dirs
        private static void RemoveDupTests(StreamWriter destWr, StreamReader srcRd)
        {
            //Console.WriteLine("+++++RemoveDupTests: start");
            try
            {
                string currentLine = srcRd.ReadLine();
                HashSet<string> previousLines = new HashSet<string>();

                //while ((currentLine = srcRd.ReadLine()) != null)
                //Debug:
                //if (currentLine == null)
                //{
                //Console.WriteLine("+++++RemoveDupTests: currentLine is null at the beginning");
                //}
                while (currentLine != null)
                {

                    // Add returns true if it was actually added,
                    // false if it was already there
                    var res = previousLines.Add(currentLine);
                    //Console.WriteLine("+++++RemoveDupTests: res is {0}", res);
                    if (res)
                    {
                        //Console.WriteLine("+++++RemoveDupTests: writing {0} into failed-for-reset.txt", currentLine);
                        destWr.WriteLine(currentLine);
                    }
                    currentLine = srcRd.ReadLine();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(
                    "ERROR in creating failed-tests-for-reset.txt: {0}",
                    e.Message);
            }
        }
        private static bool OpenSummaryStreamWriter(string fileName, out StreamWriter wr)
        {
            wr = null;
            try
            {
                wr = new StreamWriter(Path.Combine(testOutput, fileName));
            }
            catch (Exception e)
            {
                Console.WriteLine(
                    "ERROR: Could not open summary file {0} - {1}",
                    fileName,
                    e.Message);
                return false;
            }

            return true;
        }
        private static void OpenSummaryStreamReader(string fileName, out StreamReader rd)
        {
            rd = null;
            try
            {
                rd = new StreamReader(Path.Combine(testOutput, fileName));
            }
            catch (Exception e)
            {
                string msg = string.Format("ERROR: Could not open summary file {0} - {1}",
                    fileName,
                    e.Message);
                Console.WriteLine(msg);
                throw new Exception(msg);
            }
        }

        //generate list of directories for running regression from the input file (1st argument of testP.bat)
        private static List<DirectoryInfo> ExtractActiveDirsFromFile(string fileName, DirectoryInfo tstDir)
        {
            List<DirectoryInfo> result = new List<DirectoryInfo>();
            try
            {
                Uri combined = new Uri(new Uri(tstDir.FullName + "\\"), fileName);
                string resolved = combined.LocalPath;
                if (Directory.Exists(resolved))
                {
                    result.Add(new DirectoryInfo(resolved));
                }
                else
                {
                    using (var sr = new StreamReader(combined.LocalPath))
                    {
                        while (!sr.EndOfStream)
                        {
                            var dir = sr.ReadLine();
                            //Skip the line if it is blank:
                            if ((dir.Trim() == "")) break;

                            if (dir.StartsWith("\\") || dir.StartsWith("/") || dir.StartsWith("\\\\"))
                            {
                                Console.WriteLine("Failed to run tests: directory name in the test directory file cannot start with \"\\\" or \"/\" or \"\\\\\"");
                                return null;
                            }

                            result.Add(new DirectoryInfo(Path.Combine(tstDir.FullName, dir)));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                {
                    Console.WriteLine("Failed to read regression dirs from input file - {0}", e.Message);
                    Environment.ExitCode = FailCode;
                }
            }
            return result;
        }
    }

}
