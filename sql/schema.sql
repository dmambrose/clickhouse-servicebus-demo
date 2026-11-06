CREATE TABLE IF NOT EXISTS demo.sales_events
(
    run_id UUID,
    event_id UInt64,
    occurred_at DateTime('UTC'),
    product LowCardinality(String),
    country LowCardinality(String),
    quantity UInt16,
    unit_price Decimal(10, 2)
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(occurred_at)
ORDER BY (run_id, product, occurred_at, event_id)
