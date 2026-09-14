using System.Diagnostics;
using System.Globalization;
using Xiangqi.Core.Application;
using Xiangqi.Core.Application.Gomoku;
using Xiangqi.Core.Domain.Gomoku;
using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Presentation.AI;

/// <summary>Desktop adapter for an external Piskvork-compatible engine such as Rapfi. Each probe
/// or search owns a short-lived child process with redirected text pipes. Cancellation terminates
/// the process tree; disposal is idempotent. Engine text is bounded and parsed as untrusted input.
/// The executable and its model/config files remain user-owned and are never copied or packaged.</summary>
public sealed class PiskvorkProcessGomokuAiEngine : IGomokuAiEngine
{
    private const int MaximumLineLength = 4096;
    private readonly string _executablePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _processLock = new();
    private Process? _activeProcess;
    private int _boardSize;
    private GomokuRuleSet _rules;
    private bool _disposed;

    public PiskvorkProcessGomokuAiEngine(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("AI 引擎路径不能为空。", nameof(executablePath));
        _executablePath = Path.GetFullPath(executablePath);
    }

    public string DisplayName => "Rapfi";
    public bool IsReady { get; private set; }

    public async ValueTask InitializeAsync(int boardSize, GomokuRuleSet rules, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = new SquareGrid(boardSize);
        if (boardSize is < 9 or > 22)
            throw new ArgumentOutOfRangeException(nameof(boardSize), "Rapfi 2025-06-15 仅支持 9–22 路棋盘。");
        if (!Enum.IsDefined(rules)) throw new ArgumentOutOfRangeException(nameof(rules));
        if (!File.Exists(_executablePath)) throw new FileNotFoundException("找不到 Rapfi 可执行文件。", _executablePath);
        var directory = Path.GetDirectoryName(_executablePath)!;
        if (!File.Exists(Path.Combine(directory, "config.toml")))
            throw new FileNotFoundException("Rapfi 的 config.toml 必须与可执行文件位于同一目录。", Path.Combine(directory, "config.toml"));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var process = StartProcess();
            using var registration = cancellationToken.Register(() => Kill(process));
            await WriteLine(process, $"START {boardSize}", cancellationToken);
            var response = await ReadSignificantLine(process, cancellationToken);
            if (!response.Equals("OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Piskvork 握手失败：{response}");
            await EndProcess(process);
            _boardSize = boardSize;
            _rules = rules;
            IsReady = true;
        }
        finally { ClearActiveProcess(); _gate.Release(); }
    }

    public async ValueTask<GridPoint> FindMoveAsync(GomokuAiSearchRequest request, CancellationToken cancellationToken = default)
    {
        request = request.Validate();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsReady) throw new InvalidOperationException("Rapfi 尚未初始化。");
        if (request.Position.BoardSize != _boardSize || request.Position.Rules != _rules)
            throw new ArgumentException("AI 请求与初始化棋盘配置不一致。", nameof(request));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.ThinkMilliseconds + 10_000);
            using var process = StartProcess();
            using var registration = timeout.Token.Register(() => Kill(process));
            await WriteLine(process, $"START {_boardSize}", timeout.Token);
            var response = await ReadSignificantLine(process, timeout.Token);
            if (!response.Equals("OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Piskvork 握手失败：{response}");
            await WriteLine(process, $"INFO timeout_turn {request.ThinkMilliseconds}", timeout.Token);
            await WriteLine(process, $"INFO time_left {Math.Max(request.ThinkMilliseconds * 10L, request.ThinkMilliseconds)}", timeout.Token);
            await WriteLine(process, $"INFO max_memory {request.MemoryMegabytes * 1024L * 1024L}", timeout.Token);
            await WriteLine(process, $"INFO rule {(_rules == GomokuRuleSet.RenjuForbiddenMoves ? 2 : 0)}", timeout.Token);
            await WriteLine(process, $"INFO thread_num {request.Threads}", timeout.Token);
            if (request.MaxDepth > 0)
                await WriteLine(process, $"INFO max_depth {request.MaxDepth}", timeout.Token);
            await WriteLine(process, $"INFO strength {request.Strength}", timeout.Token);

            if (request.Position.Stones.Count == 0) await WriteLine(process, "BEGIN", timeout.Token);
            else
            {
                await WriteLine(process, "BOARD", timeout.Token);
                foreach (var stone in request.Position.Stones)
                {
                    var player = stone.Color == StoneColor.Black ? 1 : 2;
                    await WriteLine(process, $"{stone.Point.Column},{stone.Point.Row},{player}", timeout.Token);
                }
                await WriteLine(process, "DONE", timeout.Token);
            }

            var moveText = await ReadSignificantLine(process, timeout.Token);
            var point = ParsePoint(moveText, _boardSize);
            await EndProcess(process);
            return point;
        }
        finally { ClearActiveProcess(); _gate.Release(); }
    }

    public async ValueTask<GomokuAnalysisResult> AnalyzeAsync(GomokuAiSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        request = request.Validate();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsReady) throw new InvalidOperationException("Rapfi 尚未初始化。");
        if (request.Position.BoardSize != _boardSize || request.Position.Rules != _rules)
            throw new ArgumentException("AI 请求与初始化棋盘配置不一致。", nameof(request));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.ThinkMilliseconds + 10_000);
            using var process = StartProcess();
            using var registration = timeout.Token.Register(() => Kill(process));
            await WriteLine(process, $"START {_boardSize}", timeout.Token);
            var response = await ReadSignificantLine(process, timeout.Token);
            if (!response.Equals("OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Piskvork 握手失败：{response}");
            await WriteLine(process, $"INFO timeout_turn {request.ThinkMilliseconds}", timeout.Token);
            await WriteLine(process, $"INFO time_left {Math.Max(request.ThinkMilliseconds * 10L, request.ThinkMilliseconds)}", timeout.Token);
            await WriteLine(process, $"INFO max_memory {request.MemoryMegabytes * 1024L * 1024L}", timeout.Token);
            await WriteLine(process, $"INFO rule {(_rules == GomokuRuleSet.RenjuForbiddenMoves ? 2 : 0)}", timeout.Token);
            await WriteLine(process, $"INFO thread_num {AiDifficultyProfiles.ReplayThreads}", timeout.Token);
            await WriteLine(process, "INFO strength 100", timeout.Token);
            if (request.Position.Stones.Count == 0) await WriteLine(process, "BEGIN", timeout.Token);
            else
            {
                await WriteLine(process, "BOARD", timeout.Token);
                foreach (var stone in request.Position.Stones)
                {
                    var player = stone.Color == StoneColor.Black ? 1 : 2;
                    await WriteLine(process, $"{stone.Point.Column},{stone.Point.Row},{player}", timeout.Token);
                }
                await WriteLine(process, "DONE", timeout.Token);
            }
            var bestMove = ParsePoint(await ReadSignificantLine(process, timeout.Token), _boardSize);

            // Reset to the analyzed position in the same process before asking for Rapfi's static WDL.
            await WriteLine(process, "YXBOARD", timeout.Token);
            foreach (var stone in request.Position.Stones)
            {
                var player = stone.Color == StoneColor.Black ? 1 : 2;
                await WriteLine(process, $"{stone.Point.Column},{stone.Point.Row},{player}", timeout.Token);
            }
            await WriteLine(process, "DONE", timeout.Token);
            await WriteLine(process, "TRACESEARCH", timeout.Token);
            var analysis = await ReadStaticEvaluation(process, timeout.Token);
            await EndProcess(process);
            return analysis with { BestMove = bestMove };
        }
        finally { ClearActiveProcess(); _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        IsReady = false;
        lock (_processLock) if (_activeProcess is { } process) Kill(process);
        return ValueTask.CompletedTask;
    }

    private Process StartProcess()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        process.ErrorDataReceived += (_, _) => { };
        if (!process.Start()) throw new InvalidOperationException("无法启动 Rapfi 进程。");
        process.BeginErrorReadLine();
        lock (_processLock) _activeProcess = process;
        return process;
    }

    private static async Task WriteLine(Process process, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await process.StandardInput.WriteLineAsync(value.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<string> ReadSignificantLine(Process process, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new EndOfStreamException("Rapfi 在返回协议结果前退出。");
            if (line.Length > MaximumLineLength) throw new InvalidDataException("Rapfi 输出行超过长度上限。");
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("MESSAGE ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("DEBUG ", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("UNKNOWN", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Rapfi 协议错误：{line}");
            return line;
        }
    }

    private static GridPoint ParsePoint(string value, int boardSize)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var column) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var row))
            throw new InvalidDataException($"Rapfi 返回的坐标格式无效：{value}");
        var point = new GridPoint(column, row);
        if (column < 0 || row < 0 || column >= boardSize || row >= boardSize)
            throw new InvalidDataException($"Rapfi 返回越界坐标：{value}");
        return point;
    }

    private static async Task<GomokuAnalysisResult> ReadStaticEvaluation(Process process,
        CancellationToken cancellationToken)
    {
        const string marker = "Static Eval[Black]:";
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new EndOfStreamException("Rapfi 在返回局面估值前退出。");
            if (line.Length > MaximumLineLength) throw new InvalidDataException("Rapfi 输出行超过长度上限。");
            var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) continue;
            var payload = line[(markerIndex + marker.Length)..].Trim();
            var valueToken = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var wdlIndex = payload.IndexOf("WDL ", StringComparison.OrdinalIgnoreCase);
            if (!int.TryParse(valueToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var evaluation) || wdlIndex < 0)
                throw new InvalidDataException($"Rapfi 局面估值格式无效：{line}");
            var rateToken = payload[(wdlIndex + 4)..].Split([',', ')', ' '], StringSplitOptions.RemoveEmptyEntries)[0];
            if (!float.TryParse(rateToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || !float.IsFinite(percent))
                throw new InvalidDataException($"Rapfi 胜率格式无效：{line}");
            return new GomokuAnalysisResult(Math.Clamp(percent / 100f, 0f, 1f), evaluation, 0, null);
        }
    }

    private static async Task EndProcess(Process process)
    {
        if (process.HasExited) return;
        try { await process.StandardInput.WriteLineAsync("END"); await process.StandardInput.FlushAsync(); }
        catch (IOException) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { Kill(process); }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void ClearActiveProcess() { lock (_processLock) _activeProcess = null; }
}
