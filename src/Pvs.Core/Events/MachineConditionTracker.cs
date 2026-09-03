using Pvs.Core.Serial;

namespace Pvs.Core.Events;

/// <summary>The machine's live operating condition, derived from its own Sony R1 real-time stream.</summary>
public enum MachineCondition
{
    /// <summary>No R1 seen yet (e.g. just after a PVS restart) — genuinely unknown, not "stopped".</summary>
    Unknown,
    /// <summary>R1FL / never came on-line.</summary>
    Offline,
    /// <summary>On-line and in AUTO, but not started (R1AU/R1OL, no run yet).</summary>
    Idle,
    /// <summary>Actively placing — R1ST (auto started), R1PE/R1LD, or a board completing.</summary>
    Mounting,
    /// <summary>In auto but waiting for a board (R1PW) — running, not placing.</summary>
    Starved,
    /// <summary>Auto operation stopped (R1SP).</summary>
    Stopped,
    /// <summary>Emergency stop (R1ES).</summary>
    EmergencyStop,
    /// <summary>Operator left AUTO mode (R1MA).</summary>
    NotAuto,
    /// <summary>Recovering from a pickup/other error (R1RC).</summary>
    Recovering,
    /// <summary>Control-panel input inhibited (R1HT), until R1SH.</summary>
    Halted
}

/// <summary>
/// Latches ONE machine's operating condition from its Sony R1 "Operation Information" stream (SI-F manual
/// §8.2, Table 8-5). This is the machine's OWN declared state — the honest answer to "is it mounting" — as
/// opposed to <see cref="BoardRateTracker"/>'s boards-per-hour, which is a productive-time RATE that holds its
/// last value when the line stops and so must not be read as liveness.
///
/// Pure and hardware-free: fed decoded <see cref="SonyMessage"/>s, it exposes the current condition and when it
/// last changed. READ-ONLY by design — it reflects state, it never sends a control command.
/// </summary>
public sealed class MachineConditionTracker
{
    public MachineCondition Condition { get; private set; } = MachineCondition.Unknown;
    /// <summary>When the current condition was entered (default = never).</summary>
    public DateTime Since { get; private set; }
    /// <summary>The last board-complete time seen (for a "mounting but silent?" staleness check upstream).</summary>
    public DateTime LastBoardAt { get; private set; }

    private void Set(MachineCondition c, DateTime at)
    {
        if (Condition == c) return;
        Condition = c;
        Since = at;
    }

    /// <summary>Feed one decoded message with its arrival time; updates the latched condition.</summary>
    public void Observe(SonyMessage message, DateTime at)
    {
        // A board completing is unambiguous proof the head is placing — it wins over any latched state.
        if (message.Kind == MessageKind.BoardComplete)
        {
            LastBoardAt = at;
            Set(MachineCondition.Mounting, at);
            return;
        }

        if (message.Kind != MessageKind.Status || message.StatusCode is null) return;

        switch (message.StatusCode)
        {
            case "ST":                 // auto operation started
            case "PE":                 // released from board-wait -> placing again
            case "LD":                 // a PWB reached the placement position
                Set(MachineCondition.Mounting, at);
                break;
            case "PW":                 // ready but waiting for a board (in auto, not placing)
                Set(MachineCondition.Starved, at);
                break;
            case "SP":                 // auto operation stopped
                Set(MachineCondition.Stopped, at);
                break;
            case "ES":                 // emergency stop
                Set(MachineCondition.EmergencyStop, at);
                break;
            case "MA":                 // entered a mode other than AUTO
                Set(MachineCondition.NotAuto, at);
                break;
            case "RC":                 // recovery action underway
                Set(MachineCondition.Recovering, at);
                break;
            case "HT":                 // control-panel input inhibited
                Set(MachineCondition.Halted, at);
                break;
            case "SH":                 // input-inhibition released -> back to idle-in-auto
                if (Condition == MachineCondition.Halted) Set(MachineCondition.Idle, at);
                break;
            case "FL":                 // went off-line
                Set(MachineCondition.Offline, at);
                break;
            case "AU":                 // entered automatic mode (ready) — only an upgrade from not-running
            case "OL":                 // came on-line
                if (Condition is MachineCondition.Unknown or MachineCondition.Offline or MachineCondition.NotAuto)
                    Set(MachineCondition.Idle, at);
                break;
            // OF/RO/RF/CM/SEP/WA/AM/PT/RMP/SEM/ORG/ER/BO/DT/TC: not operating-state transitions — ignored here.
        }
    }

    /// <summary>True when the machine is in automatic run (placing or waiting for a board).</summary>
    public bool IsRunning => Condition is MachineCondition.Mounting or MachineCondition.Starved;
}
