using AutoExile.Systems;
using System;
using System.Collections.Generic;

namespace AutoExile.Modes
{
    /// <summary>
    /// Dedicated mode that runs a single currency-exchange sell run (surplus currency → Chaos)
    /// then idles. Select this mode in your hideout and press Insert to run; it does not farm.
    /// </summary>
    public class SellMode : IBotMode
    {
        private readonly SellExchangeSystem _sell = new();
        private bool _started;
        private bool _done;

        public string Name => "Sell";

        public string Status => _done
            ? (_sell.OrdersPlaced > 0
                ? $"Sell complete — {_sell.OrdersPlaced} orders placed (Insert to stop)"
                : $"Sell done, 0 placed — last: {_sell.Status} (Insert to stop)")
            : (_started ? _sell.Status : "Sell: starting…");

        public IReadOnlyList<string> Log => _sell.Log;

        public void OnEnter(BotContext ctx) { _started = false; _done = false; }
        public void OnExit() { _sell.Cancel(); }

        /// <summary>Reset so the next Tick begins a fresh sell run (called on the bot's stopped→running
        /// edge, so Stop then Start re-runs the sell instead of idling on the previous completed run).</summary>
        public void Restart() { _started = false; _done = false; _sell.Cancel(); }

        public void Tick(BotContext ctx)
        {
            if (_done) return;

            if (!_started)
            {
                var excl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in (ctx.Settings.Build.SellExclusions.Value ?? "")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    excl.Add(e);
                _sell.Start(ctx.Settings.Build.SellMaxOrdersPerRun.Value, 50.0, excl);
                _started = true;
            }

            if (_sell.IsBusy) _sell.Tick(ctx);
            else _done = true; // run finished — idle (don't farm)
        }
    }
}
