// Pedal Master Loop - ReBuzz managed control machine.
//
// Loops each section of the song a set number of times, then plays straight
// on into the next one. It does this by moving the song's own loop markers,
// so every machine in the sequence follows.
//
// Seamless hand-over (checked against ReBuzz SongCore.cs, commit ced8a01):
//   - SongCore.UpdatePlayPosition wraps to LoopStart when the position reaches
//     LoopEnd.
//   - The LoopStart setter jumps the play position if it is below the new
//     start, and the LoopEnd setter jumps it if it is at or past the new end.
// So on the final pass only LoopEnd moves (out to the end of the next
// section). LoopStart follows after playback has crossed over. Going back to
// the start is the mirror image: LoopStart drops to the song start first, and
// LoopEnd shrinks after the wrap.
//
// Both setters raise PropertyChanged synchronously, and the toolbar and
// sequence editor listen, so they are only ever called on the UI thread.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace Pedal.MasterLoop
{
    /// <summary>Saved with the song (XML-serialised by ReBuzz).</summary>
    public class MasterLoopState
    {
        public int Version = 1;
        public int SongStart = 0;
        public int SectionLen = 256;
        public int SectionCount = 1;
        public int[] Repeats = new int[0];
        public string[] Labels = new string[0];
        public string ReferenceMachine = "";
    }

    [MachineDecl(Name = "Pedal Master Loop", ShortName = "MLoop", Author = "thepedal")]
    public class MasterLoopMachine : IBuzzMachine
    {
        internal const string Version = "1.0.0";

        readonly IBuzzMachineHost _host;
        readonly LoopEngine _engine = new LoopEngine();
        readonly string[] _labels = new string[LoopEngine.MaxSections];

        volatile bool _layoutKnown;      // false until state is loaded or read from the song
        volatile string _refMachine = "";
        int _applyPending;
        volatile int _blockedPos = -1;
        int _layoutVersion;

        bool _lastNext, _lastOneMore, _lastToStart;

        public MasterLoopMachine(IBuzzMachineHost host)
        {
            _host = host;
            for (int i = 0; i < _labels.Length; i++) _labels[i] = "";
        }

        // ── Parameters. This list is frozen: the song loader restores it by ──
        // ── position (DrumGrid notes §12). New settings go in MachineState. ──

        [ParameterDecl(Name = "Enable", DefValue = true,
            Description = "On: the machine moves the song loop. Off: the loop is left alone.")]
        public bool Enable { get => _engine.Enabled; set => _engine.Enabled = value; }

        [ParameterDecl(Name = "Hold", DefValue = false,
            Description = "Stay on the current section until switched off.")]
        public bool Hold { get => _engine.Hold; set => _engine.Hold = value; }

        [ParameterDecl(Name = "Next", DefValue = false,
            Description = "0 to 1: leave the current section at the end of this pass.")]
        public bool Next
        {
            get => _lastNext;
            set { if (value && !_lastNext) _engine.RequestNext(); _lastNext = value; }
        }

        [ParameterDecl(Name = "One More", DefValue = false,
            Description = "0 to 1: add one pass to this section, or cancel a queued Next / To Start.")]
        public bool OneMore
        {
            get => _lastOneMore;
            set { if (value && !_lastOneMore) _engine.RequestOneMore(); _lastOneMore = value; }
        }

        [ParameterDecl(Name = "To Start", DefValue = false,
            Description = "0 to 1: go back to the first section at the end of this pass.")]
        public bool ToStart
        {
            get => _lastToStart;
            set { if (value && !_lastToStart) _engine.RequestToStart(); _lastToStart = value; }
        }

        [ParameterDecl(Name = "At End", DefValue = 0,
            ValueDescriptions = new[] { "Hold last", "Back to start" },
            Description = "What happens when the last section has played its repeats.")]
        public int AtEnd { get => _engine.AtEnd; set => _engine.AtEnd = value; }

        public string DescribeValue(IParameter parameter, int value)
        {
            if (parameter == null) return null;
            switch (parameter.Name)
            {
                case "Enable":
                case "Hold": return value != 0 ? "On" : "Off";
                case "Next":
                case "One More":
                case "To Start": return value != 0 ? "Fired" : "Ready";
                default: return null;
            }
        }

        // ── Audio thread ──────────────────────────────────────────────────────

        public void Work()
        {
            IBuzz buzz;
            ISong song;
            try
            {
                buzz = Global.Buzz;
                song = buzz?.Song;
            }
            catch { return; }
            if (song == null) return;

            bool playing = buzz.Playing;
            int pos = song.PlayPosition;

            if (!_layoutKnown)
            {
                // New machine: take the range from the song's loop, once, while stopped.
                if (playing) return;
                AdoptRange(song.LoopStart, song.LoopEnd);
            }

            _engine.Step(pos, playing);

            if (!_engine.Steering && !_engine.RestorePending) return;

            int ws = _engine.WantStart, we = _engine.WantEnd;
            if (song.LoopStart == ws && song.LoopEnd == we)
            {
                _engine.RestorePending = false;
                _blockedPos = -1;
                return;
            }

            if (pos == _blockedPos) return;      // last attempt was refused at this tick
            if (Interlocked.CompareExchange(ref _applyPending, 1, 0) != 0) return;

            var disp = Application.Current?.Dispatcher;
            if (disp == null) { Volatile.Write(ref _applyPending, 0); return; }
            disp.BeginInvoke((Action)ApplyLoopOnUiThread);
        }

        // ── UI thread ─────────────────────────────────────────────────────────

        void ApplyLoopOnUiThread()
        {
            try
            {
                var buzz = Global.Buzz;
                var song = buzz?.Song;
                if (song == null) return;

                // Read the targets now, not when the call was queued.
                int ws = _engine.WantStart, we = _engine.WantEnd;
                if (we <= ws) return;
                if (!_engine.Steering && !_engine.RestorePending) return;

                bool playing = buzz.Playing;
                int pos = song.PlayPosition;
                bool blocked = false;

                // Never make a change that would jump the play position:
                // growing the end and lowering the start are always safe.
                if (song.LoopEnd != we)
                {
                    if (!playing || we > song.LoopEnd || pos < we) song.LoopEnd = we;
                    else blocked = true;
                }
                if (song.LoopStart != ws)
                {
                    if (!playing || ws < song.LoopStart || pos >= ws) song.LoopStart = ws;
                    else blocked = true;
                }
                _blockedPos = blocked ? pos : -1;
            }
            catch { }
            finally { Volatile.Write(ref _applyPending, 0); }
        }

        void AdoptRange(int loopStart, int loopEnd)
        {
            int len = _engine.SectionLen;
            int span = Math.Max(len, loopEnd - loopStart);
            int count = (span + len - 1) / len;
            _engine.SetLayout(loopStart, len, count);
            _layoutKnown = true;
            Interlocked.Increment(ref _layoutVersion);
        }

        // ── API for the GUI (UI thread) ───────────────────────────────────────

        internal LoopEngine Engine => _engine;
        internal int LayoutVersion => Volatile.Read(ref _layoutVersion);
        internal string ReferenceMachine { get => _refMachine; set => _refMachine = value ?? ""; }

        internal string GetLabel(int i) { return (uint)i < _labels.Length ? _labels[i] : ""; }
        internal void SetLabel(int i, string s) { if ((uint)i < _labels.Length) _labels[i] = s ?? ""; }

        internal void SetLayout(int songStart, int sectionLen, int sectionCount)
        {
            _engine.SetLayout(songStart, sectionLen, sectionCount);
            _layoutKnown = true;
            Interlocked.Increment(ref _layoutVersion);
        }

        /// <summary>Re-read the range from the song's loop markers. Refused while steering.</summary>
        internal bool ReadRangeFromSong()
        {
            if (_engine.Steering || _engine.RestorePending) return false;
            var song = Global.Buzz?.Song;
            if (song == null) return false;
            AdoptRange(song.LoopStart, song.LoopEnd);
            return true;
        }

        static int SectionAt(LoopEngine e, int pos)
        {
            if (pos < e.SongStart || pos >= e.SongEnd) return 0;
            return (pos - e.SongStart) / Math.Max(1, e.SectionLen);
        }

        /// <summary>Section under the play position, or -1 if it is outside the range.</summary>
        internal int CuedSection()
        {
            int pos = _engine.Position;
            if (pos < _engine.SongStart || pos >= _engine.SongEnd) return -1;
            return SectionAt(_engine, pos);
        }

        /// <summary>Play from the first tick of the section under the play position.
        /// Same two calls as the sequence editor's F6 (PlayCursor): set the position,
        /// then start. While playing, it restarts the current section on the next tick.</summary>
        internal void PlayFromSectionStart()
        {
            try
            {
                var buzz = Global.Buzz;
                var song = buzz?.Song;
                if (song == null) return;
                int sec = SectionAt(_engine, song.PlayPosition);
                if (buzz.Playing) _engine.RequestRestart();
                song.PlayPosition = _engine.SectionStart(sec);
                buzz.Playing = true;
            }
            catch { }
        }

        internal void StopPlayback()
        {
            try { var buzz = Global.Buzz; if (buzz != null) buzz.Playing = false; }
            catch { }
        }

        /// <summary>While stopped, move the play position to the start of a section.</summary>
        internal bool CueSection(int section)
        {
            try
            {
                var buzz = Global.Buzz;
                var song = buzz?.Song;
                if (song == null || buzz.Playing) return false;
                if (section < 0 || section >= _engine.SectionCount) return false;
                song.PlayPosition = _engine.SectionStart(section);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Write one of this machine's own parameters without recording it (Core §45).</summary>
        internal void SetOwnParameter(string name, int value)
        {
            try
            {
                var m = _host.Machine;
                if (m == null) return;
                var p = m.ParameterGroups[1].Parameters.FirstOrDefault(x => x.Name == name);
                if (p == null) return;
                p.SetValue(1 << 16, value);
                if (p.GetValue(0) != value) p.SetValue(0, value);   // older builds: no flag
            }
            catch { }
        }

        // ── Song state ────────────────────────────────────────────────────────

        public MasterLoopState MachineState
        {
            get
            {
                int n = _engine.SectionCount;
                var s = new MasterLoopState
                {
                    SongStart = _engine.SongStart,
                    SectionLen = _engine.SectionLen,
                    SectionCount = n,
                    Repeats = new int[n],
                    Labels = new string[n],
                    ReferenceMachine = _refMachine
                };
                for (int i = 0; i < n; i++)
                {
                    s.Repeats[i] = _engine.GetRepeats(i);
                    s.Labels[i] = _labels[i] ?? "";
                }
                return s;
            }
            set
            {
                if (value == null) return;
                try
                {
                    if (value.Repeats != null)
                        for (int i = 0; i < value.Repeats.Length && i < LoopEngine.MaxSections; i++)
                            _engine.SetRepeats(i, value.Repeats[i]);
                    if (value.Labels != null)
                        for (int i = 0; i < value.Labels.Length && i < LoopEngine.MaxSections; i++)
                            _labels[i] = value.Labels[i] ?? "";
                    _refMachine = value.ReferenceMachine ?? "";
                    _engine.SetLayout(value.SongStart, value.SectionLen, value.SectionCount);
                    _layoutKnown = true;
                    // A song saved mid-play holds one section's markers: put the range back.
                    _engine.RequestRestore();
                    Interlocked.Increment(ref _layoutVersion);
                }
                catch { }
            }
        }

        // ── Right-click menu ──────────────────────────────────────────────────

        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                yield return new MenuItemVM()
                {
                    Text = "About...",
                    Command = new SimpleCommand()
                    {
                        CanExecuteDelegate = p => true,
                        ExecuteDelegate = p => MessageBox.Show(
                            $"Pedal Master Loop   v{Version}\n\n" +
                            "Loops each section of the song a set number of times,\n" +
                            "then plays on into the next, by moving the song loop.\n\n" +
                            "thepedal\n" +
                            "github.com/thepedal/pedal-master-loop\n" +
                            "GNU General Public License v3.0",
                            "About Pedal Master Loop")
                    }
                };
            }
        }
    }
}
