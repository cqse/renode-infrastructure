//
// Copyright (c) 2026 CQSE GmbH
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.UART;

namespace Antmicro.Renode.Peripherals.CPU
{
    /// <summary>
    /// Records which guest code every CPU executed during each test (testwise coverage, e.g. for test impact analysis).
    ///
    /// Recording happens at translation time: every block is translated right before its first execution, and
    /// <see cref="BeginTest"/> clears the translation caches, so the blocks translated between <see cref="BeginTest"/>
    /// and <see cref="EndTest"/> are the blocks executed in that test. A block left early (fault, interrupt) counts as
    /// fully executed, which over-approximates by a few instructions. Executing code needs no per-block hook.
    ///
    /// Output: one JSON object per test and line in <c>&lt;dir&gt;/tcov-&lt;pid&gt;.jsonl</c> with the test's names,
    /// result, duration, loaded images (path, MD5) and the executed byte ranges per CPU.
    /// </summary>
    public sealed class TestwiseCoverage
    {
        public static TestwiseCoverage Instance { get; } = new TestwiseCoverage();

        public void Enable(string outputDirectory)
        {
            lock(sync)
            {
                Directory.CreateDirectory(outputDirectory);
                outputPath = Path.Combine(Path.GetFullPath(outputDirectory), $"tcov-{Process.GetCurrentProcess().Id}.jsonl");
                if(!IsEnabled)
                {
                    EmulationManager.Instance.EmulationChanged += OnEmulationChanged;
                    IsEnabled = true;
                    AttachToEmulation(EmulationManager.Instance.CurrentEmulation);
                }
                Logger.Log(LogLevel.Info, "Testwise coverage enabled, writing to {0}", outputPath);
            }
        }

        public void BeginTest(string suite, string test)
        {
            BeginTestInner(suite, test, fromEmulation: false);
        }

        public void EndTest(string result)
        {
            EndTestOuter(result, fromEmulation: false);
        }

        /// <summary>
        /// Take test boundaries from ztest's console output instead of from BeginTest/EndTest calls, for runners that
        /// start one emulation per test binary and don't know its test cases (e.g. twister). On the given UART,
        /// "Running TESTSUITE s" sets the suite, "START - c" begins test "&lt;scenario&gt;/s"/"c" and
        /// "PASS|FAIL|SKIP - c" ends it. Code executed between test cases (boot, suite setup and teardown) is reported as
        /// a "__setup__" pseudo-test of the following suite. The UART may be created later (e.g. by a platform script).
        /// </summary>
        public void SplitByZtest(string uartName, string scenario)
        {
            lock(sync)
            {
                CheckEnabled();
                ztestUart = uartName.Substring(uartName.LastIndexOf('.') + 1);
                ztestScenario = scenario;
                recordBetweenTests = true;
                foreach(var machine in EmulationManager.Instance.CurrentEmulation.Machines)
                {
                    foreach(var uart in machine.GetPeripheralsOfType<IUART>())
                    {
                        AttachToUart(machine, uart);
                    }
                }
            }
        }

        private void BeginTestInner(string suite, string test, bool fromEmulation)
        {
            lock(sync)
            {
                CheckEnabled();
                if(currentTest != null)
                {
                    Logger.Log(LogLevel.Warning, "Testwise coverage: test '{0}' was not ended, closing it as UNKNOWN", currentTest);
                    EndTestInner("UNKNOWN", fromEmulation);
                }
                else if(cpus.Values.Any(x => x.Current?.Count > 0) || detachedRecords.Any(x => x.Count > 0))
                {
                    // Code executed outside of any test (e.g. suite setup, or CPUs created after the last test)
                    // is reported as a pseudo-test of the next test's suite.
                    currentSuite = suite;
                    currentTest = SetupTestName;
                    EndTestInner("PASS", fromEmulation);
                }

                StartRecording(fromEmulation);
                currentSuite = suite;
                currentTest = test;
                attempt = attempts.TryGetValue((suite, test), out var n) ? n + 1 : 1;
                attempts[(suite, test)] = attempt;
                stopwatch.Restart();
                virtualStartMicroseconds = CurrentVirtualMicroseconds();
            }
        }

        private void EndTestOuter(string result, bool fromEmulation)
        {
            lock(sync)
            {
                CheckEnabled();
                if(currentTest == null)
                {
                    // E.g. the test setup failed before BeginTest; don't fail the teardown as well.
                    Logger.Log(LogLevel.Warning, "Testwise coverage: EndTest called without BeginTest, ignoring");
                    return;
                }
                EndTestInner(result, fromEmulation);
            }
        }

        public string Status()
        {
            lock(sync)
            {
                if(!IsEnabled)
                {
                    return "Testwise coverage is disabled";
                }
                var builder = new StringBuilder();
                builder.AppendLine($"Output: {outputPath}");
                builder.AppendLine($"Current test: {(currentTest == null ? "none" : $"{currentSuite} / {currentTest}")}");
                foreach(var state in cpus.Values)
                {
                    var record = state.Current;
                    if(record == null)
                    {
                        builder.AppendLine($"CPU {state.Cpu.GetName()}: not recording (after EndTest)");
                        continue;
                    }
                    record.ResolveNames();
                    builder.AppendLine($"CPU {record.MachineName}.{record.CpuName}: {record.Count} blocks");
                }
                return builder.ToString();
            }
        }

        public bool IsEnabled { get; private set; }

        public const string SetupTestName = "__setup__";

        private TestwiseCoverage()
        {
        }

        private void CheckEnabled()
        {
            if(!IsEnabled)
            {
                throw new RecoverableException("Testwise coverage is not enabled; use EnableTestwiseCoverageGlobally first");
            }
        }

        private void OnEmulationChanged()
        {
            lock(sync)
            {
                // The old emulation is disposed together with its CPUs; keep what they recorded for the current test.
                detachedRecords.AddRange(cpus.Values.Select(x => x.Current).Where(x => x != null));
                cpus.Clear();
                AttachToEmulation(EmulationManager.Instance.CurrentEmulation);
            }
        }

        private void AttachToEmulation(Emulation emulation)
        {
            emulation.MachineAdded += AttachToMachine;
            emulation.MachineRemoved += DetachFromMachine;
            foreach(var machine in emulation.Machines)
            {
                AttachToMachine(machine);
            }
        }

        private void AttachToMachine(IMachine machine)
        {
            lock(sync)
            {
                machine.PeripheralsChanged += OnPeripheralsChanged;
                foreach(var cpu in machine.SystemBus.GetCPUs().OfType<TranslationCPU>())
                {
                    AttachToCpu(machine, cpu);
                }
            }
        }

        private void DetachFromMachine(IMachine machine)
        {
            lock(sync)
            {
                machine.PeripheralsChanged -= OnPeripheralsChanged;
                foreach(var cpu in cpus.Values.Where(x => x.Machine == machine).Select(x => x.Cpu).ToList())
                {
                    DetachFromCpu(cpu);
                }
                // Images of a machine that is gone before EndTest would be lost otherwise.
                if(currentTest != null)
                {
                    detachedImages.AddRange(GetImages(machine));
                }
            }
        }

        private void OnPeripheralsChanged(IMachine machine, PeripheralsChangedEventArgs args)
        {
            if(args.Peripheral is IUART uart && args.Operation == PeripheralsChangedEventArgs.PeripheralChangeType.Addition)
            {
                lock(sync)
                {
                    AttachToUart(machine, uart);
                }
                return;
            }
            if(!(args.Peripheral is TranslationCPU cpu))
            {
                return;
            }
            lock(sync)
            {
                switch(args.Operation)
                {
                case PeripheralsChangedEventArgs.PeripheralChangeType.Addition:
                    AttachToCpu(machine, cpu);
                    break;
                case PeripheralsChangedEventArgs.PeripheralChangeType.Removal:
                case PeripheralsChangedEventArgs.PeripheralChangeType.CompleteRemoval:
                    DetachFromCpu(cpu);
                    break;
                }
            }
        }

        private void AttachToCpu(IMachine machine, TranslationCPU cpu)
        {
            if(cpus.ContainsKey(cpu))
            {
                return;
            }
            var state = new CpuState(machine, cpu);
            state.Handler = (pc, size) => state.Current?.Add(pc, size);
            cpus[cpu] = state;
            cpu.BlockTranslated += state.Handler;
            // Blocks translated before attaching would never be reported again.
            cpu.ClearTranslationCache();
        }

        private void DetachFromCpu(TranslationCPU cpu)
        {
            if(!cpus.TryGetValue(cpu, out var state))
            {
                return;
            }
            cpu.BlockTranslated -= state.Handler;
            if(state.Current != null)
            {
                state.Current.ResolveNames();
                detachedRecords.Add(state.Current);
            }
            cpus.Remove(cpu);
        }

        private void AttachToUart(IMachine machine, IUART uart)
        {
            if(ztestUart == null || ztestUarts.Contains(uart))
            {
                return;
            }
            ztestUarts.Add(uart);
            var line = new StringBuilder();
            string suite = null;
            bool? isZtestUart = null;
            uart.CharReceived += c =>
            {
                // Called on the CPU thread while the guest writes to the UART. The name isn't registered yet when the
                // UART is added, so check it on the first character.
                if(isZtestUart == null)
                {
                    isZtestUart = machine.TryGetLocalName(uart, out var name) && name == ztestUart;
                }
                if(isZtestUart == false)
                {
                    return;
                }
                if(c != '\n')
                {
                    if(c != '\r')
                    {
                        line.Append((char)c);
                    }
                    return;
                }
                var text = AnsiEscape.Replace(line.ToString(), "").Trim();
                line.Clear();
                Match m;
                if((m = ZtestSuite.Match(text)).Success)
                {
                    suite = m.Groups[1].Value;
                }
                else if((m = ZtestStart.Match(text)).Success)
                {
                    BeginTestInner($"{ztestScenario}/{suite ?? "unknown"}", m.Groups[1].Value, fromEmulation: true);
                }
                else if((m = ZtestEnd.Match(text)).Success && currentTest == m.Groups[2].Value)
                {
                    EndTestOuter(m.Groups[1].Value, fromEmulation: true);
                }
            };
        }

        private void StartRecording(bool fromEmulation)
        {
            detachedRecords.Clear();
            detachedImages.Clear();
            if(fromEmulation)
            {
                // On the CPU thread (a boundary seen in the guest's output): pausing the machine here would deadlock.
                // The CPU returns after the current block and clears its cache before executing anything else.
                foreach(var state in cpus.Values)
                {
                    state.Current = new CpuRecord(state.Machine, state.Cpu);
                    state.Cpu.RequestTranslationCacheClearing();
                }
                return;
            }
            foreach(var machineCpus in cpus.Values.GroupBy(x => x.Machine))
            {
                using(machineCpus.Key.ObtainPausedState(true))
                {
                    foreach(var state in machineCpus)
                    {
                        state.Current = new CpuRecord(state.Machine, state.Cpu);
                        state.Cpu.ClearTranslationCache();
                    }
                }
            }
        }

        private void EndTestInner(string result, bool fromEmulation = false)
        {
            var records = cpus.Values.Select(x => x.Current).Where(x => x != null).Concat(detachedRecords).ToList();
            var images = EmulationManager.Instance.CurrentEmulation.Machines.SelectMany(GetImages).Concat(detachedImages).ToList();

            var json = new StringBuilder(4096);
            json.Append("{\"suite\":").Append(JsonString(currentSuite));
            json.Append(",\"test\":").Append(JsonString(currentTest));
            json.Append(",\"result\":").Append(JsonString(result));
            json.Append(",\"attempt\":").Append(attempt);
            json.Append(",\"durationMs\":").Append(stopwatch.ElapsedMilliseconds);
            json.Append(",\"virtualTimeUs\":").Append(Math.Max(0, CurrentVirtualMicroseconds() - virtualStartMicroseconds).ToString("0", CultureInfo.InvariantCulture));
            json.Append(",\"images\":[");
            json.Append(string.Join(",", images.Distinct().Select(x =>
                $"{{\"machine\":{JsonString(x.Machine)},\"path\":{JsonString(x.Path)},\"md5\":{JsonString(x.Md5)}}}")));
            json.Append("],\"cpus\":[");
            var first = true;
            foreach(var record in records)
            {
                record.ResolveNames();
                json.Append(first ? "" : ",");
                first = false;
                json.Append("{\"machine\":").Append(JsonString(record.MachineName));
                json.Append(",\"cpu\":").Append(JsonString(record.CpuName));
                json.Append(",\"blocks\":").Append(record.Count);
                json.Append(",\"ranges\":[");
                json.Append(string.Join(",", record.GetRanges().Select(r => $"[{r.Start},{r.End}]")));
                json.Append("]}");
            }
            json.Append("]}");

            File.AppendAllText(outputPath, json.Append('\n').ToString());

            currentSuite = null;
            currentTest = null;
            detachedRecords.Clear();
            detachedImages.Clear();
            if(recordBetweenTests)
            {
                // ztest: code between test cases is suite setup/teardown, reported as __setup__ of the next suite.
                StartRecording(fromEmulation);
                return;
            }
            // The test's machines may keep running until they are removed (e.g. Reset Emulation in the teardown).
            // That tail belongs to no test, so stop recording on them; CPUs created from now on are recorded as setup.
            foreach(var state in cpus.Values)
            {
                state.Current = null;
            }
        }

        private static IEnumerable<ImageInfo> GetImages(IMachine machine)
        {
            EmulationManager.Instance.CurrentEmulation.TryGetMachineName(machine, out var machineName);
            var fingerprints = (machine.SystemBus as Bus.SystemBus)?.GetLoadedFingerprints() ?? Enumerable.Empty<BinaryFingerprint>();
            return fingerprints.Select(x => new ImageInfo(machineName, x.FileName, x.Hash)).ToList();
        }

        private static double CurrentVirtualMicroseconds()
        {
            return EmulationManager.Instance.CurrentEmulation.MasterTimeSource.ElapsedVirtualTime.TotalMicroseconds;
        }

        private static string JsonString(string value)
        {
            if(value == null)
            {
                return "null";
            }
            var builder = new StringBuilder(value.Length + 2).Append('"');
            foreach(var c in value)
            {
                switch(c)
                {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if(c < 0x20)
                    {
                        builder.AppendFormat("\\u{0:x4}", (int)c);
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
                }
            }
            return builder.Append('"').ToString();
        }

        private static readonly Regex AnsiEscape = new Regex(@"\x1b\[[0-9;]*[A-Za-z]");
        private static readonly Regex ZtestSuite = new Regex(@"^Running TESTSUITE (\S+)");
        private static readonly Regex ZtestStart = new Regex(@"^START - (\S+)");
        private static readonly Regex ZtestEnd = new Regex(@"^(PASS|FAIL|SKIP) - (\S+)");

        private readonly object sync = new object();
        // ztest mode (SplitByZtest): boundaries come from the guest's console output.
        private readonly HashSet<IUART> ztestUarts = new HashSet<IUART>();
        private string ztestUart;
        private string ztestScenario;
        private bool recordBetweenTests;
        private readonly Dictionary<TranslationCPU, CpuState> cpus = new Dictionary<TranslationCPU, CpuState>();
        // Records and images of CPUs/machines removed during the current test.
        private readonly List<CpuRecord> detachedRecords = new List<CpuRecord>();
        private readonly List<ImageInfo> detachedImages = new List<ImageInfo>();
        private readonly Dictionary<(string, string), int> attempts = new Dictionary<(string, string), int>();
        private readonly Stopwatch stopwatch = new Stopwatch();
        private string outputPath;
        private string currentSuite;
        private string currentTest;
        private int attempt;
        private double virtualStartMicroseconds;

        private class CpuState
        {
            public CpuState(IMachine machine, TranslationCPU cpu)
            {
                Machine = machine;
                Cpu = cpu;
                Current = new CpuRecord(machine, cpu);
            }

            public IMachine Machine { get; }

            public TranslationCPU Cpu { get; }

            // Replaced at every test boundary; the translation callback runs on the CPU thread.
            public volatile CpuRecord Current;

            public Action<ulong, uint> Handler;
        }

        private class CpuRecord
        {
            public CpuRecord(IMachine machine, TranslationCPU cpu)
            {
                this.machine = machine;
                this.cpu = cpu;
                // Resolve early: after the emulation is replaced, the old machine can't be looked up any more.
                ResolveNames();
            }

            public void Add(ulong pc, uint size)
            {
                lock(blocks)
                {
                    // The same start address can be retranslated with a different size (e.g. single-stepping).
                    if(!blocks.TryGetValue(pc, out var known) || known < size)
                    {
                        blocks[pc] = size;
                    }
                }
            }

            public void ResolveNames()
            {
                if(MachineName == null && EmulationManager.Instance.CurrentEmulation.TryGetMachineName(machine, out var machineName))
                {
                    MachineName = machineName;
                }
                if(CpuName == null && machine.TryGetLocalName(cpu, out var cpuName))
                {
                    CpuName = cpuName;
                }
            }

            /// <summary>Sorted, merged [start, end) byte ranges.</summary>
            public IEnumerable<(ulong Start, ulong End)> GetRanges()
            {
                List<KeyValuePair<ulong, uint>> sorted;
                lock(blocks)
                {
                    sorted = blocks.OrderBy(x => x.Key).ToList();
                }
                var merged = new List<(ulong Start, ulong End)>();
                foreach(var block in sorted)
                {
                    var end = block.Key + block.Value;
                    if(merged.Count > 0 && block.Key <= merged[merged.Count - 1].End)
                    {
                        var last = merged[merged.Count - 1];
                        merged[merged.Count - 1] = (last.Start, Math.Max(last.End, end));
                    }
                    else
                    {
                        merged.Add((block.Key, end));
                    }
                }
                return merged;
            }

            public int Count
            {
                get
                {
                    lock(blocks)
                    {
                        return blocks.Count;
                    }
                }
            }

            public string MachineName { get; private set; }

            public string CpuName { get; private set; }

            private readonly IMachine machine;
            private readonly TranslationCPU cpu;
            private readonly Dictionary<ulong, uint> blocks = new Dictionary<ulong, uint>();
        }

        private struct ImageInfo : IEquatable<ImageInfo>
        {
            public ImageInfo(string machine, string path, string md5)
            {
                Machine = machine;
                Path = path;
                Md5 = md5;
            }

            public bool Equals(ImageInfo other) => Machine == other.Machine && Path == other.Path && Md5 == other.Md5;

            public override bool Equals(object obj) => obj is ImageInfo other && Equals(other);

            public override int GetHashCode() => (Machine, Path, Md5).GetHashCode();

            public string Machine { get; }

            public string Path { get; }

            public string Md5 { get; }
        }
    }
}
