using System.Collections.Concurrent;
using System.Diagnostics;
using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Domain;

namespace Xiangqi.Presentation.AI;

public readonly record struct UciEngineResourceOptions(int Threads = 1, int HashMegabytes = 64)
{
    public UciEngineResourceOptions Validate() => Threads is < 1 or > 256
        ? throw new ArgumentOutOfRangeException(nameof(Threads))
        : HashMegabytes is < 16 or > 4096
            ? throw new ArgumentOutOfRangeException(nameof(HashMegabytes))
            : this;
}

/// <summary>
/// Godot-owned adapter for UCI xiangqi engines such as Pikafish. A shell-free .NET child process
/// runs in the executable directory so relative NNUE paths work even below Unicode user profiles.
/// Background pipe callbacks only enqueue bounded lines; _Process parses them on the Godot thread.
/// The child process, pipes and callbacks are released with this node.
/// </summary>
public partial class UciProcessAiEngine : Node, IXiangqiAiEngine
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);
    private const int MaximumOutputLines = 4096;
    private const int MaximumLineLength = 8192;
    private const int MaximumLinesPerFrame = 128;
    private readonly string _executablePath;
    private readonly UciEngineResourceOptions _resources;
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private readonly Dictionary<int, AiVariation> _variations = [];
    private readonly ConcurrentQueue<OutputLine> _output = new();
    private Process? _process;
    private int _queuedLines;
    private int _outputOverflow;
    private bool _disposed;
    private int _multiPv = 1;
    private TaskCompletionSource<bool>? _uciWaiter;
    private TaskCompletionSource<bool>? _readyWaiter;
    private TaskCompletionSource<AiSearchResult>? _searchWaiter;

    public UciProcessAiEngine(string executablePath, UciEngineResourceOptions resources = default)
    {
        _executablePath = executablePath.Trim();
        _resources = (resources == default ? new UciEngineResourceOptions(1, 64) : resources).Validate();
        ProcessMode = ProcessModeEnum.Always;
    }

    public string DisplayName { get; private set; } = "UCI 象棋引擎";
    public bool IsReady { get; private set; }

    public override void _Process(double delta)
    {
        if (Interlocked.Exchange(ref _outputOverflow, 0) != 0)
            FailAll(new InvalidDataException("AI 引擎输出队列超过上限。"));
        var processed = 0;
        while (processed++ < MaximumLinesPerFrame && _output.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _queuedLines);
            if (item.Text.Length > MaximumLineLength) { FailAll(new InvalidDataException("AI 引擎输出行超过长度上限。")); continue; }
            if (item.IsError) { if (!string.IsNullOrWhiteSpace(item.Text)) GD.PrintErr($"[Xiangqi.AI] {item.Text}"); }
            else if (item.Text.Length > 0) HandleLine(item.Text.Trim());
        }
        if (_process is { HasExited: true } && !_disposed)
            FailAll(new InvalidOperationException("AI 引擎进程已意外退出。"));
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReady) return;
        if (_process is null) StartProcess();

        _uciWaiter = NewWaiter<bool>();
        Send("uci");
        await _uciWaiter.Task.WaitAsync(HandshakeTimeout, cancellationToken);
        Send($"setoption name Threads value {_resources.Threads}");
        Send($"setoption name Hash value {_resources.HashMegabytes}");
        await WaitUntilReadyAsync(cancellationToken);
        IsReady = true;
    }

    public async ValueTask NewGameAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _searchGate.WaitAsync(cancellationToken);
        try
        {
            Send("ucinewgame");
            await WaitUntilReadyAsync(cancellationToken);
        }
        finally
        {
            _searchGate.Release();
        }
    }

    public async ValueTask<AiSearchResult> SearchAsync(
        GameSnapshot position,
        AiSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        request = request.Validate();
        await InitializeAsync(cancellationToken);
        await _searchGate.WaitAsync(cancellationToken);
        try
        {
            if (_multiPv != request.VariationCount)
            {
                Send($"setoption name MultiPV value {request.VariationCount}");
                _multiPv = request.VariationCount;
                await WaitUntilReadyAsync(cancellationToken);
            }

            _variations.Clear();
            _searchWaiter = NewWaiter<AiSearchResult>();
            Send($"position fen {XiangqiUciNotation.ToFen(position)}");
            Send(request.MaxDepth > 0
                ? $"go movetime {request.MoveTimeMilliseconds} depth {request.MaxDepth}"
                : $"go movetime {request.MoveTimeMilliseconds}");
            try
            {
                var timeout = TimeSpan.FromMilliseconds(request.MoveTimeMilliseconds + 5_000);
                return await _searchWaiter.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Send("stop");
                await AwaitStoppedSearchAsync();
                throw;
            }
            catch (TimeoutException)
            {
                Send("stop");
                await AwaitStoppedSearchAsync();
                throw new TimeoutException("AI 引擎未在限定时间内返回着法。");
            }
            finally
            {
                _searchWaiter = null;
            }
        }
        finally
        {
            _searchGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        IsReady = false;
        FailAll(new ObjectDisposedException(nameof(UciProcessAiEngine)));
        try
        {
            if (_process is { HasExited: false }) Send("quit");
        }
        catch
        {
            // The process may already have closed its pipe.
        }
        if (_process is { } process)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            process.OutputDataReceived -= OnOutput;
            process.ErrorDataReceived -= OnError;
            process.Dispose();
        }
        _process = null;
    }

    public override void _ExitTree()
    {
        if (!_disposed) _ = DisposeAsync();
    }

    private void StartProcess()
    {
        if (string.IsNullOrWhiteSpace(_executablePath) || !System.IO.File.Exists(_executablePath))
            throw new FileNotFoundException("找不到 AI 引擎程序。", _executablePath);

        var fullPath = Path.GetFullPath(_executablePath);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fullPath,
                WorkingDirectory = Path.GetDirectoryName(fullPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        process.OutputDataReceived += OnOutput;
        process.ErrorDataReceived += OnError;
        if (!process.Start()) throw new InvalidOperationException("无法启动 AI 引擎进程。请检查文件权限与平台兼容性。");
        process.BeginOutputReadLine(); process.BeginErrorReadLine(); _process = process;
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        _readyWaiter = NewWaiter<bool>();
        Send("isready");
        await _readyWaiter.Task.WaitAsync(HandshakeTimeout, cancellationToken);
    }

    private async Task AwaitStoppedSearchAsync()
    {
        var waiter = _searchWaiter;
        if (waiter is null) return;
        try
        {
            await waiter.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Disposal or a later readiness check will recover a non-responsive engine.
        }
    }

    private void Send(string command)
    {
        if (_process is null || _process.HasExited) throw new InvalidOperationException("AI 引擎尚未启动。");
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }

    private void OnOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null) Enqueue(args.Data, false);
    }

    private void OnError(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null) Enqueue(args.Data, true);
    }

    private void Enqueue(string text, bool isError)
    {
        if (Interlocked.Increment(ref _queuedLines) > MaximumOutputLines)
        { Interlocked.Decrement(ref _queuedLines); Interlocked.Exchange(ref _outputOverflow, 1); return; }
        _output.Enqueue(new(text, isError));
    }

    private void HandleLine(string line)
    {
        if (line.StartsWith("id name ", StringComparison.Ordinal))
        {
            DisplayName = line[8..].Trim();
            return;
        }
        if (line == "uciok")
        {
            _uciWaiter?.TrySetResult(true);
            return;
        }
        if (line == "readyok")
        {
            _readyWaiter?.TrySetResult(true);
            return;
        }
        if (line.StartsWith("info ", StringComparison.Ordinal))
        {
            TryCaptureVariation(line);
            return;
        }
        if (!line.StartsWith("bestmove ", StringComparison.Ordinal) || _searchWaiter is null) return;
        var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        if (!XiangqiUciNotation.TryParseMove(token, out var move))
        {
            _searchWaiter.TrySetException(new InvalidOperationException($"AI 引擎返回了无效着法：{token ?? "(空)"}"));
            return;
        }
        var variations = _variations.Values.OrderBy(item => item.Rank).ToArray();
        if (variations.Length == 0)
            variations = [new AiVariation(1, move, 0, null, null, [move])];
        _searchWaiter.TrySetResult(new AiSearchResult(move, variations));
    }

    private void TryCaptureVariation(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pvIndex = Array.IndexOf(tokens, "pv");
        if (pvIndex < 0 || pvIndex + 1 >= tokens.Length ||
            !XiangqiUciNotation.TryParseMove(tokens[pvIndex + 1], out var first)) return;
        var rank = ReadIntAfter(tokens, "multipv") ?? 1;
        var depth = ReadIntAfter(tokens, "depth") ?? 0;
        int? cp = null;
        int? mate = null;
        var scoreIndex = Array.IndexOf(tokens, "score");
        if (scoreIndex >= 0 && scoreIndex + 2 < tokens.Length &&
            int.TryParse(tokens[scoreIndex + 2], out var score))
        {
            if (tokens[scoreIndex + 1] == "cp") cp = score;
            else if (tokens[scoreIndex + 1] == "mate") mate = score;
        }
        var pv = new List<ChessMove>();
        for (var index = pvIndex + 1; index < tokens.Length; index++)
        {
            if (!XiangqiUciNotation.TryParseMove(tokens[index], out var parsed)) break;
            pv.Add(parsed);
        }
        _variations[rank] = new AiVariation(rank, first, depth, cp, mate, pv);
    }

    private static int? ReadIntAfter(string[] tokens, string name)
    {
        var index = Array.IndexOf(tokens, name);
        return index >= 0 && index + 1 < tokens.Length && int.TryParse(tokens[index + 1], out var value)
            ? value
            : null;
    }

    private void FailAll(Exception error)
    {
        IsReady = false;
        _uciWaiter?.TrySetException(error);
        _readyWaiter?.TrySetException(error);
        _searchWaiter?.TrySetException(error);
    }

    private static TaskCompletionSource<T> NewWaiter<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct OutputLine(string Text, bool IsError);
}
