-- acquire_lock.lua
-- Atomically acquires a distributed lock for an idempotency key.
-- KEYS[1] = full Redis key (e.g. "idmp:order_abc_123")
-- ARGV[1] = lock owner token (unique per server instance + request)
-- ARGV[2] = lock TTL in milliseconds
-- ARGV[3] = request hash (ulong as string) — stored alongside the lock

-- Returns:
--   1 = lock acquired successfully (key did not exist)
--   0 = lock NOT acquired (key already exists — another instance owns it)

local existing = redis.call('GET', KEYS[1])
if existing then
    return 0
end

-- Store as a hash with multiple fields:
-- status: "InFlight"
-- owner:  lock owner token
-- hash:   request payload hash (for mismatch detection)
redis.call('HSET', KEYS[1],
    'status', 'InFlight',
    'owner',  ARGV[1],
    'hash',   ARGV[3]
)
redis.call('PEXPIRE', KEYS[1], ARGV[2])
return 1
