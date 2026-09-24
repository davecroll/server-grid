using System.Diagnostics;

namespace ServerGrid.Data;

/// <summary>Singleton holding the demo data set. Generated once, deterministically, on first use.</summary>
public sealed class TradeRepository
{
    private readonly Lazy<Trade[]> _trades;
    private readonly ILogger<TradeRepository> _logger;

    public TradeRepository(IConfiguration configuration, ILogger<TradeRepository> logger)
    {
        _logger = logger;
        var count = configuration.GetValue<int?>("Grid:RowCount") ?? 1_000_000;
        _trades = new Lazy<Trade[]>(() => Generate(count), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Trade[] Trades => _trades.Value;

    private Trade[] Generate(int count)
    {
        var sw = Stopwatch.StartNew();
        var rng = new Random(20240917);

        string[] firstNames = ["Ava", "Liam", "Noah", "Emma", "Olivia", "Mason", "Sophia", "Lucas", "Mia", "Ethan", "Isabella", "Logan", "Amelia", "James", "Harper", "Benjamin", "Evelyn", "Elijah", "Abigail", "Oliver", "Priya", "Wei", "Kenji", "Fatima", "Mateo", "Zara", "Ibrahim", "Chloe", "Diego", "Yuki"];
        string[] lastNames = ["Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Martinez", "Lopez", "Wilson", "Anderson", "Taylor", "Thomas", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson", "Nguyen", "Patel", "Kim", "Chen", "Singh", "Okafor", "Novak", "Fischer", "Rossi", "Silva"];
        string[] desks = ["Equities", "Rates", "FX", "Credit", "Commodities", "Index Arb", "Delta One", "Convertibles", "EM", "Structured"];
        string[] regions = ["EMEA", "Americas", "APAC"];
        string[] counterparties = ["Goldman Sachs", "Morgan Stanley", "JPMorgan", "Barclays", "Citi", "UBS", "Deutsche Bank", "BNP Paribas", "HSBC", "Nomura", "Société Générale", "Wells Fargo", "RBC", "Jefferies", "Macquarie", "Mizuho", "BofA Securities", "Credit Agricole", "Santander", "Standard Chartered"];
        (string Ticker, string Name, string Currency, decimal Base)[] instruments =
        [
            ("AAPL", "Apple Inc.", "USD", 190m), ("MSFT", "Microsoft Corp.", "USD", 410m), ("NVDA", "NVIDIA Corp.", "USD", 880m),
            ("AMZN", "Amazon.com Inc.", "USD", 180m), ("GOOGL", "Alphabet Inc.", "USD", 150m), ("META", "Meta Platforms", "USD", 500m),
            ("TSLA", "Tesla Inc.", "USD", 175m), ("BRK.B", "Berkshire Hathaway", "USD", 410m), ("JPM", "JPMorgan Chase", "USD", 195m),
            ("V", "Visa Inc.", "USD", 280m), ("XOM", "Exxon Mobil", "USD", 115m), ("UNH", "UnitedHealth Group", "USD", 490m),
            ("SHEL", "Shell plc", "GBP", 27m), ("AZN", "AstraZeneca", "GBP", 120m), ("HSBA", "HSBC Holdings", "GBP", 6.5m),
            ("ULVR", "Unilever", "GBP", 40m), ("BP", "BP plc", "GBP", 5m), ("RIO", "Rio Tinto", "GBP", 52m),
            ("SAP", "SAP SE", "EUR", 175m), ("ASML", "ASML Holding", "EUR", 900m), ("MC", "LVMH", "EUR", 800m),
            ("SIE", "Siemens AG", "EUR", 170m), ("TTE", "TotalEnergies", "EUR", 62m), ("NESN", "Nestlé", "CHF", 95m),
            ("7203", "Toyota Motor", "JPY", 3500m), ("6758", "Sony Group", "JPY", 13000m), ("9984", "SoftBank Group", "JPY", 8500m),
            ("005930", "Samsung Electronics", "KRW", 78000m), ("0700", "Tencent Holdings", "HKD", 380m), ("BHP", "BHP Group", "AUD", 45m),
        ];
        string?[] notes = [null, null, null, null, "Client instruction", "Block trade", "Hedge", "Rebalance", "Program trade", null, "Corporate action", "Manual booking", null];

        var traders = new string[120];
        for (var i = 0; i < traders.Length; i++)
            traders[i] = $"{firstNames[rng.Next(firstNames.Length)]} {lastNames[rng.Next(lastNames.Length)]}";

        var start = new DateOnly(2022, 1, 3);
        var trades = new Trade[count];
        for (var i = 0; i < count; i++)
        {
            var (ticker, name, currency, basePrice) = instruments[rng.Next(instruments.Length)];
            var tradeDate = start.AddDays(rng.Next(0, 900));
            var price = Math.Round(basePrice * (decimal)(0.7 + rng.NextDouble() * 0.6), currency == "JPY" || currency == "KRW" ? 0 : 2);
            var qty = rng.Next(1, 20) * (rng.Next(10) == 0 ? 10_000 : rng.Next(3) == 0 ? 1_000 : 100);
            var status = (TradeStatus)rng.Next(6);
            var desk = desks[rng.Next(desks.Length)];
            trades[i] = new Trade
            {
                Id = 100_000 + i,
                TradeDate = tradeDate,
                BookedAt = tradeDate.ToDateTime(new TimeOnly(rng.Next(7, 20), rng.Next(60), rng.Next(60))),
                Trader = traders[rng.Next(traders.Length)],
                Desk = desk,
                Region = regions[rng.Next(regions.Length)],
                Counterparty = counterparties[rng.Next(counterparties.Length)],
                Ticker = ticker,
                Instrument = name,
                Side = rng.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell,
                Quantity = qty,
                Price = price,
                Notional = price * qty,
                Currency = currency,
                Status = status,
                IsSettled = status == TradeStatus.Settled,
                Commission = rng.Next(5) == 0 ? null : Math.Round(price * qty * 0.0004m, 2),
                Notes = notes[rng.Next(notes.Length)],
            };
        }

        _logger.LogInformation("Generated {Count:N0} trades in {Elapsed} ms", count, sw.ElapsedMilliseconds);
        return trades;
    }
}
