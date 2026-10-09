using System.Text;

namespace DaedalusStudio.Terminal;

/// <summary>What the parser recognizes; the emulator implements it.</summary>
internal interface IVtHandler
{
    void Print(int rune);

    /// <summary>A C0 control (BEL, BS, HT, LF, VT, FF, CR, SO, SI…).</summary>
    void Execute(int control);

    /// <summary>
    /// A control sequence: <paramref name="parameters"/> (-1 when omitted), which of them are colon sub-parameters,
    /// the private marker (<c>?</c>, <c>&gt;</c>, <c>=</c>, <c>&lt;</c> or 0), intermediates and the final character.
    /// </summary>
    void CsiDispatch(ReadOnlySpan<int> parameters, ReadOnlySpan<bool> subParameters, char marker, string intermediates, char final);

    void EscDispatch(string intermediates, char final);

    /// <summary>An operating system command, e.g. <c>0;title</c> or <c>8;;https://…</c>.</summary>
    void OscDispatch(string data);
}

/// <summary>
/// The VT500-series parser state machine (after Paul Williams' DEC ANSI parser), on Unicode code points. DCS, SOS, PM and
/// APC strings are consumed and ignored; OSC strings end with BEL or ST.
/// </summary>
internal sealed class VtParser(IVtHandler handler)
{
    private const int MaxParameters = 32;
    private const int MaxOscLength = 64 * 1024;

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        DcsIgnore,
        IgnoreString,
    }

    private readonly int[] _parameters = new int[MaxParameters];
    private readonly bool[] _subParameters = new bool[MaxParameters];
    private readonly StringBuilder _intermediates = new();
    private readonly StringBuilder _osc = new();
    private State _state;
    private int _count;
    private bool _hasParameter;
    private char _marker;
    private bool _escapeInString;

    public void Feed(int rune)
    {
        // Anywhere: CAN and SUB abort a sequence; ESC starts one (except where a string may end with ESC \).
        if (rune is 0x18 or 0x1A && _state != State.Ground)
        {
            _state = State.Ground;
            return;
        }

        switch (_state)
        {
            case State.Ground:
                if (rune == 0x1B)
                {
                    EnterEscape();
                }
                else if (rune < 0x20 || rune == 0x7F)
                {
                    if (rune != 0x7F)
                    {
                        handler.Execute(rune);
                    }
                }
                else if (rune is >= 0x80 and < 0xA0)
                {
                    // C1 controls are ignored in UTF-8 mode, as xterm does.
                }
                else
                {
                    handler.Print(rune);
                }

                break;

            case State.Escape:
                if (rune == 0x1B)
                {
                    EnterEscape();
                }
                else if (rune < 0x20)
                {
                    handler.Execute(rune);
                }
                else if (rune is >= 0x20 and <= 0x2F)
                {
                    _intermediates.Append((char)rune);
                    _state = State.EscapeIntermediate;
                }
                else if (rune == '[')
                {
                    EnterCsi();
                }
                else if (rune == ']')
                {
                    _osc.Clear();
                    _escapeInString = false;
                    _state = State.OscString;
                }
                else if (rune == 'P')
                {
                    _escapeInString = false;
                    _state = State.DcsIgnore;
                }
                else if (rune is 'X' or '^' or '_')
                {
                    _escapeInString = false;
                    _state = State.IgnoreString;
                }
                else if (rune is >= 0x30 and <= 0x7E)
                {
                    handler.EscDispatch(string.Empty, (char)rune);
                    _state = State.Ground;
                }
                else
                {
                    _state = State.Ground;
                }

                break;

            case State.EscapeIntermediate:
                if (rune == 0x1B)
                {
                    EnterEscape();
                }
                else if (rune < 0x20)
                {
                    handler.Execute(rune);
                }
                else if (rune is >= 0x20 and <= 0x2F)
                {
                    _intermediates.Append((char)rune);
                }
                else if (rune is >= 0x30 and <= 0x7E)
                {
                    handler.EscDispatch(_intermediates.ToString(), (char)rune);
                    _state = State.Ground;
                }
                else
                {
                    _state = State.Ground;
                }

                break;

            case State.CsiEntry:
            case State.CsiParam:
                if (rune == 0x1B)
                {
                    EnterEscape();
                }
                else if (rune < 0x20)
                {
                    handler.Execute(rune);
                }
                else if (rune is >= '0' and <= '9')
                {
                    _hasParameter = true;
                    ref var value = ref _parameters[_count];
                    value = value < 0 ? rune - '0' : Math.Min(value * 10 + (rune - '0'), 99_999);
                    _state = State.CsiParam;
                }
                else if (rune is ';' or ':')
                {
                    if (_count < MaxParameters - 1)
                    {
                        _count++;
                        _parameters[_count] = -1;
                        _subParameters[_count] = rune == ':';
                    }

                    _hasParameter = true;
                    _state = State.CsiParam;
                }
                else if (rune is '<' or '=' or '>' or '?')
                {
                    if (_state == State.CsiEntry)
                    {
                        _marker = (char)rune;
                        _state = State.CsiParam;
                    }
                    else
                    {
                        _state = State.CsiIgnore;
                    }
                }
                else if (rune is >= 0x20 and <= 0x2F)
                {
                    _intermediates.Append((char)rune);
                    _state = State.CsiIntermediate;
                }
                else if (rune is >= 0x40 and <= 0x7E)
                {
                    DispatchCsi((char)rune);
                }
                else
                {
                    _state = State.CsiIgnore;
                }

                break;

            case State.CsiIntermediate:
                if (rune == 0x1B)
                {
                    EnterEscape();
                }
                else if (rune < 0x20)
                {
                    handler.Execute(rune);
                }
                else if (rune is >= 0x20 and <= 0x2F)
                {
                    _intermediates.Append((char)rune);
                }
                else if (rune is >= 0x40 and <= 0x7E)
                {
                    DispatchCsi((char)rune);
                }
                else
                {
                    _state = State.CsiIgnore;
                }

                break;

            case State.CsiIgnore:
                if (rune == 0x1B)
                {
                    EnterEscape();
                }
                else if (rune < 0x20)
                {
                    handler.Execute(rune);
                }
                else if (rune is >= 0x40 and <= 0x7E)
                {
                    _state = State.Ground;
                }

                break;

            case State.OscString:
                if (rune == 0x07)
                {
                    EndOsc();
                }
                else if (_escapeInString)
                {
                    _escapeInString = false;
                    if (rune == '\\')
                    {
                        EndOsc();
                    }
                    else
                    {
                        // ESC followed by something else: the OSC ends and a new escape sequence starts.
                        EndOsc();
                        EnterEscape();
                        Feed(rune);
                    }
                }
                else if (rune == 0x1B)
                {
                    _escapeInString = true;
                }
                else if (rune >= 0x20 && _osc.Length < MaxOscLength)
                {
                    _osc.Append(char.ConvertFromUtf32(rune));
                }

                break;

            case State.DcsIgnore:
            case State.IgnoreString:
                if (_escapeInString)
                {
                    _escapeInString = false;
                    _state = State.Ground;
                    if (rune != '\\')
                    {
                        EnterEscape();
                        Feed(rune);
                    }
                }
                else if (rune == 0x1B)
                {
                    _escapeInString = true;
                }
                else if (rune == 0x07 && _state == State.IgnoreString)
                {
                    _state = State.Ground;
                }

                break;
        }
    }

    private void EnterEscape()
    {
        _intermediates.Clear();
        _state = State.Escape;
    }

    private void EnterCsi()
    {
        _intermediates.Clear();
        _count = 0;
        _parameters[0] = -1;
        _subParameters[0] = false;
        _hasParameter = false;
        _marker = '\0';
        _state = State.CsiEntry;
    }

    private void DispatchCsi(char final)
    {
        var count = _hasParameter ? _count + 1 : 0;
        handler.CsiDispatch(_parameters.AsSpan(0, count), _subParameters.AsSpan(0, count), _marker, _intermediates.ToString(), final);
        _state = State.Ground;
    }

    private void EndOsc()
    {
        handler.OscDispatch(_osc.ToString());
        _osc.Clear();
        _state = State.Ground;
    }
}
