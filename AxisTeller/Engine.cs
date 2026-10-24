namespace AxisTeller;

/// <summary>提示播放状态机：单步推进 / 回退 / 跳到下一循环 / 重来。</summary>
public sealed class PlaybackEngine
{
    private Axis? _axis;

    public Axis? Axis => _axis;
    public int CycleIndex { get; private set; }
    public int StepIndex { get; private set; }
    public int Round { get; private set; } = 1;
    public bool Finished { get; private set; }

    public event Action? Changed;

    public int TotalRounds => Math.Max(1, _axis?.Loops ?? 1);
    public int TotalSteps => _axis?.Cycles.Sum(c => c.Steps.Count) ?? 0;

    public bool HasAnyStep => _axis?.Cycles.Any(c => c.Steps.Count > 0) == true;

    public Cycle? CurrentCycle =>
        _axis is not null && CycleIndex >= 0 && CycleIndex < _axis.Cycles.Count ? _axis.Cycles[CycleIndex] : null;

    public Step? CurrentStep
    {
        get
        {
            var cycle = CurrentCycle;
            if (cycle is null || StepIndex < 0 || StepIndex >= cycle.Steps.Count) return null;
            return cycle.Steps[StepIndex];
        }
    }

    /// <summary>当前动作在本轮中的序号（1 起）。</summary>
    public int StepNumberInRound
    {
        get
        {
            if (_axis is null) return 0;
            var before = 0;
            for (var i = 0; i < CycleIndex && i < _axis.Cycles.Count; i++) before += _axis.Cycles[i].Steps.Count;
            return before + StepIndex + 1;
        }
    }

    public void Attach(Axis axis, bool infinite)
    {
        _axis = axis;
        Reset(infinite);
    }

    public void Reset(bool infinite)
    {
        Finished = false;
        Round = 1;
        CycleIndex = FirstCycleWithSteps();
        StepIndex = 0;
        if (_axis is not null && !HasAnyStep) Finished = false;
        Changed?.Invoke();
    }

    public void Next(bool infinite)
    {
        if (_axis is null) return;
        if (Finished)
        {
            Reset(infinite);
            return;
        }

        var c = CycleIndex;
        var s = StepIndex;
        var r = Round;
        if (TryForward(_axis, ref c, ref s, ref r, infinite, TotalRounds))
        {
            CycleIndex = c;
            StepIndex = s;
            Round = r;
        }
        else
        {
            Finished = true;
        }

        Changed?.Invoke();
    }

    public void Previous()
    {
        if (_axis is null) return;
        Finished = false;
        var c = CycleIndex;
        var s = StepIndex;
        var r = Round;
        if (TryBackward(_axis, ref c, ref s, ref r))
        {
            CycleIndex = c;
            StepIndex = s;
            Round = r;
        }

        Changed?.Invoke();
    }

    /// <summary>跳过当前循环剩余动作，直接到下一循环的第一个动作。</summary>
    public void NextCycle(bool infinite)
    {
        if (_axis is null) return;
        Finished = false;
        if (CurrentCycle is null) return;

        if (CycleIndex + 1 < _axis.Cycles.Count)
        {
            CycleIndex++;
            StepIndex = 0;
            if (!SkipEmptyForward()) Finish(infinite);
        }
        else if (Round < TotalRounds || infinite)
        {
            Round++;
            CycleIndex = FirstCycleWithSteps();
            StepIndex = 0;
            if (!SkipEmptyForward()) Finish(infinite);
        }
        else
        {
            Finished = true;
        }

        Changed?.Invoke();
    }

    /// <summary>跳到指定的循环/动作（编辑器预览用）。</summary>
    public void JumpTo(int cycleIndex, int stepIndex)
    {
        if (_axis is null) return;
        if (cycleIndex < 0 || cycleIndex >= _axis.Cycles.Count) return;
        CycleIndex = cycleIndex;
        var count = _axis.Cycles[cycleIndex].Steps.Count;
        StepIndex = count == 0 ? 0 : Math.Clamp(stepIndex, 0, count - 1);
        Round = 1;
        Finished = false;
        Changed?.Invoke();
    }

    /// <summary>当前动作之后的 n 个动作（用于悬浮窗的“接下来”）。</summary>
    public List<Step> PeekForward(int count, bool infinite)
    {
        var list = new List<Step>();
        if (_axis is null || !HasAnyStep) return list;
        var c = CycleIndex;
        var s = StepIndex;
        var r = Round;
        for (var i = 0; i < count; i++)
        {
            if (!TryForward(_axis, ref c, ref s, ref r, infinite, TotalRounds)) break;
            var cycle = _axis.Cycles[c];
            if (s >= 0 && s < cycle.Steps.Count) list.Add(cycle.Steps[s]);
        }

        return list;
    }

    /// <summary>下一动作所属循环（悬浮窗文案用）。</summary>
    public (int cycle, int round, bool wraps)? PeekNextPosition(bool infinite)
    {
        if (_axis is null || !HasAnyStep) return null;
        var c = CycleIndex;
        var s = StepIndex;
        var r = Round;
        if (!TryForward(_axis, ref c, ref s, ref r, infinite, TotalRounds)) return null;
        return (c, r, c < CycleIndex || r != Round);
    }

    private void Finish(bool infinite)
    {
        if (infinite) return;
        Finished = true;
    }

    private int FirstCycleWithSteps()
    {
        if (_axis is null) return 0;
        for (var i = 0; i < _axis.Cycles.Count; i++)
            if (_axis.Cycles[i].Steps.Count > 0)
                return i;
        return 0;
    }

    private bool SkipEmptyForward()
    {
        if (_axis is null) return false;
        var guard = 0;
        while (guard++ < 512)
        {
            if (CycleIndex >= 0 && CycleIndex < _axis.Cycles.Count)
            {
                var steps = _axis.Cycles[CycleIndex].Steps.Count;
                if (steps > 0 && StepIndex < steps) return true;
            }

            if (CycleIndex + 1 < _axis.Cycles.Count)
            {
                CycleIndex++;
                StepIndex = 0;
                continue;
            }

            return false;
        }

        return false;
    }

    private static bool TryForward(Axis axis, ref int cycle, ref int step, ref int round, bool infinite, int totalRounds)
    {
        var cycles = axis.Cycles;
        if (cycle >= 0 && cycle < cycles.Count && step + 1 < cycles[cycle].Steps.Count)
        {
            step++;
            return true;
        }

        for (var c = cycle + 1; c < cycles.Count; c++)
        {
            if (cycles[c].Steps.Count == 0) continue;
            cycle = c;
            step = 0;
            return true;
        }

        if (round < totalRounds || infinite)
        {
            round++;
            for (var c = 0; c < cycles.Count; c++)
            {
                if (cycles[c].Steps.Count == 0) continue;
                cycle = c;
                step = 0;
                return true;
            }
        }

        return false;
    }

    private static bool TryBackward(Axis axis, ref int cycle, ref int step, ref int round)
    {
        var cycles = axis.Cycles;
        if (cycle >= 0 && cycle < cycles.Count && step > 0)
        {
            step--;
            return true;
        }

        for (var c = cycle - 1; c >= 0; c--)
        {
            if (cycles[c].Steps.Count == 0) continue;
            cycle = c;
            step = cycles[c].Steps.Count - 1;
            return true;
        }

        if (round > 1)
        {
            round--;
            for (var c = cycles.Count - 1; c >= 0; c--)
            {
                if (cycles[c].Steps.Count == 0) continue;
                cycle = c;
                step = cycles[c].Steps.Count - 1;
                return true;
            }
        }

        cycle = 0;
        step = 0;
        return false;
    }
}
