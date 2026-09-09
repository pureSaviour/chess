using System.Text;
using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Domain;

namespace Xiangqi.Presentation.AI;

/// <summary>
/// Godot-owned adapter for UCI xiangqi engines such as Pikafish. It uses Godot 4.7's
/// non-blocking redirected process API and drains output from _Process, so engine analysis never
/// blocks rendering or input. The child process and both pipes are released with this node.
/// </summary>
public partial class UciProcessAiEngine : Node, IXiangqiAiEngine
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);
    private readonly string _executablePath;
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private readonly Dictionary<int, AiVariation> _variations = [];
    private readonly StringBuilder _lineBuffer = new();
    private Godot.FileAccess? _stdio;
    private Godot.FileAccess? _stderr;
    private int _pid = -1;
    private bool _disposed;
    private int _multiPv = 1;
    private TaskCompletionSource<bool>? _uciWaiter;
    private TaskCompletionSource<bool>? _readyWaiter;
    private TaskCompletionSource<AiSearchResult>? _searchWaiter;

    public UciProcessAiEngine(string executablePath)
    {
        _executablePath = executablePath.Trim();
        ProcessMode = ProcessModeEnum.Always;
    }

    public string DisplayName { get; private set; } = "UCI 象棋引擎";
    public bool IsReady { get; private set; }

    public override void _Process(double delta)
    {
        DrainPipe(_stdio, isError: false);
        DrainPipe(_stderr, isError: true);
        if (_pid > 0 && !OS.IsProcessRunning(_pid) && !_disposed)
            FailAll(new InvalidOperationException("AI 引擎进程已意外退出。"));
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReady) return;
        if (_stdio is null) StartProcess();

        _uciWaiter = NewWaiter<bool>();
        Send("uci");
        await _uciWaiter.Task.WaitAsync(HandshakeTimeout, cancellationToken);
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
            Send($"go movetime {request.MoveTimeMilliseconds}");
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
            if (_stdio is not null) Send("quit");
        }
        catch
        {
            // The process may already have closed its pipe.
        }
        await Task.Yield();
        if (_pid > 0 && OS.IsProcessRunning(_pid)) OS.Kill(_pid);
        _stdio?.Dispose();
        _stderr?.Dispose();
        _stdio = null;
        _stderr = null;
        _pid = -1;
    }

    public override void _ExitTree()
    {
        if (!_disposed) _ = DisposeAsync();
    }

    private void StartProcess()
    {
        if (string.IsNullOrWhiteSpace(_executablePath) || !System.IO.File.Exists(_executablePath))
            throw new FileNotFoundException("找不到 AI 引擎程序。", _executablePath);

        var process = OS.ExecuteWithPipe(_executablePath, [], blocking: false);
        if (process.Count == 0)
            throw new InvalidOperationException("无法启动 AI 引擎进程。请检查文件权限与平台兼容性。");
        _stdio = process["stdio"].AsGodotObject() as Godot.FileAccess
                 ?? throw new InvalidOperationException("AI 引擎未提供标准输入输出管道。");
        _stderr = process["stderr"].AsGodotObject() as Godot.FileAccess;
        _pid = process["pid"].AsInt32();
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
        if (_stdio is null) throw new InvalidOperationException("AI 引擎尚未启动。");
        _stdio.StoreString(command + "\n");
        _stdio.Flush();
    }

    private void DrainPipe(Godot.FileAccess? pipe, bool isError)
    {
        if (pipe is null) return;
        var bytes = pipe.GetBuffer(4096);
        if (bytes.Length == 0) return;
        var text = Encoding.UTF8.GetString(bytes);
        if (isError)
        {
            if (!string.IsNullOrWhiteSpace(text)) GD.PrintErr($"[Xiangqi.AI] {text.TrimEnd()}");
            return;
        }
        _lineBuffer.Append(text);
        ConsumeCompleteLines();
    }

    private void ConsumeCompleteLines()
    {
        while (true)
        {
            var value = _lineBuffer.ToString();
            var newline = value.IndexOf('\n');
            if (newline < 0) return;
            var line = value[..newline].TrimEnd('\r').Trim();
            _lineBuffer.Remove(0, newline + 1);
            if (line.Length > 0) HandleLine(line);
        }
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
}
