using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Sells surplus currency for Chaos on the Faustus Currency Exchange.
    ///
    /// Flow: walk to Faustus → open Currency Exchange → scan the "I Have" picker to build a queue
    /// of currencies whose stack value (owned qty × ninja unit chaos) exceeds the threshold and
    /// aren't excluded → for each (up to the per-run cap): I-Have = that currency, I-Want = Chaos,
    /// place order at the exchange's default quantity/market rate.
    ///
    /// v1: places at the exchange's auto-filled quantity/rate (does NOT yet force the full stack).
    /// Reuses the interaction pattern proven in FaustusSystem.
    /// </summary>
    public class SellExchangeSystem
    {
        private const string FaustusPath = "Metadata/NPC/League/Kalguur/VillageFaustusHideout";
        private const int ClickCooldownMs = 400;
        private const float StateTimeoutSeconds = 12f;
        private const string WantCurrencyBaseName = "Chaos Orb";

        private static readonly NinjaPriceCategory[] EligibleCats =
        {
            NinjaPriceCategory.Currency, NinjaPriceCategory.Fragment, NinjaPriceCategory.Scarab,
            NinjaPriceCategory.Essence, NinjaPriceCategory.Oil, NinjaPriceCategory.Fossil,
            NinjaPriceCategory.Resonator, NinjaPriceCategory.DeliriumOrb, NinjaPriceCategory.Artifact,
            NinjaPriceCategory.Omen, NinjaPriceCategory.KalguuranRune, NinjaPriceCategory.AllflameEmber,
            NinjaPriceCategory.DjinnCoin, NinjaPriceCategory.Astrolabe,
        };

        private SellState _state = SellState.Idle;
        private DateTime _stateEnteredAt = DateTime.MinValue;
        private DateTime _lastClickAt = DateTime.MinValue;

        private readonly Queue<(string name, int qty)> _queue = new(); // still to sell
        private string _current = "";
        private int _currentQty; // full owned stack of _current, to list for sale
        private int _ordersPlaced;
        private int _maxOrders = 3;
        private double _thresholdChaos = 50.0;
        private HashSet<string> _exclusions = new(StringComparer.OrdinalIgnoreCase);
        private bool _havePicked;
        private bool _wantPicked;
        private bool _ownedFilter; // = finished typing the search filter for this candidate
        private bool _lockedWant;
        private bool _lockedHave;
        private string _searchText = "";
        private int _searchIndex;
        private bool _ownedClicked;
        private DateTime _ownedClickedAt;
        private bool _searchFocused;
        private DateTime _searchFocusedAt;
        private bool _searchCleared;
        private bool _searchEmptied;
        private int _searchRetries;
        private bool _ownedSettled;                             // Owned grid finished (re)rendering
        private int _ownedPrevOptCount = -1;                   // last option count seen while settling
        private DateTime _ownedStabilityCheckAt = DateTime.MinValue;
        private DateTime _ownedFilterAt = DateTime.MinValue;   // when the full search filter finished typing
        private int _searchAttempts;                           // focus+type retries for the current candidate
        private DateTime _lastTypeAt = DateTime.MinValue;
        private const int TypeIntervalMs = 70;  // per-key type pace (~14 keys/s); keys use PressKeyTyping
        private const int ClickSettleMs = 380;  // wait after a focus-click for it to fully land (async ~250-300ms) before Ctrl+A/typing — the fast TypeIntervalMs is too short for this
        private const int OwnedSettleMinMs = 900;   // never focus earlier than the old proven floor (grid still rendering)
        private const int OwnedSettleMaxMs = 2500;  // …but never wait longer than this for the grid to go stable
        private const int FilterResultWaitMs = 900; // after typing, wait this long for the result cell to appear
        private const int MaxSearchAttempts = 3;    // re-focus + retype this many times before skipping the item
        private bool _amountFocused;
        private bool _amountCleared;
        private string _amountText = "";
        private int _amountIndex;
        private bool _wantFocused;
        private bool _wantCleared;
        private bool _wantTyped;
        private bool _wantClickedOut;
        private bool _wantFinalized;
        private DateTime _wantPickerOpenedAt = DateTime.MinValue; // when the I-Want picker was opened (settle before clicking)
        private int _wantVerifyAttempts;                          // re-selection tries when the locked I-Want isn't Chaos
        private int _placeAttempts;
        private int _placeSlots0 = -1;
        private DateTime _lastPlaceClickAt = DateTime.MinValue;
        private bool _clrHaveFocused, _clrHaveSelected, _clrHaveDone;
        private bool _clrWantFocused, _clrWantSelected, _clrWantDone;

        public bool IsBusy => _state != SellState.Idle;
        public string Status { get; private set; } = "";
        public int OrdersPlaced => _ordersPlaced;

        private readonly List<string> _log = new();
        public IReadOnlyList<string> Log => _log;
        private void AddLog(string m)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss} {m}");
            if (_log.Count > 12) _log.RemoveAt(0);
        }

        /// <summary>Begin a sell run. Candidates are computed from the exchange once the panel opens.</summary>
        public void Start(int maxOrders, double thresholdChaos, HashSet<string> exclusions)
        {
            _queue.Clear();
            _current = "";
            _ordersPlaced = 0;
            _maxOrders = System.Math.Max(1, maxOrders);
            _thresholdChaos = thresholdChaos;
            _exclusions = exclusions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _havePicked = false;
            _wantPicked = false;
            _log.Clear();
            AddLog($"start (max={_maxOrders}, thr={_thresholdChaos:F0}c)");
            SetState(SellState.WalkingToFaustus);
        }

        public void Cancel(GameController gc = null, NavigationSystem nav = null)
        {
            if (gc != null && nav != null) nav.Stop(gc);
            _queue.Clear();
            SetState(SellState.Idle);
        }

        public void Tick(BotContext ctx)
        {
            if (_state == SellState.Idle) return;

            if ((DateTime.Now - _stateEnteredAt).TotalSeconds > StateTimeoutSeconds)
            {
                Status = $"Sell: timeout in {_state}";
                AddLog($"TIMEOUT in {_state}");
                Cancel(ctx.Game, ctx.Navigation);
                return;
            }

            switch (_state)
            {
                case SellState.WalkingToFaustus: TickWalk(ctx); break;
                case SellState.WaitingForDialog: TickWaitDialog(ctx); break;
                case SellState.ClickingExchange: TickClickExchange(ctx); break;
                case SellState.WaitingForPanel: TickWaitPanel(ctx); break;
                case SellState.ScanCandidates: TickScan(ctx); break;
                case SellState.PickingHave: TickPickHave(ctx); break;
                case SellState.PickingWant: TickPickWant(ctx); break;
                case SellState.LockingAmounts: TickLockAmounts(ctx); break;
                case SellState.PlacingOrder: TickPlaceOrder(ctx); break;
                case SellState.ClearingBuilder: TickClearBuilder(ctx); break;
            }
        }

        // ── Navigation (mirrors FaustusSystem) ──

        private void TickWalk(BotContext ctx)
        {
            var gc = ctx.Game;
            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog != null && dialog.IsVisible) { SetState(SellState.ClickingExchange); return; }

            var faustus = FindFaustus(gc);
            if (faustus == null) { Status = "Sell: Faustus not found"; return; }

            if (ctx.Interaction.IsBusy)
            {
                var r = ctx.Interaction.Tick(gc);
                Status = $"Sell: walking to Faustus ({ctx.Interaction.Status})";
                if (r == InteractionResult.Succeeded || r == InteractionResult.Failed)
                    SetState(SellState.WaitingForDialog);
                return;
            }
            ctx.Interaction.InteractWithEntity(faustus, ctx.Navigation, requireProximity: true);
            Status = "Sell: interacting with Faustus";
        }

        private void TickWaitDialog(BotContext ctx)
        {
            var gc = ctx.Game;
            if (ctx.Interaction.IsBusy)
            {
                var r = ctx.Interaction.Tick(gc);
                if (r == InteractionResult.Failed) { Status = "Sell: interaction failed"; Cancel(gc, ctx.Navigation); return; }
            }
            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog == null || !dialog.IsVisible) { Status = "Sell: waiting for dialog"; return; }
            SetState(SellState.ClickingExchange);
        }

        private void TickClickExchange(BotContext ctx)
        {
            var gc = ctx.Game;
            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog == null || !dialog.IsVisible) { Status = "Sell: dialog closed"; Cancel(gc, ctx.Navigation); return; }
            if (!CanClick()) return;

            var lines = dialog.NpcLines;
            if (lines == null || lines.Count == 0) { Status = "Sell: no dialog lines"; return; }

            foreach (var line in lines)
            {
                if (line?.Text?.Contains("Continue", StringComparison.OrdinalIgnoreCase) == true)
                { Status = "Sell: clicking Continue"; ClickElement(gc, line.Element); return; }
            }
            foreach (var line in lines)
            {
                if (line?.Text?.Contains("Exchange", StringComparison.OrdinalIgnoreCase) == true)
                { Status = "Sell: clicking Currency Exchange"; ClickElement(gc, line.Element); SetState(SellState.WaitingForPanel); return; }
            }
            Status = "Sell: looking for Currency Exchange option";
        }

        private void TickWaitPanel(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel != null && panel.IsVisible)
            {
                // Panel's up — make sure navigation isn't still issuing movement clicks (they would
                // defocus the search box mid-type). We're parked at Faustus now; nothing more to walk.
                ctx.Navigation?.Stop(gc);
                SetState(SellState.ScanCandidates);
                return;
            }
            if ((DateTime.Now - _stateEnteredAt).TotalSeconds < 2.0) { Status = "Sell: waiting for exchange panel"; return; }
            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog != null && dialog.IsVisible) { SetState(SellState.ClickingExchange); return; }
            Status = "Sell: waiting for exchange panel";
        }

        // ── Candidate scan: open the I-Have picker, read owned qty × ninja price ──

        private void TickScan(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) { Status = "Sell: panel closed"; Cancel(gc, ctx.Navigation); return; }

            var picker = panel.CurrencyPicker;
            // Need the I-Have picker open to read owned quantities.
            if (picker == null || !picker.IsVisible)
            {
                if (!CanClick()) return;
                ClickChild(gc, panel, 10, 0); // I-Have button
                Status = "Sell: opening I Have picker to scan";
                return;
            }
            if (picker.IsPickingWantedCurrency) { Status = "Sell: wrong picker open"; return; }

            // Wait for the option list to actually populate before concluding anything —
            // reading on the first visible frame yields 0 and would wrongly report "no candidates".
            int optCount = 0;
            try { foreach (var _ in picker.Options) optCount++; } catch { }
            if (optCount == 0) { Status = "Sell: waiting for I Have options to load…"; return; }

            // Build the queue from the picker options.
            var rows = new List<(string name, int qty, double total)>();
            var unpriced = new List<(string name, int qty)>(); // owned but ninja returned no price (diagnostic)
            try
            {
                foreach (var opt in picker.Options)
                {
                    string name = null;
                    try { name = (string)opt.ItemType.BaseName; } catch { }
                    if (string.IsNullOrEmpty(name) || _exclusions.Contains(name)) continue;
                    int qty = ReadPickerQty((ExileCore.PoEMemory.Element)opt);
                    if (qty <= 0) continue;
                    double unit = UnitChaos(ctx, name);
                    if (unit <= 0.0) { unpriced.Add((name, qty)); continue; }
                    double total = qty * unit;
                    if (total >= _thresholdChaos) rows.Add((name, qty, total));
                }
            }
            catch { }

            DumpUnpriced(ctx, unpriced);

            rows.Sort((a, b) => b.total.CompareTo(a.total));
            _queue.Clear();
            int added = 0;
            foreach (var r in rows)
            {
                if (added >= _maxOrders) break;
                _queue.Enqueue((r.name, r.qty));
                added++;
            }

            AddLog($"scan opts={optCount} cands={rows.Count} queued={added}");
            if (_queue.Count == 0) { Status = $"Sell: no candidates (opts={optCount})"; SetState(SellState.Idle); return; }

            var (cn, cq) = _queue.Dequeue();
            _current = cn;
            _currentQty = cq;
            _havePicked = false;
            _wantPicked = false;
            _ownedFilter = false;
            _lockedWant = false;
            _lockedHave = false;
            _searchText = SearchPrefix(_current);
            _searchIndex = 0;
            _searchRetries = 0;
            _ownedClicked = false;
            _ownedClickedAt = DateTime.MinValue;
            _ownedSettled = false;
            _ownedPrevOptCount = -1;
            _ownedStabilityCheckAt = DateTime.MinValue;
            _ownedFilterAt = DateTime.MinValue;
            _searchAttempts = 0;
            _searchFocused = false;
            _searchCleared = false;
            _searchEmptied = false;
            _amountFocused = false;
            _amountCleared = false;
            _amountText = _currentQty.ToString();
            _amountIndex = 0;
            _wantFocused = false;
            _wantCleared = false;
            _wantTyped = false;
            _wantClickedOut = false;
            _wantFinalized = false;
            _wantPickerOpenedAt = DateTime.MinValue;
            _wantVerifyAttempts = 0;
            _placeAttempts = 0;
            _placeSlots0 = -1;
            _lastPlaceClickAt = DateTime.MinValue;
            Status = $"Sell: queued {added}/{optCount}; first = {_current}";
            SetState(SellState.PickingHave);
        }

        // ── Per-candidate: pick I-Have currency, pick I-Want Chaos, place order ──

        private void TickPickHave(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) { Status = "Sell: panel closed"; Cancel(gc, ctx.Navigation); return; }
            var picker = panel.CurrencyPicker;

            if (_havePicked)
            {
                // Proceed after a short wait even if the picker lingers open (don't hang).
                if (picker != null && picker.IsVisible && (DateTime.Now - _lastClickAt).TotalMilliseconds < 2000)
                { Status = "Sell: waiting I Have picker close"; return; }
                SetState(SellState.PickingWant); return;
            }

            if (picker != null && picker.IsVisible && !picker.IsPickingWantedCurrency)
            {
                // Type the currency name into the picker's (auto-focused) search box to filter the
                // 1000+ item list down to the match, which then appears at the top / on-screen.
                // 1) Switch to the "Owned" category first.
                if (!_ownedClicked)
                {
                    if (!CanClick()) return;
                    var ownedTab = FindElementByText((ExileCore.PoEMemory.Element)panel, "Owned", 0);
                    _ownedClicked = true;
                    _ownedClickedAt = DateTime.Now;
                    _ownedSettled = false;
                    _ownedPrevOptCount = -1;
                    _ownedStabilityCheckAt = DateTime.Now;
                    if (ownedTab != null) { ClickRect(gc, ownedTab); AddLog("clicked Owned"); }
                    else AddLog("Owned tab not found");
                    return;
                }

                // 1b) Let the Owned grid finish (re)rendering BEFORE focusing the search box. The tab
                // switch kicks off an async grid re-render; focusing + typing mid-render lets the
                // re-render steal focus and keys leak into the game. A FIXED delay is unreliable —
                // at 4K the grid renders ~4x the pixels and can still be rebuilding after 900ms.
                // Instead poll the option count until it stops changing (grid stable), bounded by a
                // min (never focus too early) and a max (never hang).
                if (!_ownedSettled)
                {
                    double sinceOwned = (DateTime.Now - _ownedClickedAt).TotalMilliseconds;
                    if (sinceOwned < OwnedSettleMinMs) { Status = "Sell: settling Owned grid"; return; }
                    if (sinceOwned > OwnedSettleMaxMs) { _ownedSettled = true; AddLog("owned settle: timeout"); return; }
                    if ((DateTime.Now - _ownedStabilityCheckAt).TotalMilliseconds < 180)
                    { Status = "Sell: settling Owned grid"; return; }
                    _ownedStabilityCheckAt = DateTime.Now;
                    int cnt = CountOptions(picker);
                    if (cnt > 0 && cnt == _ownedPrevOptCount)
                    { _ownedSettled = true; AddLog($"owned settle: stable at {cnt}"); return; }
                    _ownedPrevOptCount = cnt;
                    Status = "Sell: settling Owned grid";
                    return;
                }

                // 2) Do NOT click to focus the search box. Confirmed in-game: after I-Have → Owned the
                // picker's search box is ALREADY selected (it auto-focuses on open and stays focused).
                // Clicking it only moves the cursor OFF it and DESELECTS it — that was the whole
                // "search box deselects itself" failure. So we don't click at all; just type. The
                // result-verify below re-tries if a filter somehow didn't land.
                if (!_searchFocused)
                {
                    _searchFocused = true;
                    _searchFocusedAt = DateTime.Now;
                    return;
                }

                // 3) Select-all to clear any leftover text, then type the name one key at a time.
                if (!_ownedFilter)
                {
                    if (!_searchCleared)
                    {
                        // Small settle after the Owned grid stabilised; the box is already focused.
                        if ((DateTime.Now - _searchFocusedAt).TotalMilliseconds < 350)
                        { Status = "Sell: ready to type"; return; }
                        AddLog("clear search (select-all + delete)");
                        BotInput.SelectAll();
                        _searchCleared = true;
                        _lastTypeAt = DateTime.Now;
                        return;
                    }
                    // Delete the selection to actually empty the box. On the 2nd+ item the box still
                    // holds the previous item's name; select-all + retype doesn't reliably replace it,
                    // so an explicit Delete clears it before we type the new name.
                    if (!_searchEmptied)
                    {
                        if ((DateTime.Now - _lastTypeAt).TotalMilliseconds < TypeIntervalMs) return;
                        BotInput.PressKeyTyping(System.Windows.Forms.Keys.Delete);
                        _searchEmptied = true;
                        _lastTypeAt = DateTime.Now;
                        return;
                    }
                    if (_searchIndex < _searchText.Length)
                    {
                        if ((DateTime.Now - _lastTypeAt).TotalMilliseconds < TypeIntervalMs) return;
                        if (!BotInput.CanAct) return;
                        var k = CharToKey(_searchText[_searchIndex]);
                        if (k != System.Windows.Forms.Keys.None) BotInput.PressKeyTyping(k);
                        _lastTypeAt = DateTime.Now;
                        if (_searchIndex == 0) AddLog($"typing '{_searchText}'");
                        _searchIndex++;
                        return;
                    }
                    _ownedFilter = true;         // filter typed
                    _ownedFilterAt = DateTime.Now;
                    _lastClickAt = DateTime.Now; // settle for the list to filter
                    return;
                }

                // 4) Click the filtered result — topmost on-screen match in the picker grid. The
                // visible band must span the FULL window height: on a 3840x2160 screen a result cell
                // can render below y=2000, and a hardcoded ceiling would wrongly treat it as
                // off-screen (this alone made selection "work sometimes" at 4K).
                float winH = gc.Window.GetWindowRectangle().Height;
                var cell = FindTopmostVisible((ExileCore.PoEMemory.Element)picker, _current, 60f, winH - 10f, 0);
                if (cell == null)
                {
                    // Result never appeared → the filter didn't take (focus was lost / keys leaked,
                    // which varies at 4K). Give it a moment, then re-focus + retype rather than hang.
                    if ((DateTime.Now - _ownedFilterAt).TotalMilliseconds < FilterResultWaitMs)
                    { Status = $"Sell: waiting for {_current} to filter…"; return; }
                    _searchAttempts++;
                    if (_searchAttempts >= MaxSearchAttempts)
                    {
                        AddLog($"SKIP {_current}: search never focused ({_searchAttempts} tries)");
                        Status = $"Sell: skipped {_current} (search focus failed)";
                        SkipCurrentCandidate();
                        return;
                    }
                    AddLog($"search retry {_searchAttempts} for {_current}");
                    _searchFocused = false;   // re-click the box (re-acquire focus)
                    _searchCleared = false;
                    _searchEmptied = false;
                    _ownedFilter = false;
                    _searchIndex = 0;
                    _stateEnteredAt = DateTime.Now; // give the retry a fresh state-timeout window
                    return;
                }
                if (!CanClick()) return;
                AddLog($"have {_current} y={cell.GetClientRect().Y:F0}");
                ClickRect(gc, cell);
                _havePicked = true;
                Status = $"Sell: selected I Have = {_current}";
                return;
            }

            if (!CanClick()) return;
            ClickChild(gc, panel, 10, 0); // I-Have button
            Status = "Sell: opening I Have picker";
        }

        private void TickPickWant(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) { Status = "Sell: panel closed"; Cancel(gc, ctx.Navigation); return; }
            var picker = panel.CurrencyPicker;

            if (_wantPicked)
            {
                // Wait for the I-Want picker to close, then VERIFY the locked-in currency BEFORE we
                // ever set amounts or place an order. A stale/off-screen cell rect once landed the
                // click on a neighbour and the order sold for Baubles — never trade for the wrong
                // currency. The selected I-Want name is the I-Want button's label (panel child 7/0).
                if (picker != null && picker.IsVisible && (DateTime.Now - _lastClickAt).TotalMilliseconds < 2000)
                { Status = "Sell: waiting I Want picker close"; return; }

                string wantName = SelectedWantName(panel);
                if (!IsDefinitelyWrongWant(wantName))
                {
                    // Chaos confirmed, OR the I-Want label was unreadable. Only a DEFINITELY-wrong
                    // (readable, non-Chaos) name blocks us — otherwise we trust the pick (the picker
                    // cell we clicked was a verified on-screen "Chaos Orb" match). This keeps a
                    // possibly-mismapped label element from skipping every single item ("0 orders").
                    AddLog(IsChaos(wantName)
                        ? $"verified I-Want = '{wantName}'"
                        : $"I-Want label unreadable ('{wantName}') — trusting Chaos cell pick");
                    SetState(SellState.LockingAmounts);
                    return;
                }

                // A different, readable currency is locked in. Re-open + re-select a few times; if it
                // still won't take, SKIP this candidate rather than place a bad trade.
                _wantVerifyAttempts++;
                AddLog($"I-Want WRONG ('{wantName}') try {_wantVerifyAttempts}");
                if (_wantVerifyAttempts >= 3)
                {
                    AddLog($"SKIP {_current}: could not set I-Want to Chaos");
                    Status = $"Sell: skipped {_current} (I-Want != Chaos)";
                    SkipCurrentCandidate();
                    return;
                }
                _wantPicked = false;              // fall through next tick reopens the picker
                _wantPickerOpenedAt = DateTime.MinValue;
                return;
            }

            if (picker != null && picker.IsVisible && picker.IsPickingWantedCurrency)
            {
                // Let the list settle so cell rects are stable — clicking a still-animating/off-screen
                // rect is what mis-selected a neighbouring currency (the Baubles bug).
                if ((DateTime.Now - _wantPickerOpenedAt).TotalMilliseconds < 450)
                { Status = "Sell: settling I Want picker"; return; }
                if (!CanClick()) return;

                // Click Chaos as an ON-SCREEN cell (topmost visible match), not a possibly off-screen
                // FindPickerOption rect. Fall back to the exact-BaseName option only if its rect is
                // actually within the window.
                float winH = gc.Window.GetWindowRectangle().Height;
                var cell = FindTopmostVisible((ExileCore.PoEMemory.Element)picker, WantCurrencyBaseName, 80f, winH - 20f, 0);
                if (cell == null)
                {
                    var option = FindPickerOption(picker, null, WantCurrencyBaseName);
                    if (option != null)
                    {
                        var r = option.GetClientRect();
                        if (r.Y >= 80f && r.Y <= winH - 20f) cell = option;
                    }
                }
                if (cell == null) { Status = "Sell: Chaos not visible in I Want picker"; return; }

                AddLog($"want Chaos y={cell.GetClientRect().Y:F0}");
                ClickRect(gc, cell);
                _wantPicked = true;
                Status = "Sell: selected I Want = Chaos";
                return;
            }

            if (!CanClick()) return;
            ClickChild(gc, panel, 7, 0); // I-Want button
            _wantPickerOpenedAt = DateTime.Now;
            Status = "Sell: opening I Want picker";
        }

        private void TickLockAmounts(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) { Status = "Sell: panel closed"; Cancel(gc, ctx.Navigation); return; }
            // Set the I-Have amount to the full owned stack: focus the amount box (panel child 8),
            // select-all, then type the quantity. The I-Want (Chaos) amount auto-computes from the
            // market ratio.
            if (!_amountFocused)
            {
                if (!CanClick()) return;
                // Confirm child 8 really is the (numeric) amount box before clicking/typing, so we
                // never type digits into the game (number keys are flask slots!).
                var c8 = SafeChildText(panel, 8);
                if (!IsNumericAmount(c8))
                {
                    AddLog($"abort: amount box c8='{c8}' not numeric");
                    Status = "Sell: amount box not found — aborting (not typing)";
                    Cancel(gc, ctx.Navigation);
                    return;
                }
                AddLog($"amt box c8='{c8}' -> {_amountText}");
                ClickChildSingle(gc, panel, 8);
                _amountFocused = true;
                _lastTypeAt = DateTime.Now;
                Status = "Sell: focusing amount box";
                return;
            }
            if (!_amountCleared)
            {
                if ((DateTime.Now - _lastClickAt).TotalMilliseconds < ClickSettleMs) return; // let the focus-click fully land first
                BotInput.SelectAll();
                _amountCleared = true;
                _lastTypeAt = DateTime.Now;
                return;
            }
            if (_amountIndex < _amountText.Length)
            {
                if ((DateTime.Now - _lastTypeAt).TotalMilliseconds < TypeIntervalMs) return;
                if (!BotInput.CanAct) return;
                var k = CharToKey(_amountText[_amountIndex]);
                if (k != System.Windows.Forms.Keys.None) BotInput.PressKeyTyping(k);
                _lastTypeAt = DateTime.Now;
                if (_amountIndex == 0) AddLog($"typing amount {_amountText}");
                _amountIndex++;
                return;
            }

            // I-Have stack is set. Now type 0 into the I-Want (Chaos) amount box (child 5): the
            // exchange auto-computes a valid ratio + amount, which un-greys Place Order (this is the
            // manual flow — typing an amount then 0 on the other side).
            if (!_wantFocused)
            {
                if (!CanClick()) return;
                var c5 = SafeChildText(panel, 5);
                if (!IsNumericAmount(c5))
                {
                    AddLog($"abort: I-Want box c5='{c5}' not numeric");
                    Status = "Sell: I-Want box not found — aborting";
                    Cancel(gc, ctx.Navigation);
                    return;
                }
                AddLog($"want box c5='{c5}' -> 0");
                ClickChildSingle(gc, panel, 5);
                _wantFocused = true;
                _lastTypeAt = DateTime.Now;
                return;
            }
            if (!_wantCleared)
            {
                if ((DateTime.Now - _lastClickAt).TotalMilliseconds < ClickSettleMs) return; // let the focus-click fully land first
                BotInput.SelectAll();
                _wantCleared = true;
                _lastTypeAt = DateTime.Now;
                return;
            }
            if (!_wantTyped)
            {
                if ((DateTime.Now - _lastTypeAt).TotalMilliseconds < TypeIntervalMs) return;
                if (!BotInput.CanAct) return;
                BotInput.PressKeyTyping(System.Windows.Forms.Keys.D0); // type "0"
                _wantTyped = true;
                _lastTypeAt = DateTime.Now;
                AddLog("typed 0 into I-Want");
                return;
            }
            // Give the exchange a moment to recompute the ratio.
            if ((DateTime.Now - _lastTypeAt).TotalMilliseconds < 500) return;

            // Click out of the Chaos box (Market Ratio label) so the 0 commits.
            if (!_wantClickedOut)
            {
                if (!CanClick()) return;
                ClickChildSingle(gc, panel, 14);
                _wantClickedOut = true;
                AddLog("clicked out of Chaos box");
                return;
            }
            // Now click the Chaos amount box once more to finalize the sell amount before placing.
            if (!_wantFinalized)
            {
                if (!CanClick()) return;
                ClickChildSingle(gc, panel, 5);
                _wantFinalized = true;
                AddLog("finalized Chaos amount");
                return;
            }
            SetState(SellState.PlacingOrder);
        }

        private void TickPlaceOrder(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) { Status = "Sell: panel closed"; Cancel(gc, ctx.Navigation); return; }

            // FINAL SAFETY NET: never place when a DEFINITELY-wrong (readable, non-Chaos) currency is
            // locked in. Belt-and-suspenders over the PickingWant verify. An unreadable label doesn't
            // block placement (see PickingWant) — only a positively-wrong name does.
            string wantName = SelectedWantName(panel);
            if (IsDefinitelyWrongWant(wantName))
            {
                AddLog($"ABORT place {_current}: I-Want '{wantName}' != Chaos");
                Status = $"Sell: aborted place ({_current}) — I-Want != Chaos";
                SkipCurrentCandidate();
                return;
            }

            // Give Place Order a moment to enable after finalizing the amount.
            if ((DateTime.Now - _stateEnteredAt).TotalMilliseconds < 600) return;

            // Watch the order-slot count (child 19, "N/10"): a placed order bumps it. The first
            // click after finalizing often only defocuses the amount field instead of placing, so
            // retry — but stop the instant the slot count rises so we never double-place.
            if (_placeSlots0 < 0) _placeSlots0 = FindSlotCount(panel); // capture once
            int slotsNow = FindSlotCount(panel);
            bool placed = _placeSlots0 >= 0 && slotsNow > _placeSlots0;

            if (!placed && _placeAttempts < 3)
            {
                // Wait between clicks so a successful placement registers before we click again.
                if ((DateTime.Now - _lastPlaceClickAt).TotalMilliseconds < 900) return;
                if (!CanClick()) return;
                AddLog($"place attempt {_placeAttempts + 1} (slots {slotsNow})");
                ClickChild(gc, panel, 16, 0); // place order
                _placeAttempts++;
                _lastPlaceClickAt = DateTime.Now;
                return;
            }

            if (placed)
            {
                _ordersPlaced++;
                AddLog($"placed #{_ordersPlaced} {_current} (slots {slotsNow})");
                Status = $"Sell: placed order {_ordersPlaced} ({_current})";
            }
            else
            {
                AddLog($"place FAILED after {_placeAttempts} (slots {slotsNow})");
                Status = $"Sell: place failed ({_current})";
            }

            // Clear the item + Chaos out of the builder before moving on, so no leftover order
            // lingers there to re-fire into the (only 10) order slots.
            _clrHaveFocused = _clrHaveSelected = _clrHaveDone = false;
            _clrWantFocused = _clrWantSelected = _clrWantDone = false;
            SetState(SellState.ClearingBuilder);
        }

        // Empty the I-Have (child 8) and I-Want/Chaos (child 5) amount boxes so the builder holds no
        // placeable order between/after orders — keeps the 10 order slots from filling with leftovers.
        private void TickClearBuilder(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) { AdvanceAfterClear(); return; }

            if (!_clrHaveDone)
            { if (ClearAmountBox(gc, panel, 8, ref _clrHaveFocused, ref _clrHaveSelected)) _clrHaveDone = true; return; }
            if (!_clrWantDone)
            { if (ClearAmountBox(gc, panel, 5, ref _clrWantFocused, ref _clrWantSelected)) _clrWantDone = true; return; }

            AddLog("cleared builder");
            AdvanceAfterClear();
        }

        // Focus a numeric amount box, select-all, delete. Returns true once cleared (or safely
        // skipped if the child isn't a numeric box — never types unless the box is confirmed+focused).
        private bool ClearAmountBox(GameController gc, dynamic panel, int childIdx, ref bool focused, ref bool selected)
        {
            if (!focused)
            {
                if (!CanClick()) return false;
                if (!IsNumericAmount(SafeChildText(panel, childIdx))) return true; // not the box — skip, don't type
                ClickChildSingle(gc, panel, childIdx);
                focused = true;
                _lastTypeAt = DateTime.Now;
                return false;
            }
            if (!selected)
            {
                if ((DateTime.Now - _lastClickAt).TotalMilliseconds < ClickSettleMs) return false; // let the focus-click fully land first
                BotInput.SelectAll();
                selected = true;
                _lastTypeAt = DateTime.Now;
                return false;
            }
            if ((DateTime.Now - _lastTypeAt).TotalMilliseconds < TypeIntervalMs) return false;
            if (!BotInput.CanAct) return false;
            BotInput.PressKeyTyping(System.Windows.Forms.Keys.Delete);
            _lastTypeAt = DateTime.Now;
            return true;
        }

        // Move to the next queued candidate, or finish the run.
        private void AdvanceAfterClear()
        {
            if (_ordersPlaced >= _maxOrders || _queue.Count == 0)
            { SetState(SellState.Idle); Status = $"Sell: done, {_ordersPlaced} orders placed"; return; }

            var (cn, cq) = _queue.Dequeue();
            _current = cn;
            _currentQty = cq;
            _havePicked = false;
            _wantPicked = false;
            _ownedFilter = false;
            _lockedWant = false;
            _lockedHave = false;
            _searchText = SearchPrefix(_current);
            _searchIndex = 0;
            _searchRetries = 0;
            _ownedClicked = false;
            _ownedClickedAt = DateTime.MinValue;
            _ownedSettled = false;
            _ownedPrevOptCount = -1;
            _ownedStabilityCheckAt = DateTime.MinValue;
            _ownedFilterAt = DateTime.MinValue;
            _searchAttempts = 0;
            _searchFocused = false;
            _searchCleared = false;
            _searchEmptied = false;
            _amountFocused = false;
            _amountCleared = false;
            _amountText = _currentQty.ToString();
            _amountIndex = 0;
            _wantFocused = false;
            _wantCleared = false;
            _wantTyped = false;
            _wantClickedOut = false;
            _wantFinalized = false;
            _wantPickerOpenedAt = DateTime.MinValue;
            _wantVerifyAttempts = 0;
            _placeAttempts = 0;
            _placeSlots0 = -1;
            _lastPlaceClickAt = DateTime.MinValue;
            SetState(SellState.PickingHave);
        }

        // ── Helpers (mirror FaustusSystem) ──

        // The currently selected I-Want currency name = the I-Want button's label (panel child 7/0,
        // confirmed by the panel dump). Falls back to child 7's own (aggregated) text so a slight
        // nesting difference can't blind the guard.
        private static string SelectedWantName(dynamic panel)
        {
            try
            {
                var btn = panel.GetChildAtIndex(7);
                if (btn == null) return "";
                var lbl = btn.GetChildAtIndex(0);
                string t0 = lbl == null ? "" : (string)lbl.Text ?? "";
                if (!string.IsNullOrWhiteSpace(t0)) return t0;
                return (string)btn.Text ?? "";
            }
            catch { return ""; }
        }

        private static bool IsChaos(string name)
            => !string.IsNullOrWhiteSpace(name) && Norm(name).Contains(Norm(WantCurrencyBaseName));

        // A readable name that is NOT Chaos. Empty/unreadable returns false (unknown ≠ wrong) so a
        // label we can't read never blocks a sale — only a positively-different currency does.
        private static bool IsDefinitelyWrongWant(string name)
            => !string.IsNullOrWhiteSpace(name) && !Norm(name).Contains(Norm(WantCurrencyBaseName));

        // Abandon the current candidate without placing (wrong I-Want, etc.). Route through the
        // builder-clear so any leftover amounts are emptied, then advance to the next candidate.
        // _ordersPlaced is not incremented, so this is a pure skip.
        private void SkipCurrentCandidate()
        {
            _clrHaveFocused = _clrHaveSelected = _clrHaveDone = false;
            _clrWantFocused = _clrWantSelected = _clrWantDone = false;
            SetState(SellState.ClearingBuilder);
        }

        // Count the picker's currently enumerated options. Used to detect when the Owned grid has
        // finished (re)rendering (count goes stable) — a resolution-independent settle signal.
        private static int CountOptions(dynamic picker)
        {
            int n = 0;
            try { foreach (var _ in picker.Options) { n++; if (n > 4000) break; } } catch { }
            return n;
        }

        private void SetState(SellState s) { _state = s; _stateEnteredAt = DateTime.Now; AddLog($"-> {s}"); }
        private bool CanClick() => (DateTime.Now - _lastClickAt).TotalMilliseconds >= ClickCooldownMs && BotInput.CanAct;

        private void ClickChildSingle(GameController gc, dynamic panel, int i)
        {
            try { var el = panel.GetChildAtIndex(i); if (el != null && el.IsVisible) ClickElement(gc, (ExileCore.PoEMemory.Element)el); }
            catch { }
        }

        // A plausible amount field: has at least one digit and only digit/separator characters.
        private static bool IsNumericAmount(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            bool hasDigit = false;
            foreach (var c in s)
            {
                if (char.IsDigit(c)) hasDigit = true;
                else if (c == ',' || c == '.' || c == ' ' || c == 'K' || c == 'k' || c == 'M' || c == 'm') continue;
                else return false;
            }
            return hasDigit;
        }

        private static string SafeChildText(dynamic panel, int i)
        {
            try { var e = panel.GetChildAtIndex(i); return e == null ? "" : (string)e.Text ?? ""; }
            catch { return "?"; }
        }

        // True once the search box holds typed input: the "Select currency" placeholder is gone/hidden,
        // or child 15 shows non-placeholder text. Used to confirm the box took our first keystroke.
        private static bool SearchBoxHasInput(ExileCore.PoEMemory.Element panel)
        {
            try
            {
                var ph = FindElementContaining(panel, "select currency", 0);
                if (ph == null || !ph.IsVisible) return true;
                var t = SafeChildText(panel, 15);
                return !string.IsNullOrWhiteSpace(t) && !Norm(t).Contains(Norm("select currency"));
            }
            catch { return true; } // on any read error, don't block typing (degrade to old behavior)
        }

        // Find the used-order-slot count by scanning the panel for the "N/M" label (e.g. "0/10").
        // Returns N (orders currently placed), or -1 if not found. Robust to child-index shifts.
        private static int FindSlotCount(dynamic panel)
        {
            for (int i = 10; i < 30; i++)
            {
                string t = SafeChildText(panel, i);
                if (string.IsNullOrWhiteSpace(t)) continue;
                int slash = t.IndexOf('/');
                if (slash <= 0 || slash >= t.Length - 1) continue;
                string a = t.Substring(0, slash).Trim();
                string b = t.Substring(slash + 1).Trim();
                if (int.TryParse(a, out int n) && int.TryParse(b, out int m)
                    && m > 0 && m <= 20 && n >= 0 && n <= m)
                    return n;
            }
            return -1;
        }

        // Find the first element whose (normalized) text contains 'name' and whose rect is within
        // the visible [minY, maxY] band — i.e. the on-screen picker cell, not an off-screen data row.
        private static ExileCore.PoEMemory.Element FindVisibleByText(ExileCore.PoEMemory.Element root, string name, float minY, float maxY, int depth)
        {
            if (root == null || depth > 16) return null;
            try
            {
                var t = root.Text;
                if (!string.IsNullOrEmpty(t) && Norm(t).Contains(Norm(name)))
                {
                    var r = root.GetClientRect();
                    if (r.Y >= minY && r.Y <= maxY) return root;
                }
                var kids = root.Children;
                if (kids != null)
                    for (int i = 0; i < kids.Count; i++)
                    {
                        var f = FindVisibleByText(kids[i], name, minY, maxY, depth + 1);
                        if (f != null) return f;
                    }
            }
            catch { }
            return null;
        }

        // Typeable prefix for the search box: letters/digits/spaces up to the first punctuation
        // (e.g. an apostrophe), so it stays a valid substring of the display name.
        // "Dead Man's Sulphur" -> "dead man"; "Orb of Annulment" -> "orb of annulment".
        private static System.Windows.Forms.Keys CharToKey(char ch)
        {
            char c = char.ToLowerInvariant(ch);
            if (c >= 'a' && c <= 'z') return (System.Windows.Forms.Keys)((int)System.Windows.Forms.Keys.A + (c - 'a'));
            if (c >= '0' && c <= '9') return (System.Windows.Forms.Keys)((int)System.Windows.Forms.Keys.D0 + (c - '0'));
            if (c == ' ') return System.Windows.Forms.Keys.Space;
            return System.Windows.Forms.Keys.None;
        }

        private static string SearchPrefix(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == ' ') sb.Append(char.ToLowerInvariant(c));
                else break;
            }
            var s = sb.ToString().Trim();
            if (s.Length == 0) s = name;
            // Type all but the last two characters. The exchange filters on a prefix, so the target
            // still shows up, and an incomplete entry looks less bot-like. Only trim when enough is
            // left to stay specific (short names are typed in full).
            if (s.Length > 5)
                s = s.Substring(0, s.Length - 2).TrimEnd();
            return s;
        }

        // Among all descendants whose normalized text contains 'name' with rect.Y in [minY, maxY],
        // return the topmost (smallest Y) — the on-screen grid cell, not off-screen data rows.
        private static ExileCore.PoEMemory.Element FindTopmostVisible(ExileCore.PoEMemory.Element root, string name, float minY, float maxY, int depth)
        {
            if (root == null || depth > 18) return null;
            ExileCore.PoEMemory.Element best = null;
            float bestY = float.MaxValue;
            try
            {
                var t = root.Text;
                if (!string.IsNullOrEmpty(t) && Norm(t).Contains(Norm(name)))
                {
                    var r = root.GetClientRect();
                    if (r.Y >= minY && r.Y <= maxY) { best = root; bestY = r.Y; }
                }
                var kids = root.Children;
                if (kids != null)
                    for (int i = 0; i < kids.Count; i++)
                    {
                        var f = FindTopmostVisible(kids[i], name, minY, maxY, depth + 1);
                        if (f != null) { var fy = f.GetClientRect().Y; if (fy < bestY) { best = f; bestY = fy; } }
                    }
            }
            catch { }
            return best;
        }

        // Lowercase, keep letters/digits, collapse whitespace, drop punctuation (apostrophes etc.)
        // so "Dead Man's\nSulphur" and "Dead Man's Sulphur" compare equal.
        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            bool lastSpace = true;
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); lastSpace = false; }
                else if (char.IsWhiteSpace(c)) { if (!lastSpace) { sb.Append(' '); lastSpace = true; } }
            }
            return sb.ToString().Trim();
        }

        private void ClickChild(GameController gc, dynamic panel, int i, int j)
        {
            try
            {
                var el = panel.GetChildAtIndex(i)?.GetChildAtIndex(j);
                if (el != null && el.IsVisible) { ClickElement(gc, (ExileCore.PoEMemory.Element)el); }
            }
            catch { }
        }

        private void ClickRect(GameController gc, ExileCore.PoEMemory.Element el)
        {
            try
            {
                var rect = el.GetClientRect();
                var center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                BotInput.Click(ToAbsolutePos(gc, center));
                _lastClickAt = DateTime.Now;
            }
            catch { }
        }

        private void ClickElement(GameController gc, ExileCore.PoEMemory.Element element)
        {
            try
            {
                var rect = element.GetClientRect();
                var center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                BotInput.Click(ToAbsolutePos(gc, center));
                _lastClickAt = DateTime.Now;
            }
            catch { }
        }

        private static Vector2 ToAbsolutePos(GameController gc, Vector2 clientPos)
        {
            var wr = gc.Window.GetWindowRectangle();
            return new Vector2(wr.X + clientPos.X, wr.Y + clientPos.Y);
        }

        private double UnitChaos(BotContext ctx, string name)
        {
            foreach (var cat in EligibleCats)
            {
                var pr = ctx.NinjaPrice.GetPrice(name, cat);
                if (pr.MaxChaosValue > 0.0) return pr.MaxChaosValue;
            }
            return 0.0;
        }

        // Diagnostic: write the owned currencies ninja could NOT price to <NinjaCache>/sell_unpriced.txt
        // (qty-sorted). Lets us see exactly which names aren't matching without any on-screen HUD.
        private static void DumpUnpriced(BotContext ctx, List<(string name, int qty)> unpriced)
        {
            try
            {
                var dir = ctx.NinjaPrice?.CacheDir;
                if (string.IsNullOrEmpty(dir)) return;
                System.IO.Directory.CreateDirectory(dir);
                unpriced.Sort((a, b) => b.qty.CompareTo(a.qty));
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss} — owned currencies ninja could not price ({unpriced.Count})");
                foreach (var (name, qty) in unpriced)
                    sb.AppendLine($"{qty}\t{name}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "sell_unpriced.txt"), sb.ToString());
            }
            catch { }
        }

        private static int ReadPickerQty(ExileCore.PoEMemory.Element opt)
        {
            try
            {
                var t = opt.GetChildAtIndex(1)?.GetChildAtIndex(0)?.Text;
                if (string.IsNullOrWhiteSpace(t)) return 0;
                t = t.Trim().Replace(",", "");
                double mult = 1;
                if (t.EndsWith("K", StringComparison.OrdinalIgnoreCase)) { mult = 1000; t = t[..^1]; }
                else if (t.EndsWith("M", StringComparison.OrdinalIgnoreCase)) { mult = 1_000_000; t = t[..^1]; }
                return double.TryParse(t, out var v) ? (int)(v * mult) : 0;
            }
            catch { return 0; }
        }

        private static Entity FindFaustus(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                if (entity.Path?.Contains(FaustusPath, StringComparison.OrdinalIgnoreCase) == true)
                    return entity;
            return null;
        }

        // Recursively find the first element whose normalized text contains 'needle'.
        private static ExileCore.PoEMemory.Element FindElementContaining(ExileCore.PoEMemory.Element root, string needle, int depth)
        {
            if (root == null || depth > 16) return null;
            try
            {
                var t = root.Text;
                if (!string.IsNullOrEmpty(t) && Norm(t).Contains(Norm(needle))) return root;
                var kids = root.Children;
                if (kids != null)
                    for (int i = 0; i < kids.Count; i++)
                    {
                        var f = FindElementContaining(kids[i], needle, depth + 1);
                        if (f != null) return f;
                    }
            }
            catch { }
            return null;
        }

        // Recursively find the first element whose trimmed text matches (case-insensitive).
        private static ExileCore.PoEMemory.Element FindElementByText(ExileCore.PoEMemory.Element root, string text, int depth)
        {
            if (root == null || depth > 12) return null;
            try
            {
                var t = root.Text;
                if (!string.IsNullOrEmpty(t) && t.Trim().Equals(text, StringComparison.OrdinalIgnoreCase))
                    return root;
                var kids = root.Children;
                if (kids != null)
                    for (int i = 0; i < kids.Count; i++)
                    {
                        var f = FindElementByText(kids[i], text, depth + 1);
                        if (f != null) return f;
                    }
            }
            catch { }
            return null;
        }

        private static ExileCore.PoEMemory.Element FindPickerOption(dynamic picker, string metaSubstring, string baseName)
        {
            try
            {
                foreach (var option in picker.Options)
                {
                    if (option == null) continue;
                    var itemType = option.ItemType;
                    if (itemType == null) continue;
                    string meta = itemType.Metadata;
                    string bname = itemType.BaseName;
                    if (metaSubstring != null)
                    {
                        if (meta?.Contains(metaSubstring, StringComparison.OrdinalIgnoreCase) == true)
                            return (ExileCore.PoEMemory.Element)option;
                    }
                    else if (baseName != null)
                    {
                        if (bname?.Equals(baseName, StringComparison.OrdinalIgnoreCase) == true)
                            return (ExileCore.PoEMemory.Element)option;
                    }
                }
            }
            catch { }
            return null;
        }
    }

    internal enum SellState
    {
        Idle,
        WalkingToFaustus,
        WaitingForDialog,
        ClickingExchange,
        WaitingForPanel,
        ScanCandidates,
        PickingHave,
        PickingWant,
        LockingAmounts,
        PlacingOrder,
        ClearingBuilder,
    }
}
