// Pedal Master Loop - GUI. Built in code (no XAML). Opens in its own
// resizable window; a windowed managed GUI must be a UserControl (Core §44).
//
// Dark, flat look. Buttons are FlatButton (a Border + TextBlock) so no WPF
// control templates are needed. Colour carries state:
//   blue  = the section that is playing      amber = held
//   green = leaving at the end of this pass  grey  = stopped / cued

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace Pedal.MasterLoop
{
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = true, UseThemeStyles = false,
        Width = 700, Height = 760)]
    public class MasterLoopGuiFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new MasterLoopGui();
    }

    static class Theme
    {
        public static readonly Brush Bg = B(0x1E, 0x1F, 0x22);
        public static readonly Brush Card = B(0x2A, 0x2C, 0x30);
        public static readonly Brush CardHi = B(0x36, 0x39, 0x3F);
        public static readonly Brush CardDown = B(0x22, 0x24, 0x27);
        public static readonly Brush Line = B(0x3F, 0x42, 0x48);
        public static readonly Brush Text = B(0xE8, 0xE8, 0xE4);
        public static readonly Brush Dim = B(0x9A, 0x9D, 0xA3);
        public static readonly Brush Faint = B(0x6B, 0x6E, 0x75);
        public static readonly Brush Blue = B(0x3B, 0x8E, 0xF7);
        public static readonly Brush BlueTint = B(0x1F, 0x33, 0x52);
        public static readonly Brush Amber = B(0xE8, 0x82, 0x00);
        public static readonly Brush AmberTint = B(0x4D, 0x34, 0x10);
        public static readonly Brush Green = B(0x3F, 0xB9, 0x50);
        public static readonly Brush GreenTint = B(0x1C, 0x3B, 0x26);
        public static readonly Brush Warn = B(0xFF, 0x8A, 0x7A);
        public static readonly Brush OnText = B(0x10, 0x11, 0x13);
        public static readonly FontFamily Mono = new FontFamily("Consolas");

        static Brush B(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
    }

    /// <summary>A flat, template-free button. On = lit in OnBrush.</summary>
    sealed class FlatButton : Border
    {
        readonly TextBlock _text;
        bool _on, _hover, _down;
        Brush _onBrush = Theme.Blue;

        public event Action Clicked;

        public FlatButton(string text, double fontSize = 12, double minHeight = 24)
        {
            _text = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Child = _text;
            CornerRadius = new CornerRadius(4);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(10, 2, 10, 2);
            MinHeight = minHeight;
            Cursor = Cursors.Hand;
            Focusable = false;
            SnapsToDevicePixels = true;

            MouseEnter += (_, __) => { _hover = true; Paint(); };
            MouseLeave += (_, __) => { _hover = false; _down = false; Paint(); };
            MouseLeftButtonDown += (_, e) => { _down = true; Paint(); e.Handled = true; };
            MouseLeftButtonUp += (_, e) =>
            {
                bool fire = _down;
                _down = false;
                Paint();
                e.Handled = true;
                if (fire) Clicked?.Invoke();
            };
            Paint();
        }

        public string Text
        {
            get => _text.Text;
            set { if (_text.Text != value) _text.Text = value; }
        }

        public bool On
        {
            get => _on;
            set { if (_on != value) { _on = value; Paint(); } }
        }

        public Brush OnBrush
        {
            get => _onBrush;
            set { if (!ReferenceEquals(_onBrush, value)) { _onBrush = value; Paint(); } }
        }

        void Paint()
        {
            if (_on)
            {
                Background = _onBrush;
                BorderBrush = _onBrush;
                _text.Foreground = Theme.OnText;
                _text.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                Background = _down ? Theme.CardDown : (_hover ? Theme.CardHi : Theme.Card);
                BorderBrush = _hover ? Theme.Faint : Theme.Line;
                _text.Foreground = Theme.Text;
                _text.FontWeight = FontWeights.Normal;
            }
        }
    }

    public class MasterLoopGui : UserControl, IMachineGUI
    {
        sealed class Row
        {
            public Border Root;
            public TextBlock Number, Patterns, Count, Status;
            public TextBox Label;
            public int ShownCount = -1;
            public int Look = -1;        // last painted state, see RowLook
        }

        // Row looks
        const int LookPlain = 0, LookDone = 1, LookNow = 2, LookHeld = 3, LookLeaving = 4, LookCued = 5;

        IMachine _iMachine;
        MasterLoopMachine _machine;
        readonly DispatcherTimer _timer;

        // header
        FlatButton _playBtn, _stopBtn, _enableBtn;
        Border _nowCard, _badge;
        TextBlock _badgeText, _nowText, _passText, _nextText, _warnText;
        StackPanel _pips;
        ColumnDefinition _barDone, _barRest;
        Border _barFill;
        FlatButton _holdBtn, _nextBtn, _moreBtn, _toStartBtn;

        // footer
        FlatButton _atEndHold, _atEndBack, _refBtn;
        TextBox _startBox, _lenBox, _countBox;
        TextBlock _rangeText;

        StackPanel _rowsPanel;
        Row[] _rows = new Row[0];
        int _builtVersion = -1;
        int _shownScroll = -2;
        int _pipKey = -1;
        double _shownFrac = -1;

        public IMachine Machine
        {
            get => _iMachine;
            set { _iMachine = value; _machine = value?.ManagedMachine as MasterLoopMachine; }
        }

        MasterLoopMachine M => _machine ??= _iMachine?.ManagedMachine as MasterLoopMachine;

        public MasterLoopGui()
        {
            MinWidth = 600;
            MinHeight = 420;
            Background = Theme.Bg;
            Foreground = Theme.Text;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            Content = BuildLayout();

            // Timer follows Loaded / Unloaded / visibility (Core §26.3).
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick += (_, __) => Refresh();
            Loaded += (_, __) => StartTimer();
            Unloaded += (_, __) => _timer.Stop();
            IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) StartTimer(); else _timer.Stop(); };
        }

        void StartTimer()
        {
            _builtVersion = -1;      // rebuild rows and pattern names on every open
            Refresh();
            if (!_timer.IsEnabled) _timer.Start();
        }

        // ── Small builders ────────────────────────────────────────────────────

        static TextBlock Caption(string s) => new TextBlock
        {
            Text = s.ToUpperInvariant(), Foreground = Theme.Faint, FontSize = 10, FontWeight = FontWeights.SemiBold
        };

        static TextBlock Inline(string s) => new TextBlock
        {
            Text = s, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        };

        static TextBox DarkBox(double width) => new TextBox
        {
            Width = width,
            Background = Theme.Bg,
            Foreground = Theme.Text,
            BorderBrush = Theme.Line,
            CaretBrush = Theme.Text,
            SelectionBrush = Theme.Blue,
            Padding = new Thickness(3, 2, 3, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };

        static Border CardOf(UIElement child, Thickness padding) => new Border
        {
            Background = Theme.Card, CornerRadius = new CornerRadius(6), Padding = padding, Child = child
        };

        // ── Layout ────────────────────────────────────────────────────────────

        UIElement BuildLayout()
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(12) };

            var top = BuildTopBar();
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);

            var header = BuildHeader();
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            var footer = BuildFooter();
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);

            var colHead = BuildRowGrid();
            colHead.Margin = new Thickness(7, 12, 4, 4);
            AddCell(colHead, Caption("#"), 0);
            AddCell(colHead, Caption("Patterns"), 1);
            AddCell(colHead, Caption("Label"), 2);
            var rep = Caption("Repeats");
            rep.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumnSpan(rep, 3);
            AddCell(colHead, rep, 3);
            var st = Caption("Status");
            st.Margin = new Thickness(12, 0, 0, 0);
            AddCell(colHead, st, 6);
            DockPanel.SetDock(colHead, Dock.Top);
            dock.Children.Add(colHead);

            _rowsPanel = new StackPanel();
            var scroll = new ScrollViewer
            {
                Content = _rowsPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Theme.Bg
            };
            dock.Children.Add(scroll);
            return dock;
        }

        UIElement BuildTopBar()
        {
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

            _playBtn = new FlatButton("▶  Play", 13, 30) { OnBrush = Theme.Green, MinWidth = 86, Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Play from the first tick of the section under the play position (like F6 at a section start). While playing: restart this section." };
            _playBtn.Clicked += () => M?.PlayFromSectionStart();
            DockPanel.SetDock(_playBtn, Dock.Left);
            bar.Children.Add(_playBtn);

            _stopBtn = new FlatButton("■  Stop", 13, 30) { MinWidth = 76, ToolTip = "Stop (F8)" };
            _stopBtn.Clicked += () => M?.StopPlayback();
            DockPanel.SetDock(_stopBtn, Dock.Left);
            bar.Children.Add(_stopBtn);

            _enableBtn = new FlatButton("Enabled", 12, 30) { OnBrush = Theme.Blue, MinWidth = 86,
                ToolTip = "Off: the song loop is released to the full range and left alone" };
            _enableBtn.Clicked += () =>
            {
                var m = M; if (m == null) return;
                bool on = !m.Engine.Enabled;
                m.Engine.Enabled = on;
                m.SetOwnParameter("Enable", on ? 1 : 0);
            };
            DockPanel.SetDock(_enableBtn, Dock.Right);
            bar.Children.Add(_enableBtn);

            bar.Children.Add(new TextBlock
            {
                Text = "Pedal Master Loop", Foreground = Theme.Dim, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
            });
            return bar;
        }

        UIElement BuildHeader()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var now = new StackPanel();

            var capLine = new DockPanel();
            _badgeText = new TextBlock { FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Theme.OnText };
            _badge = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 1, 7, 2), Child = _badgeText, Background = Theme.Faint };
            DockPanel.SetDock(_badge, Dock.Right);
            capLine.Children.Add(_badge);
            capLine.Children.Add(Caption("Now playing"));
            now.Children.Add(capLine);

            _nowText = new TextBlock { FontSize = 24, FontWeight = FontWeights.SemiBold, Text = "Stopped", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            now.Children.Add(_nowText);

            var passLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            _passText = new TextBlock { FontSize = 14, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            passLine.Children.Add(_passText);
            _pips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            passLine.Children.Add(_pips);
            now.Children.Add(passLine);

            // position within the pass
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 8) };
            _barDone = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
            _barRest = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            bar.ColumnDefinitions.Add(_barDone);
            bar.ColumnDefinitions.Add(_barRest);
            var track = new Border { Background = Theme.Bg, CornerRadius = new CornerRadius(3) };
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            _barFill = new Border { Background = Theme.Blue, CornerRadius = new CornerRadius(3) };
            Grid.SetColumn(_barFill, 0);
            bar.Children.Add(_barFill);
            now.Children.Add(bar);

            _nextText = new TextBlock { Foreground = Theme.Dim, TextWrapping = TextWrapping.Wrap };
            now.Children.Add(_nextText);
            _warnText = new TextBlock { Foreground = Theme.Warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
            now.Children.Add(_warnText);

            _nowCard = new Border
            {
                Background = Theme.Card,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(4, 0, 0, 0),
                BorderBrush = Theme.Faint,
                Padding = new Thickness(14, 10, 14, 10),
                Child = now
            };
            Grid.SetColumn(_nowCard, 0);
            grid.Children.Add(_nowCard);

            // live buttons, 2 x 2
            var btns = new Grid { Margin = new Thickness(10, 0, 0, 0), Width = 230 };
            btns.ColumnDefinitions.Add(new ColumnDefinition());
            btns.ColumnDefinitions.Add(new ColumnDefinition());
            btns.RowDefinitions.Add(new RowDefinition());
            btns.RowDefinitions.Add(new RowDefinition());

            _holdBtn = Live("Hold", Theme.Amber, "Stay on this section until released", 0, 0);
            _holdBtn.Clicked += () =>
            {
                var m = M; if (m == null) return;
                bool on = !m.Engine.Hold;
                m.Engine.Hold = on;
                m.SetOwnParameter("Hold", on ? 1 : 0);
            };
            btns.Children.Add(_holdBtn);

            _nextBtn = Live("Next", Theme.Green, "Leave this section at the end of this pass", 0, 1);
            _nextBtn.Clicked += () => M?.Engine.RequestNext();
            btns.Children.Add(_nextBtn);

            _moreBtn = Live("One more", Theme.Blue, "Add one pass, or cancel a queued Next / To start", 1, 0);
            _moreBtn.Clicked += () => M?.Engine.RequestOneMore();
            btns.Children.Add(_moreBtn);

            _toStartBtn = Live("To start", Theme.Green, "Go back to section 01 at the end of this pass", 1, 1);
            _toStartBtn.Clicked += () => M?.Engine.RequestToStart();
            btns.Children.Add(_toStartBtn);

            Grid.SetColumn(btns, 1);
            grid.Children.Add(btns);
            return grid;
        }

        static FlatButton Live(string text, Brush on, string tip, int row, int col)
        {
            var b = new FlatButton(text, 15, 52)
            {
                OnBrush = on,
                ToolTip = tip,
                Margin = new Thickness(col == 0 ? 0 : 3, row == 0 ? 0 : 3, col == 0 ? 3 : 0, row == 0 ? 3 : 0)
            };
            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            return b;
        }

        UIElement BuildFooter()
        {
            var outer = new StackPanel();

            var line1 = new StackPanel { Orientation = Orientation.Horizontal };
            line1.Children.Add(Inline("After last section"));
            _atEndHold = new FlatButton("Hold last") { CornerRadius = new CornerRadius(4, 0, 0, 4) };
            _atEndBack = new FlatButton("Back to start") { CornerRadius = new CornerRadius(0, 4, 4, 0), Margin = new Thickness(-1, 0, 18, 0) };
            _atEndHold.Clicked += () => SetAtEnd(LoopEngine.AtEndHold);
            _atEndBack.Clicked += () => SetAtEnd(LoopEngine.AtEndBackToStart);
            line1.Children.Add(_atEndHold);
            line1.Children.Add(_atEndBack);

            line1.Children.Add(Inline("Set all"));
            foreach (int v in new[] { 1, 2, 4, 8, 0 })
            {
                int val = v;
                var b = new FlatButton(v == 0 ? "∞" : "×" + v) { MinWidth = 34, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(6, 2, 6, 2) };
                b.Clicked += () =>
                {
                    var m = M; if (m == null) return;
                    for (int i = 0; i < m.Engine.SectionCount; i++) m.Engine.SetRepeats(i, val);
                };
                line1.Children.Add(b);
            }
            outer.Children.Add(line1);

            var line2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            line2.Children.Add(Inline("First tick"));
            _startBox = DarkBox(58);
            line2.Children.Add(_startBox);
            line2.Children.Add(Inline("Section length"));
            _lenBox = DarkBox(58);
            line2.Children.Add(_lenBox);
            line2.Children.Add(Inline("Sections"));
            _countBox = DarkBox(46);
            line2.Children.Add(_countBox);

            var apply = new FlatButton("Apply") { Margin = new Thickness(0, 0, 6, 0) };
            apply.Clicked += ApplyLayout;
            line2.Children.Add(apply);

            var read = new FlatButton("Read from song loop")
            {
                ToolTip = "Use the song's loop start and end as the range. Only while stopped."
            };
            read.Clicked += () =>
            {
                var m = M; if (m == null) return;
                if (!m.ReadRangeFromSong()) ShowRangeNote("Stop playback first.");
            };
            line2.Children.Add(read);
            outer.Children.Add(line2);

            var line3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            line3.Children.Add(Inline("Pattern names from"));
            _refBtn = new FlatButton("—  ▾") { MinWidth = 150, Margin = new Thickness(0, 0, 6, 0) };
            _refBtn.Clicked += OpenReferenceMenu;
            line3.Children.Add(_refBtn);
            var refresh = new FlatButton("Refresh") { Margin = new Thickness(0, 0, 16, 0), ToolTip = "Re-read pattern names from the sequence" };
            refresh.Clicked += () => { var m = M; if (m != null) { EnsureReference(m); UpdatePatternNames(m); } };
            line3.Children.Add(refresh);
            _rangeText = new TextBlock { Foreground = Theme.Faint, VerticalAlignment = VerticalAlignment.Center };
            line3.Children.Add(_rangeText);
            outer.Children.Add(line3);

            var card = CardOf(outer, new Thickness(12, 10, 12, 10));
            card.Margin = new Thickness(0, 10, 0, 0);
            return card;
        }

        void SetAtEnd(int mode)
        {
            var m = M; if (m == null) return;
            m.Engine.AtEnd = mode;
            m.SetOwnParameter("At End", mode);
        }

        static Grid BuildRowGrid()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            return g;
        }

        static void AddCell(Grid g, UIElement e, int col)
        {
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        // ── Rows ──────────────────────────────────────────────────────────────

        void BuildRows(MasterLoopMachine m)
        {
            int n = m.Engine.SectionCount;
            _rowsPanel.Children.Clear();
            _rows = new Row[n];

            for (int i = 0; i < n; i++)
            {
                int idx = i;
                var r = new Row();
                var g = BuildRowGrid();

                r.Number = new TextBlock
                {
                    Text = (i + 1).ToString("00"), FontFamily = Theme.Mono, FontSize = 13, Foreground = Theme.Dim,
                    VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                    ToolTip = "While stopped: click to cue this section"
                };
                r.Number.MouseLeftButtonDown += (_, e) => { M?.CueSection(idx); e.Handled = true; };
                AddCell(g, r.Number, 0);

                r.Patterns = new TextBlock { FontFamily = Theme.Mono, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                AddCell(g, r.Patterns, 1);

                r.Label = new TextBox
                {
                    Text = m.GetLabel(i),
                    Margin = new Thickness(0, 0, 10, 0),
                    Background = Brushes.Transparent,
                    Foreground = Theme.Text,
                    BorderBrush = Theme.Line,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    CaretBrush = Theme.Text,
                    SelectionBrush = Theme.Blue,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                r.Label.TextChanged += (_, __) => { var mm = M; if (mm != null) mm.SetLabel(idx, r.Label.Text); };
                AddCell(g, r.Label, 2);

                var minus = new FlatButton("−", 13, 22) { Padding = new Thickness(0), ToolTip = "Fewer repeats. Below 1 is ∞: loop until Next." };
                minus.Clicked += () =>
                {
                    var mm = M; if (mm == null) return;
                    int v = mm.Engine.GetRepeats(idx);
                    if (v > 0) mm.Engine.SetRepeats(idx, v - 1);     // 1 -> infinite
                };
                AddCell(g, minus, 3);

                r.Count = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                AddCell(g, r.Count, 4);

                var plus = new FlatButton("+", 13, 22) { Padding = new Thickness(0), ToolTip = "More repeats" };
                plus.Clicked += () =>
                {
                    var mm = M; if (mm == null) return;
                    mm.Engine.SetRepeats(idx, mm.Engine.GetRepeats(idx) + 1);
                };
                AddCell(g, plus, 5);

                r.Status = new TextBlock { Foreground = Theme.Dim, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                AddCell(g, r.Status, 6);

                r.Root = new Border
                {
                    Child = g,
                    Padding = new Thickness(4, 3, 4, 3),
                    Margin = new Thickness(0, 0, 0, 1),
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = Brushes.Transparent,
                    Background = Theme.Card
                };
                _rows[i] = r;
                _rowsPanel.Children.Add(r.Root);
            }

            _shownScroll = -2;
            _startBox.Text = m.Engine.SongStart.ToString();
            _lenBox.Text = m.Engine.SectionLen.ToString();
            _countBox.Text = n.ToString();

            EnsureReference(m);
            UpdatePatternNames(m);
            ShowRangeNote("");
        }

        void PaintRow(Row r, int look, string status)
        {
            if (r.Status.Text != status) r.Status.Text = status;
            if (r.Look == look) return;
            r.Look = look;

            Brush bg = Theme.Card, edge = Brushes.Transparent, num = Theme.Dim, text = Theme.Text, stat = Theme.Dim;
            switch (look)
            {
                case LookDone: bg = Theme.Bg; num = Theme.Faint; text = Theme.Faint; break;
                case LookNow: bg = Theme.BlueTint; edge = Theme.Blue; num = Theme.Blue; stat = Theme.Text; break;
                case LookHeld: bg = Theme.AmberTint; edge = Theme.Amber; num = Theme.Amber; stat = Theme.Amber; break;
                case LookLeaving: bg = Theme.GreenTint; edge = Theme.Green; num = Theme.Green; stat = Theme.Green; break;
                case LookCued: edge = Theme.Faint; num = Theme.Text; break;
            }
            r.Root.Background = bg;
            r.Root.BorderBrush = edge;
            r.Number.Foreground = num;
            r.Number.FontWeight = look >= LookNow && look != LookCued ? FontWeights.Bold : FontWeights.Normal;
            r.Patterns.Foreground = text;
            r.Label.Foreground = text;
            r.Count.Foreground = text;
            r.Status.Foreground = stat;
            r.Status.FontWeight = look == LookHeld ? FontWeights.Bold : FontWeights.Normal;
        }

        void ShowRangeNote(string extra)
        {
            var m = M; if (m == null) return;
            var e = m.Engine;
            string s = "Range: ticks " + e.SongStart + " to " + e.SongEnd;
            if (!string.IsNullOrEmpty(extra)) s += "   " + extra;
            _rangeText.Text = s;
        }

        void ApplyLayout()
        {
            var m = M; if (m == null) return;
            int start, len, count;
            if (!int.TryParse(_startBox.Text, out start) || !int.TryParse(_lenBox.Text, out len) ||
                !int.TryParse(_countBox.Text, out count) || start < 0 || len < 1 || count < 1 ||
                count > LoopEngine.MaxSections)
            {
                ShowRangeNote("Enter whole numbers: first tick 0 or more, length 1 or more, sections 1 to " + LoopEngine.MaxSections + ".");
                return;
            }
            m.SetLayout(start, len, count);     // rows rebuild on the next refresh
        }

        // ── Pattern names from the sequence ───────────────────────────────────

        static List<string> SequenceNames(ISong song)
        {
            var names = new List<string>();
            if (song == null) return names;
            foreach (var seq in song.Sequences)
            {
                string n = seq?.Machine?.Name;
                if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
            }
            return names;
        }

        void EnsureReference(MasterLoopMachine m)
        {
            try
            {
                var song = Global.Buzz?.Song;
                if (song == null) return;
                var names = SequenceNames(song);
                if (string.IsNullOrEmpty(m.ReferenceMachine) || !names.Contains(m.ReferenceMachine))
                    m.ReferenceMachine = DefaultReference(song, m);
                _refBtn.Text = (string.IsNullOrEmpty(m.ReferenceMachine) ? "—" : m.ReferenceMachine) + "  ▾";
            }
            catch { }
        }

        void OpenReferenceMenu()
        {
            var m = M; if (m == null) return;
            try
            {
                var menu = new ContextMenu { PlacementTarget = _refBtn };
                foreach (var name in SequenceNames(Global.Buzz?.Song))
                {
                    string pick = name;
                    var item = new MenuItem { Header = name.Replace("_", "__"), IsChecked = name == m.ReferenceMachine };
                    item.Click += (_, __) =>
                    {
                        m.ReferenceMachine = pick;
                        EnsureReference(m);
                        UpdatePatternNames(m);
                    };
                    menu.Items.Add(item);
                }
                menu.IsOpen = true;
            }
            catch { }
        }

        // First non-Master sequence with a pattern in the first section.
        static string DefaultReference(ISong song, MasterLoopMachine m)
        {
            int s = m.Engine.SectionStart(0), e = m.Engine.SectionEnd(0);
            foreach (var seq in song.Sequences)
            {
                string n = seq?.Machine?.Name;
                if (string.IsNullOrEmpty(n) || n == "Master") continue;
                if (NamesIn(seq, s, e).Length > 0) return n;
            }
            return "";
        }

        static string NamesIn(ISequence seq, int start, int end)
        {
            var hits = new List<KeyValuePair<int, string>>();
            foreach (var kv in seq.Events)
            {
                if (kv.Key < start || kv.Key >= end) continue;
                var ev = kv.Value;
                if (ev == null || ev.Type != SequenceEventType.PlayPattern || ev.Pattern == null) continue;
                hits.Add(new KeyValuePair<int, string>(kv.Key, ev.Pattern.Name));
            }
            if (hits.Count == 0) return "";
            hits.Sort((a, b) => a.Key.CompareTo(b.Key));
            return string.Join(" + ", hits.Select(h => h.Value));
        }

        void UpdatePatternNames(MasterLoopMachine m)
        {
            try
            {
                var song = Global.Buzz?.Song;
                string refName = m.ReferenceMachine;
                for (int i = 0; i < _rows.Length; i++)
                {
                    string text = "";
                    if (song != null && !string.IsNullOrEmpty(refName))
                    {
                        int s = m.Engine.SectionStart(i), e = m.Engine.SectionEnd(i);
                        string fallback = "";
                        foreach (var seq in song.Sequences)
                        {
                            string n = seq?.Machine?.Name;
                            if (string.IsNullOrEmpty(n)) continue;
                            bool exact = n == refName;
                            // A machine split across several sequences (DG_01_A, DG_01_A48plus).
                            if (!exact && !n.StartsWith(refName, StringComparison.Ordinal)) continue;
                            string names = NamesIn(seq, s, e);
                            if (names.Length == 0) continue;
                            if (exact) { text = names; break; }
                            if (fallback.Length == 0) fallback = names;
                        }
                        if (text.Length == 0) text = fallback;
                    }
                    _rows[i].Patterns.Text = text;
                }
            }
            catch { }
        }

        // ── Refresh (20 Hz). Only touches what changed. ───────────────────────

        void SetBadge(string text, Brush bg)
        {
            if (_badgeText.Text != text) _badgeText.Text = text;
            if (!ReferenceEquals(_badge.Background, bg)) _badge.Background = bg;
        }

        void SetAccent(Brush b)
        {
            if (!ReferenceEquals(_nowCard.BorderBrush, b)) _nowCard.BorderBrush = b;
            if (!ReferenceEquals(_barFill.Background, b)) _barFill.Background = b;
        }

        void SetBar(double frac)
        {
            if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
            if (Math.Abs(frac - _shownFrac) < 0.004) return;
            _shownFrac = frac;
            _barDone.Width = new GridLength(frac, GridUnitType.Star);
            _barRest.Width = new GridLength(1 - frac, GridUnitType.Star);
        }

        // One pip per pass, up to 16. Rebuilt only when pass, target or colour changes.
        void SetPips(int pass, int target, Brush accent, int accentId)
        {
            int key = target > 16 || target <= 0 ? 0 : (pass * 1000 + target) * 8 + accentId;
            if (key == _pipKey) return;
            _pipKey = key;
            _pips.Children.Clear();
            if (key == 0) return;
            for (int i = 1; i <= target; i++)
            {
                _pips.Children.Add(new Border
                {
                    Width = i == pass ? 18 : 12,
                    Height = 6,
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(0, 0, 3, 0),
                    Background = i <= pass ? accent : Theme.Line,
                    Opacity = i < pass ? 0.45 : 1.0
                });
            }
        }

        void Refresh()
        {
            var m = M;
            if (m == null) return;
            var e = m.Engine;

            if (m.LayoutVersion != _builtVersion || _rows.Length != e.SectionCount)
            {
                _builtVersion = m.LayoutVersion;
                BuildRows(m);
            }

            bool playing = false, looping = true;
            try
            {
                var buzz = Global.Buzz;
                if (buzz != null) { playing = buzz.Playing; looping = buzz.Looping; }
            }
            catch { }

            int cur = e.Current;
            int pass = e.Pass;
            int count = e.SectionCount;
            LoopPhase phase = e.Phase;
            bool hold = e.Hold;
            bool enabled = e.Enabled;
            bool active = cur >= 0 && cur < _rows.Length;
            int cued = active ? -1 : m.CuedSection();

            // buttons
            _playBtn.On = playing;
            _enableBtn.On = enabled;
            _enableBtn.Text = enabled ? "Enabled" : "Disabled";
            _holdBtn.On = hold;
            _holdBtn.Text = hold ? "Held" : "Hold";
            _nextBtn.On = active && !hold && (e.NextQueued || phase == LoopPhase.Leaving);
            _toStartBtn.On = active && !hold && (e.ToStartQueued || phase == LoopPhase.Returning);
            _atEndHold.On = e.AtEnd == LoopEngine.AtEndHold;
            _atEndBack.On = e.AtEnd == LoopEngine.AtEndBackToStart;

            // repeat counts
            for (int i = 0; i < _rows.Length; i++)
            {
                int rep = e.GetRepeats(i);
                var r = _rows[i];
                if (r.ShownCount != rep)
                {
                    r.ShownCount = rep;
                    r.Count.Text = rep == 0 ? "∞" : rep.ToString();
                }
            }

            string warn = "";
            if (!looping) warn = "Song looping is off in ReBuzz: playback will stop at the end of the first pass.";
            if (!enabled) warn = "Disabled: the song loop is not being moved.";

            if (!active)
            {
                for (int i = 0; i < _rows.Length; i++)
                    PaintRow(_rows[i], i == cued ? LookCued : LookPlain, i == cued ? "cued" : "");

                SetAccent(Theme.Faint);
                SetBadge(playing ? (enabled ? "OUTSIDE RANGE" : "DISABLED") : "STOPPED", Theme.Faint);
                SetPips(0, 0, Theme.Faint, 0);
                SetBar(0);
                _passText.Text = hold ? "Hold is on" : "";
                if (cued >= 0)
                {
                    string cl = m.GetLabel(cued);
                    _nowText.Text = "Section " + (cued + 1).ToString("00") + (string.IsNullOrEmpty(cl) ? "" : "  " + cl);
                    _nextText.Text = playing ? "" : "Cued. Play starts from its first tick. Click a section number to cue another.";
                }
                else
                {
                    _nowText.Text = "—";
                    _nextText.Text = playing ? "The play position is outside the range." : "Play starts from section 01.";
                }
                if (_shownScroll != -1000 - cued)
                {
                    _shownScroll = -1000 - cued;
                    if (cued >= 0 && cued < _rows.Length) _rows[cued].Root.BringIntoView();
                }
            }
            else
            {
                int rep = e.GetRepeats(cur);
                int target = rep + e.Extra;
                bool last = cur == count - 1;
                string label = m.GetLabel(cur);
                string passStr = rep == 0 ? "pass " + pass : "pass " + pass + " of " + target;

                Brush accent;
                int accentId, look;
                string badge, next, status;

                switch (phase)
                {
                    case LoopPhase.Held:
                        accent = Theme.Amber; accentId = 2; look = LookHeld;
                        badge = "HELD";
                        status = "HELD  ·  pass " + pass;
                        next = "Held on this section. Press Held again to release.";
                        if (e.ToStartQueued) next += " To start is queued.";
                        else if (e.NextQueued) next += " Next is queued.";
                        else if (rep != 0 && pass >= target) next += " The count is done, so it will leave at the end of that pass.";
                        break;
                    case LoopPhase.Leaving:
                        accent = Theme.Green; accentId = 3; look = LookLeaving;
                        badge = "LAST PASS";
                        status = passStr + "  →  " + (cur + 2).ToString("00");
                        next = "Next: section " + (cur + 2).ToString("00") + " at the end of this pass.";
                        break;
                    case LoopPhase.Returning:
                        accent = Theme.Green; accentId = 3; look = LookLeaving;
                        badge = "LAST PASS";
                        status = passStr + "  →  01";
                        next = "Next: back to section 01 at the end of this pass.";
                        break;
                    default:
                        accent = Theme.Blue; accentId = 1; look = LookNow;
                        badge = "LOOPING";
                        status = passStr;
                        if (rep == 0) next = "Loops until Next.";
                        else if (last && pass >= target)
                        {
                            badge = "LAST SECTION";
                            next = "Last section: looping until you press To start or Next (both go back to section 01).";
                        }
                        else
                        {
                            int left = target - pass;
                            string where = last
                                ? (e.AtEnd == LoopEngine.AtEndBackToStart ? "back to section 01" : "stays on this section")
                                : "section " + (cur + 2).ToString("00");
                            next = "Then: " + where + " after " + left + (left == 1 ? " more pass." : " more passes.");
                        }
                        break;
                }

                for (int i = 0; i < _rows.Length; i++)
                {
                    if (i == cur) PaintRow(_rows[i], look, status);
                    else PaintRow(_rows[i], i < cur ? LookDone : LookPlain, "");
                }
                if (_shownScroll != cur)
                {
                    _shownScroll = cur;
                    _rows[cur].Root.BringIntoView();
                }

                SetAccent(accent);
                SetBadge(badge, accent);
                _nowText.Text = "Section " + (cur + 1).ToString("00") + (string.IsNullOrEmpty(label) ? "" : "  " + label);
                _passText.Text = passStr;
                SetPips(pass, rep == 0 ? 0 : Math.Max(target, pass), accent, accentId);
                SetBar((double)(e.Position - e.SectionStart(cur)) / Math.Max(1, e.SectionLen));
                _nextText.Text = next;
            }

            if (warn.Length > 0)
            {
                if (_warnText.Text != warn) _warnText.Text = warn;
                _warnText.Visibility = Visibility.Visible;
            }
            else if (_warnText.Visibility != Visibility.Collapsed) _warnText.Visibility = Visibility.Collapsed;
        }
    }
}
