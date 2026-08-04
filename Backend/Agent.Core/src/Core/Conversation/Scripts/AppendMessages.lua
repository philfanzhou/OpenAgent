local current = redis.call('GET', KEYS[1])
if not current then
    return cjson.encode({status = 'NOT_FOUND'})
end

local record = cjson.decode(current)
if tonumber(record.version) ~= tonumber(ARGV[1]) then
    return cjson.encode({status = 'CONFLICT', actual = record.version, expected = tonumber(ARGV[1])})
end

-- Build idempotency key set from existing messages
local existingKeys = {}
for _, m in ipairs(record.messages) do
    if type(m.idempotencyKey) == 'string' and m.idempotencyKey ~= '' then
        existingKeys[string.lower(m.idempotencyKey)] = true
    end
end

local newMsgs = cjson.decode(ARGV[2])
local skipped = 0
for _, m in ipairs(newMsgs) do
    if type(m.idempotencyKey) == 'string' and m.idempotencyKey ~= '' then
        if existingKeys[string.lower(m.idempotencyKey)] then
            skipped = skipped + 1
        else
            table.insert(record.messages, m)
            existingKeys[string.lower(m.idempotencyKey)] = true
        end
    else
        table.insert(record.messages, m)
    end
end
record.version = record.version + 1
record.messageCount = #record.messages
record.updatedAt = ARGV[4]
record.lastMessageAt = ARGV[5]
if record.lastMessageAt == '' then
    record.lastMessageAt = record.updatedAt
end

local ok = redis.call('SET', KEYS[1], cjson.encode(record), 'EX', tonumber(ARGV[3]))
if ok then
    return cjson.encode({status = 'OK', version = record.version, count = record.messageCount, skipped = skipped})
else
    return cjson.encode({status = 'ERROR', reason = 'SET failed'})
end
