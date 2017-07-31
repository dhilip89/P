﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Pc;
using NUnit.Framework;
using System.Runtime.InteropServices;

namespace UnitTests
{
    [TestFixture]
    [Parallelizable(ParallelScope.Children)]
    public class RegressionTests
    {
        private Compiler PCompiler => compiler.Value;

        private readonly ThreadLocal<Compiler> compiler = new ThreadLocal<Compiler>(() => new Compiler(true));

        private static string TestResultsDirectory { get; } = Path.Combine(
            Constants.TestDirectory,
            $"TestResult_{Constants.Configuration}_{Constants.Platform}");

        public static IEnumerable<TestCaseData> TestCases => TestCaseLoader.FindTestCasesInDirectory(Constants.TestDirectory);

        private static DirectoryInfo PrepareTestDir(DirectoryInfo testDir)
        {
            var testRoot = new Uri(Constants.TestDirectory + Path.DirectorySeparatorChar);
            var curTest = new Uri(testDir.FullName);
            Uri relativePath = testRoot.MakeRelativeUri(curTest);
            string destinationDir = Path.GetFullPath(Path.Combine(TestResultsDirectory, relativePath.OriginalString));
            try
            {
                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, true);
                }
            }
            catch (Exception e)
            {
                WriteError("ERROR: Could not delete old test directory: {0}", e.Message);
            }
            
            DeepCopy(testDir, destinationDir);
            return new DirectoryInfo(destinationDir);
        }

        private static void DeepCopy(DirectoryInfo src, string target)
        {
            Directory.CreateDirectory(target);
            CopyFiles(src, target);
            foreach (DirectoryInfo dir in src.GetDirectories())
            {
                DeepCopy(dir, Path.Combine(target, dir.Name));
            }
        }

        private static void CopyFiles(DirectoryInfo src, string target)
        {
            foreach (FileInfo file in src.GetFiles())
            {
                File.Copy(file.FullName, Path.Combine(target, file.Name), true);
            }
        }

        private static void WriteError(string format, params object[] args)
        {
            ConsoleColor saved = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(format, args);
            Console.ForegroundColor = saved;
        }

        private int TestPc(TestConfig config, TextWriter tmpWriter, DirectoryInfo workDirectory, string activeDirectory, CompilerOutput outputLanguage)
        {
            List<string> pFiles = workDirectory.EnumerateFiles("*.p").Select(pFile => pFile.FullName).ToList();
            if (!pFiles.Any())
            {
                throw new Exception("no .p file found in test directory");
            }

            string inputFileName = pFiles.First();
            string linkFileName = Path.ChangeExtension(inputFileName, ".4ml");

            var compilerOutput = new CompilerTestOutputStream(tmpWriter);
            var compileArgs = new CommandLineOptions
            {
                inputFileNames = new List<string>(pFiles),
                shortFileNames = true,
                outputDir = workDirectory.FullName,
                unitName = linkFileName,
                //liveness = LivenessOption.None,
                liveness = (outputLanguage == CompilerOutput.Zing && config.Arguments.Contains("/liveness")) ? LivenessOption.Standard
                            : LivenessOption.None,
                compilerOutput = outputLanguage
            };

            // Compile
            if (!PCompiler.Compile(compilerOutput, compileArgs))
            {
                tmpWriter.WriteLine("EXIT: -1");
                return -1;
            }

            //Skip the link step if outputLanguage == CompilerOutput.CSharp?
            // Link
            //if (!(outputLanguage == CompilerOutput.CSharp))
            //{
                compileArgs.dependencies.Add(linkFileName);
                compileArgs.inputFileNames.Clear();

                if (config.Link != null)
                {
                    compileArgs.inputFileNames.Add(Path.Combine(activeDirectory, config.Link));
                }

                if (!PCompiler.Link(compilerOutput, compileArgs))
                {
                    tmpWriter.WriteLine("EXIT: -1");
                    return -1;
                }
            //}
            
            //pc.exe with Zing option is called when outputLanguage is C;
            //pc.exe with CSharp option is called when outputLanguage is CSharp; 
            if (outputLanguage == CompilerOutput.C)
            {
                // compile *.p again, this time with Zing option:
                compileArgs.compilerOutput = CompilerOutput.Zing;
                compileArgs.inputFileNames = new List<string>(pFiles);
                compileArgs.dependencies.Clear();
                int zingResult = PCompiler.Compile(compilerOutput, compileArgs) ? 0 : -1;
                tmpWriter.WriteLine($"EXIT: {zingResult}");
                if (!(zingResult == 0))
                { 
                    return -1;
                }
            }
            //if (outputLanguage == CompilerOutput.CSharp)
            //{
            //    // compile *.p again, this time with CSharp option:
            //    compileArgs.compilerOutput = CompilerOutput.CSharp;
            //    compileArgs.inputFileNames = new List<string>(pFiles);
            //    compileArgs.dependencies.Clear();
            //    if (!PCompiler.Compile(compilerOutput, compileArgs))
            //    {
            //        tmpWriter.WriteLine("EXIT: -1");
            //        return -1;
            //    }
            //    else
            //    {
            //        tmpWriter.WriteLine("EXIT: 0");
            //    }
            //    // Link
            //    compileArgs.dependencies.Add(linkFileName);
            //    compileArgs.inputFileNames.Clear();

            //    if (config.Link != null)
            //    {
            //        compileArgs.inputFileNames.Add(Path.Combine(activeDirectory, config.Link));
            //    }

            //    if (!PCompiler.Link(compilerOutput, compileArgs))
            //    {
            //        tmpWriter.WriteLine("EXIT: -1");
            //        return -1;
            //    }
            //}

            return 0;
        }

        private static void WriteHeader(TextWriter tmpWriter)
        {
            tmpWriter.WriteLine("=================================");
            tmpWriter.WriteLine("         Console output          ");
            tmpWriter.WriteLine("=================================");
        }

        private static void TestPt(TestConfig config, TextWriter tmpWriter, DirectoryInfo workDirectory, string activeDirectory, DirectoryInfo origTestDir)
        {
            //Delete generated files from previous PTester run:
            //<test>.cs,  <test>.dll, <test>.pdb
            //foreach (var file in workDirectory.EnumerateFiles())
            //{
            //    if (file.Extension == ".cs" || ((file.Extension == ".dll" || file.Extension == ".pdb") && file.Name == origTestDir.Name))
            //    {
            //        file.Delete();
            //    }

            //}
            //Run CSharp compiler on generated .cs:
            // % 1: workDirectory
            // % 2: (origTestDir (test name)
            //csc.exe "%1\%2.cs" "%1\linker.cs" /debug /target:library /r:"D:\PLanguage\P\Bld\Drops\Debug\x86\Binaries\Prt.dll" /out:"%1\%2.dll"
            //string cscFilePath = "csc.exe";
            
            var frameworkPath = RuntimeEnvironment.GetRuntimeDirectory();
            var cscFilePath = Path.Combine(frameworkPath, "csc.exe");
            if (!File.Exists(cscFilePath))
            {
                throw new Exception("Could not find csc.exe");
            }

            // Find .cs input to pt.exe:
            string csFileName = null;
            foreach (var fileName in workDirectory.EnumerateFiles())
            {
                if (fileName.Extension == ".cs" && (Path.GetFileNameWithoutExtension(fileName.Name)).Equals(origTestDir.Name))
                {
                    csFileName = fileName.FullName;
                }
            }
            //string csFileName = (from fileName in workDirectory.EnumerateFiles()
            //                     where fileName.Extension == ".cs" && (Path.GetFileNameWithoutExtension(fileName.Name)).Equals(origTestDir.Name))
            //                     select fileName.FullName).FirstOrDefault();
            //Debug:
            //if (!(csFileName == null))
            //
            //    Console.WriteLine(".cs input for pt.exe: {}", csFileName);
            //}

            if (csFileName == null)
            {
                throw new Exception("Could not find .cs input for pt.exe");
            }

            // Find linker.cs:
            string linkerFileName = (from fileName1 in workDirectory.EnumerateFiles()
                                 where fileName1.Extension == ".cs" && (Path.GetFileNameWithoutExtension(fileName1.Name)).Equals("linker")
                                 select fileName1.FullName).FirstOrDefault();
            //Debug:
            //if (!(linkerFileName == null))
            //{
            //    Console.WriteLine("linker.cs input for pt.exe: {}", linkerFileName);
            //}
            if (linkerFileName == null)
            {
                throw new Exception("Could not find linker.cs input for pt.exe");
            }

            // Find Prt.dll:
            string prtDLLPath = Path.Combine(
                Constants.SolutionDirectory,
                "Bld",
                "Drops",
                Constants.Configuration,
                Constants.Platform,
                "Binaries",
                "Prt.dll");
            //Debug:
            //Console.WriteLine("Prt.dll input for pt.exe: {}", prtDLLPath);
            if (!File.Exists(prtDLLPath))
            {
                throw new Exception("Could not find Prt.dll");
            }

            // Output DLL file name:
            string outputDLLName = origTestDir.Name + ".dll";
            string outputDLLPath = Path.Combine(workDirectory.FullName, outputDLLName);
            //Debug:
            //Console.WriteLine("output DLL for csc.exe: {}", outputDLLPath);

            //Delete generated files from previous PTester run:
            //<test>.cs,  <test>.dll, <test>.pdb, 
            foreach (var file in workDirectory.EnumerateFiles())
            {
                if (file.Name == origTestDir.Name && (file.Extension == ".dll" || file.Extension == ".pdb"))
                {
                    file.Delete();
                }
            }
            // Run C# compiler
            //IMPORTANT: since there's no way to suppress all warnings, if warnings other than specified below are detected, those would have to be added
            //Another option would be to not write csc.exe output into the acceptor at all
            //var arguments = new List<string>(config.Arguments) { "/debug", "/nowarn:1692,168,162", "/nologo", "/target:library", "/r:" + prtDLLPath, "/out:" + outputDLLPath, csFileName, linkerFileName };
            var arguments = new List<string>(config.Arguments) { "/debug", "/target:library", "/r:" + prtDLLPath, "/out:" + outputDLLPath, csFileName, linkerFileName };
            string stdout, stderr;
            int exitCode = RunWithOutput(cscFilePath, activeDirectory, arguments, out stdout, out stderr);
            //tmpWriter.Write(stdout);
            //tmpWriter.Write(stderr);
            tmpWriter.WriteLine($"EXIT (csc.exe): {exitCode}");

            // Append includes
            foreach (string include in config.Includes)
            {
                tmpWriter.WriteLine();
                tmpWriter.WriteLine("=================================");
                tmpWriter.WriteLine(include);
                tmpWriter.WriteLine("=================================");

                try
                {
                    using (var sr = new StreamReader(Path.Combine(activeDirectory, include)))
                    {
                        while (!sr.EndOfStream)
                        {
                            tmpWriter.WriteLine(sr.ReadLine());
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    if (!include.EndsWith("trace"))
                    {
                        throw;
                    }
                }
            }

            //Run pt.exe: pt.exe "%1\%2.dll"
            // Find pt.exe:
            string ptExePath = Path.Combine(
                Constants.SolutionDirectory,
                "Bld",
                "Drops",
                Constants.Configuration,
                Constants.Platform,
                "Binaries",
                "Pt.exe");
            //Debug:
            //Console.WriteLine("Pt.exe input for pt.exe: {}", ptExePath);
            if (!File.Exists(ptExePath))
            {
                throw new Exception("Could not find pt.exe");
            }

            // input DLL file name: same as outputDLLPath

            // Run pt.exe
            arguments = new List<string>(config.Arguments) { outputDLLPath };
            int exitCode1 = RunWithOutput(ptExePath, activeDirectory, arguments, out stdout, out stderr);
            tmpWriter.Write(stdout);
            tmpWriter.Write(stderr);
            tmpWriter.WriteLine($"EXIT: {exitCode1}");

            // Append includes
            foreach (string include in config.Includes)
            {
                tmpWriter.WriteLine();
                tmpWriter.WriteLine("=================================");
                tmpWriter.WriteLine(include);
                tmpWriter.WriteLine("=================================");

                try
                {
                    using (var sr = new StreamReader(Path.Combine(activeDirectory, include)))
                    {
                        while (!sr.EndOfStream)
                        {
                            tmpWriter.WriteLine(sr.ReadLine());
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    if (!include.EndsWith("trace"))
                    {
                        throw;
                    }
                }
            }
        }

        private static void TestZing(TestConfig config, TextWriter tmpWriter, DirectoryInfo workDirectory, string activeDirectory)
        {
            // Find Zing tool
            string zingFilePath = Path.Combine(
                Constants.SolutionDirectory,
                "Bld",
                "Drops",
                Constants.Configuration,
                Constants.Platform,
                "Binaries",
                "zinger.exe");

            // Find DLL input to Zing
            string zingDllName = (from fileName in workDirectory.EnumerateFiles()
                                  where fileName.Extension == ".dll" && !fileName.Name.Contains("linker")
                                  select fileName.FullName).FirstOrDefault();
            if (zingDllName == null)
            {
                throw new Exception("Could not find Zinger input.");
            }

            // Run Zing tool
            var arguments = new List<string>(config.Arguments) {zingDllName};
            string stdout, stderr;
            int exitCode = RunWithOutput(zingFilePath, activeDirectory, arguments, out stdout, out stderr);
            tmpWriter.Write(stdout);
            tmpWriter.Write(stderr);
            tmpWriter.WriteLine($"EXIT: {exitCode}");

            // Append includes
            foreach (string include in config.Includes)
            {
                tmpWriter.WriteLine();
                tmpWriter.WriteLine("=================================");
                tmpWriter.WriteLine(include);
                tmpWriter.WriteLine("=================================");

                try
                {
                    using (var sr = new StreamReader(Path.Combine(activeDirectory, include)))
                    {
                        while (!sr.EndOfStream)
                        {
                            tmpWriter.WriteLine(sr.ReadLine());
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    if (!include.EndsWith("trace"))
                    {
                        throw;
                    }
                }
            }
        }

        private void TestPrt(TestConfig config, TextWriter tmpWriter, DirectoryInfo workDirectory, string activeDirectory)
        {
            // copy PrtTester to the work directory
            var testerDir = new DirectoryInfo(Path.Combine(Constants.TestDirectory, Constants.CRuntimeTesterDirectoryName));
            CopyFiles(testerDir, workDirectory.FullName);

            string testerExeDir = Path.Combine(workDirectory.FullName, Constants.Configuration, Constants.Platform);
            string testerExePath = Path.Combine(testerExeDir, Constants.CTesterExecutableName);
            string prtTesterProj = Path.Combine(workDirectory.FullName, Constants.CTesterVsProjectName);

            // build the Pc output with the test harness

            BuildTester(prtTesterProj, activeDirectory, true);
            BuildTester(prtTesterProj, activeDirectory, false);

            // run the harness

            string stdout, stderr;
            int exitCode = RunWithOutput(testerExePath, activeDirectory, config.Arguments, out stdout, out stderr);
            tmpWriter.Write(stdout);
            tmpWriter.Write(stderr);
            tmpWriter.WriteLine($"EXIT: {exitCode}");
        }

        private static void BuildTester(string prtTesterProj, string activeDirectory, bool clean)
        {
            var argumentList = new[]
            {
                prtTesterProj, clean ? "/t:Clean" : "/t:Build", $"/p:Configuration={Constants.Configuration}",
                $"/p:Platform={Constants.Platform}", "/nologo"
            };

            string stdout, stderr;
            if (RunWithOutput("msbuild.exe", activeDirectory, argumentList, out stdout, out stderr) != 0)
            {
                throw new Exception($"Failed to build {prtTesterProj}\nOutput:\n{stdout}\n\nErrors:\n{stderr}\n");
            }
        }

        private static int RunWithOutput(
            string exeName,
            string activeDirectory,
            IEnumerable<string> argumentList,
            out string stdout,
            out string stderr)
        {
            var psi = new ProcessStartInfo(exeName)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                WorkingDirectory = activeDirectory,
                Arguments = string.Join(" ", argumentList)
            };

            string mStdout = "", mStderr = "";

            var proc = new Process {StartInfo = psi};
            proc.OutputDataReceived += (s, e) => { mStdout += $"OUT: {e.Data}\n"; };
            proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    mStderr += $"ERROR: {e.Data}\n";
                }
            };

            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            proc.WaitForExit();
            stdout = mStdout;
            stderr = mStderr;
            return proc.ExitCode;
        }

        [Test]
        [TestCaseSource(nameof(TestCases))]
        public void TestProgramAndBackends(DirectoryInfo origTestDir, Dictionary<TestType, TestConfig> testConfigs)
        {
            // First step: clone test folder to new spot
            DirectoryInfo workDirectory = PrepareTestDir(origTestDir);

            File.Delete(Path.Combine(Constants.TestDirectory, Constants.DisplayDiffsFile));

            var sbd = new StringBuilder();
            foreach (KeyValuePair<TestType, TestConfig> kv in testConfigs.OrderBy(kv => kv.Key))
            {
                TestType testType = kv.Key;
                TestConfig config = kv.Value;

                Console.WriteLine($"*** {config.Description}");

                string activeDirectory = Path.Combine(workDirectory.FullName, testType.ToString());

                // Delete temp files as specified by test configuration.
                IEnumerable<FileInfo> toDelete = config
                    .Deletes.Select(file => new FileInfo(Path.Combine(activeDirectory, file))).Where(file => file.Exists);
                foreach (FileInfo fileInfo in toDelete)
                {
                    fileInfo.Delete();
                }

                var sb = new StringBuilder();
                int pcResult;
                using (var tmpWriter = new StringWriter(sb))
                {
                    WriteHeader(tmpWriter);
                    switch (testType)
                    {
                        case TestType.Pc:
                            TestPc(config, tmpWriter, workDirectory, activeDirectory, CompilerOutput.C);
                            break;
                        case TestType.Prt:
                            TestPrt(config, tmpWriter, workDirectory, activeDirectory);
                            break;
                        case TestType.Pt:
                            pcResult = TestPc(config, tmpWriter, workDirectory, activeDirectory, CompilerOutput.CSharp);
                            if (pcResult == 0)
                            {
                                TestPt(config, tmpWriter, workDirectory, activeDirectory, origTestDir);
                            }
                            break;
                        case TestType.Zing:
                            TestZing(config, tmpWriter, workDirectory, activeDirectory);
                            break;
                        default: throw new ArgumentOutOfRangeException();
                    }
                }

                /* TODO: Add test case freezing code here. 
                    * Check for a FREEZE_P_TESTS environment variable, and if present, overwrite the contents of
                    * Path.Combine(origTestDir.FullName, testType.ToString(), Constants.CorrectOutputFileName)
                    * with the value in actualText and, of course, skip the assertion.
                    */
                string correctOutputPath = Path.Combine(activeDirectory, Constants.CorrectOutputFileName);
                string correctText = File.ReadAllText(correctOutputPath);
                correctText = Regex.Replace(correctText, Constants.NewLinePattern, Environment.NewLine);
                string actualText = sb.ToString();
                actualText = Regex.Replace(actualText, Constants.NewLinePattern, Environment.NewLine);
                //if (Constants.ShouldFreezeTests)
                //{
                    if (!actualText.Equals(correctText))
                    {
                        try
                        {
                            //Save actual test output:
                            File.WriteAllText(Path.Combine(activeDirectory, Constants.ActualOutputFileName), actualText);
                            //add diffing command to "display-diffs.bat":
                            string diffCmd = string.Format("{0} {1}\\acc_0.txt {1}\\{2}", Constants.DiffTool,
                                activeDirectory, Constants.ActualOutputFileName);
                            File.AppendAllText(Path.Combine(Constants.TestDirectory, Constants.DisplayDiffsFile), diffCmd);
                        }
                        catch (Exception e)
                        {
                            WriteError("ERROR: exception: {0}", e.Message);
                        }
                    }
               // }

                Assert.AreEqual(correctText, actualText);
                Console.WriteLine(actualText);
            }
        }
    }
}