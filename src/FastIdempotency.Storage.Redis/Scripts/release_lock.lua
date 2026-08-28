-- release_lock.lua
-- Releases the distributed lock ONLY if the caller is the current owner.
-- This prevents a crashed-and-restarted server from releasing another instance's active lock.
--
-- KEYS[1] = full Redis key
-- ARGV[1] = lock owner token (must match stored 'owner' field)
--
-- Returns:
--   1 = lock released successfully
--   0 = NOT released (owner mismatch — someone else holds the lock)

local owner = redis.call('HGET', KEYS[1], 'owner')
if owner == ARGV[1] then
    redis.call('DEL', KEYS[1])
    return 1
end
return 0
