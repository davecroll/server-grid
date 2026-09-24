namespace ServerGrid.Data;

public enum TradeSide { Buy, Sell }
public enum TradeStatus { New, Pending, Confirmed, Settled, Cancelled, Failed }

public sealed class Trade
{
    public int Id { get; init; }
    public DateOnly TradeDate { get; init; }
    public DateTime BookedAt { get; init; }
    public required string Trader { get; init; }
    public required string Desk { get; init; }
    public required string Region { get; init; }
    public required string Counterparty { get; init; }
    public required string Ticker { get; init; }
    public required string Instrument { get; init; }
    public TradeSide Side { get; init; }
    public int Quantity { get; init; }
    public decimal Price { get; init; }
    public decimal Notional { get; init; }
    public required string Currency { get; init; }
    public TradeStatus Status { get; init; }
    public bool IsSettled { get; init; }
    public decimal? Commission { get; init; }
    public string? Notes { get; init; }
}
