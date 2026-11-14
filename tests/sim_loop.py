"""Headless simulation of Pedal Master Loop against a model of ReBuzz's song loop.

LoopEngine below is a line-for-line Python port of LoopEngine.cs. Song models
SongCore.cs (commit ced8a01): PlayPosition / LoopStart / LoopEnd setters and
UpdatePlayPosition. Machine models MasterLoopMachine.Work and
ApplyLoopOnUiThread, with the UI-thread call delayed by a number of Work calls.

Run:  python3 tests/sim_loop.py
"""
import random
import sys

IDLE, LOOPING, LEAVING, RETURNING, HELD = range(5)


class LoopEngine:
    MAX = 512

    def __init__(self):
        self.song_start, self.section_len, self.section_count = 0, 256, 1
        self.repeats = [4] * self.MAX
        self.enabled, self.hold, self.at_end = True, False, 0
        self.req_next = self.req_to_start = self.req_one_more = self.req_resync = self.req_restart = 0
        self.restart_armed = False
        self.cur, self.passn, self.extra = -1, 0, 0
        self.next_queued = self.to_start_queued = False
        self.phase = IDLE
        self.want_start, self.want_end = 0, 16
        self.steering = self.restore_pending = False
        self.active, self.prev, self.pos = False, -1, 0

    @property
    def song_end(self):
        return self.song_start + self.section_count * self.section_len

    def set_layout(self, start, length, count):
        self.song_start, self.section_len, self.section_count = max(0, start), max(1, length), min(max(1, count), self.MAX)
        self.req_resync = 1

    def go_idle(self, restore):
        if self.active or self.steering:
            self.want_start, self.want_end = self.song_start, self.song_end
            if restore:
                self.restore_pending = True
        self.active = self.steering = False
        self.cur, self.passn, self.extra = -1, 0, 0
        self.next_queued = self.to_start_queued = False
        self.phase, self.prev = IDLE, -1
        self.restart_armed = False
        self.req_next = self.req_to_start = self.req_one_more = self.req_restart = 0

    def enter(self, sec):
        self.cur, self.passn, self.extra = sec, 1, 0
        self.next_queued = self.to_start_queued = False
        self.restart_armed = False

    def step(self, pos, playing):
        self.pos = pos
        if not self.enabled or not playing:
            self.go_idle(True)
            return
        s0, ln, count = self.song_start, self.section_len, self.section_count
        end = s0 + count * ln
        if pos < s0 or pos >= end:
            self.go_idle(False)
            return
        sec = (pos - s0) // ln
        if self.req_resync:
            self.req_resync = 0
            self.active = False
        restart, self.req_restart = self.req_restart != 0, 0
        sec_start = s0 + sec * ln
        if not self.active:
            self.active = True
            self.enter(sec)
        else:
            if restart:
                if pos == sec_start and sec == self.cur:
                    self.enter(sec)
                else:
                    self.restart_armed = True
            if pos != self.prev:
                if sec != self.cur:
                    self.enter(sec)
                elif pos < self.prev:
                    if self.restart_armed and pos == sec_start:
                        self.enter(sec)
                    else:
                        self.passn += 1
        self.prev = pos

        if self.req_next:
            self.req_next = 0
            self.next_queued = True
        if self.req_to_start:
            self.req_to_start = 0
            self.to_start_queued = True
        more, self.req_one_more = self.req_one_more, 0
        if more > 0:
            if self.next_queued or self.to_start_queued:
                self.next_queued = self.to_start_queued = False
            else:
                self.extra += more

        cur = self.cur
        if cur == 0:
            self.to_start_queued = False
        rep = self.repeats[cur]
        last = cur == count - 1
        count_done = rep != 0 and self.passn >= rep + self.extra
        ws = s0 + cur * ln
        we = ws + ln
        phase = LOOPING
        if self.hold:
            phase = HELD
        elif self.to_start_queued:
            phase, ws = RETURNING, s0
        elif self.next_queued or count_done:
            if not last:
                phase = LEAVING
                we += ln
            elif (self.next_queued or self.at_end == 1) and cur != 0:
                phase, ws = RETURNING, s0
        self.want_start, self.want_end, self.phase = ws, we, phase
        self.steering, self.restore_pending = True, False


class Song:
    """SongCore position / loop semantics."""

    def __init__(self, loop_start, loop_end):
        self.pos, self._ls, self._le = loop_start, loop_start, loop_end
        self.set_next = -1
        self.playing = False
        self.forced_jumps = 0

    def set_play_position(self, v):
        if v < self._ls:
            v = self._ls
        if self.playing:
            self.set_next = v
            self.forced_jumps += 1
        else:
            self.pos = v

    @property
    def loop_start(self):
        return self._ls

    @loop_start.setter
    def loop_start(self, v):
        self._ls = v
        if self.pos < self._ls:
            self.set_play_position(self._ls)

    @property
    def loop_end(self):
        return self._le

    @loop_end.setter
    def loop_end(self, v):
        self._le = v
        if self.pos >= self._le:
            self.set_play_position(self._ls)

    def tick(self):
        if self.set_next != -1:
            self.pos, self.set_next = self.set_next, -1
        else:
            self.pos += 1
        if self.pos >= self._le:
            self.pos = self._ls


class Machine:
    def __init__(self, song, latency=0, rng=None):
        self.song, self.e = song, LoopEngine()
        self.latency, self.rng = latency, rng
        self.pending = None          # countdown until the UI-thread call runs
        self.blocked_pos = -1

    def work(self):
        s, e = self.song, self.e
        e.step(s.pos, s.playing)
        if self.pending is not None:
            self.pending -= 1
            if self.pending < 0:
                self.pending = None
                self.apply()
        if not e.steering and not e.restore_pending:
            return
        if s.loop_start == e.want_start and s.loop_end == e.want_end:
            e.restore_pending = False
            self.blocked_pos = -1
            return
        if s.pos == self.blocked_pos or self.pending is not None:
            return
        lat = self.latency if self.rng is None else self.rng.randint(0, self.latency)
        self.pending = lat

    def apply(self):
        s, e = self.song, self.e
        ws, we = e.want_start, e.want_end
        if we <= ws or (not e.steering and not e.restore_pending):
            return
        blocked = False
        if s.loop_end != we:
            if not s.playing or we > s.loop_end or s.pos < we:
                s.loop_end = we
            else:
                blocked = True
        if s.loop_start != ws:
            if not s.playing or ws < s.loop_start or s.pos >= ws:
                s.loop_start = ws
            else:
                blocked = True
        self.blocked_pos = s.pos if blocked else -1


WORKS_PER_TICK = 3
LEN = 16


def make(counts, latency=0, rng=None, at_end=0):
    n = len(counts)
    song = Song(0, n * LEN)
    m = Machine(song, latency, rng)
    m.e.set_layout(0, LEN, n)
    for i, c in enumerate(counts):
        m.e.repeats[i] = c
    m.e.at_end = at_end
    return song, m


def run(song, m, ticks, actions=None, check=True):
    """Play `ticks` ticks. Returns the list of (section, passes) visits.
    actions: {tick: callable(machine)}. With check, asserts that every position
    change is +1, a wrap from a section's last tick to that section's start, or
    a wrap from a section's last tick to the song start."""
    e = m.e
    visits = []
    song.playing = True
    for t in range(ticks):
        if actions and t in actions:
            actions[t](m)
        for _ in range(WORKS_PER_TICK):
            m.work()
        sec = (song.pos - e.song_start) // LEN
        if not visits or visits[-1][0] != sec or song.pos < prev_pos:
            if visits and visits[-1][0] == sec:
                visits[-1][1] += 1
            else:
                visits.append([sec, 1])
        prev_pos = song.pos
        song.tick()
        if check:
            d = song.pos - prev_pos
            sec_start = e.song_start + sec * LEN
            at_last_tick = prev_pos == sec_start + LEN - 1
            ok = d == 1 or (at_last_tick and song.pos in (sec_start, e.song_start))
            assert ok, f"tick {t}: position jumped {prev_pos} -> {song.pos}"
    assert song.forced_jumps == 0 or not check, "a loop marker write forced the play position"
    return [tuple(v) for v in visits]


def expect(name, got, want):
    if got[:len(want)] != want:
        print(f"FAIL {name}\n  got  {got}\n  want {want}")
        sys.exit(1)
    print(f"ok   {name}")


def main():
    # 1. Counts followed in order; last section holds.
    for lat in (0, 1, 7, 30):
        song, m = make([2, 1, 3, 1, 1, 2], latency=lat)
        v = run(song, m, LEN * 30)
        expect(f"counts in order, hold on last (latency {lat} work calls)", v,
               [(0, 2), (1, 1), (2, 3), (3, 1), (4, 1), (5, 22)])
        assert (song.loop_start, song.loop_end) == (5 * LEN, 6 * LEN)

    # 2. At End = Back to start: cycles.
    song, m = make([1, 2, 1], at_end=1, latency=5)
    v = run(song, m, LEN * 12)
    expect("back to start cycles", v, [(0, 1), (1, 2), (2, 1), (0, 1), (1, 2), (2, 1), (0, 1), (1, 2), (2, 1)])

    # 3. Holding on the last section, then To Start.
    song, m = make([1, 1, 2], latency=3)
    v = run(song, m, LEN * 12, {LEN * 6 + 4: lambda mm: mm.e.__setattr__("req_to_start", 1)})
    expect("to start from held last section", v, [(0, 1), (1, 1), (2, 5), (0, 1), (1, 1), (2, 3)])

    # 4. Next on the last section also goes back to the start.
    song, m = make([1, 4], latency=2)
    v = run(song, m, LEN * 8, {LEN + 3: lambda mm: mm.e.__setattr__("req_next", 1)})
    expect("next on last section returns to start", v, [(0, 1), (1, 1), (0, 1), (1, 5)])

    # 5. Next leaves early; infinite section waits for Next.
    song, m = make([8, 0, 2, 1], latency=2)
    acts = {LEN + 5: lambda mm: mm.e.__setattr__("req_next", 1),
            LEN * 7 + 1: lambda mm: mm.e.__setattr__("req_next", 1)}
    v = run(song, m, LEN * 14, acts)
    expect("next + infinite", v, [(0, 2), (1, 6), (2, 2), (3, 4)])

    # 6. Hold keeps a section past its count; release leaves at the end of that pass.
    song, m = make([2, 2, 2], latency=2)
    acts = {3: lambda mm: mm.e.__setattr__("hold", True),
            LEN * 4 + 9: lambda mm: mm.e.__setattr__("hold", False)}
    v = run(song, m, LEN * 10, acts)
    expect("hold then release", v, [(0, 5), (1, 2), (2, 3)])

    # 7. Hold pressed on the final pass (loop end already extended) pulls it back.
    song, m = make([2, 2], latency=0)
    acts = {LEN + 8: lambda mm: mm.e.__setattr__("hold", True),
            LEN * 3 + 2: lambda mm: mm.e.__setattr__("hold", False)}
    v = run(song, m, LEN * 7, acts)
    expect("hold on final pass", v, [(0, 4), (1, 3)])

    # 8. One More adds a pass; One More cancels a queued Next.
    song, m = make([2, 2, 1], latency=1)
    acts = {LEN + 2: lambda mm: mm.e.__setattr__("req_one_more", 1),            # 2 -> 3 passes
            LEN * 3 + 2: lambda mm: mm.e.__setattr__("req_next", 1),            # queued on pass 1
            LEN * 3 + 9: lambda mm: mm.e.__setattr__("req_one_more", 1)}        # cancelled
    v = run(song, m, LEN * 8, acts)
    expect("one more", v, [(0, 3), (1, 2), (2, 3)])

    # 9. Stop puts the full range back; play resumes on the section under the cursor.
    song, m = make([2, 2, 2], latency=4)
    run(song, m, LEN * 2 + 5)
    assert (song.loop_start, song.loop_end) == (LEN, 2 * LEN), (song.loop_start, song.loop_end)
    song.playing = False
    for _ in range(20):
        m.work()
    assert (song.loop_start, song.loop_end) == (0, 3 * LEN), (song.loop_start, song.loop_end)
    assert not m.e.restore_pending
    song.loop_end = 5 * LEN             # user edits the marker while stopped
    for _ in range(20):
        m.work()
    assert song.loop_end == 5 * LEN, "machine fought a marker edit while stopped"
    song.loop_end = 3 * LEN
    v = run(song, m, LEN * 3)
    expect("resume after stop starts a fresh count on the same section", v, [(1, 2), (2, 2)])
    print("ok   stop restores the range and leaves it alone afterwards")

    # 10. Disable while playing releases the loop; playback runs on through.
    song, m = make([4, 4, 4], latency=2)
    v = run(song, m, LEN * 4, {5: lambda mm: mm.e.__setattr__("enabled", False)})
    expect("disable while playing", v, [(0, 1), (1, 1), (2, 1), (0, 1)])
    assert (song.loop_start, song.loop_end) == (0, 3 * LEN)

    # 11. Nonzero first tick.
    song = Song(0, 200)
    m = Machine(song, 3)
    m.e.set_layout(32, LEN, 3)
    for i, c in enumerate([2, 1, 1]):
        m.e.repeats[i] = c
    song.pos = 32
    v = run(song, m, LEN * 6)
    expect("range not starting at tick 0", v, [(0, 2), (1, 1), (2, 3)])

    # 12. Fuzz: random presses, random UI latency of up to 10 ticks (sections are
    #     16 ticks here). The play position must never be jumped by a marker
    #     write, and every wrap must land on a section boundary. A UI stall
    #     longer than a whole section is outside this guarantee: see test 13.
    for seed in range(400):
        rng = random.Random(seed)
        n = rng.randint(1, 6)
        counts = [rng.choice([0, 1, 1, 2, 3]) for _ in range(n)]
        song, m = make(counts, latency=rng.choice([0, 1, 3, 10, 30]), rng=rng, at_end=rng.randint(0, 1))
        acts = {}
        for _ in range(rng.randint(0, 25)):
            t = rng.randint(0, LEN * 40)
            kind = rng.choice(["req_next", "req_to_start", "req_one_more", "hold_on", "hold_off"])
            if kind == "hold_on":
                acts[t] = lambda mm: mm.e.__setattr__("hold", True)
            elif kind == "hold_off":
                acts[t] = lambda mm: mm.e.__setattr__("hold", False)
            else:
                acts[t] = (lambda k: lambda mm: mm.e.__setattr__(k, 1))(kind)
        run(song, m, LEN * 40, acts)
    print("ok   fuzz: 400 runs, no forced position jump, every wrap on a section boundary")

    # 14. Play button while playing: restart the current section, count from pass 1.
    def press_play(mm):
        e, sg = mm.e, mm.song
        sec = (sg.pos - e.song_start) // e.section_len
        if sg.playing:
            e.req_restart = 1
        sg.set_play_position(e.song_start + sec * e.section_len)
        sg.playing = True

    for when in (LEN + 6, LEN):          # mid-pass, and exactly on the first tick
        song, m = make([3, 2], latency=2)
        run(song, m, when, check=False)
        m.work()
        assert (m.e.cur, m.e.passn) == (0, 2), (m.e.cur, m.e.passn)
        press_play(m)
        run(song, m, 3, check=False)
        assert (m.e.cur, m.e.passn) == (0, 1), (m.e.cur, m.e.passn)
        assert song.pos in (2, 3), song.pos
        v = run(song, m, LEN * 6 - song.pos, check=False)
        assert v[-1][0] == 1 and m.e.passn >= 2, v        # 3 fresh passes, then section 2
        assert song.pos // LEN == 1
    # ...and from stopped: starts at the first tick of the section under the cursor.
    song, m = make([2, 2], latency=2)
    song.pos = LEN + 9
    m.work()
    press_play(m)
    assert song.pos == LEN
    v = run(song, m, LEN * 3)
    expect("play from stopped starts the cued section at its first tick", v, [(1, 3)])
    print("ok   play button restarts the section with a fresh count")

    # 13. UI stall longer than a section: wraps can go to a stale loop start, but
    #     a marker write still never forces the position.
    for seed in range(200):
        rng = random.Random(1000 + seed)
        counts = [rng.choice([0, 1, 2]) for _ in range(rng.randint(2, 6))]
        song, m = make(counts, latency=rng.choice([60, 120, 300]), rng=rng, at_end=rng.randint(0, 1))
        acts = {rng.randint(0, LEN * 40): (lambda k: lambda mm: mm.e.__setattr__(k, 1))(rng.choice(["req_next", "req_to_start", "req_one_more"]))
                for _ in range(10)}
        run(song, m, LEN * 40, acts, check=False)
        assert song.forced_jumps == 0, "marker write forced the play position under a long stall"
    print("ok   long UI stalls: 200 runs, no forced position jump")

    print("\nall checks passed")


if __name__ == "__main__":
    main()
