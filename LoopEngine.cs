// Pedal Master Loop - section loop logic.
//
// Pure logic, no ReBuzz or WPF types, so it can be tested headless.
// Step() is called from the audio thread (the machine's Work()). Everything
// the GUI reads is a volatile int/bool; everything the GUI writes is either a
// volatile config field or a request flag that Step() consumes.

using System.Threading;

namespace Pedal.MasterLoop
{
    public enum LoopPhase
    {
        Idle = 0,       // stopped, disabled, or play position outside the range
        Looping = 1,    // looping the current section
        Leaving = 2,    // final pass: loop end moved out to the end of the next section
        Returning = 3,  // final pass: loop start moved back to the song start
        Held = 4        // Hold is on
    }

    public sealed class LoopEngine
    {
        public const int MaxSections = 512;
        public const int MaxRepeats = 999;
        public const int AtEndHold = 0;
        public const int AtEndBackToStart = 1;

        // ---- configuration (GUI / setters write, Step reads) ----------------
        volatile int _songStart = 0;
        volatile int _sectionLen = 256;
        volatile int _sectionCount = 1;
        readonly int[] _repeats = new int[MaxSections];   // 0 = until Next

        public volatile bool Enabled = true;
        public volatile bool Hold;
        public volatile int AtEnd = AtEndHold;

        // ---- requests (any thread sets, Step consumes) ----------------------
        int _reqNext, _reqToStart, _reqOneMore, _reqResync, _reqRestart;

        // ---- runtime (Step writes, GUI reads) -------------------------------
        volatile int _cur = -1;
        volatile int _pass;
        volatile int _extra;
        volatile bool _nextQueued;
        volatile bool _toStartQueued;
        volatile int _phase;
        volatile int _wantStart;
        volatile int _wantEnd = 16;
        volatile bool _steering;
        volatile bool _restorePending;
        volatile int _pos;

        bool _active;
        bool _restartArmed;      // a jump back to the section start is on its way
        int _prev = -1;

        public LoopEngine()
        {
            for (int i = 0; i < MaxSections; i++) _repeats[i] = 4;
        }

        // ---- configuration access -------------------------------------------
        public int SongStart => _songStart;
        public int SectionLen => _sectionLen;
        public int SectionCount => _sectionCount;
        public int SongEnd => _songStart + _sectionCount * _sectionLen;

        public void SetLayout(int songStart, int sectionLen, int sectionCount)
        {
            if (songStart < 0) songStart = 0;
            if (sectionLen < 1) sectionLen = 1;
            if (sectionCount < 1) sectionCount = 1;
            if (sectionCount > MaxSections) sectionCount = MaxSections;
            _songStart = songStart;
            _sectionLen = sectionLen;
            _sectionCount = sectionCount;
            Interlocked.Exchange(ref _reqResync, 1);
        }

        public int GetRepeats(int section)
        {
            return (uint)section < MaxSections ? Volatile.Read(ref _repeats[section]) : 0;
        }

        public void SetRepeats(int section, int value)
        {
            if ((uint)section >= MaxSections) return;
            if (value < 0) value = 0;
            if (value > MaxRepeats) value = MaxRepeats;
            Volatile.Write(ref _repeats[section], value);
        }

        // ---- requests -------------------------------------------------------
        public void RequestNext() { Interlocked.Exchange(ref _reqNext, 1); }
        public void RequestToStart() { Interlocked.Exchange(ref _reqToStart, 1); }
        public void RequestOneMore() { Interlocked.Increment(ref _reqOneMore); }

        /// <summary>The caller is about to jump playback to the start of the current
        /// section: count that as a fresh pass 1, not as a completed pass.</summary>
        public void RequestRestart() { Interlocked.Exchange(ref _reqRestart, 1); }

        // ---- runtime readout ------------------------------------------------
        public int Current => _cur;              // 0-based section, -1 when idle
        public int Pass => _pass;                // 1-based
        public int Extra => _extra;              // One More presses this visit
        public bool NextQueued => _nextQueued;
        public bool ToStartQueued => _toStartQueued;
        public LoopPhase Phase => (LoopPhase)_phase;
        public int Position => _pos;
        public int WantStart => _wantStart;
        public int WantEnd => _wantEnd;
        public bool Steering => _steering;       // machine should enforce WantStart/WantEnd

        // Set once when steering ends: the machine puts the full range back,
        // then clears it.
        public bool RestorePending
        {
            get => _restorePending;
            set => _restorePending = value;
        }

        /// <summary>Ask the machine to put the full range back (used after song load).</summary>
        public void RequestRestore()
        {
            _wantStart = _songStart;
            _wantEnd = SongEnd;
            _restorePending = true;
        }

        public int SectionStart(int section) { return _songStart + section * _sectionLen; }
        public int SectionEnd(int section) { return _songStart + (section + 1) * _sectionLen; }

        void GoIdle(bool restore)
        {
            if (_active || _steering)
            {
                _wantStart = _songStart;
                _wantEnd = SongEnd;
                if (restore) _restorePending = true;
            }
            _active = false;
            _steering = false;
            _cur = -1;
            _pass = 0;
            _extra = 0;
            _nextQueued = false;
            _toStartQueued = false;
            _phase = (int)LoopPhase.Idle;
            _prev = -1;
            _restartArmed = false;
            Interlocked.Exchange(ref _reqRestart, 0);
            // Requests made while idle are dropped.
            Interlocked.Exchange(ref _reqNext, 0);
            Interlocked.Exchange(ref _reqToStart, 0);
            Interlocked.Exchange(ref _reqOneMore, 0);
        }

        void Enter(int section)
        {
            _cur = section;
            _pass = 1;
            _extra = 0;
            _nextQueued = false;
            _toStartQueued = false;
            _restartArmed = false;
        }

        /// <summary>Call once per Work() with the song position in ticks.</summary>
        public void Step(int pos, bool playing)
        {
            _pos = pos;

            if (!Enabled || !playing)
            {
                GoIdle(true);
                return;
            }

            int s0 = _songStart, len = _sectionLen, count = _sectionCount;
            int end = s0 + count * len;

            if (pos < s0 || pos >= end)
            {
                // Outside the configured range: leave the song's loop alone.
                GoIdle(false);
                return;
            }

            int sec = (pos - s0) / len;

            if (Interlocked.Exchange(ref _reqResync, 0) != 0) _active = false;

            bool restart = Interlocked.Exchange(ref _reqRestart, 0) != 0;
            int secStart = s0 + sec * len;

            if (!_active)
            {
                _active = true;
                Enter(sec);
            }
            else
            {
                if (restart)
                {
                    // Already on the first tick: the jump will not move the position.
                    if (pos == secStart && sec == _cur) Enter(sec);
                    else _restartArmed = true;
                }
                if (pos != _prev)
                {
                    if (sec != _cur) Enter(sec);                    // crossed into another section
                    else if (pos < _prev)
                    {
                        if (_restartArmed && pos == secStart) Enter(sec);   // Play pressed: fresh count
                        else _pass++;                               // wrapped inside this section
                    }
                }
            }
            _prev = pos;

            if (Interlocked.Exchange(ref _reqNext, 0) != 0) _nextQueued = true;
            if (Interlocked.Exchange(ref _reqToStart, 0) != 0) _toStartQueued = true;
            int more = Interlocked.Exchange(ref _reqOneMore, 0);
            if (more > 0)
            {
                // One More cancels a queued Next / To Start; otherwise it adds a pass.
                if (_nextQueued || _toStartQueued) { _nextQueued = false; _toStartQueued = false; }
                else _extra += more;
            }

            int cur = _cur;
            if (cur == 0) _toStartQueued = false;   // already there

            int rep = GetRepeats(cur);
            bool last = cur == count - 1;
            bool countDone = rep != 0 && _pass >= rep + _extra;

            int ws = s0 + cur * len;
            int we = ws + len;
            LoopPhase phase = LoopPhase.Looping;

            if (Hold)
            {
                phase = LoopPhase.Held;
            }
            else if (_toStartQueued)
            {
                phase = LoopPhase.Returning;
                ws = s0;
            }
            else if (_nextQueued || countDone)
            {
                if (!last)
                {
                    phase = LoopPhase.Leaving;
                    we += len;
                }
                else if ((_nextQueued || AtEnd == AtEndBackToStart) && cur != 0)
                {
                    phase = LoopPhase.Returning;
                    ws = s0;
                }
                // else: last section, At End = Hold -> keep looping it
            }

            _wantStart = ws;
            _wantEnd = we;
            _phase = (int)phase;
            _steering = true;
            _restorePending = false;
        }
    }
}
